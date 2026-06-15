using System;
using System.Collections.Generic;
using PurrNet.Packing;

namespace PurrNet.Modules
{
    internal class SceneOwnership
    {
        static readonly List<OwnershipInfo> _cache = new List<OwnershipInfo>();

        readonly Dictionary<NetworkID, PlayerID> _owners = new Dictionary<NetworkID, PlayerID>();

        readonly Dictionary<PlayerID, HashSet<NetworkID>> _playerOwnedIds =
            new Dictionary<PlayerID, HashSet<NetworkID>>();

        private bool _asServer;

        public SceneOwnership(bool asServer)
        {
            _asServer = asServer;
        }

        public void PromoteToServerModule(HierarchyV2 hierarchy)
        {
            _asServer = true;

            foreach (var (owner, ids) in _playerOwnedIds)
            {
                foreach (var id in ids)
                {
                    if (hierarchy.TryGetIdentity(id, out var identity))
                    {
                        identity.internalOwnerServer = owner;
                        identity.internalOwnerClient = null;
                        identity.RecacheHasConnectedOwner();
                    }
                }
            }
        }

        /// <summary>Writes every (identity -> owner) pair in this scene for a relay snapshot.</summary>
        internal void ExportSnapshot(BitPacker packer)
        {
            Packer<int>.Write(packer, _owners.Count);
            foreach (var (id, player) in _owners)
            {
                Packer<NetworkID>.Write(packer, id);
                Packer<PlayerID>.Write(packer, player);
            }
        }

        /// <summary>
        /// Re-establishes ownership of an existing identity from a snapshot on the promoted
        /// host (server context): rebuilds the owner maps and re-seeds the server owner field,
        /// mirroring <see cref="PromoteToServerModule"/> for entries the promoted host's own
        /// replica was missing (e.g. visibility-culled or in-flight at host loss).
        /// </summary>
        internal void RestoreOwner(NetworkIdentity identity, PlayerID player)
        {
            if (!identity || !identity.id.HasValue)
                return;

            var id = identity.id.Value;
            _owners[id] = player;

            if (!_playerOwnedIds.TryGetValue(player, out var ownedIds))
            {
                ownedIds = new HashSet<NetworkID> { id };
                _playerOwnedIds[player] = ownedIds;
            }
            else ownedIds.Add(id);

            identity.internalOwnerServer = player;
            identity.internalOwnerClient = null;
            identity.RecacheHasConnectedOwner();
        }

        public List<OwnershipInfo> GetState()
        {
            _cache.Clear();

            foreach (var (id, player) in _owners)
                _cache.Add(new OwnershipInfo { identity = id, player = player });

            return _cache;
        }

        public ICollection<NetworkID> TryGetOwnedObjects(PlayerID player)
        {
            if (_playerOwnedIds.TryGetValue(player, out var players))
                return players;
            return Array.Empty<NetworkID>();
        }

        public bool TryGetOwner(NetworkIdentity id, out PlayerID player)
        {
            if (!id.id.HasValue)
            {
                player = default;
                return false;
            }

            return _owners.TryGetValue(id.id.Value, out player);
        }

        public bool GiveOwnership(NetworkIdentity identity, PlayerID player)
        {
            if (identity.id == null)
                return false;

            _owners[identity.id.Value] = player;

            var oldOwner = identity.GetOwner(_asServer);

            // Remove from old owner's owned list
            if (oldOwner.HasValue && oldOwner.Value != player && _playerOwnedIds.TryGetValue(oldOwner.Value, out var owned))
                owned.Remove(identity.id.Value);

            // Add to new owner's owned list
            if (!_playerOwnedIds.TryGetValue(player, out var ownedIds))
            {
                ownedIds = new HashSet<NetworkID> { identity.id.Value };
                _playerOwnedIds[player] = ownedIds;
            }
            else ownedIds.Add(identity.id.Value);

            if (_asServer)
                identity.internalOwnerServer = player;
            else identity.internalOwnerClient = player;

            return true;
        }

        public bool RemoveOwnership(NetworkIdentity identity)
        {
            if (identity.id.HasValue && _owners.Remove(identity.id.Value, out var oldOwner))
            {
                if (_playerOwnedIds.TryGetValue(oldOwner, out var ownedIds))
                {
                    ownedIds.Remove(identity.id.Value);

                    if (ownedIds.Count == 0)
                        _playerOwnedIds.Remove(oldOwner);
                }

                if (_asServer)
                    identity.internalOwnerServer = null;
                else identity.internalOwnerClient = null;
                return true;
            }

            return false;
        }
    }
}
