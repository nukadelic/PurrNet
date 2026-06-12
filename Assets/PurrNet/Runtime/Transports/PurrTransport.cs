using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using JamesFrowen.SimpleWeb;
using JetBrains.Annotations;
using LiteNetLib;
using PurrNet.Logging;
using PurrNet.Packing;
using UnityEngine;

namespace PurrNet.Transports
{
    [AddComponentMenu("PurrNet/Transport/Purr Transport")]
    // ReSharper disable once PartialTypeWithSinglePart
    public partial class PurrTransport : GenericTransport, ITransport
    {
        enum SERVER_PACKET_TYPE : byte
        {
            SERVER_CLIENT_CONNECTED = 0,
            SERVER_CLIENT_DISCONNECTED = 1,
            SERVER_CLIENT_DATA = 2,
            SERVER_AUTHENTICATED = 3,
            SERVER_AUTHENTICATION_FAILED = 4,
            SERVER_PIPE_AUTHENTICATED = 5,
            SERVER_NAT_INTRODUCE = 6,
            SERVER_HOST_LOST = 7,
            SERVER_PROMOTE_TO_HOST = 8,
            SERVER_SNAPSHOT_BEGIN = 9,
            SERVER_SNAPSHOT_CHUNK = 10,
            SERVER_SNAPSHOT_COMMIT = 11,
            SERVER_HOST_MIGRATED = 12
        }

        enum HOST_PACKET_TYPE : byte
        {
            SEND_KEEPALIVE = 0,
            SEND_ONE = 1,
            KICK_PLAYER = 2,
            SNAPSHOT_BEGIN = 3,
            SNAPSHOT_CHUNK = 4,
            SNAPSHOT_COMMIT = 5,
            SNAPSHOT_CLEAR = 6,
            MIGRATION_READY = 7
        }

        /// <summary>
        /// Relay protocol version sent during authentication when persistent rooms are
        /// enabled. Version 1 keeps the relay→client receive path framed after
        /// authentication (every packet starts with a SERVER_PACKET_TYPE byte), which is
        /// what makes the host-migration control packets possible.
        /// </summary>
        const int RELAY_PROTOCOL_VERSION = 1;

        /// <summary>Magic prefix of MIGRATION_READY ("PRMG"), mirrored by the relay.</summary>
        const uint MIGRATION_READY_MAGIC = 0x50524D47;

        /// <summary>Chunk size used when uploading a room snapshot to the relay.</summary>
        const int SNAPSHOT_CHUNK_SIZE = 8 * 1024;

        [Serializable, UsedImplicitly]
        private struct ClientAuthenticate
        {
            public string roomName;
            public string clientSecret;
            public bool nat;
            public int protocolVersion;
        }

        [Header("Remote Settings")]
        [SerializeField, HideInInspector] private string _masterServer = "https://purrtransport.purrservers.com/";
        [SerializeField, HideInInspector] private string _roomName;
        [SerializeField, HideInInspector] private string _region = "eu-central";
        [SerializeField, HideInInspector] private string _host;

        [Header("Shared Settings")]
        [Tooltip("The amount of time in seconds before socket is disconnected due to no data being received.")]
        [SerializeField, HideInInspector] private float _timeoutInSeconds = 5f;
        [SerializeField, HideInInspector] private bool _pollEventsInUpdate;

        [Tooltip("Use NAT hole-punching to establish a direct P2P link when possible. " +
                 "If a punch succeeds the session runs over P2P; if that link is later " +
                 "lost the session is disconnected cleanly.")]
        [SerializeField, HideInInspector] private bool _useNat;
        [SerializeField, HideInInspector] private float _natResolveTimeout = 8f;

        [Tooltip("Persistent rooms survive host loss: the relay keeps the room (and an optional " +
                 "host-uploaded state snapshot) alive and promotes a surviving client to host. " +
                 "Requires a relay that supports protocol v1.")]
        [SerializeField, HideInInspector] private bool _persistentRoom;

        [Tooltip("When this peer is elected as the new host, automatically run " +
                 "NetworkManager.PromoteToServer(). Disable to drive promotion manually " +
                 "via the onPromotedToHost event.")]
        [SerializeField, HideInInspector] private bool _autoPromoteToHost = true;

        [Tooltip("When the room migrated to a new host, automatically run " +
                 "NetworkManager.TransferToNewServer(). Disable to drive the transfer manually " +
                 "via the onHostMigrated event.")]
        [SerializeField, HideInInspector] private bool _autoTransferOnMigration = true;

        [Tooltip("Automatically report MIGRATION_READY to the relay right after promotion. " +
                 "Disable if a state-restore step (e.g. a persistence module) should decide " +
                 "when the new host is ready, then call SendMigrationReady() manually.")]
        [SerializeField, HideInInspector] private bool _autoSendMigrationReady = true;

        [SerializeField, HideInInspector] private NetworkSimulation _networkSimulation = NetworkSimulation.@default;

        public string region
        {
            get => _region;
            [Obsolete("Use SetServer() instead")]
            set => _region = value;
        }

        public string host
        {
            get => _host;
            [Obsolete("Use SetServer() instead")]
            set => _host = value;
        }

        public string masterServer
        {
            get => _masterServer;
            set => _masterServer = value;
        }

        public string roomName
        {
            get => _roomName;
            set => _roomName = value;
        }

        public bool useNat
        {
            get => _useNat;
            set => _useNat = value;
        }

        /// <summary>
        /// Whether the room should be allocated as persistent (survives host loss via
        /// relay-side host migration). Requires a protocol v1 relay.
        /// </summary>
        public bool persistentRoom
        {
            get => _persistentRoom;
            set => _persistentRoom = value;
        }

        /// <summary>See the matching inspector toggle: auto-run PromoteToServer when elected.</summary>
        public bool autoPromoteToHost
        {
            get => _autoPromoteToHost;
            set => _autoPromoteToHost = value;
        }

        /// <summary>See the matching inspector toggle: auto-run TransferToNewServer on migration.</summary>
        public bool autoTransferOnMigration
        {
            get => _autoTransferOnMigration;
            set => _autoTransferOnMigration = value;
        }

        /// <summary>See the matching inspector toggle: auto-send MIGRATION_READY after promotion.</summary>
        public bool autoSendMigrationReady
        {
            get => _autoSendMigrationReady;
            set => _autoSendMigrationReady = value;
        }

        /// <summary>Raised when the room host was lost and the relay opened a migration window. The session is paused.</summary>
        public event Action onHostLost;

        /// <summary>Raised when the relay elected this peer as the new host (arg: whether a room snapshot will follow).</summary>
        public event Action<bool> onPromotedToHost;

        /// <summary>Raised when the room finished migrating to a new host (arg: new host relay connId). The session resumes.</summary>
        public event Action<int> onHostMigrated;

        /// <summary>Raised when a room snapshot finished downloading (args: data, version). Also fires for hosts resuming a dormant room.</summary>
        public event Action<byte[], uint> onRoomSnapshotReceived;

        // --- host migration / persistent room state ---
        private bool _framedClientSession;   // protocol v1: relay→client packets stay framed after auth
        private bool _sessionPaused;         // between SERVER_HOST_LOST and SERVER_HOST_MIGRATED
        private bool _pendingPromotion;      // got SERVER_PROMOTE_TO_HOST, waiting for promoted Listen()
        private bool _promotedHostMode;      // acting as room host over the adopted client socket
        private bool _promotedFlushPending;  // surface inherited connections + MIGRATION_READY next tick
        private bool _promotionHasSnapshot;
        private string _promotedHostSecret;
        private readonly List<int> _pendingPromotionClients = new List<int>();

        // --- room snapshot download (assembled from SERVER_SNAPSHOT_* packets) ---
        private byte[] _snapshotDownloadBuffer;
        private uint _snapshotDownloadVersion;
        private int _snapshotDownloadReceived;
        private int _snapshotDownloadChunks;
        private int _snapshotDownloadChunksReceived;
        private byte[] _roomSnapshot;
        private uint _roomSnapshotVersion;

        /// <summary>True while the session is paused by a host-migration window.</summary>
        public bool isSessionPaused => _sessionPaused;

        /// <summary>True when this peer acts as the room host after a relay-side promotion.</summary>
        public bool isPromotedHost => _promotedHostMode;

        /// <summary>
        /// Latest fully downloaded room snapshot (from a promotion or a dormant-room resume).
        /// </summary>
        public bool TryGetRoomSnapshot(out byte[] data, out uint version)
        {
            data = _roomSnapshot;
            version = _roomSnapshotVersion;
            return data != null;
        }

        /// <summary>Drops the locally cached room snapshot.</summary>
        public void ClearLocalRoomSnapshot()
        {
            _roomSnapshot = null;
            _roomSnapshotVersion = 0;
        }

        /// <summary>Which link a session is running over: the relay or a direct NAT-punched P2P link.</summary>
        public enum SessionLink
        {
            /// <summary>No active session.</summary>
            None,
            /// <summary>Connected, still waiting for the NAT punch to resolve P2P vs relay.</summary>
            Resolving,
            /// <summary>Session is running over the relay.</summary>
            Relay,
            /// <summary>Session is running over a direct P2P link.</summary>
            P2P
        }

