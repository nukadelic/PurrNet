using System;
using PurrNet.Transports;
using UnityEditor;
using UnityEngine;

namespace PurrNet.Editor
{
    [CustomEditor(typeof(PurrTransport), true)]
    public class PurrTransportInspector : UnityEditor.Editor
    {
        private SerializedProperty _masterServer;
        private SerializedProperty _roomName;
        private SerializedProperty _region;
        private SerializedProperty _host;
        private SerializedProperty _timeoutInSeconds;
        private SerializedProperty _pollEventsInUpdate;
        private SerializedProperty _useNat;
        private SerializedProperty _natResolveTimeout;
        private SerializedProperty _persistentRoom;
        private SerializedProperty _autoPromoteToHost;
        private SerializedProperty _autoTransferOnMigration;
        private SerializedProperty _autoSendMigrationReady;
        private SerializedProperty _networkSimulation;

        private bool _lookingForBestRegion;
        string[] _regions = Array.Empty<string>();
        string[] _hosts = Array.Empty<string>();

        void OnEnable()
        {
            _masterServer = serializedObject.FindProperty("_masterServer");
            _roomName = serializedObject.FindProperty("_roomName");
            _region = serializedObject.FindProperty("_region");
            _host = serializedObject.FindProperty("_host");
            _timeoutInSeconds = serializedObject.FindProperty("_timeoutInSeconds");
            _pollEventsInUpdate = serializedObject.FindProperty("_pollEventsInUpdate");
            _useNat = serializedObject.FindProperty("_useNat");
            _natResolveTimeout = serializedObject.FindProperty("_natResolveTimeout");
            _persistentRoom = serializedObject.FindProperty("_persistentRoom");
            _autoPromoteToHost = serializedObject.FindProperty("_autoPromoteToHost");
            _autoTransferOnMigration = serializedObject.FindProperty("_autoTransferOnMigration");
            _autoSendMigrationReady = serializedObject.FindProperty("_autoSendMigrationReady");
            _networkSimulation = serializedObject.FindProperty("_networkSimulation");

            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                LoadRegions();
        }

        bool _loadingRegions;

        async void LoadRegions()
        {
            try
            {
                if (_loadingRegions)
                    return;

                _loadingRegions = true;
                var servers = await PurrTransportUtils.ActualGetRelayServersAsync(_masterServer.stringValue);

                if (servers.servers == null)
                {
                    _loadingRegions = false;
                    return;
                }

                _hosts = new string[servers.servers.Length];
                _regions = new string[servers.servers.Length];

                for (var i = 0; i < servers.servers.Length; i++)
                {
                    _hosts[i] = servers.servers[i].host;
                    _regions[i] = servers.servers[i].region;
                }

                _loadingRegions = false;
            }
            catch (Exception e)
            {
                _loadingRegions = false;
                Debug.LogException(e);
            }
        }

        int RegionId(string region, string host)
        {
            for (var i = 0; i < _regions.Length; i++)
            {
                if (_regions[i] == region)
                {
                    if (_hosts[i] != host)
                        return -1;
                    return i;
                }
            }

            return -1;
        }

        private async void FindBestRegion()
        {
            try
            {
                if (_lookingForBestRegion)
                    return;

                _lookingForBestRegion = true;

                var server = await PurrTransportUtils.ActualGetRelayServerAsync(_masterServer.stringValue);

                _region.stringValue = server.region;
                serializedObject.ApplyModifiedProperties();

                _lookingForBestRegion = false;
            }
            catch (Exception e)
            {
                _lookingForBestRegion = false;
                Debug.LogException(e);
            }
        }

        float _lastMasterServerUpdate;

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            var transport = (PurrTransport)target;

            var oldMasterServer = _masterServer.stringValue;
            EditorGUILayout.PropertyField(_masterServer);

            if (oldMasterServer != _masterServer.stringValue)
                _lastMasterServerUpdate = Time.realtimeSinceStartup;

            if (_lastMasterServerUpdate != 0 && Time.realtimeSinceStartup - _lastMasterServerUpdate > 1)
            {
                LoadRegions();
                _lastMasterServerUpdate = 0;
            }

            var server = _masterServer.stringValue;
            if (Uri.TryCreate(server, UriKind.Absolute, out var url) && url.Host.EndsWith("purrtransport.purrservers.com"))
            {
                EditorGUILayout.HelpBox("This server is meant for development use only.\n" +
                                        "Usage in production is strictly prohibited.\n" +
                                        "You need to host your own relay servers for production.", MessageType.Warning);
            }

