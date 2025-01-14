using kcp2k;
using Mirror;
using Mirror.SimpleWeb;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace LightReflectiveMirror
{
    [DefaultExecutionOrder(1001)]
    public partial class LightReflectiveMirrorTransport : Transport
    {
        private void OnConnectedToRelay() => _connectedToRelay = true;
        public bool IsAuthenticated() => _isAuthenticated;
        private void Awake()
        {
#if !NET_4_6
            throw new Exception("LRM | Please switch to .NET 4.x for LRM to function properly!");
#endif

            if (clientToServerTransport is LightReflectiveMirrorTransport)
                throw new Exception("Haha real funny... Use a different transport.");

            _directConnectModule = GetComponent<LRMDirectConnectModule>();

            if (_directConnectModule != null)
            {
                if (useNATPunch && !_directConnectModule.SupportsNATPunch())
                {
                    Debug.LogWarning("LRM | NATPunch is turned on but the transport used does not support it. It will be disabled.");
                    useNATPunch = false;
                }
            }

            SetupCallbacks();

            if (connectOnAwake)
                ConnectToRelay();

            InvokeRepeating(nameof(SendHeartbeat), heartBeatInterval, heartBeatInterval);
        }

        private void SetupCallbacks()
        {
            if (_callbacksInitialized)
                return;

            _callbacksInitialized = true;
            clientToServerTransport.OnClientConnected = OnConnectedToRelay;
            clientToServerTransport.OnClientDataReceived = DataReceived;
            clientToServerTransport.OnClientDisconnected = Disconnected;
        }

        void Disconnected()
        {
            _connectedToRelay = false;
            _isAuthenticated = false;
            diconnectedFromRelay?.Invoke();
            serverStatus = "Disconnected from relay.";
        }

        public void ConnectToRelay()
        {
            if (!useLoadBalancer)
            {
                if (!_connectedToRelay)
                {
                    Connect(serverIP);
                }
                else
                {
                    Debug.LogWarning("LRM | Already connected to relay!");
                }
            }
            else
            {
                if (!_connectedToRelay)
                {
                    StartCoroutine(RelayConnect());
                }
                else
                {
                    Debug.LogWarning("LRM | Already connected to relay!");
                }
            }
        }

        /// <summary>
        /// Connects to the desired relay
        /// </summary>
        /// <param name="serverIP"></param>
        private void Connect(string serverIP, ushort port = 7777)
        {
            // need to implement custom port
            if (clientToServerTransport is LightReflectiveMirrorTransport)
                throw new Exception("LRM | Client to Server Transport cannot be LRM.");

            SetTransportPort(port);
            

            this.serverIP = serverIP;
            serverStatus = "Connecting to relay...";
            _clientSendBuffer = new byte[clientToServerTransport.GetMaxPacketSize()];
            clientToServerTransport.ClientConnect(serverIP);
        }

        private void DisconnectFromRelay()
        {
            if (IsAuthenticated())
            {
                clientToServerTransport.ClientDisconnect();
            }
        }

        void SendHeartbeat()
        {
            if (_connectedToRelay)
            {
                int pos = 0;
                _clientSendBuffer.WriteByte(ref pos, 200);
                clientToServerTransport.ClientSend(0, new ArraySegment<byte>(_clientSendBuffer, 0, pos));

                if (_NATPuncher != null)
                    _NATPuncher.Send(new byte[] { 0 }, 1, _relayPuncherIP);

                var keys = new List<IPEndPoint>(_serverProxies.GetAllKeys());

                for (int i = 0; i < keys.Count; i++)
                {
                    if (DateTime.Now.Subtract(_serverProxies.GetByFirst(keys[i]).lastInteractionTime).TotalSeconds > 10)
                    {
                        _serverProxies.GetByFirst(keys[i]).Dispose();
                        _serverProxies.Remove(keys[i]);
                    }
                }
            }
        }

        void DataReceived(ArraySegment<byte> segmentData, int channel)
        {
            try
            {
                var data = segmentData.Array;
                int pos = segmentData.Offset;

                OpCodes opcode = (OpCodes)data.ReadByte(ref pos);

                switch (opcode)
                {
                    case OpCodes.Authenticated:
                        serverStatus = "Authenticated! Good to go!";
                        _isAuthenticated = true;
                        break;
                    case OpCodes.AuthenticationRequest:
                        serverStatus = "Sent authentication to relay...";
                        SendAuthKey();
                        break;
                    case OpCodes.GetData:
                        var recvData = data.ReadBytes(ref pos);

                        if (_isServer)
                        {
                            if (_connectedRelayClients.TryGetByFirst(data.ReadInt(ref pos), out int clientID))
                                OnServerDataReceived?.Invoke(clientID, new ArraySegment<byte>(recvData), channel);
                        }

                        if (_isClient)
                            OnClientDataReceived?.Invoke(new ArraySegment<byte>(recvData), channel);
                        break;
                    case OpCodes.ServerLeft:
                        if (_isClient)
                        {
                            _isClient = false;
                            OnClientDisconnected?.Invoke();
                        }
                        break;
                    case OpCodes.PlayerDisconnected:
                        if (_isServer)
                        {
                            int user = data.ReadInt(ref pos);
                            if (_connectedRelayClients.TryGetByFirst(user, out int clientID))
                            {
                                OnServerDisconnected?.Invoke(clientID);
                                _connectedRelayClients.Remove(user);
                            }
                        }
                        break;
                    case OpCodes.RoomCreated:
                        serverId = data.ReadString(ref pos);
                        break;
                    case OpCodes.ServerJoined:
                        int clientId = data.ReadInt(ref pos);
                        if (_isClient)
                        {
                            OnClientConnected?.Invoke();
                        }
                        if (_isServer)
                        {
                            _connectedRelayClients.Add(clientId, _currentMemberId);
                            OnServerConnected?.Invoke(_currentMemberId);
                            _currentMemberId++;
                        }
                        break;
                    case OpCodes.DirectConnectIP:
                        var ip = data.ReadString(ref pos);
                        int port = data.ReadInt(ref pos);
                        bool attemptNatPunch = data.ReadBool(ref pos);

                        _directConnectEndpoint = new IPEndPoint(IPAddress.Parse(ip), port);

                        if (useNATPunch && attemptNatPunch)
                        {
                            StartCoroutine(NATPunch(_directConnectEndpoint));
                        }

                        if (!_isServer)
                        {
                            if (_clientProxy == null && useNATPunch && attemptNatPunch)
                            {
                                _clientProxy = new SocketProxy(_NATIP.Port - 1);
                                _clientProxy.dataReceived += ClientProcessProxyData;
                            }

                            if (useNATPunch && attemptNatPunch)
                                _directConnectModule.JoinServer("127.0.0.1", _NATIP.Port - 1);
                            else
                                _directConnectModule.JoinServer(ip, port);
                        }

                        break;
                    case OpCodes.RequestNATConnection:
                        if (GetLocalIp() != null && _directConnectModule != null)
                        {
                            byte[] initalData = new byte[150];
                            int sendPos = 0;

                            initalData.WriteBool(ref sendPos, true);
                            initalData.WriteString(ref sendPos, data.ReadString(ref pos));
                            NATPunchtroughPort = data.ReadInt(ref pos);

                            if (_NATPuncher == null)
                            {
                                _NATPuncher = new UdpClient { ExclusiveAddressUse = false };
                                while (true)
                                {
                                    try
                                    {
                                        _NATIP = new IPEndPoint(IPAddress.Parse(GetLocalIp()), UnityEngine.Random.Range(16000, 17000));
                                        _NATPuncher.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                                        _NATPuncher.Client.Bind(_NATIP);
                                        break;
                                    }
                                    catch {} // Binding port is in use, keep trying :P
                                }
                            }

                            IPAddress serverAddr;

                            if (!IPAddress.TryParse(serverIP, out serverAddr))
                                serverAddr = Dns.GetHostEntry(serverIP).AddressList[0];

                            _relayPuncherIP = new IPEndPoint(serverAddr, NATPunchtroughPort);

                            // Send 3 to lower chance of it being dropped or corrupted when received on server.
                            _NATPuncher.Send(initalData, sendPos, _relayPuncherIP);
                            _NATPuncher.Send(initalData, sendPos, _relayPuncherIP);
                            _NATPuncher.Send(initalData, sendPos, _relayPuncherIP);
                            _NATPuncher.BeginReceive(new AsyncCallback(RecvData), _NATPuncher);
                        }
                        break;
                }
            }
            catch (Exception e) { print(e); }
        }

        public void SetTransportPort(ushort port)
        {
            if (clientToServerTransport is KcpTransport kcp)
                kcp.Port = port;

            if (clientToServerTransport is TelepathyTransport telepathy)
                telepathy.port = port;

            if (clientToServerTransport is SimpleWebTransport swt)
                swt.port = port;
        }

        public void UpdateRoomInfo(string newServerName = null, string newServerData = null, bool? newServerIsPublic = null, int? newPlayerCap = null)
        {
            if (_isServer)
            {
                int pos = 0;

                _clientSendBuffer.WriteByte(ref pos, (byte)OpCodes.UpdateRoomData);

                if (!string.IsNullOrEmpty(newServerName))
                {
                    _clientSendBuffer.WriteBool(ref pos, true);
                    _clientSendBuffer.WriteString(ref pos, newServerName);
                }
                else
                    _clientSendBuffer.WriteBool(ref pos, false);

                if (!string.IsNullOrEmpty(newServerData))
                {
                    _clientSendBuffer.WriteBool(ref pos, true);
                    _clientSendBuffer.WriteString(ref pos, newServerData);
                }
                else
                    _clientSendBuffer.WriteBool(ref pos, false);

                if (newServerIsPublic != null)
                {
                    _clientSendBuffer.WriteBool(ref pos, true);
                    _clientSendBuffer.WriteBool(ref pos, newServerIsPublic.Value);
                }
                else
                    _clientSendBuffer.WriteBool(ref pos, false);

                if (newPlayerCap != null)
                {
                    _clientSendBuffer.WriteBool(ref pos, true);
                    _clientSendBuffer.WriteInt(ref pos, newPlayerCap.Value);
                }
                else
                    _clientSendBuffer.WriteBool(ref pos, false);

                clientToServerTransport.ClientSend(0, new ArraySegment<byte>(_clientSendBuffer, 0, pos));
            }
        }

        Room GetServerForID(string serverID)
        {
            for(int i = 0; i < relayServerList.Count; i++)
            {
                if(relayServerList[i].serverId == serverID)
                    return relayServerList[i];
            }

            OnClientDisconnected?.Invoke();
            throw new Exception("LRM | An attempt was made to connect to a server which does not exist!");
        }

        void SendAuthKey()
        {
            int pos = 0;
            _clientSendBuffer.WriteByte(ref pos, (byte)OpCodes.AuthenticationResponse);
            _clientSendBuffer.WriteString(ref pos, authenticationKey);
            clientToServerTransport.ClientSend(0, new ArraySegment<byte>(_clientSendBuffer, 0, pos));
        }

        public enum OpCodes
        {
            Default = 0, RequestID = 1, JoinServer = 2, SendData = 3, GetID = 4, ServerJoined = 5, GetData = 6, CreateRoom = 7, ServerLeft = 8, PlayerDisconnected = 9, RoomCreated = 10,
            LeaveRoom = 11, KickPlayer = 12, AuthenticationRequest = 13, AuthenticationResponse = 14, Authenticated = 17, UpdateRoomData = 18, ServerConnectionData = 19, RequestNATConnection = 20,
            DirectConnectIP = 21
        }

        private static string GetLocalIp()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }

            return null;
        }
    }

    [Serializable]
    public struct Room
    {
        public string serverName;
        public int maxPlayers;
        public string serverId;
        public string serverData;
        public int hostId;
        public List<int> clients;

        public RelayAddress relayInfo;

        /// <summary>
        /// This variable is only available on the client
        /// </summary>
        public int currentPlayers { get => clients.Count + 1; }
    }

    [Serializable]
    public struct RelayAddress
    {
        public ushort Port;
        public ushort EndpointPort;
        public string Address;
    }

}
