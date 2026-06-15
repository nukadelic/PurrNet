using System;
using PurrNet.Logging;
using PurrNet.Packing;
using PurrNet.Transports;
using UnityEngine;

namespace PurrNet.Modules
{
    /// <summary>
    /// captures an authoritative copy of the room's player-identity and ownership state, uploads 
	/// it into the relay's (PurrLay) RAM via <see cref="PurrTransport.UploadRoomSnapshot"/>, and
    /// restores it on the promoted host. This closes the gap where a <i>transferring</i> client
    /// reconnected against an incomplete replica and got a fresh <see cref="PlayerID"/> +
    /// duplicate character: the snapshot is authoritative, so cookie→PlayerID and per-object
    /// ownership are correct regardless of what was visibility-culled or in-flight at host loss.
    ///
    /// <para>Registered on BOTH module stacks. The promotion path migrates the live <i>client</i>
    /// modules into the server collection (server-only modules are dropped), so the client-side
    /// instance — the one that stashed the downloaded snapshot — is the one that ends up on the
    /// promoted host and applies it. Capture only runs server-side; restore runs in
    /// <see cref="PostPromoteToServerModule"/>, which executes before the transport's
    /// auto-<c>MIGRATION_READY</c>.</para>
    ///
    /// <para>Current scope: players (cookie↔PlayerID + id counter + last NetworkID) and ownership
    /// (per-scene identity→owner). Reconstructing visibility-culled objects from a full hierarchy
    /// snapshot is still future work — with the recommended host-migration rules
    /// (<c>identitiesAlwaysVisible</c>/<c>scenesAlwaysPublic</c>) those objects already exist on
    /// the promoted host, so ownership restore re-attaches to them.</para>
    /// </summary>
    public class RoomPersistenceModule : INetworkModule, IFixedUpdate, IPromoteToServerModule
    {
        // "PRSN" — room-persistence snapshot magic, guards against feeding the relay's opaque
        // blob to the wrong reader, and a format version so stale formats are discarded.
        private const uint SNAPSHOT_MAGIC = 0x5052534E;
        private const uint SNAPSHOT_FORMAT_VERSION = 1;

        private readonly NetworkManager _manager;
        private readonly PlayersManager _players;
        private readonly GlobalOwnershipModule _ownership;

        private bool _asServer;
        private bool _subscribed;
        private PurrTransport _purr;

        private float _nextCaptureTime;
        private uint _snapshotVersion;
        private byte[] _pendingRestore;

        /// <summary>Seconds between automatic snapshot uploads while hosting a persistent room.</summary>
        public float captureInterval = 5f;

        public RoomPersistenceModule(NetworkManager manager, PlayersManager players,
            GlobalOwnershipModule ownership)
        {
            _manager = manager;
            _players = players;
            _ownership = ownership;
        }

        private PurrTransport ResolveTransport()
        {
            if (_purr)
                return _purr;
            _purr = _manager.transport as PurrTransport;
            return _purr;
        }

        public void Enable(bool asServer)
        {
            _asServer = asServer;
            _nextCaptureTime = Time.time + captureInterval;

            // Only the client-side instance needs to catch the incoming snapshot — it is the
            // instance migrated onto the promoted host. (A host's own local client subscribes
            // too, harmlessly: it never receives a snapshot.)
            if (!asServer)
            {
                var purr = ResolveTransport();
                if (purr && !_subscribed)
                {
                    purr.onRoomSnapshotReceived += OnRoomSnapshotReceived;
                    _subscribed = true;
                }
            }
        }

        public void Disable(bool asServer)
        {
            if (_subscribed && _purr)
            {
                _purr.onRoomSnapshotReceived -= OnRoomSnapshotReceived;
                _subscribed = false;
            }
        }

