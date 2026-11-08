using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.LevelGeneration;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Utilities;
using UnityEngine;

namespace Ryuka.Sulfur.DynamicPressure
{
    public sealed partial class DynamicPressurePlugin
    {
        private const int TrickleMaxUnits = 3;
        private const int SurgeMaxUnits = 12;

        private const float LegacyDistanceMin = 8f;
        private const float DistanceMax = 60f;
        private const float FairDistanceMin = 15f;
        private const float RearDistanceMin = 25f;
        private const float SurgeClusterRadius = 14f;

        // Behind is more than about 105 degrees off where the camera points.
        private const float RearDotThreshold = -0.25f;
        private const int MaxLineChecksPerPick = 16;
        private const int NearestPointsToChooseFrom = 5;
        private const float ViewportMargin = 0.05f;
        private const float SightCheckHeight = 1.2f;
        private const float BigSightCheckHeight = 2.5f;

        private static readonly MutationDefinitions[] AllowedMutations =
        {
            MutationDefinitions.Mutation_Big,
            MutationDefinitions.Mutation_Fire,
            MutationDefinitions.Mutation_Frost,
            MutationDefinitions.Mutation_Poison,
            MutationDefinitions.Mutation_Surge,
        };

        private readonly List<UnitSO> _eligible = new List<UnitSO>();
        private readonly List<MutationDefinitions> _mutationChoices = new List<MutationDefinitions>();
        private readonly HashSet<NPCSpawn> _usedThisBatch = new HashSet<NPCSpawn>();

        private enum SpawnZone
        {
            Ahead,
            Side,
            Rear
        }

        private sealed class ZoneBuckets
        {
            public readonly List<SpawnPointChoice> Ahead = new List<SpawnPointChoice>();
            public readonly List<SpawnPointChoice> Side = new List<SpawnPointChoice>();
            public readonly List<SpawnPointChoice> Rear = new List<SpawnPointChoice>();

            public List<SpawnPointChoice> For(SpawnZone zone)
            {
                switch (zone)
                {
                    case SpawnZone.Ahead:
                        return Ahead;
                    case SpawnZone.Side:
                        return Side;
                    default:
                        return Rear;
                }
            }
        }

        /// <summary>
        /// Places up to <paramref name="maxUnits"/> units of one faction, one per frame, spending
        /// from <paramref name="budget"/> and reporting each placed unit to the director.
        /// </summary>
        private async Task SpawnBatchAsync(float budget, int maxUnits, bool surge, bool manual, string reason)
        {
            if (_spawnInProgress)
            {
                return;
            }

            GameManager gm = StaticInstance<GameManager>.Instance;
            if (gm == null || gm.graphContext == null)
            {
                return;
            }

            _spawnInProgress = true;
            _waveId++;

            int generation = _levelGeneration;
            int placed = 0;

            try
            {
                Roster.CollectEligible(gm, CurrentPreset, _packBudget, _lateGame, _settings.EliteMultiplier.Value, _eligible);
                if (_eligible.Count == 0)
                {
                    Block(SpawnBlockReason.NoCandidateUnits);
                    return;
                }

                KeepOneFaction(gm, _eligible);
                _usedThisBatch.Clear();

                float spent = 0f;
                bool anchored = false;
                Vector3 anchor = Vector3.zero;

                while (placed < maxUnits && (manual || spent < budget))
                {
                    UnitSO unit;
                    SpawnPointChoice point;
                    if (!TryChooseUnitAndPoint(gm, anchored, anchor, out unit, out point))
                    {
                        Block(SpawnBlockReason.NoFairSpawnPoint);
                        break;
                    }

                    _usedThisBatch.Add(point.spawn);

                    if (!await SpawnOneAsync(gm, generation, unit, point, reason, !manual))
                    {
                        break;
                    }

                    placed++;
                    spent += unit.SpawnCost;

                    if (surge && !anchored)
                    {
                        anchored = true;
                        anchor = point.position;
                    }

                    // Rebuilding the game's NPC lists per spawn is not free; give each one a frame.
                    await Task.Yield();
                }

                if (placed > 0)
                {
                    _lastDecision = "SPAWNED";
                    _lastBlockReason = SpawnBlockReason.None;
                }
            }
            catch (Exception e)
            {
                Block(SpawnBlockReason.SpawnAsyncFailed);
                _lastDecision = "FAILED";
                AddEvent("Spawn exception: " + e.GetType().Name);
                Logger.LogError(e);
            }
            finally
            {
                _diagWindowBatches++;
                _diagWindowSpawned += placed;
                _spawnInProgress = false;
            }
        }

