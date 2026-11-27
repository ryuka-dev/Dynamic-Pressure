using System;
using System.Collections.Generic;
using BepInEx.Logging;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;

namespace Ryuka.Sulfur.DynamicPressure
{
    /// <summary>
    /// Carries what the host decided to its clients. The host owns the director, the units it
    /// adds and the boss health of its level; a client only applies what it is told.
    ///
    /// SULFUR Together copies each added unit to clients by itself, but as a plain unit. This
    /// adds the two things the copy is missing: that it belongs to this mod, so encounters do not
    /// wait for it, and the mutation the host rolled.
    /// </summary>
    internal sealed class CoopSync
    {
        private const float HostIdWaitSeconds = 2f;
        private const float ClientWaitSeconds = 4f;
        private const float BossHealthResendSeconds = 2f;
        private const int MaxPending = 128;

        private readonly ICoopLink _link;
        private readonly BossHealthScaler _bossHealth;
        private readonly ManualLogSource _log;

        private readonly List<HostPending> _hostPending = new List<HostPending>();
        private readonly List<ClientPending> _clientPending = new List<ClientPending>();

        private CoopRole _lastRole = CoopRole.Offline;
        private float _sentBossHealth = -1f;
        private float _nextBossHealthSend;
        private float _hostBossHealth = -1f;
        private bool _loggedVersionMismatch;

        public CoopSync(ICoopLink link, BossHealthScaler bossHealth, ManualLogSource log)
        {
            _link = link;
            _bossHealth = bossHealth;
            _log = log;
            _link.FromHost += OnFromHost;
        }

        public int NoticesSent { get; private set; }
        public int NoticesApplied { get; private set; }
        public int NoticesDropped { get; private set; }

        /// <summary>
        /// The role as of the last <see cref="Tick"/>. Everything outside Tick reads this instead
        /// of asking the session, so a co-op mod that changes under us fails in one guarded place.
        /// </summary>
        public CoopRole Role
        {
            get { return _lastRole; }
        }

        public string LinkName
        {
            get { return _link.Name; }
        }

        /// <summary>Host: a unit the director just added, with the mutation it was given (None for none).</summary>
        public void NoteHostSpawn(Npc npc, MutationDefinitions mutation)
        {
            if (_lastRole != CoopRole.Host || npc == null || npc.unitSO == null)
            {
                return;
            }

            if (_hostPending.Count >= MaxPending)
            {
                _hostPending.RemoveAt(0);
                NoticesDropped++;
            }

            _hostPending.Add(new HostPending
            {
                Npc = npc,
                UnitId = npc.unitSO.id.value,
                Mutation = (int)mutation,
                GiveUpAt = Time.time + HostIdWaitSeconds
            });
        }

        /// <summary>
        /// Main thread, every frame. A client follows the host whatever its own settings say,
        /// master switch included: the host owns the level, and its bosses are what the client sees.
        /// </summary>
        public void Tick()
        {
            CoopRole role = _link.Role;
            if (role != _lastRole)
            {
                OnRoleChanged(_lastRole, role);
                _lastRole = role;
            }

            if (role == CoopRole.Host)
            {
                SendHostNotices();
                SendBossHealth();
            }
            else if (role == CoopRole.Client)
            {
                ApplyClientNotices();

                // A host without this mod, or one not heard from yet, has bosses at their normal health.
                _bossHealth.FollowHost(_hostBossHealth > 0f ? _hostBossHealth : 1f);
            }
        }

        /// <summary>
        /// A new level is starting. Spawn ids belong to one level, so a notice still waiting from
        /// the last one could otherwise land on a unit of the new one.
        /// </summary>
        public void OnNewLevel()
        {
            NoticesDropped += _hostPending.Count + _clientPending.Count;
            _hostPending.Clear();
            _clientPending.Clear();
        }

        private void OnRoleChanged(CoopRole from, CoopRole to)
        {
            _hostPending.Clear();
            _clientPending.Clear();
            _sentBossHealth = -1f;
            _nextBossHealthSend = 0f;
            _hostBossHealth = -1f;

            // Leaving a session: bosses go back to this machine's own value from the next level.
            _bossHealth.StopFollowingHost();

            _log.LogInfo("Co-op role " + from + " -> " + to + (to == CoopRole.Client ? ": the host's director adds enemies; this one stays off." : "."));
        }