        /// <summary>Which link the local client's session is running over (relay vs direct P2P).</summary>
        public SessionLink clientSessionLink
        {
            get
            {
                if (_clientConnPending)
                    return SessionLink.Resolving;
                if (_clientState != ConnectionState.Connected)
                    return SessionLink.None;
                return _clientP2pSession ? SessionLink.P2P : SessionLink.Relay;
            }
        }

        /// <summary>Number of host-side connections currently running over a direct P2P link.</summary>
        public int p2pConnectionCount => _p2pSessionConns.Count;

        /// <summary>Remote endpoint of the direct P2P link to the host, or null when not on a P2P session.</summary>
        public string p2pHostEndpoint =>
            _clientP2pSession && _p2pHostPeer != null ? _p2pHostPeer.ToString() : null;

        /// <summary>Remote endpoint of a host-side connection's direct P2P link, or null if it runs over the relay.</summary>
        public string GetP2pEndpoint(Connection conn)
        {
            return _p2pSessionConns.Contains(conn.connectionId) &&
                   _p2pPeersByConnId.TryGetValue(conn.connectionId, out var peer)
                ? peer.ToString()
                : null;
        }

        public void SetServer(RelayServer server)
        {
            _region = server.region;
            _host = server.host;
        }

        public bool hasRegionAndHost => !string.IsNullOrEmpty(_region) && !string.IsNullOrEmpty(_host);

        public NetworkSimulation networkSimulation
        {
            get => _networkSimulation;
            set
            {
                _networkSimulation = value;
                ApplySimulationSettings();
            }
        }

        public override bool isSupported => true;

        public override ITransport transport => this;

        private bool _isPipeMode;
        private int _pipeConnId;

        /// <summary>
        /// This client's connection ID on the relay (pipe mode only).
        /// </summary>
        public int pipeConnId => _pipeConnId;

        /// <summary>
        /// Whether this transport is connected in pipe mode.
        /// </summary>
        public bool isPipeMode => _isPipeMode;

        /// <summary>
        /// Data received from a pipe peer. Args: senderConnId, data.
        /// </summary>
        public event Action<int, ByteData> onPipeDataReceived;

        public event OnConnected onConnected;
        public event OnDisconnected onDisconnected;
        public event OnDataReceived onDataReceived;
        public event OnDataSent onDataSent;
        public event OnConnectionState onConnectionState;

        private ConnectionState _listenerState = ConnectionState.Disconnected;
        private ConnectionState _clientState = ConnectionState.Disconnected;

        public bool shouldServerSendKeepAlive => true;

        public bool shouldClientSendKeepAlive => true;

        public ConnectionState listenerState
        {
            get => _listenerState;
            private set
            {
                if (_listenerState == value)
                    return;

                _listenerState = value;
                onConnectionState?.Invoke(value, true);
            }
        }

        public ConnectionState clientState
        {
            get => _clientState;
            private set
            {
                if (_clientState == value)
                    return;

                _clientState = value;
                onConnectionState?.Invoke(value, false);
            }
        }

        public bool SupportsChannel(Channel channel)
        {
            return true;
        }

        public int GetMTU(Connection target, Channel channel, bool asServer)
        {
            if (_isUsingUDP)
            {
                try
                {
                    var method = UDPTransport.ToDeliveryMethod(channel);
                    // A promoted host's link lives on the client-side manager.
                    var peer = asServer
                        ? (_promotedHostMode ? _relayServerPeer : _udpServer.FirstPeer)
                        : _udpClient.FirstPeer;
                    var result = peer.GetMaxSinglePacketSize(method);
                    return result - 16; // give the relay some space for metadata
                }
                catch
                {
                    return 1024;
                }
            }

            return 8192 * 2;
        }


        public IReadOnlyList<Connection> connections => _connections;
        private readonly List<Connection> _connections = new List<Connection>();

        private void Reset()
        {
            _roomName = Guid.NewGuid().ToString().Replace("-", "");
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_masterServer == "https://purrbalancer.riten.dev:8080/")
            {
                _masterServer = "https://purrtransport.purrservers.com/";
                UnityEditor.EditorUtility.SetDirty(this);
                UnityEditor.AssetDatabase.SaveAssets();
                UnityEditor.AssetDatabase.Refresh();
            }

            ApplySimulationSettings();
        }
#endif

        readonly List<CancellationTokenSource> _cancellationTokenSourcesServer = new List<CancellationTokenSource>();
        readonly List<CancellationTokenSource> _cancellationTokenSourcesClient = new List<CancellationTokenSource>();

        private void CancelAll(bool asServer)
        {
            var sources = asServer ? _cancellationTokenSourcesServer : _cancellationTokenSourcesClient;
            for (var i = 0; i < sources.Count; i++)
                sources[i].Cancel();
            sources.Clear();
        }

        private void AddCancellation(CancellationTokenSource token, bool asServer)
        {
            if (asServer)
                _cancellationTokenSourcesServer.Add(token);
            else _cancellationTokenSourcesClient.Add(token);
        }

        protected override void StartClientInternal()
        {
            Connect(null, 0);
        }

        private EventBasedNetListener _serverListener;
        private EventBasedNetListener _clientListener;
        private NetManager _udpClient;
        private NetManager _udpServer;

        private bool _isUsingUDP;

        private NetPeer _relayServerPeer;
        private NetPeer _relayClientPeer;
        private NetPeer _p2pHostPeer;

        private EventBasedNatPunchListener _clientNatListener;
        private EventBasedNatPunchListener _serverNatListener;
        private IPEndPoint _relayUdpEndPoint;

        private readonly Dictionary<int, NetPeer> _p2pPeersByConnId = new();
        private readonly Dictionary<NetPeer, int> _connIdByP2pPeer = new();

        private readonly Dictionary<string, PunchSession> _serverPunches = new();
        private PunchSession _clientPunch;
        private readonly List<string> _punchScratch = new();

        private bool _clientConnPending;
        private bool _clientP2pSession;
        private bool _p2pHostEstablished;
        private float _clientConnDeadline;

        private readonly Dictionary<int, float> _pendingHostConns = new();
        private readonly HashSet<int> _p2pSessionConns = new();
        private readonly List<int> _hostConnScratch = new();

        /// <summary>
        /// How long to wait for a NAT punch to produce a usable P2P link before falling back
        /// to relay. Inspector-configurable via <see cref="_natResolveTimeout"/>; clamped to a
        /// sane minimum so a punch always gets a few retries.
        /// </summary>
        private float P2PResolveTimeout => Mathf.Max(1f, _natResolveTimeout);

        /// <summary>NAT punching is only relevant on the (non-pipe) UDP transport.</summary>
        private bool natEnabled => _useNat && _isUsingUDP && !_isPipeMode;

        /// <summary>Tracks an in-flight NAT introduce handshake keyed by the relay-issued token.</summary>
        private sealed class PunchSession
        {
            public string token;
            public int clientConnId;
            public float nextSendTime;
            public float deadline;
            public bool done;
        }

        /// <summary>True if a server-side punch handshake is currently expecting the given connId.</summary>
        private bool HasServerPunchFor(int connId)
        {
            foreach (var s in _serverPunches.Values)
                if (s.clientConnId == connId)
                    return true;
            return false;
        }

        /// <summary>Host-side: commit a pending connection to the relay and raise onConnected.</summary>
        private void ResolveHostConnAsRelay(int connId)
        {
            _pendingHostConns.Remove(connId);
            DropP2pPeer(connId);

            var conn = new Connection(connId);
            if (!_connections.Contains(conn))
                _connections.Add(conn);
            onConnected?.Invoke(conn, true);
        }

        /// <summary>Host-side: commit a pending connection to its direct P2P link and raise onConnected.</summary>
        private void ResolveHostConnAsP2p(int connId)
        {
            _pendingHostConns.Remove(connId);
            _p2pSessionConns.Add(connId);

            var conn = new Connection(connId);
            if (!_connections.Contains(conn))
                _connections.Add(conn);
            onConnected?.Invoke(conn, true);
        }

        /// <summary>Drops and disconnects the direct P2P peer for the given connId, if any.</summary>
        private void DropP2pPeer(int connId)
        {
            if (_p2pPeersByConnId.Remove(connId, out var peer))
            {
                _connIdByP2pPeer.Remove(peer);
                peer.Disconnect();
            }
        }

        /// <summary>Client-side: commit the pending session to P2P or relay and raise onConnected.</summary>
        private void ResolveClientConn(bool p2p)
        {
            if (!_clientConnPending)
                return;

            _clientConnPending = false;
            _clientP2pSession = p2p;

            clientState = ConnectionState.Connected;
            onConnected?.Invoke(new Connection(0), false);
        }