        private void OnRoomSnapshotReceived(byte[] data, uint version)
        {
            // Stash; applied after promotion completes (PostPromoteToServerModule). Copy because
            // the transport reuses its download buffer.
            if (data == null || data.Length == 0)
            {
                _pendingRestore = null;
                return;
            }

            _pendingRestore = (byte[])data.Clone();
        }

        public void FixedUpdate()
        {
            if (!_asServer)
                return;

            var purr = ResolveTransport();
            if (!purr || !purr.persistentRoom)
                return;

            if (Time.time < _nextCaptureTime)
                return;

            _nextCaptureTime = Time.time + Mathf.Max(0.5f, captureInterval);
            CaptureAndUpload(purr);
        }

        /// <summary>
        /// Captures the current room state and uploads it to the relay immediately. Useful on a
        /// graceful host exit to make the relay copy zero-staleness. No-op off-server / off-relay.
        /// </summary>
        public void CaptureAndUpload()
        {
            var purr = ResolveTransport();
            if (_asServer && purr && purr.persistentRoom)
                CaptureAndUpload(purr);
        }

        private void CaptureAndUpload(PurrTransport purr)
        {
            byte[] blob;
            var packer = BitPackerPool.Get();
            try
            {
                packer.ResetPositionAndMode(false);
                Packer<uint>.Write(packer, SNAPSHOT_MAGIC);
                Packer<uint>.Write(packer, SNAPSHOT_FORMAT_VERSION);
                _players.ExportSnapshot(packer);
                _ownership.ExportSnapshot(packer);
                blob = packer.ToByteData().span.ToArray();
            }
            catch (Exception e)
            {
                PurrLogger.LogError($"[RoomPersistence] snapshot capture failed: {e.Message}\n{e.StackTrace}");
                packer.Dispose();
                return;
            }
            packer.Dispose();

            bool migrationLogging = _manager.networkRules && _manager.networkRules.IsHostMigrationEnabled();
            if (purr.UploadRoomSnapshot(blob, ++_snapshotVersion))
            {
                if (migrationLogging)
                    PurrLogger.Log($"[HostMigration] uploaded room snapshot v{_snapshotVersion} ({blob.Length} bytes).");
            }
        }

        public void PromoteToServerModule()
        {
            _asServer = true;
        }

        public void PostPromoteToServerModule()
        {
            if (_pendingRestore == null)
                return;

            var blob = _pendingRestore;
            _pendingRestore = null;
            ApplySnapshot(blob);
        }

        /// <summary>
        /// Restores players (cookie↔PlayerID + id counter + last NetworkID) and ownership
        /// (per-scene identity→owner) from a snapshot blob. Safe to call manually from
        /// <see cref="PurrTransport.onPromotedToHost"/> when <c>autoSendMigrationReady</c> is off.
        /// </summary>
        public void ApplySnapshot(byte[] blob)
        {
            if (blob == null || blob.Length == 0)
                return;

            var packer = BitPackerPool.Get(blob);
            try
            {
                uint magic = 0;
                Packer<uint>.Read(packer, ref magic);
                if (magic != SNAPSHOT_MAGIC)
                {
                    PurrLogger.LogError("[RoomPersistence] snapshot magic mismatch; discarding.");
                    return;
                }

                uint format = 0;
                Packer<uint>.Read(packer, ref format);
                if (format != SNAPSHOT_FORMAT_VERSION)
                {
                    PurrLogger.LogWarning(
                        $"[RoomPersistence] snapshot format v{format} != expected v{SNAPSHOT_FORMAT_VERSION}; discarding.");
                    return;
                }

                _players.ImportSnapshot(packer);
                _ownership.ImportSnapshot(packer);

                if (_manager.networkRules && _manager.networkRules.IsHostMigrationEnabled())
                    PurrLogger.Log("[HostMigration] applied room snapshot on promoted host (players + ownership restored).");
            }
            catch (Exception e)
            {
                PurrLogger.LogError($"[RoomPersistence] snapshot restore failed: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                packer.Dispose();
            }
        }
    }
}