        private void SendHostNotices()
        {
            for (int i = _hostPending.Count - 1; i >= 0; i--)
            {
                HostPending pending = _hostPending[i];
                if (pending.Npc == null)
                {
                    _hostPending.RemoveAt(i);
                    NoticesDropped++;
                    continue;
                }

                int spawnId = _link.GetSpawnId(pending.Npc);
                if (spawnId <= 0)
                {
                    if (Time.time >= pending.GiveUpAt)
                    {
                        _hostPending.RemoveAt(i);
                        NoticesDropped++;
                    }

                    continue;
                }

                byte[] payload = CoopProtocol.Encode(new SpawnNotice
                {
                    SpawnId = spawnId,
                    UnitId = pending.UnitId,
                    Mutation = pending.Mutation
                });

                _hostPending.RemoveAt(i);
                if (_link.SendToClients(payload))
                {
                    NoticesSent++;
                }
            }
        }

        /// <summary>
        /// Sent when it changes and again every couple of seconds, so a player who joins partway
        /// through a level gets it without the host tracking who joined when.
        /// </summary>
        private void SendBossHealth()
        {
            float value = _bossHealth.HostAnnouncement;
            if (value == _sentBossHealth && Time.time < _nextBossHealthSend)
            {
                return;
            }

            if (_link.SendToClients(CoopProtocol.EncodeBossHealth(value)))
            {
                _sentBossHealth = value;
            }

            _nextBossHealthSend = Time.time + BossHealthResendSeconds;
        }

        private void OnFromHost(byte[] payload)
        {
            CoopMessageKind kind;
            byte version;
            if (!CoopProtocol.TryReadKind(payload, out kind, out version))
            {
                if (!_loggedVersionMismatch)
                {
                    _loggedVersionMismatch = true;
                    _log.LogWarning("Co-op: the host's Dynamic Pressure sent message version " + version + ", but this one only reads version "
                        + CoopProtocol.Version + ". Use the same Dynamic Pressure version as the host.");
                }

                return;
            }

            switch (kind)
            {
                case CoopMessageKind.SpawnNotice:
                    SpawnNotice notice;
                    if (CoopProtocol.TryDecode(payload, out notice) && IsAllowedMutation(notice.Mutation))
                    {
                        QueueClientNotice(notice);
                    }
                    else
                    {
                        NoticesDropped++;
                    }

                    break;

                case CoopMessageKind.BossHealth:
                    float multiplier;
                    if (CoopProtocol.TryDecodeBossHealth(payload, out multiplier))
                    {
                        _hostBossHealth = multiplier;
                    }

                    break;
            }
        }

        private static bool IsAllowedMutation(int mutation)
        {
            return mutation == (int)MutationDefinitions.None
                || Array.IndexOf(DynamicPressurePlugin.AllowedMutations, (MutationDefinitions)mutation) >= 0;
        }

        private void QueueClientNotice(SpawnNotice notice)
        {
            if (_clientPending.Count >= MaxPending)
            {
                _clientPending.RemoveAt(0);
                NoticesDropped++;
            }

            _clientPending.Add(new ClientPending { Notice = notice, GiveUpAt = Time.time + ClientWaitSeconds });
        }

        /// <summary>
        /// The copy is spawned asynchronously, so a notice can arrive first. It waits a few seconds
        /// for the copy, and is applied only if the copy is the unit the host named.
        /// </summary>
        private void ApplyClientNotices()
        {
            for (int i = _clientPending.Count - 1; i >= 0; i--)
            {
                ClientPending pending = _clientPending[i];
                Component copy = _link.ResolveSpawn(pending.Notice.SpawnId);
                Npc npc = copy != null ? copy.GetComponent<Npc>() : null;

                if (npc == null || npc.unitSO == null)
                {
                    if (Time.time >= pending.GiveUpAt)
                    {
                        _clientPending.RemoveAt(i);
                        NoticesDropped++;
                    }

                    continue;
                }

                _clientPending.RemoveAt(i);

                if (npc.unitSO.id.value != pending.Notice.UnitId)
                {
                    NoticesDropped++;
                    continue;
                }

                if (npc.GetComponent<DynamicPressureSpawnMarker>() == null)
                {
                    DynamicPressureSpawnMarker marker = npc.gameObject.AddComponent<DynamicPressureSpawnMarker>();
                    marker.sourceUnitSo = npc.unitSO;
                    marker.spawnTime = Time.time;
                    marker.spawnPosition = npc.transform.position;
                    marker.spawnReason = "Host";
                }

                MutationDefinitions mutation = (MutationDefinitions)pending.Notice.Mutation;
                if (mutation != MutationDefinitions.None && npc.IsAlive)
                {
                    npc.ToggleMutation(mutation, state: true);
                }

                NoticesApplied++;
            }
        }

        private struct HostPending
        {
            public Npc Npc;
            public ushort UnitId;
            public int Mutation;
            public float GiveUpAt;
        }

        private struct ClientPending
        {
            public SpawnNotice Notice;
            public float GiveUpAt;
        }
    }
}