        private void OnEnable()
        {
            _serverListener = new EventBasedNetListener();
            _clientListener = new EventBasedNetListener();

            _udpClient = new NetManager(_clientListener)
            {
                UnconnectedMessagesEnabled = true,
                PingInterval = 900,
                AutoRecycle = true,
                EnableStatistics = false,
                IPv6Enabled = false,
                DisconnectTimeout = Mathf.RoundToInt(_timeoutInSeconds * 1000)
            };

            _udpServer = new NetManager(_serverListener)
            {
                UnconnectedMessagesEnabled = true,
                PingInterval = 900,
                AutoRecycle = true,
                EnableStatistics = false,
                IPv6Enabled = false,
                DisconnectTimeout = Mathf.RoundToInt(_timeoutInSeconds * 1000)
            };

            _clientListener.PeerConnectedEvent += OnClientOrPipeConnectedUDP;
            _clientListener.PeerDisconnectedEvent += OnClientOrPipeDisconnectedUDP;
            _clientListener.NetworkReceiveEvent += OnClientOrPipeDataUDP;

            _serverListener.PeerConnectedEvent += OnHostConnectedUDP;
            _serverListener.PeerDisconnectedEvent += OnHostDisconnectedUDP;
            _serverListener.NetworkReceiveEvent += OnHostDataUDP;
            _serverListener.ConnectionRequestEvent += OnServerConnectionRequestUDP;

            if (_useNat)
            {
                _clientNatListener = new EventBasedNatPunchListener();
                _clientNatListener.NatIntroductionSuccess += OnClientNatPunchSuccess;
                _udpClient.NatPunchEnabled = true;
                _udpClient.NatPunchModule.UnsyncedEvents = false;
                _udpClient.NatPunchModule.Init(_clientNatListener);

                _serverNatListener = new EventBasedNatPunchListener();
                _serverNatListener.NatIntroductionSuccess += OnServerNatPunchSuccess;
                _udpServer.NatPunchEnabled = true;
                _udpServer.NatPunchModule.UnsyncedEvents = false;
                _udpServer.NatPunchModule.Init(_serverNatListener);
            }

            ApplySimulationSettings();
        }

        private void ApplySimulationSettings()
        {
            bool apply = _networkSimulation.ShouldApply();

            if (_udpClient != null)
            {
                _udpClient.SimulateLatency = apply && _networkSimulation.simulateLatency;
                _udpClient.SimulationMinLatency = _networkSimulation.minLatency;
                _udpClient.SimulationMaxLatency = _networkSimulation.maxLatency;
                _udpClient.SimulatePacketLoss = apply && _networkSimulation.simulatePacketLoss;
                _udpClient.SimulationPacketLossChance = _networkSimulation.packetLossChance;
            }

            if (_udpServer != null)
            {
                _udpServer.SimulateLatency = apply && _networkSimulation.simulateLatency;
                _udpServer.SimulationMinLatency = _networkSimulation.minLatency;
                _udpServer.SimulationMaxLatency = _networkSimulation.maxLatency;
                _udpServer.SimulatePacketLoss = apply && _networkSimulation.simulatePacketLoss;
                _udpServer.SimulationPacketLossChance = _networkSimulation.packetLossChance;
            }
        }

        private void CleanupUdp()
        {
            _udpClient.Stop();
            _udpServer?.Stop();
        }

        private SimpleWebClient _server;
        private SimpleWebClient _client;
        private HostJoinInfo _hostJoinInfo;
        readonly TcpConfig _tcpConfig = new(noDelay: true, sendTimeout: 0, receiveTimeout: 0);

        protected override void StartServerInternal()
        {
            Listen(0);
        }

        private void OnHostDataUDP(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            var data = new ByteData(reader.RawData, reader.UserDataOffset, reader.UserDataSize);

            // Any non-relay peer is a P2P peer. If it is still mapped, deliver its game
            // data; if it was already dropped (relay won the race, or the session ended)
            // ignore the stale packets — raw P2P bytes must never reach OnHostData, which
            // would misparse them as a relay control frame.
            if (natEnabled && !ReferenceEquals(peer, _relayServerPeer))
            {
                if (_connIdByP2pPeer.TryGetValue(peer, out var p2pConnId))
                {
                    if (_pendingHostConns.ContainsKey(p2pConnId))
                        ResolveHostConnAsP2p(p2pConnId);

                    RaiseDataReceived(new Connection(p2pConnId), data, true);
                }
                return;
            }

            OnHostData(data.segment);
        }

        private void OnHostData(ArraySegment<byte> data)
        {
            if (data.Array == null || data.Count == 0)
                return;

            var type = (SERVER_PACKET_TYPE)data.Array[data.Offset];

            switch (type)
            {
                case SERVER_PACKET_TYPE.SERVER_AUTHENTICATED:
                    listenerState = ConnectionState.Connected;
                    break;
                case SERVER_PACKET_TYPE.SERVER_AUTHENTICATION_FAILED:
                    StopListening();
                    break;
                case SERVER_PACKET_TYPE.SERVER_CLIENT_CONNECTED:
                {
                    _packer.ResetPositionAndMode(false);
                    _packer.WriteBytes(new ByteData(data.Array, data.Offset + 1, data.Count - 1));
                    _packer.ResetPositionAndMode(true);

                    int clientId = default;
                    int connectionCount = (data.Count - 1) / 4;

                    for (var i = 0; i < connectionCount; i++)
                    {
                        Packer<int>.Read(_packer, ref clientId);

                        if (natEnabled &&
                            (HasServerPunchFor(clientId) || _p2pPeersByConnId.ContainsKey(clientId)))
                        {
                            if (_p2pPeersByConnId.TryGetValue(clientId, out var p2p) &&
                                p2p.ConnectionState == LiteNetLib.ConnectionState.Connected)
                            {
                                ResolveHostConnAsP2p(clientId);
                            }
                            else
                            {
                                _pendingHostConns[clientId] =
                                    Time.realtimeSinceStartup + P2PResolveTimeout;
                            }
                        }
                        else
                        {
                            var conn = new Connection(clientId);
                            _connections.Add(conn);
                            onConnected?.Invoke(conn, true);
                        }
                    }

                    break;
                }
                case SERVER_PACKET_TYPE.SERVER_CLIENT_DISCONNECTED:
                {
                    var subdata = new ByteData(data.Array, data.Offset + 1, data.Count - 1);

                    _packer.ResetPositionAndMode(false);
                    _packer.WriteBytes(subdata);
                    _packer.ResetPositionAndMode(true);

                    int clientId = default;
                    Packer<int>.Read(_packer, ref clientId);

                    var conn = new Connection(clientId);

                    if (_connections.Remove(conn))
                        onDisconnected?.Invoke(conn, DisconnectReason.ClientRequest, true);

                    _pendingHostConns.Remove(clientId);
                    _p2pSessionConns.Remove(clientId);
                    DropP2pPeer(clientId);
                    break;
                }
                case SERVER_PACKET_TYPE.SERVER_CLIENT_DATA:
                {
                    if (data.Count <= 5)
                        return;

                    int connId = data.Array[data.Offset + 1] |
                                 data.Array[data.Offset + 2] << 8 |
                                 data.Array[data.Offset + 3] << 16 |
                                 data.Array[data.Offset + 4] << 24;

                    if (natEnabled && _pendingHostConns.ContainsKey(connId))
                        ResolveHostConnAsRelay(connId);

                    RaiseDataReceived(new Connection(connId), new ByteData(data.Array, data.Offset + 5, data.Count - 5),
                        true);
                    break;
                }
                case SERVER_PACKET_TYPE.SERVER_NAT_INTRODUCE:
                {
                    if (natEnabled && data.Count > 1)
                    {
                        var token = Encoding.UTF8.GetString(data.Array, data.Offset + 1, data.Count - 1);
                        int underscore = token.LastIndexOf('_');
                        if (underscore >= 0 && underscore < token.Length - 1 &&
                            int.TryParse(token.Substring(underscore + 1), out var clientConnId))
                        {
                            BeginPunch(token, true, clientConnId);
                        }
                    }
                    break;
                }
                case SERVER_PACKET_TYPE.SERVER_SNAPSHOT_BEGIN:
                case SERVER_PACKET_TYPE.SERVER_SNAPSHOT_CHUNK:
                case SERVER_PACKET_TYPE.SERVER_SNAPSHOT_COMMIT:
                {
                    // A host resuming a dormant persistent room receives the stored
                    // snapshot right after SERVER_AUTHENTICATED.
                    HandleSnapshotPacket(type, data);
                    break;
                }
                default:
                    PurrLogger.LogError($"Unexpected packet type {type} from server");
                    break;
            }
        }

        private void OnClientOrPipeConnectedUDP(NetPeer peer)
        {
            // The promoted host link lives on the client-side manager — never treat it
            // as a client/pipe/P2P peer.
            if (_promotedHostMode && ReferenceEquals(peer, _relayServerPeer))
                return;

            if (_isPipeMode)
            {
                OnPipeConnected();
                return;
            }

            if (natEnabled && ReferenceEquals(peer, _p2pHostPeer))
            {
                _clientPunch = null;

                if (_clientConnPending)
                {
                    _p2pHostEstablished = true;
                    ResolveClientConn(true);
                }
                else
                {
                    var stale = _p2pHostPeer;
                    _p2pHostPeer = null;
                    stale.Disconnect();
                }
                return;
            }

            OnClientConnectedUDP(peer);
        }

        private void OnClientOrPipeDisconnectedUDP(NetPeer peer, DisconnectInfo info)
        {
            // The promoted host link died — bring down the whole listener.
            if (_promotedHostMode && ReferenceEquals(peer, _relayServerPeer))
            {
                StopListening();
                return;
            }

            if (_isPipeMode)
            {
                OnPipeDisconnectedUDP();
                return;
            }

            if (natEnabled && !ReferenceEquals(peer, _relayClientPeer))
            {
                if (!ReferenceEquals(peer, _p2pHostPeer))
                    return;

                _p2pHostPeer = null;

                if (_p2pHostEstablished)
                {
                    _p2pHostEstablished = false;
                    _clientP2pSession = false;

                    // During a migration window the P2P host link dying is expected (the
                    // host is gone) — the relay link carries the migration, so don't tear
                    // the session down.
                    if (!_sessionPaused && !_pendingPromotion)
                        Disconnect();
                }
                else if (_clientConnPending)
                {
                    ResolveClientConn(false);
                }
                return;
            }

            OnClientDisconnectedUDP();
        }