        private void Block(SpawnBlockReason reason)
        {
            _lastDecision = "BLOCKED";
            _lastBlockReason = reason;
        }

        /// <summary>
        /// One pack, one faction, as vanilla does: factions that hate each other would otherwise
        /// spend the wave fighting among themselves. Reinforce whoever the player is fighting.
        /// </summary>
        private void KeepOneFaction(GameManager gm, List<UnitSO> units)
        {
            FactionIds faction = FactionFightingPlayer(gm);

            if (faction == FactionIds.None || !units.Exists(u => u.factionId == faction))
            {
                faction = units[UnityEngine.Random.Range(0, units.Count)].factionId;
            }

            units.RemoveAll(u => u.factionId != faction);
        }

        private FactionIds FactionFightingPlayer(GameManager gm)
        {
            FactionIds faction = FactionIds.None;
            float nearest = float.MaxValue;
            List<Npc> alive = gm.aliveNpcs;

            for (int i = 0; i < alive.Count; i++)
            {
                Npc npc = alive[i];
                if (!IsActiveThreat(npc) || !SafeTargetIsPlayer(npc))
                {
                    continue;
                }

                float distance = Vector3.Distance(npc.transform.position, gm.PlayerPosition);
                if (distance < nearest)
                {
                    nearest = distance;
                    faction = npc.unitSO.factionId;
                }
            }

            return faction;
        }

        private bool TryChooseUnitAndPoint(
            GameManager gm,
            bool anchored,
            Vector3 anchor,
            out UnitSO unit,
            out SpawnPointChoice point)
        {
            // Not every unit fits every spawn point, so one that fits nowhere must not end the wave.
            int start = UnityEngine.Random.Range(0, _eligible.Count);
            for (int i = 0; i < _eligible.Count; i++)
            {
                unit = _eligible[(start + i) % _eligible.Count];

                if (anchored && TryFindPointNear(gm, unit, anchor, out point))
                {
                    return true;
                }

                if (TryFindSpawnPoint(gm, unit, out point))
                {
                    return true;
                }
            }

            unit = null;
            point = default(SpawnPointChoice);
            return false;
        }

        private async Task<bool> SpawnOneAsync(
            GameManager gm,
            int generation,
            UnitSO unit,
            SpawnPointChoice point,
            string reason,
            bool chargeBudget)
        {
            Unit spawned = await unit.SpawnUnitAsync(this, point.position, Quaternion.identity);

            if (spawned == null)
            {
                return false;
            }

            Npc npc = spawned as Npc;
            if (npc == null)
            {
                npc = spawned.GetComponent<Npc>();
            }

            if (npc == null)
            {
                return false;
            }

            DynamicPressureSpawnMarker marker = npc.gameObject.AddComponent<DynamicPressureSpawnMarker>();
            marker.sourceUnitSo = unit;
            marker.spawnTime = Time.time;
            marker.spawnPosition = point.position;
            marker.spawnRoom = point.room;
            marker.spawnReason = reason;
            marker.waveId = _waveId;

            // The level can change during the await. A unit that outlives it keeps its marker, so
            // no encounter waits on it, but it is not charged to the new level.
            if (generation != _levelGeneration)
            {
                return false;
            }

            npc.currentRoom = point.room;
            npc.lastValidCurrentRoom = point.room;
            npc.lastRoomCalcPosition = npc.transform.position;

            string mutation = TryApplyMutation(gm, npc, unit);

            if (chargeBudget)
            {
                _director.ReportSpent(unit.SpawnCost);
            }

            string summary = unit.name + (mutation != null ? " [" + mutation + "]" : string.Empty)
                + " " + point.zone + " " + point.distanceToPlayer.ToString("0") + "m";
            _lastSpawnSummary = summary;
            AddEvent("Spawned: " + summary);

            if (_settings.LogSpawnDecisions.Value)
            {
                Logger.LogInfo("Spawned " + summary + " | cost " + unit.SpawnCost + " | " + reason);
            }

            RecordSpawn(unit, point, reason, mutation);

            StartCoroutine(ReportPlayerPositionLater(npc));
            return true;
        }

