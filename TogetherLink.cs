using System;
using System.Collections.Generic;
using SULFURTogether.Api;
using UnityEngine;

namespace Ryuka.Sulfur.DynamicPressure
{
    /// <summary>
    /// The only class that uses SULFUR Together's API. It is created only when that plugin is
    /// installed (see <see cref="CoopLinks"/>), and turns its session, message channel and spawn
    /// mirroring into <see cref="ICoopLink"/>.
    ///
    /// No field may have a SULFUR Together type, not even as a generic argument. The game scans
    /// every loaded assembly's types when it loads a save, and a field type it cannot resolve
    /// throws there for a player without SULFUR Together, which stops the save from loading.
    /// Method bodies are compiled only when they run, so they may use the API freely.
    /// </summary>
    internal sealed class TogetherLink : ICoopLink
    {
        private const string ChannelId = "ryuka.sulfur.dynamicpressure";

        private readonly IDisposable _channel;
        private readonly List<IDisposable> _spawners = new List<IDisposable>();

        public TogetherLink()
        {
            _channel = NetExternalChannel.Register(ChannelId, OnReceive);
        }

        public event Action<byte[]> FromHost;

        public CoopRole Role
        {
            get
            {
                switch (NetSessionInfo.Role)
                {
                    case SessionRole.Host:
                        return CoopRole.Host;
                    case SessionRole.Client:
                        return CoopRole.Client;
                    default:
                        return CoopRole.Offline;
                }
            }
        }

        public string Name
        {
            get { return "SULFUR Together"; }
        }

        public bool SendToClients(byte[] payload)
        {
            return NetSessionInfo.Role == SessionRole.Host
                && NetExternalChannel.Send(ChannelId, payload, ExternalDelivery.ReliableOrdered, ExternalTarget.AllClients);
        }

        public void DeclareSpawner(MonoBehaviour owner)
        {
            _spawners.Add(NetExternalSpawns.RegisterHostAuthoritativeOwner(owner));
        }

        public int GetSpawnId(Component unit)
        {
            return NetExternalSpawns.GetSpawnId(unit);
        }

        public Component ResolveSpawn(int spawnId)
        {
            return NetExternalSpawns.ResolveSpawn(spawnId);
        }

        public void Dispose()
        {
            _channel.Dispose();
            for (int i = 0; i < _spawners.Count; i++)
            {
                _spawners[i].Dispose();
            }

            _spawners.Clear();
        }

        /// <summary>
        /// The sender id is stamped by SULFUR Together from the connection, so it can be trusted to
        /// say who sent a message. Only the host's messages carry anything this mod acts on.
        /// </summary>
        private void OnReceive(string senderPeerId, byte[] payload)
        {
            if (NetSessionInfo.Role != SessionRole.Client || payload == null || !IsHost(senderPeerId))
            {
                return;
            }

            Action<byte[]> handler = FromHost;
            if (handler != null)
            {
                handler(payload);
            }
        }

        private static bool IsHost(string peerId)
        {
            IReadOnlyList<ExternalPeer> peers = NetSessionInfo.Peers;
            for (int i = 0; i < peers.Count; i++)
            {
                if (peers[i].IsHost)
                {
                    return string.Equals(peers[i].PeerId, peerId, StringComparison.Ordinal);
                }
            }

            return false;
        }
    }
}