        private void OnClientOrPipeDataUDP(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            var data = reader.GetRemainingBytesSegment();

            // Data on the promoted host link is host-side traffic (no P2P peers exist
            // in promoted mode, so it goes straight to the relay frame parser).
            if (_promotedHostMode && ReferenceEquals(peer, _relayServerPeer))
            {
                OnHostData(data);
                return;
            }

            if (_isPipeMode)
            {
                OnPipeData(data);
                return;
            }

            // Any non-relay peer is the P2P link. Deliver while it is the live host
            // peer; ignore stale packets from a dropped P2P peer so they never reach
            // OnClientData and get misparsed as a relay control frame.
            if (natEnabled && !ReferenceEquals(peer, _relayClientPeer))
            {
                if (ReferenceEquals(peer, _p2pHostPeer))
                {
                    if (_clientConnPending)
                    {
                        _p2pHostEstablished = true;
                        ResolveClientConn(true);
                    }

                    if (clientState == ConnectionState.Connected)
                        RaiseDataReceived(new Connection(0), new ByteData(data.Array, data.Offset, data.Count), false);
                }
                return;
            }

            OnClientData(data);
        }

        private void OnClientData(ArraySegment<byte> data)
        {
            if (data.Array == null || data.Count == 0)
                return;

            // A promotion is in flight: the socket is about to become the host link and
            // only relay control packets (snapshot stream, room client list) are expected,
            // regardless of the client session state.
            if (_pendingPromotion)
            {
                HandleFramedClientPacket(data);
                return;
            }

            if (_clientConnPending)
                ResolveClientConn(false);

            if (clientState == ConnectionState.Connected)
            {
                if (_framedClientSession)
                {
                    // Protocol v1: every relay→client packet stays framed after auth.
                    HandleFramedClientPacket(data);
                    return;
                }

                var bdata = new ByteData(data.Array, data.Offset, data.Count);
                RaiseDataReceived(new Connection(0), bdata, false);
                return;
            }

            var type = (SERVER_PACKET_TYPE)data.Array[data.Offset];

            switch (type)
            {
                case SERVER_PACKET_TYPE.SERVER_AUTHENTICATED:
                    if (natEnabled && _clientPunch != null)
                    {
                        _clientConnPending = true;
                        _clientConnDeadline = Time.realtimeSinceStartup + P2PResolveTimeout;
                    }
                    else
                    {
                        clientState = ConnectionState.Connected;
                        onConnected?.Invoke(new Connection(0), false);
                    }
                    break;
                case SERVER_PACKET_TYPE.SERVER_AUTHENTICATION_FAILED:
                    Disconnect();
                    break;
                case SERVER_PACKET_TYPE.SERVER_NAT_INTRODUCE:
                    if (natEnabled && data.Count > 1)
                    {
                        var token = Encoding.UTF8.GetString(data.Array, data.Offset + 1, data.Count - 1);
                        BeginPunch(token, false, 0);
                    }
                    break;
                default:
                    PurrLogger.LogError($"Unexpected packet type {type} from server");
                    break;
            }
        }

        /// <summary>
        /// Handles a framed relay→client packet (protocol v1 sessions and promotions in
        /// flight): game data is wrapped as [SERVER_CLIENT_DATA][payload] and host-migration
        /// control packets are dispatched here.
        /// </summary>
        private void HandleFramedClientPacket(ArraySegment<byte> data)
        {
            var type = (SERVER_PACKET_TYPE)data.Array[data.Offset];

            switch (type)
            {
                case SERVER_PACKET_TYPE.SERVER_CLIENT_DATA:
                {
                    if (_pendingPromotion || _sessionPaused || clientState != ConnectionState.Connected)
                        return;

                    if (data.Count <= 1)
                        return;

                    RaiseDataReceived(new Connection(0),
                        new ByteData(data.Array, data.Offset + 1, data.Count - 1), false);
                    return;
                }
                case SERVER_PACKET_TYPE.SERVER_HOST_LOST:
                {
                    // The relay opened a migration window. Pause instead of disconnecting:
                    // either we get promoted or SERVER_HOST_MIGRATED resumes the session.
                    _sessionPaused = true;
                    PurrLogger.Log("PurrTransport: room host lost, session paused while a new host is elected.");
                    onHostLost?.Invoke();
                    return;
                }
                case SERVER_PACKET_TYPE.SERVER_PROMOTE_TO_HOST:
                {
                    // [hasSnapshot(1)][newHostSecret(UTF8, remaining)]
                    if (data.Count < 2)
                        return;

                    _promotionHasSnapshot = data.Array[data.Offset + 1] != 0;
                    _promotedHostSecret = data.Count > 2
                        ? Encoding.UTF8.GetString(data.Array, data.Offset + 2, data.Count - 2)
                        : null;

                    _pendingPromotion = true;
                    _sessionPaused = true;
                    _pendingPromotionClients.Clear();

                    PurrLogger.Log($"PurrTransport: elected as new room host (snapshot incoming: {_promotionHasSnapshot}).");

                    // With a snapshot, promotion is announced once the download commits.
                    if (!_promotionHasSnapshot)
                        AnnouncePromotion();
                    return;
                }
                case SERVER_PACKET_TYPE.SERVER_SNAPSHOT_BEGIN:
                case SERVER_PACKET_TYPE.SERVER_SNAPSHOT_CHUNK:
                case SERVER_PACKET_TYPE.SERVER_SNAPSHOT_COMMIT:
                {
                    HandleSnapshotPacket(type, data);
                    return;
                }
                case SERVER_PACKET_TYPE.SERVER_CLIENT_CONNECTED:
                {
                    // Only meaningful mid-promotion: the relay hands us the connIds of the
                    // room's other clients, which we surface once we run as the host.
                    if (!_pendingPromotion)
                        return;

                    int count = (data.Count - 1) / 4;
                    for (var i = 0; i < count; i++)
                    {
                        int offset = data.Offset + 1 + i * 4;
                        int connId = data.Array[offset] |
                                     data.Array[offset + 1] << 8 |
                                     data.Array[offset + 2] << 16 |
                                     data.Array[offset + 3] << 24;

                        if (!_pendingPromotionClients.Contains(connId))
                            _pendingPromotionClients.Add(connId);
                    }
                    return;
                }
                case SERVER_PACKET_TYPE.SERVER_CLIENT_DISCONNECTED:
                {
                    if (!_pendingPromotion || data.Count < 5)
                        return;

                    int connId = data.Array[data.Offset + 1] |
                                 data.Array[data.Offset + 2] << 8 |
                                 data.Array[data.Offset + 3] << 16 |
                                 data.Array[data.Offset + 4] << 24;

                    _pendingPromotionClients.Remove(connId);
                    return;
                }
                case SERVER_PACKET_TYPE.SERVER_HOST_MIGRATED:
                {
                    // [newHostConnId(4)] — the new host is ready, resume the session.
                    _sessionPaused = false;

                    int newHostConnId = data.Count >= 5
                        ? data.Array[data.Offset + 1] |
                          data.Array[data.Offset + 2] << 8 |
                          data.Array[data.Offset + 3] << 16 |
                          data.Array[data.Offset + 4] << 24
                        : -1;

                    PurrLogger.Log($"PurrTransport: room migrated to new host (relay conn {newHostConnId}), resuming session.");
                    onHostMigrated?.Invoke(newHostConnId);

                    var nm = NetworkManager.main;
                    if (_autoTransferOnMigration)
                    {
                        if (nm && ReferenceEquals(nm.transport, this))
                        {
                            PurrLogger.Log("PurrTransport: auto-transferring via NetworkManager.TransferToNewServer().");
                            nm.TransferToNewServer();
                        }
                        else if (!_promotedHostMode)
                        {
                            PurrLogger.LogWarning("PurrTransport: autoTransferOnMigration is on but TransferToNewServer() " +
                                                  $"was not called (NetworkManager.main: {(nm ? nm.name : "null")}, transport is " +
                                                  $"this component: {nm && ReferenceEquals(nm.transport, this)}).");
                        }
                    }
                    return;
                }
                case SERVER_PACKET_TYPE.SERVER_AUTHENTICATED:
                case SERVER_PACKET_TYPE.SERVER_NAT_INTRODUCE:
                    // Late/duplicate control packets — nothing to do post-auth.
                    return;
                default:
                    PurrLogger.LogError($"Unexpected framed packet type {type} from relay");
                    return;
            }
        }

        /// <summary>
        /// Announces this peer as the new room host. With auto-promotion enabled this runs
        /// NetworkManager.PromoteToServer(), whose StartServer() lands in PromotedListen().
        /// </summary>
        private void AnnouncePromotion()
        {
            onPromotedToHost?.Invoke(_promotionHasSnapshot);

            if (!_autoPromoteToHost)
                return;

            var nm = NetworkManager.main;
            if (nm && ReferenceEquals(nm.transport, this))
            {
                PurrLogger.Log("PurrTransport: auto-promoting via NetworkManager.PromoteToServer().");
                nm.PromoteToServer();
            }
            else
            {
                PurrLogger.LogWarning("PurrTransport: autoPromoteToHost is on but PromoteToServer() was not called " +
                                      $"(NetworkManager.main: {(nm ? nm.name : "null")}, transport is this component: " +
                                      $"{nm && ReferenceEquals(nm.transport, this)}) — promote manually via onPromotedToHost.");
            }
        }

