using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.LevelGeneration;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Units.AI;
using PerfectRandom.Sulfur.Core.Utilities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ryuka.Sulfur.DynamicPressure
{
    public sealed partial class DynamicPressurePlugin
    {
        private void ScanPressure()
        {
            Snapshot s = new Snapshot();
            _candidatePool.Clear();

            GameManager gm = StaticInstance<GameManager>.Instance;
            if (gm == null)
            {
                _snapshot = s;
                _lastBlockReason = SpawnBlockReason.NoGameManager;
                return;
            }

            s.hasGameManager = true;
            s.gameState = gm.gameState.ToString();
            s.inSafeZone = gm.InSafeZone;
            s.currentEnvironment = gm.currentEnvironment != null ? gm.currentEnvironment.id.ToString() : "null";

            if (gm.PlayerUnit == null || gm.PlayerObject == null)
            {
                _snapshot = s;
                _lastBlockReason = SpawnBlockReason.NoPlayer;
                return;
            }

            Unit playerUnit = gm.PlayerUnit;
            Room playerRoom = GetUnitRoom(playerUnit);

            s.hasPlayer = true;
            s.playerAlive = playerUnit.IsAlive;
            s.playerHp = SafeGetPlayerHp(playerUnit);
            s.playerTimeSinceDamage = PlayerTimeSinceDamage;
            s.playerRoomName = playerRoom != null ? playerRoom.name : "null";
            s.playerRoomIsEndRoom = playerRoom != null && playerRoom.IsEndRoom;

            List<Npc> alive = gm.aliveNpcs;
            s.aliveNpcs = alive != null ? alive.Count : 0;

            if (alive != null)
            {
                for (int i = 0; i < alive.Count; i++)
                {
                    Npc npc = alive[i];
                    if (!IsValidAliveNpc(npc))
                    {
                        continue;
                    }

                    DynamicPressureSpawnMarker marker = npc.GetComponent<DynamicPressureSpawnMarker>();
                    bool isMod = marker != null;
                    bool hostile = IsHostileToPlayer(npc);
                    if (!hostile)
                    {
                        continue;
                    }

                    float distance = Vector3.Distance(npc.transform.position, gm.PlayerPosition);
                    bool nearby = distance <= _enemyScanRadius.Value;
                    bool targeting = SafeTargetIsPlayer(npc);
                    bool knowsPlayer = SafeHasKnownPlayerPosition(npc, gm.PlayerUnit);
                    bool roomRelevant = IsSameOrConnectedRoom(GetUnitRoom(npc), playerRoom);
                    bool engaged = nearby || targeting || knowsPlayer || roomRelevant;

                    int pressure = CalculateNpcPressure(npc, distance, targeting, knowsPlayer);

                    if (isMod)
                    {
                        s.modHostileAlive++;
                        if (nearby)
                        {
                            s.nearbyModHostiles++;
                            s.modPressure += pressure;
                        }

                        if (targeting)
                        {
                            s.modTargetingPlayer++;
                        }
                    }
                    else
                    {
                        s.originalHostileAlive++;

                        if (engaged)
                        {
                            s.originalEngagedHostiles++;
                        }

                        if (nearby)
                        {
                            s.nearbyOriginalHostiles++;
                            s.originalPressure += pressure;
                        }

                        if (targeting)
                        {
                            s.originalTargetingPlayer++;
                        }

                        TryAddCandidateFromNpc(npc);
                    }
                }
            }

            if (_candidatePool.Count == 0 && _allowEnvironmentFallbackCandidates.Value)
            {
                AddEnvironmentFallbackCandidates(gm);
                s.candidateSource = _candidatePool.Count > 0 ? "EnvironmentMetadata" : "None";
            }
            else
            {
                s.candidateSource = _candidatePool.Count > 0 ? "CurrentLevelAliveNpcs" : "None";
            }

            s.candidateCount = _candidatePool.Count;
            s.targetPressure = GetTargetPressure();
            s.currentPressure = s.originalPressure + s.modPressure;
            s.deficit = s.targetPressure - s.currentPressure;
            s.spawnedThisLevel = _spawnedThisLevel;
            s.spawnedSinceLastOriginalKill = _spawnedSinceLastOriginalKill;
            s.timeSinceLastOriginalKill = Time.time - _lastOriginalKillTime;
            s.timeSinceLastModKill = Time.time - _lastModKillTime;
            s.nextSpawnIn = Mathf.Max(0f, _nextSpawnTime - Time.time);

            s.wouldDelayOnAllEnemiesDead = s.originalHostileAlive == 0 && s.modHostileAlive > 0;

            _snapshot = s;
        }

        private void TryAutoSpawn()
        {
            SpawnBlockReason blockReason;
            int spawnCount;

            if (!CanSpawn(out blockReason, out spawnCount))
            {
                _lastDecision = "BLOCKED";
                _lastBlockReason = blockReason;
                return;
            }

            _ = SpawnWaveAsync(spawnCount, "Auto pressure deficit");
        }

        private bool CanSpawn(out SpawnBlockReason blockReason, out int spawnCount)
        {
            spawnCount = 0;

            GameManager gm = StaticInstance<GameManager>.Instance;
            if (gm == null)
            {
                blockReason = SpawnBlockReason.NoGameManager;
                return false;
            }

            if (_inLevelTransition)
            {
                blockReason = SpawnBlockReason.LevelTransition;
                return false;
            }

            if (gm.PlayerUnit == null || gm.PlayerObject == null)
            {
                blockReason = SpawnBlockReason.NoPlayer;
                return false;
            }

            if (gm.gameState.ToString() != "Running")
            {
                blockReason = SpawnBlockReason.GameStateNotRunning;
                return false;
            }

            if (gm.InSafeZone)
            {
                blockReason = SpawnBlockReason.SafeZone;
                return false;
            }

            Unit playerUnit = gm.PlayerUnit;
            if (!playerUnit.IsAlive)
            {
                blockReason = SpawnBlockReason.PlayerDead;
                return false;
            }

            if (SafeGetPlayerHp(playerUnit) <= _lowHealthStopThreshold.Value)
            {
                blockReason = SpawnBlockReason.LowHealth;
                return false;
            }

            if (PlayerTimeSinceDamage <= _recentDamageStopWindow.Value)
            {
                blockReason = SpawnBlockReason.RecentDamage;
                return false;
            }

            Room playerRoom = GetUnitRoom(playerUnit);
            if (playerRoom != null && playerRoom.IsEndRoom)
            {
                blockReason = SpawnBlockReason.EndRoomBlocked;
                return false;
            }

            if (_snapshot.originalEngagedHostiles < _minOriginalEngagedHostilesForSpawn.Value)
            {
                blockReason = SpawnBlockReason.NoEngagedOriginalHostiles;
                return false;
            }

            if (_snapshot.currentPressure >= _snapshot.targetPressure)
            {
                blockReason = SpawnBlockReason.PressureAlreadyHigh;
                return false;
            }

            if (_snapshot.modHostileAlive >= GetMaxModAlive())
            {
                blockReason = SpawnBlockReason.MaxModSpawnedAlive;
                return false;
            }

            if (_spawnedThisLevel >= GetMaxModPerLevel())
            {
                blockReason = SpawnBlockReason.LevelSpawnBudgetExceeded;
                return false;
            }

            int supportedSpawnBudget = Mathf.Max(1, _snapshot.originalEngagedHostiles) * Mathf.Max(1, _maxModSpawnsPerOriginalEngaged.Value);
            if (_spawnedSinceLastOriginalKill >= supportedSpawnBudget)
            {
                blockReason = SpawnBlockReason.SpawnBudgetPerOriginalExceeded;
                return false;
            }

            if (_spawnedSinceLastOriginalKill > 0 && Time.time - _lastOriginalKillTime > _maxSecondsWithoutOriginalKill.Value)
            {
                blockReason = SpawnBlockReason.NoOriginalKillProgress;
                return false;
            }

            if (_lastModKillTime > _lastOriginalKillTime && Time.time - _lastModKillTime < _cooldownAfterModKillOnly.Value)
            {
                blockReason = SpawnBlockReason.RecentModKillOnly;
                return false;
            }

            if (_candidatePool.Count == 0)
            {
                blockReason = SpawnBlockReason.NoCandidateUnits;
                return false;
            }

            spawnCount = Mathf.Min(GetMaxSpawnPerWave(), Mathf.Max(1, _snapshot.deficit));
            spawnCount = Mathf.Min(spawnCount, GetMaxModAlive() - _snapshot.modHostileAlive);
            spawnCount = Mathf.Min(spawnCount, GetMaxModPerLevel() - _spawnedThisLevel);

            if (spawnCount <= 0)
            {
                blockReason = SpawnBlockReason.MaxModSpawnedAlive;
                return false;
            }

            blockReason = SpawnBlockReason.None;
            return true;
        }

        private async Task SpawnWaveAsync(int count, string reason)
        {
            if (_spawnInProgress)
            {
                return;
            }

            _spawnInProgress = true;
            _waveId++;

            int spawned = 0;

            try
            {
                ScanPressure();

                for (int i = 0; i < count; i++)
                {
                    UnitSO unitSo = PickCandidate();
                    if (unitSo == null)
                    {
                        _lastDecision = "BLOCKED";
                        _lastBlockReason = SpawnBlockReason.NoCandidateUnits;
                        AddEvent("Spawn blocked: no candidate.");
                        break;
                    }

                    SpawnPointChoice spawnPoint;
                    if (!TryFindSpawnPoint(unitSo, out spawnPoint))
                    {
                        _lastDecision = "BLOCKED";
                        _lastBlockReason = SpawnBlockReason.NoValidNpcSpawnPoint;
                        AddEvent("Spawn blocked: no valid NPCSpawn point.");
                        break;
                    }

                    int roomId = GetRoomId(spawnPoint.room);
                    int roomCount;
                    _modSpawnedPerRoom.TryGetValue(roomId, out roomCount);
                    if (roomCount >= GetMaxModPerRoom())
                    {
                        _lastDecision = "BLOCKED";
                        _lastBlockReason = SpawnBlockReason.RoomSpawnBudgetExceeded;
                        AddEvent("Spawn blocked: room budget exceeded.");
                        break;
                    }

                    bool ok = await SpawnOneAsync(unitSo, spawnPoint, reason);
                    if (!ok)
                    {
                        _lastDecision = "FAILED";
                        _lastBlockReason = SpawnBlockReason.SpawnAsyncFailed;
                        break;
                    }

                    spawned++;
                }

                if (spawned > 0)
                {
                    _lastDecision = "SPAWNED";
                    _lastBlockReason = SpawnBlockReason.None;
                    _nextSpawnTime = Time.time + GetSpawnCooldown();
                }
            }
            catch (Exception e)
            {
                _lastDecision = "FAILED";
                _lastBlockReason = SpawnBlockReason.SpawnAsyncFailed;
                AddEvent("Spawn exception: " + e.GetType().Name);
                Logger.LogError(e);
            }
            finally
            {
                // Only a spawning wave used to arm the cooldown, so any wave that broke out
                // early - no candidate, no reachable NPCSpawn, room budget full, spawn failure -
                // left _nextSpawnTime in the past and was retried on the very next frame. Each
                // retry re-ran two ScanPressure passes plus a full orderedRooms x NPCSpawn sweep,
                // for as long as the blocking condition held. Give the non-spawning outcomes a
                // short backoff of their own so a persistently blocked wave costs one attempt per
                // second rather than one per frame.
                if (spawned == 0)
                {
                    _nextSpawnTime = Time.time + FailedWaveRetrySeconds;
                }

                _diagWindowWaves++;
                _diagWindowSpawned += spawned;

                _spawnInProgress = false;
                ScanPressure();
            }
        }

        private async Task<bool> SpawnOneAsync(UnitSO unitSo, SpawnPointChoice spawnPoint, string reason)
        {
            if (unitSo == null)
            {
                return false;
            }

            Unit spawnedUnit = await unitSo.SpawnUnitAsync(this, spawnPoint.position, Quaternion.identity);
            if (spawnedUnit == null)
            {
                return false;
            }

            Npc npc = spawnedUnit as Npc;
            if (npc == null)
            {
                npc = spawnedUnit.GetComponent<Npc>();
            }

            if (npc == null)
            {
                return false;
            }

            if (spawnPoint.room != null)
            {
                npc.currentRoom = spawnPoint.room;
                npc.lastValidCurrentRoom = spawnPoint.room;
                npc.lastRoomCalcPosition = npc.transform.position;
            }

            DynamicPressureSpawnMarker marker = npc.gameObject.AddComponent<DynamicPressureSpawnMarker>();
            marker.sourceUnitSo = unitSo;
            marker.spawnTime = Time.time;
            marker.spawnPosition = spawnPoint.position;
            marker.spawnRoom = spawnPoint.room;
            marker.spawnReason = reason;
            marker.waveId = _waveId;

            int roomId = GetRoomId(spawnPoint.room);
            int roomCount;
            _modSpawnedPerRoom.TryGetValue(roomId, out roomCount);
            _modSpawnedPerRoom[roomId] = roomCount + 1;

            _spawnedThisLevel++;
            _spawnedSinceLastOriginalKill++;

            _lastSpawnSummary = string.Format(
                "{0} at {1}, room={2}, source={3}",
                unitSo.name,
                spawnPoint.position,
                spawnPoint.room != null ? spawnPoint.room.name : "null",
                spawnPoint.source
            );

            AddEvent("Spawned: " + unitSo.name);
            StartCoroutine(ReportPlayerPositionLater(npc, marker));

            return true;
        }

        private IEnumerator ReportPlayerPositionLater(Npc npc, DynamicPressureSpawnMarker marker)
        {
            yield return new WaitForSeconds(0.5f);

            GameManager gm = StaticInstance<GameManager>.Instance;
            if (gm == null || gm.PlayerObject == null || gm.PlayerUnit == null)
            {
                _lastAggroReport = "Failed: no player";
                yield break;
            }

            if (npc == null || !npc.IsAlive || npc.AiAgent == null)
            {
                _lastAggroReport = "Failed: invalid npc";
                yield break;
            }

            try
            {
                // Use the Unit overload directly. The GameObject overload routes through
                // UnitManager.TryGetUnitFromObject, whose negative cache can incorrectly
                // classify the already-known player root after repeated reports.
                // Supplying the known PlayerUnit preserves the original successful path
                // without touching UnitManager or any global player-trigger behavior.
                npc.AiAgent.ReportLastSeen(
                    gm.PlayerUnit,
                    gm.PlayerUnit.transform.position,
                    npc.AiAgent.transform.position,
                    npc.AiAgent.propagateToGroup
                );

                marker.aggroReported = true;
                marker.lastAggroReportTime = Time.time;
                marker.targetingPlayerAfterReport = SafeTargetIsPlayer(npc);
                marker.hasKnownPlayerPositionAfterReport = SafeHasKnownPlayerPosition(npc, gm.PlayerUnit);

                _lastAggroReport = marker.targetingPlayerAfterReport || marker.hasKnownPlayerPositionAfterReport
                    ? "Success"
                    : "Reported but not targeting yet";

                AddEvent("ReportLastSeen: " + _lastAggroReport);
            }
            catch (Exception e)
            {
                _lastAggroReport = "Exception: " + e.GetType().Name;
                Logger.LogWarning(e);
            }
        }

        private UnitSO PickCandidate()
        {
            if (_candidatePool.Count == 0)
            {
                return null;
            }

            return _candidatePool[UnityEngine.Random.Range(0, _candidatePool.Count)];
        }

        private void TryAddCandidateFromNpc(Npc npc)
        {
            if (npc == null || npc.unitSO == null)
            {
                return;
            }

            UnitSO so = npc.unitSO;
            if (!IsValidCandidateUnitSo(so))
            {
                return;
            }

            if (!_candidatePool.Contains(so))
            {
                _candidatePool.Add(so);
            }
        }

        private void AddEnvironmentFallbackCandidates(GameManager gm)
        {
            try
            {
                if (gm == null || gm.environmentsInOrder == null || gm.currentEnvironment == null)
                {
                    return;
                }

                WorldEnvironmentList.Metadata metadata = gm.environmentsInOrder.GetMetadata(gm.currentEnvironment.id);
                if (metadata.availableEnemiesReadOnly == null)
                {
                    return;
                }

                for (int i = 0; i < metadata.availableEnemiesReadOnly.Length; i++)
                {
                    UnitSO so = metadata.availableEnemiesReadOnly[i].GetAsset();
                    if (so != null && IsValidCandidateUnitSo(so) && !_candidatePool.Contains(so))
                    {
                        _candidatePool.Add(so);
                    }
                }
            }
            catch (Exception e)
            {
                AddEvent("Candidate fallback failed: " + e.GetType().Name);
            }
        }

        private bool IsValidCandidateUnitSo(UnitSO so)
        {
            if (so == null)
            {
                return false;
            }

            if (so.isCivilian)
            {
                return false;
            }

            if (so.isProtectedNpc)
            {
                return false;
            }

            if (so.ExperienceOnKill <= 0)
            {
                return false;
            }

            if ((so.unitType & UnitType.Boss) != 0)
            {
                return false;
            }

            return true;
        }

        private bool TryFindSpawnPoint(UnitSO unitSo, out SpawnPointChoice result)
        {
            result = default;

            GameManager gm = StaticInstance<GameManager>.Instance;
            if (gm == null || gm.PlayerUnit == null || gm.PlayerObject == null || unitSo == null)
                return false;

            Vector3 playerPos = gm.PlayerPosition;

            List<SpawnPointChoice> strictChoices = new List<SpawnPointChoice>();
            List<SpawnPointChoice> looseChoices = new List<SpawnPointChoice>();

            List<Room> rooms = gm.orderedRooms;
            if (rooms == null || rooms.Count == 0)
                return false;

            for (int r = 0; r < rooms.Count; r++)
            {
                Room room = rooms[r];
                if (!IsValidSpawnRoom(room))
                    continue;

                // Game 0.19 dropped the baked NPCSpawn.Data[] snapshot; GetNPCSpawns now hands back the
                // room's own live NPCSpawn components, and unlike the old accessor it does not filter out
                // destroyed entries - the game skips them one by one, so we do the same. The owning room is
                // the room we asked, which is exactly what the retired Data.room used to carry.
                NPCSpawn[] spawns = room.GetNPCSpawns();
                if (spawns == null || spawns.Length == 0)
                    continue;

                for (int i = 0; i < spawns.Length; i++)
                {
                    NPCSpawn spawn = spawns[i];
                    if (spawn == null)
                        continue;

                    if ((spawn.usableByTypes & unitSo.unitType) == 0)
                        continue;

                    Vector3 spawnPosition = spawn.transform.position;
                    float distance = Vector3.Distance(spawnPosition, playerPos);

                    SpawnPointChoice choice = new SpawnPointChoice
                    {
                        position = spawnPosition,
                        room = room,
                        distanceToPlayer = distance,
                        source = "GameManager.orderedRooms NPCSpawn"
                    };

                    if (distance >= _spawnDistanceMin.Value && distance <= _spawnDistanceMax.Value)
                    {
                        strictChoices.Add(choice);
                    }
                    else if (distance > _spawnDistanceMax.Value && distance <= _spawnDistanceMax.Value * 2f)
                    {
                        looseChoices.Add(choice);
                    }
                }
            }

            if (strictChoices.Count > 0)
            {
                result = PickNearestRandomized(strictChoices, playerPos);
                return true;
            }

            if (looseChoices.Count > 0)
            {
                result = PickNearestRandomized(looseChoices, playerPos);
                result.source += " / loose distance";
                return true;
            }

            return false;
        }

        private bool IsValidSpawnRoom(Room room)
        {
            if (room == null)
                return false;

            if (room.IsStartRoom)
                return false;

            if (room.IsEndRoom)
                return false;

            if (room.disallowEnemySpawn)
                return false;

            // The per-room budget has to be applied while choosing the point, not after. Choice is
            // made among the points nearest the player, so once the player's own room was full it
            // kept winning the selection and then failing the budget check, which aborted the wave
            // and left the neighbouring rooms untried for as long as the player stayed there.
            int roomCount;
            _modSpawnedPerRoom.TryGetValue(GetRoomId(room), out roomCount);
            if (roomCount >= GetMaxModPerRoom())
                return false;

            return true;
        }

        private SpawnPointChoice PickNearestRandomized(List<SpawnPointChoice> choices, Vector3 playerPos)
        {
            choices.Sort((a, b) => a.distanceToPlayer.CompareTo(b.distanceToPlayer));

            int maxPick = Mathf.Min(choices.Count, 5);
            return choices[UnityEngine.Random.Range(0, maxPick)];
        }

        private int CalculateNpcPressure(Npc npc, float distance, bool targeting, bool knowsPlayer)
        {
            int pressure = 1;

            if (npc != null && npc.unitSO != null)
            {
                pressure = Mathf.Clamp(npc.unitSO.SpawnCost, 1, 4);
            }

            if (distance <= _closeEnemyDistance.Value)
            {
                pressure += 1;
            }

            if (targeting)
            {
                pressure += 2;
            }
            else if (knowsPlayer)
            {
                pressure += 1;
            }

            return pressure;
        }

        private bool IsValidAliveNpc(Npc npc)
        {
            return npc != null && npc.gameObject != null && npc.IsAlive;
        }

        private bool IsHostileToPlayer(Npc npc)
        {
            try
            {
                return npc != null && npc.IsHostileTo(FactionIds.Player);
            }
            catch
            {
                return false;
            }
        }

        private bool SafeTargetIsPlayer(Npc npc)
        {
            try
            {
                return npc != null && npc.targetIsPlayer;
            }
            catch
            {
                return false;
            }
        }

        private bool SafeHasKnownPlayerPosition(Npc npc, Unit playerUnit)
        {
            try
            {
                return npc != null && npc.AiAgent != null && playerUnit != null && npc.AiAgent.HasKnownPosition(playerUnit);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Seconds since the last hit that actually took health off the player, or
        /// <see cref="NeverDamagedSeconds"/> if that has not happened yet this session.
        /// </summary>
        /// <remarks>
        /// This replaces the removed Unit.TimeSinceLastDamageTaken. The timestamp comes from
        /// <see cref="OnUnitReceiveDamagePostfix"/>, so the value stays meaningful across level
        /// transitions - the player Unit is destroyed and rebuilt on every level, but the clock
        /// this reads is Time.time, which is not.
        /// </remarks>
        private static float PlayerTimeSinceDamage
        {
            get
            {
                if (_playerLastDamageTime <= NeverDamagedTime)
                {
                    return NeverDamagedSeconds;
                }

                return Mathf.Clamp(Time.time - _playerLastDamageTime, 0f, NeverDamagedSeconds);
            }
        }

        private float SafeGetPlayerHp(Unit playerUnit)
        {
            try
            {
                return playerUnit != null ? playerUnit.GetNormalizedHealth() : 0f;
            }
            catch
            {
                return 0f;
            }
        }

        private Room GetUnitRoom(Unit unit)
        {
            if (unit == null)
            {
                return null;
            }

            if (unit.currentRoom != null)
            {
                return unit.currentRoom;
            }

            return unit.lastValidCurrentRoom;
        }

        private bool IsSameOrConnectedRoom(Room npcRoom, Room playerRoom)
        {
            if (npcRoom == null || playerRoom == null)
            {
                return false;
            }

            if (npcRoom == playerRoom)
            {
                return true;
            }

            if (playerRoom.connectedRooms == null)
            {
                return false;
            }

            for (int i = 0; i < playerRoom.connectedRooms.Length; i++)
            {
                if (playerRoom.connectedRooms[i] == npcRoom)
                {
                    return true;
                }
            }

            return false;
        }

        private int GetRoomId(Room room)
        {
            return room != null ? room.GetInstanceID() : 0;
        }

    }
}