        /// <summary>
        /// Rolls the style's chance of a mutated enemy, using the same entry point Endless mode
        /// calls right after spawning.
        /// </summary>
        private string TryApplyMutation(GameManager gm, Npc npc, UnitSO unit)
        {
            PressurePreset preset = CurrentPreset;
            float chance = preset.MutationChance * _settings.EliteMultiplier.Value;
            if (chance <= 0f
                || (preset.MutationsOnlyLate && !_lateGame)
                || unit.disableMutations
                || npc.PreventMutations
                || unit.availableMutations == null
                || UnityEngine.Random.value > chance)
            {
                return null;
            }

            _mutationChoices.Clear();
            List<MutationDefinitions> disabled = gm.Settings != null ? gm.Settings.disabledMutations : null;

            for (int i = 0; i < AllowedMutations.Length; i++)
            {
                MutationDefinitions candidate = AllowedMutations[i];
                if (Array.IndexOf(unit.availableMutations, candidate) >= 0
                    && (disabled == null || !disabled.Contains(candidate)))
                {
                    _mutationChoices.Add(candidate);
                }
            }

            if (_mutationChoices.Count == 0)
            {
                return null;
            }

            MutationDefinitions chosen = _mutationChoices[UnityEngine.Random.Range(0, _mutationChoices.Count)];
            npc.ToggleMutation(chosen, state: true);
            return chosen.ToString();
        }

        private IEnumerator ReportPlayerPositionLater(Npc npc)
        {
            yield return new WaitForSeconds(0.5f);

            GameManager gm = StaticInstance<GameManager>.Instance;
            if (gm == null || gm.PlayerObject == null || gm.PlayerUnit == null)
            {
                yield break;
            }

            if (npc == null || !npc.IsAlive || npc.AiAgent == null)
            {
                yield break;
            }

            try
            {
                // Use the Unit overload directly. The GameObject overload routes through
                // UnitManager.TryGetUnitFromObject, whose negative cache can incorrectly
                // classify the already-known player root after repeated reports.
                npc.AiAgent.ReportLastSeen(
                    gm.PlayerUnit,
                    gm.PlayerUnit.transform.position,
                    npc.AiAgent.transform.position,
                    npc.AiAgent.propagateToGroup
                );
            }
            catch (Exception e)
            {
                Logger.LogWarning(e);
            }
        }

        private bool TryFindSpawnPoint(GameManager gm, UnitSO unit, out SpawnPointChoice result)
        {
            result = default(SpawnPointChoice);

            List<Room> rooms = gm.orderedRooms;
            if (rooms == null || rooms.Count == 0 || gm.PlayerUnit == null)
            {
                return false;
            }

            bool anywhere = _settings.Position.Value == SpawnPosition.Anywhere;
            float minimum = anywhere ? LegacyDistanceMin : FairDistanceMin;

            Room playerRoom = GetUnitRoom(gm.PlayerUnit);
            Camera camera = PlayerCamera(gm);
            Vector3 playerPosition = gm.PlayerPosition;

            ZoneBuckets strict = new ZoneBuckets();
            ZoneBuckets loose = new ZoneBuckets();

            for (int r = 0; r < rooms.Count; r++)
            {
                Room room = rooms[r];
                if (!IsValidSpawnRoom(room, anywhere))
                {
                    continue;
                }

                NPCSpawn[] spawns = room.GetNPCSpawns();
                if (spawns == null)
                {
                    continue;
                }

                for (int i = 0; i < spawns.Length; i++)
                {
                    NPCSpawn spawn = spawns[i];
                    if (spawn == null || _usedThisBatch.Contains(spawn) || (spawn.usableByTypes & unit.unitType) == 0)
                    {
                        continue;
                    }

                    Vector3 position = spawn.transform.position;
                    float distance = Vector3.Distance(position, playerPosition);
                    if (distance < minimum || distance > DistanceMax * 2f)
                    {
                        continue;
                    }

                    SpawnZone zone = anywhere
                        ? SpawnZone.Ahead
                        : ClassifyZone(position, room, playerRoom, playerPosition, camera);

                    if (zone == SpawnZone.Rear && distance < RearDistanceMin)
                    {
                        continue;
                    }

                    SpawnPointChoice choice = new SpawnPointChoice
                    {
                        spawn = spawn,
                        position = position,
                        room = room,
                        distanceToPlayer = distance,
                        zone = zone
                    };

                    (distance <= DistanceMax ? strict : loose).For(zone).Add(choice);
                }
            }

            if (anywhere)
            {
                return PickNearest(strict.Ahead, out result) || PickNearest(loose.Ahead, out result);
            }

            return PickByPlacement(strict, loose, camera, unit, out result);
        }