        /// <summary>
        /// Assembles SERVER_SNAPSHOT_BEGIN / CHUNK / COMMIT into a room snapshot. Used by
        /// both the promotion path (client socket) and the dormant-room resume path (host socket).
        /// </summary>
        private void HandleSnapshotPacket(SERVER_PACKET_TYPE type, ArraySegment<byte> data)
        {
            switch (type)
            {
                case SERVER_PACKET_TYPE.SERVER_SNAPSHOT_BEGIN:
                {
                    // [version(u32)][totalSize(u32)][chunkCount(u16)]
                    if (data.Count < 11)
                        return;

                    _snapshotDownloadVersion = ReadUInt(data.Array, data.Offset + 1);
                    int totalSize = (int)ReadUInt(data.Array, data.Offset + 5);
                    _snapshotDownloadChunks = data.Array[data.Offset + 9] | data.Array[data.Offset + 10] << 8;

                    if (totalSize <= 0)
                    {
                        ResetSnapshotDownload();
                        return;
                    }

                    _snapshotDownloadBuffer = new byte[totalSize];
                    _snapshotDownloadReceived = 0;
                    _snapshotDownloadChunksReceived = 0;
                    return;
                }
                case SERVER_PACKET_TYPE.SERVER_SNAPSHOT_CHUNK:
                {
                    // [chunkIndex(u16)][bytes...] — reliable-ordered, so always in order.
                    if (_snapshotDownloadBuffer == null || data.Count < 4)
                        return;

                    int payload = data.Count - 3;
                    if (_snapshotDownloadReceived + payload > _snapshotDownloadBuffer.Length)
                    {
                        ResetSnapshotDownload();
                        return;
                    }

                    Buffer.BlockCopy(data.Array, data.Offset + 3,
                        _snapshotDownloadBuffer, _snapshotDownloadReceived, payload);
                    _snapshotDownloadReceived += payload;
                    _snapshotDownloadChunksReceived++;
                    return;
                }
                case SERVER_PACKET_TYPE.SERVER_SNAPSHOT_COMMIT:
                {
                    // [version(u32)][crc32(u32)]
                    if (data.Count < 9)
                        return;

                    var version = ReadUInt(data.Array, data.Offset + 1);
                    var crc = ReadUInt(data.Array, data.Offset + 5);

                    bool valid = _snapshotDownloadBuffer != null &&
                                 version == _snapshotDownloadVersion &&
                                 _snapshotDownloadReceived == _snapshotDownloadBuffer.Length &&
                                 _snapshotDownloadChunksReceived == _snapshotDownloadChunks &&
                                 Crc32(_snapshotDownloadBuffer, 0, _snapshotDownloadBuffer.Length) == crc;

                    if (valid)
                    {
                        _roomSnapshot = _snapshotDownloadBuffer;
                        _roomSnapshotVersion = version;
                        PurrLogger.Log($"PurrTransport: room snapshot v{version} downloaded ({_roomSnapshot.Length} bytes).");
                        onRoomSnapshotReceived?.Invoke(_roomSnapshot, version);
                    }
                    else
                    {
                        PurrLogger.LogError("PurrTransport: room snapshot download failed validation, discarding.");
                        _promotionHasSnapshot = false;
                    }

                    _snapshotDownloadBuffer = null;
                    _snapshotDownloadReceived = 0;
                    _snapshotDownloadChunks = 0;
                    _snapshotDownloadChunksReceived = 0;

                    if (_pendingPromotion)
                        AnnouncePromotion();
                    return;
                }
            }
        }

        private void ResetSnapshotDownload()
        {
            _snapshotDownloadBuffer = null;
            _snapshotDownloadVersion = 0;
            _snapshotDownloadReceived = 0;
            _snapshotDownloadChunks = 0;
            _snapshotDownloadChunksReceived = 0;
        }

        static uint[] _crcTable;

