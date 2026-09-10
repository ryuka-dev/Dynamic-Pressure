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
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class DynamicPressurePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "ryuka.sulfur.dynamicpressure";
        public const string PluginName = "Dynamic Pressure";
        public const string PluginVersion = "0.1.9";

        private static DynamicPressurePlugin _instance;
        private Harmony _harmony;

        private ConfigEntry<bool> _enableMod;
        private ConfigEntry<bool> _enableAutoSpawn;
        private ConfigEntry<int> _pressureStyle;

        private ConfigEntry<bool> _enableOverlay;
        private ConfigEntry<Key> _overlayToggleKey;
        private ConfigEntry<Key> _manualSpawnKey;
        private ConfigEntry<bool> _logSpawnDecisions;

        private ConfigEntry<float> _scanInterval;
        private ConfigEntry<float> _enemyScanRadius;
        private ConfigEntry<float> _closeEnemyDistance;
        private ConfigEntry<float> _lowHealthStopThreshold;
        private ConfigEntry<float> _recentDamageStopWindow;

        private ConfigEntry<int> _minOriginalEngagedHostilesForSpawn;
        private ConfigEntry<float> _cooldownAfterModKillOnly;
        private ConfigEntry<float> _maxSecondsWithoutOriginalKill;
        private ConfigEntry<int> _maxModSpawnsPerOriginalEngaged;
        private ConfigEntry<bool> _allowEnvironmentFallbackCandidates;

        private ConfigEntry<float> _spawnDistanceMin;
        private ConfigEntry<float> _spawnDistanceMax;
        private ConfigEntry<float> _style1Cooldown;
        private ConfigEntry<float> _style2Cooldown;
        private ConfigEntry<float> _style3Cooldown;

        private ConfigEntry<int> _style1TargetPressure;
        private ConfigEntry<int> _style2TargetPressure;
        private ConfigEntry<int> _style3TargetPressure;

        private ConfigEntry<int> _style1MaxSpawnPerWave;
        private ConfigEntry<int> _style2MaxSpawnPerWave;
        private ConfigEntry<int> _style3MaxSpawnPerWave;

        private ConfigEntry<int> _style1MaxModAlive;
        private ConfigEntry<int> _style2MaxModAlive;
        private ConfigEntry<int> _style3MaxModAlive;

        private ConfigEntry<int> _style1MaxModPerRoom;
        private ConfigEntry<int> _style2MaxModPerRoom;
        private ConfigEntry<int> _style3MaxModPerRoom;

        private ConfigEntry<int> _style1MaxModPerLevel;
        private ConfigEntry<int> _style2MaxModPerLevel;
        private ConfigEntry<int> _style3MaxModPerLevel;

        // A wave that places nothing leaves the pressure deficit untouched, so the next Update()
        // would re-enter the whole attempt immediately. Back off for this long instead.
        private const float FailedWaveRetrySeconds = 1.0f;

        // Diagnostics only. Wave outcomes are otherwise invisible to the log, which leaves a wave
        // that keeps retrying without placing anything impossible to see after the fact. The
        // per-window wave count is the number that matters: a blocked wave backs off for
        // FailedWaveRetrySeconds, so a healthy window holds a handful of attempts and a window in
        // the hundreds means the wave is repeating every frame.
        private const float DiagLogIntervalSeconds = 5.0f;

        private readonly List<UnitSO> _candidatePool = new List<UnitSO>();
        private readonly List<string> _recentEvents = new List<string>();
        private readonly Dictionary<int, int> _modSpawnedPerRoom = new Dictionary<int, int>();
        private readonly HashSet<int> _deadNpcIds = new HashSet<int>();

        private object _lastGraphContext;
        private float _nextScanTime;
        private float _nextSpawnTime;
        private float _nextDiagLogTime;
        private int _diagWindowWaves;
        private int _diagWindowSpawned;
        private string _pluginDirectory;
        private float _nextLanguageRetryTime;
        private GUIStyle _overlayStyle;
        private Vector2 _overlayScroll;
        private AsyncAssetLoading.OnLanguageChange _reloadOverlayLanguage;
        private bool _spawnInProgress;
        private bool _inLevelTransition;
        private int _waveId;
        private int _spawnedThisLevel;
        private int _spawnedSinceLastOriginalKill;
        private float _lastOriginalKillTime = -9999f;
        private float _lastModKillTime = -9999f;

        // Game 0.19.7 removed Unit.TimeSinceLastDamageTaken, which used to answer "how long has it
        // been since the player was hit". Both readers of it were about the player only - one
        // safety gate, one overlay readout - so Dynamic Pressure keeps the timestamp itself from
        // the damage call rather than depending on a property the game may rename or drop again.
        private const float NeverDamagedTime = -99999f;
        private const float NeverDamagedSeconds = 9999f;
        private static float _playerLastDamageTime = NeverDamagedTime;
        private static bool _playerDamageTimerActive;

        private string _lastUpdateFailure;
        private int _updateFailureCount;

        private Snapshot _snapshot = new Snapshot();
        private SpawnBlockReason _lastBlockReason = SpawnBlockReason.None;
        private string _lastDecision = "None";
        private string _lastSpawnSummary = "None";
        private string _lastAggroReport = "None";

        private void Awake()
        {
            _instance = this;
            BindConfig();

            // The loader searches lang/ under here and, for managers that flatten a package, this
            // folder itself.
            _pluginDirectory = Path.GetDirectoryName(Info.Location) ?? string.Empty;

            _harmony = new Harmony(PluginGuid);
            TryPatchTransitions();
            TryPatchNpcDie();
            TryPatchAllDeadTrigger();
            TryPatchPlayerDamageTimer();

            Log("Loaded.");
        }

        private void OnDestroy()
        {
            StopFollowingLanguage();

            try
            {
                _harmony?.UnpatchSelf();
            }
            catch
            {
                // ignored
            }

            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void BindConfig()
        {
            _enableMod = Config.Bind("General", "EnableMod", true, "Enable Dynamic Pressure.");
            _enableAutoSpawn = Config.Bind("General", "EnableAutoSpawn", true, "Enable automatic dynamic pressure spawning. Keep false for first debug testing.");
            _pressureStyle = Config.Bind("General", "PressureStyle", 1, "1 = Light, 2 = Heavy, 3 = Nightmare.");

            _enableOverlay = Config.Bind("Debug Overlay", "EnableOverlay", false, "Show debug overlay.");
            _overlayToggleKey = Config.Bind("Debug Overlay", "OverlayToggleKey", Key.F9, "Toggle overlay key.");
            _manualSpawnKey = Config.Bind("Debug Overlay", "ManualSpawnKey", Key.F8, "Manual debug spawn key.");
            _logSpawnDecisions = Config.Bind("Debug Overlay", "LogSpawnDecisions", false, "Write a periodic summary of spawn wave outcomes to the BepInEx log. Diagnostics only; does not affect spawning.");

            _scanInterval = Config.Bind("Pressure", "ScanInterval", 1.0f, "Seconds between pressure scans.");
            _enemyScanRadius = Config.Bind("Pressure", "EnemyScanRadius", 25.0f, "Nearby enemy scan radius.");
            _closeEnemyDistance = Config.Bind("Pressure", "CloseEnemyDistance", 8.0f, "Enemies closer than this add extra pressure.");
            _lowHealthStopThreshold = Config.Bind("Safety", "LowHealthStopThreshold", 0.35f, "Do not spawn if player health is below this normalized value.");
            _recentDamageStopWindow = Config.Bind("Safety", "RecentDamageStopWindow", 4.0f, "Do not spawn if player was damaged within this many seconds.");

            _minOriginalEngagedHostilesForSpawn = Config.Bind("Anti Loop", "MinOriginalEngagedHostilesForSpawn", 1, "Require this many engaged original hostiles before spawning mod enemies.");
            _cooldownAfterModKillOnly = Config.Bind("Anti Loop", "CooldownAfterModKillOnly", 8.0f, "If only mod enemies are dying, pause spawning for this many seconds.");
            _maxSecondsWithoutOriginalKill = Config.Bind("Anti Loop", "MaxSecondsWithoutOriginalKill", 30.0f, "Stop spawning if no original enemy has died for this long after mod spawns.");
            _maxModSpawnsPerOriginalEngaged = Config.Bind("Anti Loop", "MaxModSpawnsPerOriginalEngaged", 2, "Soft budget: each engaged original enemy can support this many mod spawns.");
            _allowEnvironmentFallbackCandidates = Config.Bind("Candidates", "AllowEnvironmentFallbackCandidates", false, "Use current environment enemy metadata if current level alive NPC candidate pool is empty.");

            _spawnDistanceMin = Config.Bind("Spawn", "SpawnDistanceMin", 8.0f, "Minimum spawn distance from player.");
            _spawnDistanceMax = Config.Bind("Spawn", "SpawnDistanceMax", 60.0f, "Maximum spawn distance from player.");

            _style1TargetPressure = Config.Bind("Style 1 - Light", "TargetPressure", 6, "Target pressure.");
            _style1Cooldown = Config.Bind("Style 1 - Light", "SpawnCooldown", 16.0f, "Spawn cooldown.");
            _style1MaxSpawnPerWave = Config.Bind("Style 1 - Light", "MaxSpawnPerWave", 1, "Max spawns per wave.");
            _style1MaxModAlive = Config.Bind("Style 1 - Light", "MaxModSpawnedAlive", 2, "Max alive mod-spawned NPCs.");
            _style1MaxModPerRoom = Config.Bind("Style 1 - Light", "MaxModSpawnedPerRoom", 3, "Max mod spawns per room.");
            _style1MaxModPerLevel = Config.Bind("Style 1 - Light", "MaxModSpawnedPerLevel", 8, "Max mod spawns per level.");

            _style2TargetPressure = Config.Bind("Style 2 - Heavy", "TargetPressure", 9, "Target pressure.");
            _style2Cooldown = Config.Bind("Style 2 - Heavy", "SpawnCooldown", 10.0f, "Spawn cooldown.");
            _style2MaxSpawnPerWave = Config.Bind("Style 2 - Heavy", "MaxSpawnPerWave", 1, "Max spawns per wave.");
            _style2MaxModAlive = Config.Bind("Style 2 - Heavy", "MaxModSpawnedAlive", 4, "Max alive mod-spawned NPCs.");
            _style2MaxModPerRoom = Config.Bind("Style 2 - Heavy", "MaxModSpawnedPerRoom", 5, "Max mod spawns per room.");
            _style2MaxModPerLevel = Config.Bind("Style 2 - Heavy", "MaxModSpawnedPerLevel", 16, "Max mod spawns per level.");

            _style3TargetPressure = Config.Bind("Style 3 - Nightmare", "TargetPressure", 13, "Target pressure.");
            _style3Cooldown = Config.Bind("Style 3 - Nightmare", "SpawnCooldown", 7.0f, "Spawn cooldown.");
            _style3MaxSpawnPerWave = Config.Bind("Style 3 - Nightmare", "MaxSpawnPerWave", 2, "Max spawns per wave.");
            _style3MaxModAlive = Config.Bind("Style 3 - Nightmare", "MaxModSpawnedAlive", 6, "Max alive mod-spawned NPCs.");
            _style3MaxModPerRoom = Config.Bind("Style 3 - Nightmare", "MaxModSpawnedPerRoom", 8, "Max mod spawns per room.");
            _style3MaxModPerLevel = Config.Bind("Style 3 - Nightmare", "MaxModSpawnedPerLevel", 28, "Max mod spawns per level.");
        }

        private void Update()
        {
            HandleKeys();
            EnsureLanguageSubscription();

            if (!_enableMod.Value)
            {
                _lastDecision = "BLOCKED";
                _lastBlockReason = SpawnBlockReason.Disabled;
                return;
            }

            try
            {
                UpdatePressure();
            }
            catch (Exception e)
            {
                ReportUpdateFailure(e);
            }

            LogSpawnDiagnostics();
        }

        private void UpdatePressure()
        {
            GameManager gm = StaticInstance<GameManager>.Instance;
            if (gm != null && gm.graphContext != null && !ReferenceEquals(_lastGraphContext, gm.graphContext))
            {
                _lastGraphContext = gm.graphContext;
                ResetRuntimeState("New graphContext");
            }

            if (Time.time >= _nextScanTime)
            {
                _nextScanTime = Time.time + Mathf.Max(0.2f, _scanInterval.Value);
                ScanPressure();
            }

            if (_enableAutoSpawn.Value && !_spawnInProgress && Time.time >= _nextSpawnTime)
            {
                TryAutoSpawn();
            }
        }

        /// <summary>
        /// A game update that removes or renames something read every frame used to take the whole
        /// plugin down with it and bury the player's log: 0.19.7 dropping
        /// Unit.TimeSinceLastDamageTaken threw on the first frame of the session and wrote roughly
        /// 27000 stack traces into a single run, with no working overlay left to say why. Report a
        /// given failure once, count the repeats, and leave the overlay and the keys alive so the
        /// cause is still visible in game.
        /// </summary>
        private void ReportUpdateFailure(Exception e)
        {
            string signature = e.GetType().Name + ": " + e.Message;

            if (signature != _lastUpdateFailure)
            {
                _lastUpdateFailure = signature;
                _updateFailureCount = 0;
                AddEvent("Pressure update failed: " + e.GetType().Name);
                Logger.LogError(
                    "Pressure update failed. Repeats of this same failure are counted, not logged."
                );
                Logger.LogError(e);
            }

            _updateFailureCount++;

            // Nothing below this point produced a decision, so do not leave the last successful one
            // on screen as though it still described the current frame.
            _lastDecision = "FAILED";
            _lastBlockReason = SpawnBlockReason.UpdateFailed;
        }

        /// <summary>
        /// Rate-limited spawn-wave summary. Purely observational: nothing here feeds a spawn
        /// decision, and with the setting off the method costs one bool read per frame.
        /// </summary>
        private void LogSpawnDiagnostics()
        {
            if (!_logSpawnDecisions.Value)
            {
                // Keep the window aligned with the moment logging is switched on, so the first
                // line covers a full interval instead of however long the setting sat off.
                _nextDiagLogTime = Time.time + DiagLogIntervalSeconds;
                _diagWindowWaves = 0;
                _diagWindowSpawned = 0;
                return;
            }

            if (Time.time < _nextDiagLogTime)
            {
                return;
            }

            _nextDiagLogTime = Time.time + DiagLogIntervalSeconds;

            if (_diagWindowWaves > 0)
            {
                Logger.LogInfo(
                    "Spawn waves in last " + DiagLogIntervalSeconds.ToString("0") + "s: " + _diagWindowWaves
                    + " | placed: " + _diagWindowSpawned
                    + " | last decision: " + _lastDecision + " / " + _lastBlockReason
                    + " | room: " + _snapshot.playerRoomName
                    + " | mod alive: " + _snapshot.modHostileAlive
                    + " | this level: " + _spawnedThisLevel + "/" + GetMaxModPerLevel());
            }

            _diagWindowWaves = 0;
            _diagWindowSpawned = 0;
        }

        private void HandleKeys()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (keyboard[_overlayToggleKey.Value].wasPressedThisFrame)
            {
                _enableOverlay.Value = !_enableOverlay.Value;
            }

            if (keyboard[_manualSpawnKey.Value].wasPressedThisFrame)
            {
                if (!_spawnInProgress)
                {
                    _ = SpawnWaveAsync(1, "Manual debug spawn");
                }
            }
        }

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

        private int GetStyle()
        {
            return Mathf.Clamp(_pressureStyle.Value, 1, 3);
        }

        private int GetTargetPressure()
        {
            switch (GetStyle())
            {
                case 1:
                    return _style1TargetPressure.Value;
                case 2:
                    return _style2TargetPressure.Value;
                default:
                    return _style3TargetPressure.Value;
            }
        }

        private float GetSpawnCooldown()
        {
            switch (GetStyle())
            {
                case 1:
                    return _style1Cooldown.Value;
                case 2:
                    return _style2Cooldown.Value;
                default:
                    return _style3Cooldown.Value;
            }
        }

        private int GetMaxSpawnPerWave()
        {
            switch (GetStyle())
            {
                case 1:
                    return _style1MaxSpawnPerWave.Value;
                case 2:
                    return _style2MaxSpawnPerWave.Value;
                default:
                    return _style3MaxSpawnPerWave.Value;
            }
        }

        private int GetMaxModAlive()
        {
            switch (GetStyle())
            {
                case 1:
                    return _style1MaxModAlive.Value;
                case 2:
                    return _style2MaxModAlive.Value;
                default:
                    return _style3MaxModAlive.Value;
            }
        }

        private int GetMaxModPerRoom()
        {
            switch (GetStyle())
            {
                case 1:
                    return _style1MaxModPerRoom.Value;
                case 2:
                    return _style2MaxModPerRoom.Value;
                default:
                    return _style3MaxModPerRoom.Value;
            }
        }

        private int GetMaxModPerLevel()
        {
            switch (GetStyle())
            {
                case 1:
                    return _style1MaxModPerLevel.Value;
                case 2:
                    return _style2MaxModPerLevel.Value;
                default:
                    return _style3MaxModPerLevel.Value;
            }
        }

        private void ResetRuntimeState(string reason)
        {
            _candidatePool.Clear();
            _recentEvents.Clear();
            _modSpawnedPerRoom.Clear();
            _deadNpcIds.Clear();

            _waveId = 0;
            _spawnedThisLevel = 0;
            _spawnedSinceLastOriginalKill = 0;
            _lastOriginalKillTime = Time.time;
            _lastModKillTime = -9999f;
            _lastDecision = "Reset";
            _lastBlockReason = SpawnBlockReason.None;
            _lastSpawnSummary = "None";
            _lastAggroReport = "None";
            _inLevelTransition = false;
            _nextSpawnTime = Time.time + 3f;

            AddEvent("Reset: " + reason);
        }

        /// <summary>
        /// Dynamic Pressure enemies are ambient pressure additions, not scripted
        /// encounter objectives. AllDeadTrigger can capture later-spawned NPCs from
        /// GameManager.aliveNpcs, including NPCs outside the encounter room. Remove
        /// marked NPCs immediately before the original completion check.
        /// </summary>
        private static void OnAllDeadTriggerCheckAllDeadPrefix(List<Npc> ___allAliveNpcs)
        {
            if (___allAliveNpcs == null || ___allAliveNpcs.Count == 0)
            {
                return;
            }

            ___allAliveNpcs.RemoveAll(IsDynamicPressureSpawnedNpc);
        }

        /// <summary>
        /// AllDeadTrigger subscribes RegisterDeath to NPCs captured after Start.
        /// Ignore a stale callback from a Dynamic Pressure NPC after it has already
        /// been excluded from the encounter's tracked list.
        /// </summary>
        private static bool OnAllDeadTriggerRegisterDeathPrefix(Unit __0)
        {
            Npc npc = __0 as Npc;
            return !IsDynamicPressureSpawnedNpc(npc);
        }

        private static bool IsDynamicPressureSpawnedNpc(Npc npc)
        {
            return npc != null && npc.GetComponent<DynamicPressureSpawnMarker>() != null;
        }

        private static void OnNpcDiePostfix(Npc __instance)
        {
            if (_instance == null || __instance == null)
            {
                return;
            }

            _instance.HandleNpcDeath(__instance);
        }

        private void HandleNpcDeath(Npc npc)
        {
            int id = npc.GetInstanceID();
            if (_deadNpcIds.Contains(id))
            {
                return;
            }

            _deadNpcIds.Add(id);

            bool isMod = npc.GetComponent<DynamicPressureSpawnMarker>() != null;
            bool hostile = IsHostileToPlayer(npc);

            if (!hostile)
            {
                return;
            }

            if (isMod)
            {
                _lastModKillTime = Time.time;
                AddEvent("Mod NPC died: " + npc.name);
            }
            else
            {
                _lastOriginalKillTime = Time.time;
                _spawnedSinceLastOriginalKill = 0;
                AddEvent("Original NPC died: " + npc.name);
            }
        }

        private static void OnLevelTransitionPrefix()
        {
            if (_instance == null)
            {
                return;
            }

            _instance._inLevelTransition = true;
            _instance.ResetRuntimeState("Level transition");
        }

        private void TryPatchTransitions()
        {
            MethodInfo prefix = AccessTools.Method(typeof(DynamicPressurePlugin), nameof(OnLevelTransitionPrefix));

            TryPatchMethod(typeof(NextLevelTrigger), "MakeTransition", prefix);
            TryPatchMethod(typeof(GameManager), "CompleteLevel", prefix);
            TryPatchAllNamedMethods(typeof(GameManager), "GoToLevel", prefix);
            TryPatchAllNamedMethods(typeof(GameManager), "GoToChurchHub", prefix);
            TryPatchAllNamedMethods(typeof(GameManager), "GoToCarHub", prefix);

            // GameManager.SwitchLevel is deliberately not in that list. The actual level swap lives
            // in the private coroutine SwitchLevelRoutine, and every way in already goes through a
            // target above: GoToChurchHub and GoToCarHub both delegate to GoToLevel, and the two
            // direct StartCoroutine(SwitchLevelRoutine(...)) calls are inside the routine that
            // CompleteLevel starts. Asking for a public "SwitchLevel" only produced a startup
            // warning about a method that has never existed in any shipped build.
        }

        /// <summary>
        /// Records when the player last took damage. Unit.ReceiveDamage returns true only once the
        /// hit has survived armour, mitigation, invulnerability and the player's i-frame window and
        /// has been applied to health, which is exactly the "player is under fire" signal the
        /// safety gate wants.
        /// </summary>
        private static void OnUnitReceiveDamagePostfix(Unit __instance, bool __result)
        {
            if (__result && __instance != null && __instance.isPlayer)
            {
                _playerLastDamageTime = Time.time;
            }
        }

        /// <summary>
        /// Patches the single damage entry point every hit funnels into. The other overload builds
        /// a DamageSourceData and calls this one, and Npc's override ends in base.ReceiveDamage, so
        /// one patch on Unit sees every unit's damage; the postfix filters down to the player.
        /// </summary>
        private void TryPatchPlayerDamageTimer()
        {
            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(Unit),
                    "ReceiveDamage",
                    new[] { typeof(float), typeof(DamageSourceData), typeof(Hitmesh.Data), typeof(Vector3?) }
                );
                MethodInfo postfix = AccessTools.Method(
                    typeof(DynamicPressurePlugin),
                    nameof(OnUnitReceiveDamagePostfix)
                );

                if (target == null || postfix == null)
                {
                    Logger.LogWarning(
                        "Failed to patch Unit.ReceiveDamage: method not found. The RecentDamage "
                        + "safety hold is inactive - waves will not wait out incoming player damage."
                    );
                    return;
                }

                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                _playerDamageTimerActive = true;
                Log("Patched Unit.ReceiveDamage (player damage timer).");
            }
            catch (Exception e)
            {
                Logger.LogWarning(
                    "Failed to patch Unit.ReceiveDamage: " + e.Message
                    + " | The RecentDamage safety hold is inactive."
                );
            }
        }

        private void TryPatchNpcDie()
        {
            try
            {
                MethodInfo target = AccessTools.Method(typeof(Npc), "Die");
                MethodInfo postfix = AccessTools.Method(typeof(DynamicPressurePlugin), nameof(OnNpcDiePostfix));

                if (target != null && postfix != null)
                {
                    _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                    Log("Patched Npc.Die.");
                }
                else
                {
                    Logger.LogWarning("Failed to patch Npc.Die: method not found.");
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("Failed to patch Npc.Die: " + e.Message);
            }
        }

        private void TryPatchAllDeadTrigger()
        {
            try
            {
                MethodInfo checkAllDead = AccessTools.Method(
                    typeof(AllDeadTrigger),
                    "CheckAllDead",
                    Type.EmptyTypes
                );
                MethodInfo registerDeath = AccessTools.Method(
                    typeof(AllDeadTrigger),
                    "RegisterDeath",
                    new[] { typeof(Unit) }
                );
                MethodInfo checkPrefix = AccessTools.Method(
                    typeof(DynamicPressurePlugin),
                    nameof(OnAllDeadTriggerCheckAllDeadPrefix)
                );
                MethodInfo registerPrefix = AccessTools.Method(
                    typeof(DynamicPressurePlugin),
                    nameof(OnAllDeadTriggerRegisterDeathPrefix)
                );

                if (checkAllDead == null || registerDeath == null ||
                    checkPrefix == null || registerPrefix == null)
                {
                    Logger.LogWarning(
                        "Failed to patch AllDeadTrigger: expected methods were not found."
                    );
                    return;
                }

                _harmony.Patch(checkAllDead, prefix: new HarmonyMethod(checkPrefix));
                _harmony.Patch(registerDeath, prefix: new HarmonyMethod(registerPrefix));
                Log("Patched AllDeadTrigger progression filtering.");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Failed to patch AllDeadTrigger: " + e.Message);
            }
        }

        private void TryPatchMethod(Type type, string methodName, MethodInfo prefix)
        {
            try
            {
                MethodInfo target = AccessTools.Method(type, methodName);
                if (target == null)
                {
                    Logger.LogWarning("Patch skipped: " + type.Name + "." + methodName);
                    return;
                }

                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                Log("Patched " + type.Name + "." + methodName);
            }
            catch (Exception e)
            {
                Logger.LogWarning("Patch failed: " + type.Name + "." + methodName + " / " + e.Message);
            }
        }

        private void TryPatchAllNamedMethods(Type type, string methodName, MethodInfo prefix)
        {
            try
            {
                MethodInfo[] methods = AccessTools.GetDeclaredMethods(type)
                    .Where(m => m.Name == methodName)
                    .ToArray();

                if (methods.Length == 0)
                {
                    Logger.LogWarning("Patch skipped: " + type.Name + "." + methodName);
                    return;
                }

                for (int i = 0; i < methods.Length; i++)
                {
                    _harmony.Patch(methods[i], prefix: new HarmonyMethod(prefix));
                }

                Log("Patched " + type.Name + "." + methodName + " x" + methods.Length);
            }
            catch (Exception e)
            {
                Logger.LogWarning("Patch failed: " + type.Name + "." + methodName + " / " + e.Message);
            }
        }

        private void AddEvent(string text)
        {
            _recentEvents.Add(DateTime.Now.ToString("HH:mm:ss") + " " + text);
            while (_recentEvents.Count > 8)
            {
                _recentEvents.RemoveAt(0);
            }
        }

        private void Log(string text)
        {
            Logger.LogInfo(text);
        }

        private static string Label(string key, string english)
        {
            return OverlayPalette.Tint(OverlayPalette.Label, OverlayText.Get(key, english) + ": ");
        }

        private static string Header(string key, string english)
        {
            return OverlayPalette.Tint(OverlayPalette.Header, OverlayText.Get(key, english));
        }

        private static string Separator()
        {
            return OverlayPalette.Tint(OverlayPalette.Muted, "  |  ");
        }

        /// <summary>
        /// Colour for a decision word: a wave that placed enemies reads green, a wave that was
        /// held back reads amber, and a wave that tried and failed reads red.
        /// </summary>
        private static string TintDecision(string decision)
        {
            if (decision == "SPAWNED")
            {
                return OverlayPalette.Tint(OverlayPalette.Good, decision);
            }

            if (decision == "FAILED")
            {
                return OverlayPalette.Tint(OverlayPalette.Bad, decision);
            }

            if (decision == "BLOCKED")
            {
                return OverlayPalette.Tint(OverlayPalette.Warn, decision);
            }

            return OverlayPalette.Tint(OverlayPalette.Muted, decision);
        }

        /// <summary>
        /// Colour for a block reason. The reasons raised from inside a wave mean the wave ran its
        /// full cost and still placed nothing, so they read red; the reasons that stop a wave
        /// before it starts are ordinary pacing and read amber.
        /// </summary>
        private static string TintReason(SpawnBlockReason reason)
        {
            if (reason == SpawnBlockReason.None)
            {
                return OverlayPalette.Tint(OverlayPalette.Good, reason);
            }

            if (reason == SpawnBlockReason.RoomSpawnBudgetExceeded
                || reason == SpawnBlockReason.NoValidNpcSpawnPoint
                || reason == SpawnBlockReason.NoCandidateUnits
                || reason == SpawnBlockReason.SpawnAsyncFailed
                || reason == SpawnBlockReason.UpdateFailed)
            {
                return OverlayPalette.Tint(OverlayPalette.Bad, reason);
            }

            return OverlayPalette.Tint(OverlayPalette.Warn, reason);
        }

        private void OnGUI()
        {
            if (!_enableOverlay.Value)
            {
                return;
            }

            if (_overlayStyle == null)
            {
                _overlayStyle = new GUIStyle(GUI.skin.label);
                _overlayStyle.richText = true;
            }

            // The panel grows with the window instead of being a fixed 620x620, and its body
            // scrolls, so a long recent-events list or a language whose labels wrap onto extra
            // lines cannot push content out of sight.
            const float margin = 20f;
            const float top = 120f;
            float width = Mathf.Min(680f, Screen.width - margin * 2f);
            float height = Mathf.Max(240f, Screen.height - top - margin);

            Rect rect = new Rect(margin, top, width, height);
            GUI.Box(rect, OverlayText.Get("overlay.title", "Dynamic Pressure Debug"));

            GUILayout.BeginArea(new Rect(rect.x + 10f, rect.y + 25f, rect.width - 20f, rect.height - 35f));
            _overlayScroll = GUILayout.BeginScrollView(_overlayScroll);

            Row(Label("overlay.enabled", "Enabled") + OverlayPalette.Flag(_enableMod.Value, true)
                + Separator() + Label("overlay.autoSpawn", "AutoSpawn") + OverlayPalette.Flag(_enableAutoSpawn.Value, true)
                + Separator() + Label("overlay.style", "Style") + OverlayPalette.Tint(OverlayPalette.Accent, GetStyle()));

            Row(Label("overlay.gameState", "GameState")
                + OverlayPalette.Tint(_snapshot.gameState == "Running" ? OverlayPalette.Good : OverlayPalette.Warn, _snapshot.gameState)
                + Separator() + Label("overlay.safeZone", "SafeZone") + OverlayPalette.Flag(_snapshot.inSafeZone, false)
                + Separator() + Label("overlay.env", "Env") + OverlayPalette.Tint(OverlayPalette.Value, _snapshot.currentEnvironment));

            string hpHex = OverlayPalette.Good;
            if (_snapshot.playerHp <= _lowHealthStopThreshold.Value)
            {
                hpHex = OverlayPalette.Bad;
            }
            else if (_snapshot.playerHp < 0.5f)
            {
                hpHex = OverlayPalette.Warn;
            }

            Row(Label("overlay.player", "Player")
                + (_snapshot.hasPlayer
                    ? OverlayPalette.Tint(OverlayPalette.Good, OverlayText.Get("overlay.ok", "OK"))
                    : OverlayPalette.Tint(OverlayPalette.Bad, OverlayText.Get("overlay.missing", "Missing")))
                + Separator() + Label("overlay.alive", "Alive") + OverlayPalette.Flag(_snapshot.playerAlive, true)
                + Separator() + Label("overlay.hp", "HP") + OverlayPalette.Tint(hpHex, Mathf.RoundToInt(_snapshot.playerHp * 100f) + "%")
                + Separator() + Label("overlay.damageAgo", "DamageAgo")
                + (_playerDamageTimerActive
                    ? OverlayPalette.Tint(
                        _snapshot.playerTimeSinceDamage <= _recentDamageStopWindow.Value ? OverlayPalette.Warn : OverlayPalette.Value,
                        _snapshot.playerTimeSinceDamage >= NeverDamagedSeconds
                            ? OverlayText.Get("overlay.never", "never")
                            : _snapshot.playerTimeSinceDamage.ToString("0.0") + "s")
                    : OverlayPalette.Tint(OverlayPalette.Bad, OverlayText.Get("overlay.unavailable", "n/a"))));

            if (_lastUpdateFailure != null)
            {
                Row(Label("overlay.updateFailure", "Update Error")
                    + OverlayPalette.Tint(OverlayPalette.Bad, _lastUpdateFailure)
                    + OverlayPalette.Tint(OverlayPalette.Muted, " x" + _updateFailureCount));
            }

            Row(Label("overlay.room", "Room") + OverlayPalette.Tint(OverlayPalette.Value, _snapshot.playerRoomName)
                + Separator() + Label("overlay.endRoom", "EndRoom") + OverlayPalette.Flag(_snapshot.playerRoomIsEndRoom, false));

            GUILayout.Space(8f);
            Row(Header("overlay.section.pressure", "Pressure"));
            Row(Label("overlay.originalHostileAlive", "Original Hostile Alive") + OverlayPalette.Count(_snapshot.originalHostileAlive)
                + Separator() + Label("overlay.engaged", "Engaged") + OverlayPalette.Count(_snapshot.originalEngagedHostiles)
                + Separator() + Label("overlay.targeting", "Targeting") + OverlayPalette.Count(_snapshot.originalTargetingPlayer));

            Row(Label("overlay.modHostileAlive", "Mod Hostile Alive") + OverlayPalette.Budget(_snapshot.modHostileAlive, GetMaxModAlive())
                + Separator() + Label("overlay.targeting", "Targeting") + OverlayPalette.Count(_snapshot.modTargetingPlayer));

            Row(Label("overlay.originalPressure", "Original Pressure") + OverlayPalette.Count(_snapshot.originalPressure)
                + Separator() + Label("overlay.modPressure", "Mod Pressure") + OverlayPalette.Count(_snapshot.modPressure)
                + Separator() + Label("overlay.current", "Current")
                + OverlayPalette.Tint(OverlayPalette.Value, _snapshot.currentPressure)
                + OverlayPalette.Tint(OverlayPalette.Muted, " / " + _snapshot.targetPressure)
                + Separator() + Label("overlay.deficit", "Deficit")
                + OverlayPalette.Tint(_snapshot.deficit > 0 ? OverlayPalette.Warn : OverlayPalette.Good, _snapshot.deficit));

            GUILayout.Space(8f);
            Row(Header("overlay.section.spawnDecision", "Spawn Decision"));
            Row(Label("overlay.lastDecision", "Last Decision") + TintDecision(_lastDecision)
                + Separator() + Label("overlay.reason", "Reason") + TintReason(_lastBlockReason));

            Row(Label("overlay.nextSpawnIn", "Next Spawn In")
                + OverlayPalette.Tint(_snapshot.nextSpawnIn > 0f ? OverlayPalette.Value : OverlayPalette.Muted,
                    _snapshot.nextSpawnIn.ToString("0.0") + "s")
                + Separator() + Label("overlay.candidateSource", "Candidate Source")
                + OverlayPalette.Tint(OverlayPalette.Value, _snapshot.candidateSource)
                + Separator() + Label("overlay.candidates", "Candidates")
                + OverlayPalette.Tint(_snapshot.candidateCount > 0 ? OverlayPalette.Value : OverlayPalette.Bad, _snapshot.candidateCount));

            GUILayout.Space(8f);
            Row(Header("overlay.section.modImpact", "Mod Impact"));
            Row(Label("overlay.spawnedThisLevel", "Spawned This Level") + OverlayPalette.Budget(_spawnedThisLevel, GetMaxModPerLevel()));
            Row(Label("overlay.spawnedSinceOriginalKill", "Spawned Since Last Original Kill") + OverlayPalette.Count(_spawnedSinceLastOriginalKill));
            Row(Label("overlay.timeSinceOriginalKill", "Time Since Original Kill")
                + OverlayPalette.Tint(OverlayPalette.Value, _snapshot.timeSinceLastOriginalKill.ToString("0.0") + "s")
                + Separator() + Label("overlay.timeSinceModKill", "Time Since Mod Kill")
                + OverlayPalette.Tint(OverlayPalette.Value, _snapshot.timeSinceLastModKill.ToString("0.0") + "s"));
            Row(Label("overlay.wouldDelayAllDead", "Would Delay OnAllEnemiesDead") + OverlayPalette.Flag(_snapshot.wouldDelayOnAllEnemiesDead, false));
            Row(Label("overlay.lastSpawn", "Last Spawn") + OverlayPalette.Tint(OverlayPalette.Value, _lastSpawnSummary));
            Row(Label("overlay.lastAggroReport", "Last Aggro Report") + OverlayPalette.Tint(OverlayPalette.Value, _lastAggroReport));

            GUILayout.Space(8f);
            Row(OverlayPalette.Tint(OverlayPalette.Muted,
                OverlayText.Get("overlay.keys", "Keys: F8 Manual Spawn | F9 Toggle Overlay")));

            GUILayout.Space(8f);
            Row(Header("overlay.section.recentEvents", "Recent Events"));
            for (int i = 0; i < _recentEvents.Count; i++)
            {
                Row(OverlayPalette.Tint(OverlayPalette.Muted, _recentEvents[i]));
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void Row(string text)
        {
            GUILayout.Label(text, _overlayStyle);
        }

        /// <summary>
        /// Subscribes the overlay to the game's own language change event, so the strings follow
        /// the player's language without anything polling for it.
        /// </summary>
        /// <remarks>
        /// AsyncAssetLoading is a StaticInstance that does not exist yet when the plugin awakes, so
        /// this is attempted from Update until the instance is up, then never again. The initial
        /// load happens here too, because the language is already chosen by the time we subscribe
        /// and the event only fires on later changes.
        /// </remarks>
        private void EnsureLanguageSubscription()
        {
            if (_reloadOverlayLanguage != null)
            {
                // Localisation loads asynchronously, so the first read usually lands before the
                // game knows its own language and the overlay settles on English. onLanguageChange
                // only fires on an actual change, so a session that starts in another language
                // would stay English for its whole run. Keep asking until the game answers once.
                if (OverlayText.GameCode == null && Time.unscaledTime >= _nextLanguageRetryTime)
                {
                    _nextLanguageRetryTime = Time.unscaledTime + 1f;
                    LoadOverlayLanguage();
                }

                return;
            }

            try
            {
                AsyncAssetLoading assets = StaticInstance<AsyncAssetLoading>.Instance;
                if (assets == null)
                {
                    return;
                }

                _reloadOverlayLanguage = LoadOverlayLanguage;
                assets.onLanguageChange += _reloadOverlayLanguage;
                LoadOverlayLanguage();
            }
            catch (Exception e)
            {
                _reloadOverlayLanguage = null;
                Logger.LogWarning("Overlay language will not follow the game language: " + e.Message);
            }
        }

        private void StopFollowingLanguage()
        {
            AsyncAssetLoading.OnLanguageChange reload = _reloadOverlayLanguage;
            _reloadOverlayLanguage = null;
            if (reload == null)
            {
                return;
            }

            try
            {
                AsyncAssetLoading assets = StaticInstance<AsyncAssetLoading>.Instance;
                if (assets != null)
                {
                    assets.onLanguageChange -= reload;
                }
            }
            catch (Exception)
            {
                // Nothing outlives the plugin either way.
            }
        }

        private void LoadOverlayLanguage()
        {
            string resolvedCode;
            if (!OverlayText.Reload(_pluginDirectory, out resolvedCode))
            {
                return;
            }

            // Report what was actually loaded, not just which language was picked: every lookup
            // falls back to English on its own, so a language file that does not load looks exactly
            // like a language that resolved correctly.
            string line = "Overlay language: " + resolvedCode
                + " (game language code: " + (OverlayText.GameCode ?? "not loaded yet") + ")"
                + " - " + OverlayText.LoadedCount + " strings from "
                + (OverlayText.LoadedFrom ?? _pluginDirectory);

            if (OverlayText.LoadedCount == 0)
            {
                Logger.LogWarning(line + " | " + (OverlayText.LastLoadNote ?? "no reason recorded"));
            }
            else
            {
                Logger.LogInfo(line);
            }
        }

        private struct SpawnPointChoice
        {
            public Vector3 position;
            public Room room;
            public float distanceToPlayer;
            public string source;
        }

        private sealed class Snapshot
        {
            public bool hasGameManager;
            public bool hasPlayer;
            public bool playerAlive;
            public bool inSafeZone;
            public string gameState = "Unknown";
            public string currentEnvironment = "Unknown";

            public float playerHp;
            public float playerTimeSinceDamage;
            public string playerRoomName = "null";
            public bool playerRoomIsEndRoom;

            public int aliveNpcs;
            public int originalHostileAlive;
            public int originalEngagedHostiles;
            public int nearbyOriginalHostiles;
            public int originalTargetingPlayer;

            public int modHostileAlive;
            public int nearbyModHostiles;
            public int modTargetingPlayer;

            public int originalPressure;
            public int modPressure;
            public int currentPressure;
            public int targetPressure;
            public int deficit;

            public int candidateCount;
            public string candidateSource = "None";

            public int spawnedThisLevel;
            public int spawnedSinceLastOriginalKill;
            public float timeSinceLastOriginalKill;
            public float timeSinceLastModKill;
            public float nextSpawnIn;

            public bool wouldDelayOnAllEnemiesDead;
        }
    }

    public sealed class DynamicPressureSpawnMarker : MonoBehaviour
    {
        public UnitSO sourceUnitSo;
        public float spawnTime;
        public Vector3 spawnPosition;
        public Room spawnRoom;
        public string spawnReason;
        public int waveId;

        public bool aggroReported;
        public float lastAggroReportTime;
        public bool targetingPlayerAfterReport;
        public bool hasKnownPlayerPositionAfterReport;
    }

    public enum SpawnBlockReason
    {
        None,
        Disabled,
        NoGameManager,
        NoPlayer,
        PlayerDead,
        SafeZone,
        GameStateNotRunning,
        LowHealth,
        RecentDamage,
        EndRoomBlocked,
        SpawnRoomIsEndRoom,
        NoOriginalHostiles,
        NoEngagedOriginalHostiles,
        NotEnoughOriginalHostiles,
        NoCandidateUnits,
        NoValidNpcSpawnPoint,
        Cooldown,
        MaxModSpawnedAlive,
        RoomSpawnBudgetExceeded,
        LevelSpawnBudgetExceeded,
        PressureAlreadyHigh,
        SpawnAsyncFailed,
        LevelTransition,
        RecentModKillOnly,
        NoOriginalKillProgress,
        SpawnBudgetPerOriginalExceeded,
        UpdateFailed
    }
}