        private bool PickByPlacement(
            ZoneBuckets strict,
            ZoneBuckets loose,
            Camera camera,
            UnitSO unit,
            out SpawnPointChoice result)
        {
            bool neverBehind = _settings.Position.Value == SpawnPosition.Ahead;
            float roll = UnityEngine.Random.value;

            // A weighted first choice keeps it unpredictable; the rest is what to fall back on.
            SpawnZone first;
            if (neverBehind)
            {
                first = roll < 0.7f ? SpawnZone.Ahead : SpawnZone.Side;
            }
            else
            {
                first = roll < 0.6f ? SpawnZone.Ahead : (roll < 0.9f ? SpawnZone.Side : SpawnZone.Rear);
            }

            SpawnZone[] order = OrderFrom(first, neverBehind);

            // Rear only leads when the roll chose it; otherwise it is the last resort below.
            foreach (ZoneBuckets band in new[] { strict, loose })
            {
                foreach (SpawnZone zone in order)
                {
                    if ((zone != SpawnZone.Rear || first == SpawnZone.Rear)
                        && PickHidden(band.For(zone), camera, unit, out result))
                    {
                        return true;
                    }
                }
            }

            if (!neverBehind)
            {
                foreach (ZoneBuckets band in new[] { strict, loose })
                {
                    if (PickHidden(band.Rear, camera, unit, out result))
                    {
                        return true;
                    }
                }
            }

            result = default(SpawnPointChoice);
            return false;
        }

        private static SpawnZone[] OrderFrom(SpawnZone first, bool neverBehind)
        {
            if (neverBehind)
            {
                return first == SpawnZone.Ahead
                    ? new[] { SpawnZone.Ahead, SpawnZone.Side }
                    : new[] { SpawnZone.Side, SpawnZone.Ahead };
            }

            switch (first)
            {
                case SpawnZone.Side:
                    return new[] { SpawnZone.Side, SpawnZone.Ahead, SpawnZone.Rear };
                case SpawnZone.Rear:
                    return new[] { SpawnZone.Rear, SpawnZone.Ahead, SpawnZone.Side };
                default:
                    return new[] { SpawnZone.Ahead, SpawnZone.Side, SpawnZone.Rear };
            }
        }

        private static SpawnZone ClassifyZone(
            Vector3 position,
            Room room,
            Room playerRoom,
            Vector3 playerPosition,
            Camera camera)
        {
            if (camera != null)
            {
                Vector3 forward = camera.transform.forward;
                forward.y = 0f;
                Vector3 toPoint = position - playerPosition;
                toPoint.y = 0f;

                if (forward.sqrMagnitude > 0.0001f
                    && toPoint.sqrMagnitude > 0.0001f
                    && Vector3.Dot(forward.normalized, toPoint.normalized) < RearDotThreshold)
                {
                    return SpawnZone.Rear;
                }
            }

            return IsAheadOnRoute(position, room, playerRoom, playerPosition) ? SpawnZone.Ahead : SpawnZone.Side;
        }

        private static bool IsAheadOnRoute(Vector3 position, Room room, Room playerRoom, Vector3 playerPosition)
        {
            if (playerRoom == null)
            {
                return false;
            }

            if (room.lodRoomIndex != playerRoom.lodRoomIndex)
            {
                return room.lodRoomIndex > playerRoom.lodRoomIndex;
            }

            if (playerRoom.nextRoomConnector == null)
            {
                return false;
            }

            Vector3 exit = playerRoom.nextRoomConnector.transform.position;
            return Vector3.Distance(position, exit) < Vector3.Distance(playerPosition, exit);
        }

        private bool PickHidden(
            List<SpawnPointChoice> choices,
            Camera camera,
            UnitSO unit,
            out SpawnPointChoice result)
        {
            result = default(SpawnPointChoice);
            if (choices.Count == 0)
            {
                return false;
            }

            choices.Sort((a, b) => a.distanceToPlayer.CompareTo(b.distanceToPlayer));

            List<SpawnPointChoice> hidden = new List<SpawnPointChoice>(NearestPointsToChooseFrom);
            int checks = Mathf.Min(choices.Count, MaxLineChecksPerPick);
            for (int i = 0; i < checks && hidden.Count < NearestPointsToChooseFrom; i++)
            {
                if (!IsExposed(camera, choices[i].position, unit))
                {
                    hidden.Add(choices[i]);
                }
            }

            if (hidden.Count == 0)
            {
                return false;
            }

            result = hidden[UnityEngine.Random.Range(0, hidden.Count)];
            return true;
        }