            EditorGUILayout.PropertyField(_roomName);

            bool oldEnabled = GUI.enabled;
            if (_lookingForBestRegion || _loadingRegions || _lastMasterServerUpdate != 0)
                GUI.enabled = false;

            EditorGUILayout.BeginHorizontal();

            if (_regions.Length == 0)
            {
                bool enabled = GUI.enabled;
                GUI.enabled = false;
                EditorGUILayout.PropertyField(_region);
                GUI.enabled = enabled;
            }
            else
            {
                int region = RegionId(transport.region, transport.host);
                var newRegion = EditorGUILayout.Popup("Region", region, _regions);

                if (newRegion < 0 && _regions.Length > 0)
                    newRegion = 0;

                if (region != newRegion && newRegion >= 0 && newRegion < _regions.Length)
                {
                    _region.stringValue = _regions[newRegion];
                    _host.stringValue = _hosts[newRegion];
                }
            }

            if (GUILayout.Button("Find Best Region", GUILayout.ExpandWidth(false)))
                FindBestRegion();

            GUI.enabled = oldEnabled;

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUI.color = new Color(0.8f, 0.8f, 0.8f);
            GUILayout.Label(_host.stringValue);
            GUI.color = Color.white;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.PropertyField(_timeoutInSeconds);
            EditorGUILayout.PropertyField(_pollEventsInUpdate);
            EditorGUILayout.PropertyField(_useNat, new GUIContent("Use NAT",
                "Attempt a direct P2P link via NAT hole-punching. Fully transparent to the " +
                "game: traffic falls back to the relay automatically when P2P is unavailable."));

            if (_useNat.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_natResolveTimeout, new GUIContent("NAT Resolve Timeout",
                    "Seconds to wait for a NAT punch to establish a direct P2P link before " +
                    "falling back to the relay. Keep this comfortably below your connection/" +
                    "auth timeout so a failed punch still falls back in time."));
                if (_natResolveTimeout.floatValue < 1f)
                    _natResolveTimeout.floatValue = 1f;
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.PropertyField(_persistentRoom, new GUIContent("Persistent Room",
                "Persistent rooms survive host loss: the relay keeps the room (and an optional " +
                "host-uploaded state snapshot) alive and promotes a surviving client to host. " +
                "Requires a relay that supports protocol v1."));

            if (_persistentRoom.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_autoPromoteToHost, new GUIContent("Auto Promote To Host",
                    "When this peer is elected as the new host, automatically run " +
                    "NetworkManager.PromoteToServer(). Disable to drive promotion manually " +
                    "via the onPromotedToHost event."));
                EditorGUILayout.PropertyField(_autoTransferOnMigration, new GUIContent("Auto Transfer On Migration",
                    "When the room migrated to a new host, automatically run " +
                    "NetworkManager.TransferToNewServer(). Disable to drive the transfer manually " +
                    "via the onHostMigrated event."));
                EditorGUILayout.PropertyField(_autoSendMigrationReady, new GUIContent("Auto Send Migration Ready",
                    "Automatically report MIGRATION_READY to the relay right after promotion. " +
                    "Disable if a state-restore step should decide when the new host is ready, " +
                    "then call SendMigrationReady() manually."));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(_networkSimulation, new GUIContent("Network Simulation (UDP)"));

            if (GUILayout.Button("Refresh"))
                LoadRegions();

            TransportInspector.DrawTransportStatus(transport);

            DrawConnectionMode(transport);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawConnectionMode(PurrTransport transport)
        {
            if (!Application.isPlaying)
                return;

            var link = transport.clientSessionLink;
            string clientLine = link switch
            {
                PurrTransport.SessionLink.P2P => "Direct P2P (NAT)",
                PurrTransport.SessionLink.Relay => "Relay",
                PurrTransport.SessionLink.Resolving => "resolving NAT punch…",
                _ => null
            };

            int total = transport.connections.Count;
            bool hasHost = total > 0;

            if (clientLine == null && !hasHost)
                return;

            EditorGUILayout.Space(4);

            if (clientLine != null)
                EditorGUILayout.LabelField("Client session", clientLine);

            if (hasHost)
            {
                EditorGUILayout.LabelField("Host links",
                    $"{transport.p2pConnectionCount} P2P / {total - transport.p2pConnectionCount} relay");

                foreach (var conn in transport.connections)
                {
                    var isP2p = transport.GetP2pEndpoint(conn) != null;
                    EditorGUILayout.LabelField($"    conn {conn.connectionId}", isP2p ? "P2P" : "Relay");
                }
            }
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;
    }
}