        /// <summary>Standard CRC-32 (IEEE 802.3), matching the relay's implementation.</summary>
        static uint Crc32(byte[] data, int offset, int count)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    var c = i;
                    for (var k = 0; k < 8; k++)
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    _crcTable[i] = c;
                }
            }

            var crc = 0xFFFFFFFFu;
            for (var i = offset; i < offset + count; i++)
                crc = _crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        static uint ReadUInt(byte[] data, int offset)
        {
            return (uint)(data[offset]
                          | data[offset + 1] << 8
                          | data[offset + 2] << 16
                          | data[offset + 3] << 24);
        }

        private void OnHostConnected()
        {
            var authenticate = new ClientAuthenticate()
            {
                roomName = _roomName,
                clientSecret = _hostJoinInfo.secret,
                protocolVersion = _persistentRoom ? RELAY_PROTOCOL_VERSION : 0
            };

            string json = JsonUtility.ToJson(authenticate);
            var data = Encoding.UTF8.GetBytes(json);

            _server.Send(data);
        }

        private void OnHostConnectedUDP(NetPeer peer)
        {
            if (natEnabled && !ReferenceEquals(peer, _relayServerPeer))
            {
                if (_connIdByP2pPeer.TryGetValue(peer, out var p2pConnId))
                {
                    if (_pendingHostConns.ContainsKey(p2pConnId))
                        ResolveHostConnAsP2p(p2pConnId);
                    else if (!_p2pSessionConns.Contains(p2pConnId) &&
                             _connections.Contains(new Connection(p2pConnId)))
                        DropP2pPeer(p2pConnId);
                }
                return;
            }

            var authenticate = new ClientAuthenticate()
            {
                roomName = _roomName,
                clientSecret = _hostJoinInfo.secret,
                nat = _useNat,
                protocolVersion = _persistentRoom ? RELAY_PROTOCOL_VERSION : 0
            };

            string json = JsonUtility.ToJson(authenticate);
            var data = Encoding.UTF8.GetBytes(json);

            peer.Send(data, DeliveryMethod.ReliableOrdered);
        }

        private void OnClientConnected()
        {
            var authenticate = new ClientAuthenticate()
            {
                roomName = _roomName,
                clientSecret = _clientJoinInfo.secret,
                protocolVersion = _persistentRoom ? RELAY_PROTOCOL_VERSION : 0
            };

            _framedClientSession = authenticate.protocolVersion >= RELAY_PROTOCOL_VERSION;

            string json = JsonUtility.ToJson(authenticate);
            var data = Encoding.UTF8.GetBytes(json);

            _client.Send(data);
        }

        private void OnClientConnectedUDP(NetPeer peer)
        {
            var authenticate = new ClientAuthenticate
            {
                roomName = _roomName,
                clientSecret = _clientJoinInfo.secret,
                nat = _useNat,
                protocolVersion = _persistentRoom ? RELAY_PROTOCOL_VERSION : 0
            };

            _framedClientSession = authenticate.protocolVersion >= RELAY_PROTOCOL_VERSION;

            string json = JsonUtility.ToJson(authenticate);
            var data = Encoding.UTF8.GetBytes(json);

            peer.Send(data, DeliveryMethod.ReliableOrdered);
        }

        private void OnHostDisconnectedUDP(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            if (natEnabled && !ReferenceEquals(peer, _relayServerPeer))
            {
                if (!_connIdByP2pPeer.Remove(peer, out var p2pConnId))
                    return;

                _p2pPeersByConnId.Remove(p2pConnId);

                if (_p2pSessionConns.Remove(p2pConnId))
                {
                    var conn = new Connection(p2pConnId);
                    if (_connections.Remove(conn))
                        onDisconnected?.Invoke(conn, DisconnectReason.Timeout, true);
                    CloseConnection(conn);
                }
                else if (_pendingHostConns.ContainsKey(p2pConnId))
                {
                    ResolveHostConnAsRelay(p2pConnId);
                }
                return;
            }

            StopListening();
        }

        private void OnHostDisconnected()
        {
            StopListening();
        }

        private void OnClientDisconnected()
        {
            Disconnect();
        }

        private void OnClientDisconnectedUDP()
        {
            Disconnect();
        }

        public async void Listen(ushort port)
        {
            try
            {
                // A relay-side promotion is pending: become the host over the existing
                // socket instead of allocating a new room.
                if (_pendingPromotion)
                {
                    PromotedListen();
                    return;
                }

                if (listenerState != ConnectionState.Disconnected)
                    StopListening();

                listenerState = ConnectionState.Connecting;

                _server = SimpleWebClient.Create(ushort.MaxValue, 5000, _tcpConfig);

                _server.onConnect += OnHostConnected;
                _server.onData += OnHostData;
                _server.onDisconnect += OnHostDisconnected;
                _server.onError += OnError;

                try
                {
                    var token = new CancellationTokenSource();
                    AddCancellation(token, true);

                    if (!hasRegionAndHost)
                    {
                        var relayServer = await PurrTransportUtils.GetRelayServerAsync(_masterServer, token);
                        _region = relayServer.region;
                        _host = relayServer.host;
                    }

                    if (token.IsCancellationRequested)
                        return;

                    _hostJoinInfo = await PurrTransportUtils.Alloc(_masterServer, _region, _roomName, _persistentRoom, token);

                    if (token.IsCancellationRequested)
                        return;

                    if (Application.platform != RuntimePlatform.WebGLPlayer)
                    {
                        _isUsingUDP = true;

                        _udpServer.StartInManualMode(0);
                        var addresses = await Dns.GetHostAddressesAsync(_host);
                        var ipv4 = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                                   ?? IPAddress.Any;
                        _relayUdpEndPoint = new IPEndPoint(ipv4, _hostJoinInfo.udpPortV2);
                        _relayServerPeer = _udpServer.Connect(_relayUdpEndPoint, "PurrNet");
                    }
                    else
                    {
                        _isUsingUDP = false;
                        var builder = new UriBuilder
                        {
                            Scheme = _hostJoinInfo.ssl ? "wss" : "ws",
                            Host = _host,
                            Port = _hostJoinInfo.port,
                            Query = string.Empty,
                            Path = string.Empty
                        };

                        _server.Connect(builder.Uri);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    StopListening();
                    PurrLogger.LogException(e);
                }
            }
            catch (Exception e)
            {
                StopListening();
                PurrLogger.LogException(e.Message);
            }
        }

        private static void OnError(Exception obj)
        {
            PurrLogger.LogException(obj);
        }

        /// <summary>
        /// Completes a relay-side host promotion: adopts the existing client socket as the
        /// host link (the relay already re-pointed the room host to this connection) and
        /// brings the listener up without allocating anything.
        /// </summary>
        private void PromotedListen()
        {
            listenerState = ConnectionState.Connecting;

            if (_isUsingUDP)
            {
                // The relay peer simply changes roles; it stays on _udpClient's manager,
                // which keeps being polled. Incoming routing is handled by the
                // promoted-peer checks in the client UDP callbacks.
                _relayServerPeer = _relayClientPeer;
                _relayClientPeer = null;
            }
            else
            {
                // Move the WebSocket into the host slot and rewire it to the host handlers,
                // so every host-side code path works unchanged.
                _client.onConnect -= OnClientConnected;
                _client.onData -= OnClientData;
                _client.onDisconnect -= OnClientDisconnected;

                _server = _client;
                _client = null;

                _server.onData += OnHostData;
                _server.onDisconnect += OnHostDisconnected;
            }

            _hostJoinInfo = new HostJoinInfo { secret = _promotedHostSecret };

            _pendingPromotion = false;
            _promotedHostMode = true;
            _sessionPaused = false;

            listenerState = ConnectionState.Connected;

            PurrLogger.Log($"PurrTransport: promotion adopted the relay link as host ({(_isUsingUDP ? "UDP" : "WebSocket")}), " +
                           $"{_pendingPromotionClients.Count} inherited client(s) pending.");

            // Surface inherited connections and report readiness on the next tick, after
            // NetworkManager.PromoteToServer() finished migrating the modules.
            _promotedFlushPending = true;
        }

        /// <summary>
        /// Surfaces the room's surviving clients as fresh server connections and (when
        /// enabled) reports MIGRATION_READY to the relay. Runs one tick after promotion.
        /// </summary>
        private void FlushPromotedPromotion()
        {
            if (!_promotedFlushPending)
                return;

            _promotedFlushPending = false;

            for (var i = 0; i < _pendingPromotionClients.Count; i++)
            {
                var conn = new Connection(_pendingPromotionClients[i]);
                if (!_connections.Contains(conn))
                    _connections.Add(conn);
                onConnected?.Invoke(conn, true);
            }

            _pendingPromotionClients.Clear();

            if (_autoSendMigrationReady)
                SendMigrationReady();
        }

        /// <summary>
        /// Tells the relay this promoted host restored state and is ready to serve; the
        /// relay then broadcasts SERVER_HOST_MIGRATED to the room. Called automatically
        /// unless <see cref="autoSendMigrationReady"/> is disabled.
        /// </summary>
        public void SendMigrationReady()
        {
            if (!_promotedHostMode || listenerState != ConnectionState.Connected)
            {
                PurrLogger.LogWarning($"PurrTransport: MIGRATION_READY not sent (promotedHost: {_promotedHostMode}, " +
                                      $"listenerState: {listenerState}) — the relay will time this candidate out.");
                return;
            }

            _packer.ResetPositionAndMode(false);
            Packer<byte>.Write(_packer, (byte)HOST_PACKET_TYPE.MIGRATION_READY);
            WriteRawUInt(_packer, MIGRATION_READY_MAGIC);

            var data = _packer.ToByteData();
            SendHostControlPacket(data);
            PurrLogger.Log("PurrTransport: MIGRATION_READY sent to relay.");
        }

        /// <summary>
        /// Uploads an opaque room-state snapshot to the relay's RAM (persistent rooms only).
        /// The relay hands the latest committed snapshot to the next host on migration or
        /// dormant-room resume. Returns false when not hosting or the data is empty.
        /// </summary>
        public bool UploadRoomSnapshot(byte[] data, uint version)
        {
            if (listenerState != ConnectionState.Connected || data == null || data.Length == 0)
                return false;

            int chunkCount = (data.Length + SNAPSHOT_CHUNK_SIZE - 1) / SNAPSHOT_CHUNK_SIZE;

            if (chunkCount > ushort.MaxValue)
                return false;

            _packer.ResetPositionAndMode(false);
            Packer<byte>.Write(_packer, (byte)HOST_PACKET_TYPE.SNAPSHOT_BEGIN);
            WriteRawUInt(_packer, version);
            WriteRawUInt(_packer, (uint)data.Length);
            WriteRawUShort(_packer, (ushort)chunkCount);
            SendHostControlPacket(_packer.ToByteData());

            for (var i = 0; i < chunkCount; i++)
            {
                int offset = i * SNAPSHOT_CHUNK_SIZE;
                int count = Math.Min(SNAPSHOT_CHUNK_SIZE, data.Length - offset);

                _packer.ResetPositionAndMode(false);
                Packer<byte>.Write(_packer, (byte)HOST_PACKET_TYPE.SNAPSHOT_CHUNK);
                WriteRawUInt(_packer, version);
                WriteRawUShort(_packer, (ushort)i);
                _packer.WriteBytes(new ByteData(data, offset, count));
                SendHostControlPacket(_packer.ToByteData());
            }

            _packer.ResetPositionAndMode(false);
            Packer<byte>.Write(_packer, (byte)HOST_PACKET_TYPE.SNAPSHOT_COMMIT);
            WriteRawUInt(_packer, version);
            WriteRawUInt(_packer, Crc32(data, 0, data.Length));
            SendHostControlPacket(_packer.ToByteData());

            return true;
        }

        /// <summary>Clears both the pending and the stored snapshot for the room on the relay.</summary>
        public void ClearRoomSnapshot()
        {
            if (listenerState != ConnectionState.Connected)
                return;

            _packer.ResetPositionAndMode(false);
            Packer<byte>.Write(_packer, (byte)HOST_PACKET_TYPE.SNAPSHOT_CLEAR);
            SendHostControlPacket(_packer.ToByteData());
        }

        /// <summary>Sends a host-framed control packet over the host link, reliable-ordered.</summary>
        private void SendHostControlPacket(ByteData data)
        {
            if (_isUsingUDP)
                _relayServerPeer?.Send(data.data, data.offset, data.length, DeliveryMethod.ReliableOrdered);
            else
                _server?.Send(new ArraySegment<byte>(data.data, data.offset, data.length));
        }

        static void WriteRawUInt(BitPacker packer, uint value)
        {
            Packer<byte>.Write(packer, (byte)value);
            Packer<byte>.Write(packer, (byte)(value >> 8));
            Packer<byte>.Write(packer, (byte)(value >> 16));
            Packer<byte>.Write(packer, (byte)(value >> 24));
        }

        static void WriteRawUShort(BitPacker packer, ushort value)
        {
            Packer<byte>.Write(packer, (byte)value);
            Packer<byte>.Write(packer, (byte)(value >> 8));
        }

        public void StopListening()
        {
            // A promoted host's link lives on the client-side socket — tear that down too.
            if (_promotedHostMode)
            {
                _promotedHostMode = false;
                _promotedFlushPending = false;
                _pendingPromotionClients.Clear();
                _relayServerPeer = null;

                if (_isUsingUDP)
                    _udpClient?.Stop();
            }

            _connections.Clear();
            CancelAll(true);

            if (_server != null)
            {
                _server.onConnect -= OnHostConnected;
                _server.onData -= OnHostData;
                _server.onDisconnect -= OnHostDisconnected;
                _server.onError -= OnError;
                _server.Disconnect();
            }

            _udpServer?.Stop();

            _server = null;
            _relayServerPeer = null;
            _p2pPeersByConnId.Clear();
            _connIdByP2pPeer.Clear();
            _serverPunches.Clear();
            _pendingHostConns.Clear();
            _p2pSessionConns.Clear();

            if (listenerState is ConnectionState.Connecting or ConnectionState.Connected)
                listenerState = ConnectionState.Disconnecting;
            listenerState = ConnectionState.Disconnected;
        }

        public void Disconnect()
        {
            // During a promotion the relay socket must survive — it is about to become the
            // host link. Only end the client session from PurrNet's point of view.
            if (_pendingPromotion)
            {
                if (clientState != ConnectionState.Disconnected)
                    onDisconnected?.Invoke(default, DisconnectReason.ClientRequest, false);

                CancelAll(false);

                _p2pHostPeer = null;
                _clientPunch = null;
                _clientConnPending = false;
                _clientP2pSession = false;
                _p2pHostEstablished = false;

                if (clientState is ConnectionState.Connecting or ConnectionState.Connected)
                    clientState = ConnectionState.Disconnecting;
                clientState = ConnectionState.Disconnected;
                return;
            }

            if (clientState != ConnectionState.Disconnected)
                onDisconnected?.Invoke(default, DisconnectReason.ClientRequest, false);

            CancelAll(false);

            if (_client != null)
            {
                _client.onConnect -= OnClientConnected;
                _client.onData -= OnClientData;
                _client.onDisconnect -= OnClientDisconnected;
                _client.onError -= OnError;
                _client.Disconnect();
            }

            // A promoted host shares the client-side UDP manager with its local client:
            // stopping the manager would kill the host link, so only drop the client peer.
            if (_promotedHostMode)
                _relayClientPeer?.Disconnect();
            else
                _udpClient?.Stop();

            _client = null;
            _relayClientPeer = null;
            _p2pHostPeer = null;
            _clientPunch = null;
            _clientConnPending = false;
            _clientP2pSession = false;
            _p2pHostEstablished = false;
            _framedClientSession = false;
            _sessionPaused = false;
            ResetSnapshotDownload();

            if (clientState is ConnectionState.Connecting or ConnectionState.Connected)
                clientState = ConnectionState.Disconnecting;
            clientState = ConnectionState.Disconnected;
        }

        private ClientJoinInfo _clientJoinInfo;

        public async void Connect(string ip, ushort port)
        {
            try
            {
                if (clientState != ConnectionState.Disconnected)
                    Disconnect();

                clientState = ConnectionState.Connecting;

                var token = new CancellationTokenSource();

                while (listenerState == ConnectionState.Connecting)
                    await UnityLatestUpdate.Yield();

                if (token.IsCancellationRequested)
                    return;

                _client = SimpleWebClient.Create(ushort.MaxValue, 5000, _tcpConfig);
                _client.onConnect += OnClientConnected;
                _client.onData += OnClientData;
                _client.onDisconnect += OnClientDisconnected;
                _client.onError += OnError;

                AddCancellation(token, false);

                _clientJoinInfo = await PurrTransportUtils.Join(_masterServer, _roomName, token);

                if (token.IsCancellationRequested)
                    return;

                if (Application.platform != RuntimePlatform.WebGLPlayer)
                {
                    _isUsingUDP = true;
                    _udpClient.StartInManualMode(0);

                    var addresses = await Dns.GetHostAddressesAsync(_clientJoinInfo.host);
                    var ipv4 = addresses.FirstOrDefault(ipArd => ipArd.AddressFamily == AddressFamily.InterNetwork)
                               ?? IPAddress.Any;

                    _relayUdpEndPoint = new IPEndPoint(ipv4, _clientJoinInfo.udpPortV2);
                    _relayClientPeer = _udpClient.Connect(_relayUdpEndPoint, "PurrNet");
                }
                else
                {
                    var builder = new UriBuilder
                    {
                        Scheme = _clientJoinInfo.ssl ? "wss" : "ws",
                        Host = _clientJoinInfo.host,
                        Port = _clientJoinInfo.port
                    };

                    _client.Connect(builder.Uri);
                }
            }
            catch (OperationCanceledException)
            {
                Disconnect();
            }
            catch (Exception e)
            {
                Disconnect();
                PurrLogger.LogException(e.Message);
            }
        }

        public void RaiseDataReceived(Connection conn, ByteData data, bool asServer)
        {
            onDataReceived?.Invoke(conn, data, asServer);
        }

        public void RaiseDataSent(Connection conn, ByteData data, bool asServer)
        {
            onDataSent?.Invoke(conn, data, asServer);
        }

        static readonly BitPacker _packer = new BitPacker();

        public void SendServerKeepAlive()
        {
            if (_server == null)
                return;

            _packer.ResetPositionAndMode(false);
            Packer<byte>.Write(_packer, (byte)HOST_PACKET_TYPE.SEND_KEEPALIVE);
            var data = _packer.ToByteData();
            _server.Send(new ArraySegment<byte>(data.data, data.offset, data.length));
        }

        public void SendToClient(Connection target, ByteData odata, Channel method = Channel.ReliableOrdered)
        {
            if (listenerState != ConnectionState.Connected)
                return;

            if (!target.isValid)
                return;

            if (natEnabled && _p2pSessionConns.Contains(target.connectionId))
            {
                if (_p2pPeersByConnId.TryGetValue(target.connectionId, out var p2pPeer))
                    p2pPeer.Send(odata.data, odata.offset, odata.length, UDPTransport.ToDeliveryMethod(method));
                RaiseDataSent(target, odata, true);
                return;
            }

            _packer.ResetPositionAndMode(false);

            Packer<byte>.Write(_packer, (byte)HOST_PACKET_TYPE.SEND_ONE);
            Packer<int>.Write(_packer, target.connectionId);

            if (_isUsingUDP)
                Packer<byte>.Write(_packer, (byte)UDPTransport.ToDeliveryMethod(method));

            _packer.WriteBytes(odata);

            var data = _packer.ToByteData();

            if (_isUsingUDP)
            {
                var deliveryMethod = UDPTransport.ToDeliveryMethod(method);
                _relayServerPeer.Send(data.data, data.offset, data.length, deliveryMethod);
            }
            else _server.Send(new ArraySegment<byte>(data.data, data.offset, data.length));
            RaiseDataSent(target, data, true);
        }

        public void SendToServer(ByteData data, Channel method = Channel.ReliableOrdered)
        {
            if (clientState != ConnectionState.Connected)
                return;

            // While a migration window is open there is no host to receive this.
            if (_sessionPaused || _pendingPromotion)
                return;

            if (_isUsingUDP)
            {
                var deliveryMethod = UDPTransport.ToDeliveryMethod(method);

                if (natEnabled && _clientP2pSession)
                {
                    if (_p2pHostPeer != null)
                        _p2pHostPeer.Send(data.data, data.offset, data.length, deliveryMethod);
                    RaiseDataSent(default, data, false);
                    return;
                }

                _packer.ResetPositionAndMode(false);
                Packer<byte>.Write(_packer, (byte)deliveryMethod);
                _packer.WriteBytes(data);
                var byteData = _packer.ToByteData();
                _relayClientPeer.Send(byteData.data, byteData.offset, byteData.length, deliveryMethod);
            }
            else
            {
                _client.Send(new ArraySegment<byte>(data.data, data.offset, data.length));
            }

            RaiseDataSent(default, data, false);
        }

        /// <summary>
        /// Connect to a relay server in pipe mode. No rooms, no host — just
        /// connId-based forwarding. Use SendPipeData to send to specific peers.
        /// </summary>
        public async void ConnectAsPipe(string relayHost, int udpPort, int wsPort)
        {
            try
            {
                if (_isPipeMode && clientState != ConnectionState.Disconnected)
                    DisconnectPipe();

                _isPipeMode = true;
                clientState = ConnectionState.Connecting;

                var token = new CancellationTokenSource();
                AddCancellation(token, false);

                _client = SimpleWebClient.Create(ushort.MaxValue, 5000, _tcpConfig);
                _client.onConnect += OnPipeConnected;
                _client.onData += OnPipeData;
                _client.onDisconnect += OnPipeDisconnected;
                _client.onError += OnError;

                if (Application.platform != RuntimePlatform.WebGLPlayer)
                {
                    _isUsingUDP = true;
                    _udpClient.StartInManualMode(0);

                    var addresses = await Dns.GetHostAddressesAsync(relayHost);
                    var ipv4 = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                               ?? IPAddress.Any;

                    if (token.IsCancellationRequested)
                        return;

                    _relayUdpEndPoint = new IPEndPoint(ipv4, udpPort);
                    _relayClientPeer = _udpClient.Connect(_relayUdpEndPoint, "PurrNet");
                }
                else
                {
                    _isUsingUDP = false;
                    var builder = new UriBuilder
                    {
                        Scheme = "ws",
                        Host = relayHost,
                        Port = wsPort
                    };
                    _client.Connect(builder.Uri);
                }
            }
            catch (Exception e)
            {
                DisconnectPipe();
                PurrLogger.LogException(e.Message);
            }
        }

        private void OnPipeConnected()
        {
            string json = "{\"pipe\":true}";
            var data = Encoding.UTF8.GetBytes(json);

            if (_isUsingUDP)
                _udpClient.SendToAll(data, DeliveryMethod.ReliableOrdered);
            else
                _client.Send(data);
        }

        private void OnPipeData(ArraySegment<byte> data)
        {
            if (data.Array == null || data.Count == 0)
                return;

            if (clientState != ConnectionState.Connected)
            {
                var type = (SERVER_PACKET_TYPE)data.Array[data.Offset];

                if (type == SERVER_PACKET_TYPE.SERVER_PIPE_AUTHENTICATED && data.Count >= 5)
                {
                    _pipeConnId = data.Array[data.Offset + 1]
                                | data.Array[data.Offset + 2] << 8
                                | data.Array[data.Offset + 3] << 16
                                | data.Array[data.Offset + 4] << 24;

                    clientState = ConnectionState.Connected;
                    onConnected?.Invoke(new Connection(_pipeConnId), false);
                }
                else if (type == SERVER_PACKET_TYPE.SERVER_AUTHENTICATION_FAILED)
                {
                    DisconnectPipe();
                }
            }
            else
            {
                if (data.Count < 5) return;

                int senderConnId = data.Array[data.Offset]
                                 | data.Array[data.Offset + 1] << 8
                                 | data.Array[data.Offset + 2] << 16
                                 | data.Array[data.Offset + 3] << 24;

                var payload = new ByteData(data.Array, data.Offset + 4, data.Count - 4);
                onPipeDataReceived?.Invoke(senderConnId, payload);
            }
        }

        private void OnPipeDisconnected()
        {
            if (clientState != ConnectionState.Disconnected)
            {
                clientState = ConnectionState.Disconnecting;
                onDisconnected?.Invoke(new Connection(_pipeConnId), DisconnectReason.ServerRequest, false);
                clientState = ConnectionState.Disconnected;
            }
        }

        private void OnPipeDisconnectedUDP()
        {
            OnPipeDisconnected();
        }

        /// <summary>
        /// Send data to a specific peer via the relay (pipe mode only).
        /// </summary>
        public void SendPipeData(int targetConnId, ByteData data, Channel channel = Channel.ReliableOrdered)
        {
            if (!_isPipeMode || clientState != ConnectionState.Connected)
                return;

            _packer.ResetPositionAndMode(false);

            if (_isUsingUDP)
                Packer<byte>.Write(_packer, (byte)UDPTransport.ToDeliveryMethod(channel));

            Packer<int>.Write(_packer, targetConnId);
            _packer.WriteBytes(data);

            var byteData = _packer.ToByteData();

            if (_isUsingUDP)
            {
                var deliveryMethod = UDPTransport.ToDeliveryMethod(channel);
                _udpClient.SendToAll(byteData.data, byteData.offset, byteData.length, deliveryMethod);
            }
            else
            {
                _client.Send(new ArraySegment<byte>(byteData.data, byteData.offset, byteData.length));
            }
        }

        /// <summary>
        /// Disconnect from pipe mode.
        /// </summary>
        public void DisconnectPipe()
        {
            _isPipeMode = false;
            _pipeConnId = 0;

            CancelAll(false);

            if (_client != null)
            {
                _client.onConnect -= OnPipeConnected;
                _client.onData -= OnPipeData;
                _client.onDisconnect -= OnPipeDisconnected;
                _client.onError -= OnError;
                _client.Disconnect();
            }

            _udpClient?.Stop();
            _client = null;

            if (clientState is ConnectionState.Connecting or ConnectionState.Connected)
                clientState = ConnectionState.Disconnecting;
            clientState = ConnectionState.Disconnected;
        }

        public void CloseConnection(Connection conn)
        {
            if (natEnabled && _p2pPeersByConnId.Remove(conn.connectionId, out var kickP2pPeer))
            {
                _connIdByP2pPeer.Remove(kickP2pPeer);
                kickP2pPeer.Disconnect();
            }

            _packer.ResetPositionAndMode(false);

            Packer<byte>.Write(_packer, (byte)HOST_PACKET_TYPE.KICK_PLAYER);
            Packer<int>.Write(_packer, conn.connectionId);

            var data = _packer.ToByteData();

            if (_isUsingUDP)
                _relayServerPeer?.Send(data.data, data.offset, data.length, DeliveryMethod.ReliableSequenced);
            else _server.Send(new ArraySegment<byte>(data.data, data.offset, data.length));
            RaiseDataSent(conn, data, true);
        }

        /// <summary>
        /// Host-side accept handler for inbound P2P connections. A punched client connects
        /// using the relay-issued token as the connection key; we accept only tokens we are
        /// expecting and map the resulting peer to that client's relay connection ID.
        /// </summary>
        private void OnServerConnectionRequestUDP(ConnectionRequest request)
        {
            string token = null;
            try { token = request.Data.GetString(); }
            catch { /* ignored */ }

            if (!string.IsNullOrEmpty(token) &&
                _serverPunches.TryGetValue(token, out var session))
            {
                var peer = request.Accept();
                _p2pPeersByConnId[session.clientConnId] = peer;
                _connIdByP2pPeer[peer] = session.clientConnId;
                _serverPunches.Remove(token);
            }
            else
            {
                request.Reject();
            }
        }

        /// <summary>
        /// Starts (or no-ops if already running) a NAT introduce handshake for the given token.
        /// PollNatPunch resends the introduce request until it succeeds or the deadline passes;
        /// failure is harmless — the relay link keeps carrying the traffic.
        /// </summary>
        private void BeginPunch(string token, bool isServer, int clientConnId)
        {
            if (!natEnabled || string.IsNullOrEmpty(token))
                return;

            var session = new PunchSession
            {
                token = token,
                clientConnId = clientConnId,
                nextSendTime = 0f,
                deadline = Time.realtimeSinceStartup + 6f
            };

            if (isServer)
            {
                _serverPunches.TryAdd(token, session);
            }
            else
            {
                _clientPunch ??= session;
            }
        }

        /// <summary>Client side of a successful NAT introduction — connect directly to the host.</summary>
        private void OnClientNatPunchSuccess(IPEndPoint targetEndPoint, NatAddressType type, string token)
        {
            if (_clientPunch != null && _clientPunch.token == token)
                _clientPunch.done = true;

            if (_p2pHostPeer != null)
                return;

            _p2pHostPeer = _udpClient.Connect(targetEndPoint, token);
        }

        /// <summary>
        /// Host side of a successful NAT introduction. The punch opened our NAT mapping;
        /// we simply wait for the client's inbound connection (OnServerConnectionRequestUDP).
        /// </summary>
        private void OnServerNatPunchSuccess(IPEndPoint targetEndPoint, NatAddressType type, string token)
        {
            if (_serverPunches.TryGetValue(token, out var session))
                session.done = true;
        }

        /// <summary>
        /// Pumps NAT punch events and resends pending introduce requests to the relay mediator.
        /// </summary>
        private void PollNatPunch()
        {
            if (!natEnabled)
                return;

            var clientNat = _udpClient is { IsRunning: true } ? _udpClient.NatPunchModule : null;
            var serverNat = _udpServer is { IsRunning: true } ? _udpServer.NatPunchModule : null;
            clientNat?.PollEvents();
            serverNat?.PollEvents();

            float now = Time.realtimeSinceStartup;

            if (_clientConnPending && now >= _clientConnDeadline)
                ResolveClientConn(false);

            if (_pendingHostConns.Count > 0)
            {
                _hostConnScratch.Clear();
                foreach (var kv in _pendingHostConns)
                    if (now >= kv.Value)
                        _hostConnScratch.Add(kv.Key);

                for (var i = 0; i < _hostConnScratch.Count; i++)
                    ResolveHostConnAsRelay(_hostConnScratch[i]);
            }

            if (_relayUdpEndPoint == null)
                return;

            if (_serverPunches.Count > 0)
            {
                _punchScratch.Clear();

                foreach (var kv in _serverPunches)
                {
                    var session = kv.Value;

                    if (now >= session.deadline)
                    {
                        _punchScratch.Add(kv.Key);
                        continue;
                    }

                    if (session.done || now < session.nextSendTime)
                        continue;

                    serverNat?.SendNatIntroduceRequest(_relayUdpEndPoint, kv.Key);
                    session.nextSendTime = now + 0.5f;
                }

                for (var i = 0; i < _punchScratch.Count; i++)
                    _serverPunches.Remove(_punchScratch[i]);
            }

            if (_clientPunch != null)
            {
                if (now >= _clientPunch.deadline)
                {
                    _clientPunch = null;
                }
                else if (!_clientPunch.done && now >= _clientPunch.nextSendTime)
                {
                    clientNat?.SendNatIntroduceRequest(_relayUdpEndPoint, _clientPunch.token);
                    _clientPunch.nextSendTime = now + 0.5f;
                }
            }
        }

        public void ReceiveMessages(float delta)
        {
            FlushPromotedPromotion();

            if (!_pollEventsInUpdate)
            {
                if (_isUsingUDP)
                {
                    if (_udpClient.IsRunning)
                        _udpClient.PollEvents();
                    if (_udpServer.IsRunning)
                        _udpServer.PollEvents();
                    PollNatPunch();
                }
                else
                {
                    _server?.ProcessMessageQueue();
                    _client?.ProcessMessageQueue();
                }
            }
        }

        public void UnityUpdate(float delta)
        {
            FlushPromotedPromotion();

            if (_pollEventsInUpdate)
            {
                if (_isUsingUDP)
                {
                    if (_udpClient.IsRunning)
                        _udpClient.PollEvents();
                    if (_udpServer.IsRunning)
                        _udpServer.PollEvents();
                    PollNatPunch();
                }
                else
                {
                    _server?.ProcessMessageQueue();
                    _client?.ProcessMessageQueue();
                }
            }
        }

        public void SendMessages(float delta)
        {
            if (_isUsingUDP)
            {
                var dInMs = delta * 1000f;

                if (_udpClient.IsRunning)
                    _udpClient.ManualUpdate(dInMs);
                if (_udpServer.IsRunning)
                    _udpServer.ManualUpdate(dInMs);
            }
        }

        private void OnDisable()
        {
            StopListening();
            Disconnect();
            CleanupUdp();
        }
    }
}