        private bool TryFindPointNear(GameManager gm, UnitSO unit, Vector3 anchor, out SpawnPointChoice result)
        {
            result = default(SpawnPointChoice);

            Camera camera = PlayerCamera(gm);
            bool anywhere = _settings.Position.Value == SpawnPosition.Anywhere;
            bool neverBehind = _settings.Position.Value == SpawnPosition.Ahead;
            float minimum = anywhere ? LegacyDistanceMin : FairDistanceMin;
            Room playerRoom = GetUnitRoom(gm.PlayerUnit);
            int checks = 0;

            List<Room> rooms = gm.orderedRooms;
            for (int r = 0; r < rooms.Count; r++)
            {
                Room room = rooms[r];
                if (!IsValidSpawnRoom(room, anywhere))
                {
                    continue;
                }

                NPCSpawn[] spawns = room.GetNPCSpawns();
                if (spawns == null)
                {
                    continue;
                }

                for (int i = 0; i < spawns.Length && checks < MaxLineChecksPerPick; i++)
                {
                    NPCSpawn spawn = spawns[i];
                    if (spawn == null || _usedThisBatch.Contains(spawn) || (spawn.usableByTypes & unit.unitType) == 0)
                    {
                        continue;
                    }

                    Vector3 position = spawn.transform.position;
                    float distance = Vector3.Distance(position, gm.PlayerPosition);
                    if (Vector3.Distance(position, anchor) > SurgeClusterRadius || distance < minimum)
                    {
                        continue;
                    }

                    SpawnZone zone = anywhere
                        ? SpawnZone.Ahead
                        : ClassifyZone(position, room, playerRoom, gm.PlayerPosition, camera);

                    if (zone == SpawnZone.Rear && (neverBehind || distance < RearDistanceMin))
                    {
                        continue;
                    }

                    checks++;
                    if (!anywhere && IsExposed(camera, position, unit))
                    {
                        continue;
                    }

                    result = new SpawnPointChoice
                    {
                        spawn = spawn,
                        position = position,
                        room = room,
                        distanceToPlayer = distance,
                        zone = zone
                    };
                    return true;
                }
            }

            return false;
        }

        private static bool PickNearest(List<SpawnPointChoice> choices, out SpawnPointChoice result)
        {
            result = default(SpawnPointChoice);
            if (choices.Count == 0)
            {
                return false;
            }

            choices.Sort((a, b) => a.distanceToPlayer.CompareTo(b.distanceToPlayer));
            result = choices[UnityEngine.Random.Range(0, Mathf.Min(choices.Count, NearestPointsToChooseFrom))];
            return true;
        }

        private static bool IsValidSpawnRoom(Room room, bool anywhere)
        {
            if (room == null || room.IsStartRoom || room.IsEndRoom || room.disallowEnemySpawn)
            {
                return false;
            }

            // Vanilla never places enemies in corridors.
            return anywhere || room.roomSize != RoomSize.Corridor;
        }

        private static Camera PlayerCamera(GameManager gm)
        {
            // Not gm.currentCamera: that can be a cinematic or developer camera.
            return gm.PlayerScript != null ? gm.PlayerScript.playerCamera : null;
        }

        /// <summary>True when a unit standing at the point would be in the player's view.</summary>
        private static bool IsExposed(Camera camera, Vector3 position, UnitSO unit)
        {
            if (camera == null)
            {
                return false;
            }

            GameManager gm = StaticInstance<GameManager>.Instance;
            if (gm == null)
            {
                return false;
            }

            if (CanSee(camera, position + Vector3.up * SightCheckHeight, gm))
            {
                return true;
            }

            return (unit.unitType & UnitType.Big) != 0
                && CanSee(camera, position + Vector3.up * BigSightCheckHeight, gm);
        }

        private static bool CanSee(Camera camera, Vector3 point, GameManager gm)
        {
            Vector3 viewport = camera.WorldToViewportPoint(point);
            if (viewport.z <= 0f
                || viewport.x < -ViewportMargin || viewport.x > 1f + ViewportMargin
                || viewport.y < -ViewportMargin || viewport.y > 1f + ViewportMargin)
            {
                return false;
            }

            return !Physics.Linecast(camera.transform.position, point, gm.enemyOcclusionLayers, QueryTriggerInteraction.Ignore);
        }

        private struct SpawnPointChoice
        {
            public NPCSpawn spawn;
            public Vector3 position;
            public Room room;
            public float distanceToPlayer;
            public SpawnZone zone;
        }
    }
}
