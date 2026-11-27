using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using LevelGeneration;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.LevelGeneration;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.UI;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Units.AI;
using PerfectRandom.Sulfur.Core.Utilities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ryuka.Sulfur.DynamicPressure
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(CoopLinks.TogetherGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed partial class DynamicPressurePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "ryuka.sulfur.dynamicpressure";
        public const string PluginName = "Dynamic Pressure";
        public const string PluginVersion = "2.0.0";

        private const float ScanIntervalSeconds = 0.5f;
        private const float DiagLogIntervalSeconds = 5.0f;

        private const float NearbyThreatDistance = 40f;
        private const float ModUnitTrackedDistance = 60f;
        private const float OriginalThreatWeight = 0.5f;

        // Used when a level has no vanilla enemy generation to measure.
        private const float FallbackPackBudget = 12f;
        private const float FallbackPackDensity = 1f;

        private static DynamicPressurePlugin _instance;
        private static int _levelGeneration;
        private static int _capturedGeneration = -1;
        private static float _capturedPackBudget;
        private static float _capturedPackDensity;

        private Harmony _harmony;
        private Settings _settings;
        private Director _director;
        private readonly BossHealthScaler _bossHealth = new BossHealthScaler();
        private ICoopLink _coop;
        private CoopSync _coopSync;

        private readonly List<string> _recentEvents = new List<string>();

        private int _seenGeneration;
        private bool _levelStarted;
        private PressureStyle _appliedStyle;
        private DirectorTuning _appliedTuning;
        private float _packBudget;
        private bool _lateGame;
        private float _nextScanTime;
        private float _lastTickTime;
        private float _nextDiagLogTime;
        private int _diagWindowBatches;
        private int _diagWindowSpawned;
        private string _pluginDirectory;
        private float _nextLanguageRetryTime;
        private GUIStyle _overlayStyle;
        private Vector2 _overlayScroll;
        private AsyncAssetLoading.OnLanguageChange _reloadOverlayLanguage;
        private bool _spawnInProgress;
        private bool _inLevelTransition;
        private bool _hitPatchActive;
        private bool _pendingMigrationNotice;
        private float _pendingSurgeBudget;
        private int _waveId;

        private string _lastUpdateFailure;
        private int _updateFailureCount;

        private Snapshot _snapshot = new Snapshot();
        private SpawnBlockReason _lastBlockReason = SpawnBlockReason.None;
        private string _lastDecision = "None";
        private string _lastSpawnSummary = "None";

        private PressurePreset CurrentPreset
        {
            get { return PressurePreset.For(_settings.EffectiveStyle); }
        }

        private void Awake()
        {
            _instance = this;
            _settings = Settings.Bind(Config);
            _pendingMigrationNotice = ConfigMigration.Run(Config, _settings, Logger);
            _director = new Director((min, max) => UnityEngine.Random.Range(min, max));
            CreateRecorder();
            _bossHealth.BeginLevel(ConfiguredBossHealth());

            // In co-op only the host's director adds enemies, and the session copies them to
            // everyone else. Declaring this as their spawner is harmless offline.
            _coop = CoopLinks.Create(Logger);
            _coop.DeclareSpawner(this);
            _coopSync = new CoopSync(_coop, _bossHealth, Logger);

            // The loader searches lang/ under here and, for managers that flatten a package, this
            // folder itself.
            _pluginDirectory = Path.GetDirectoryName(Info.Location) ?? string.Empty;

            _harmony = new Harmony(PluginGuid);
            TryPatchTransitions();
            TryPatchAllDeadTrigger();
            TryPatchPlayerHits();
            TryPatchNpcDeath();
            TryPatchLevelBudget();
            TryPatchBossSpawn();

            Log("Loaded.");
        }

        private void OnDestroy()
        {
            StopFollowingLanguage();
            RecordLevelEnd();
            _recorder?.Stop();
            _coop?.Dispose();

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

        private void Update()
        {
            HandleKeys();
            EnsureLanguageSubscription();

            // Kept apart: the recorder is diagnostics, and a failure there must not stop co-op.
            try
            {
                _coopSync.Tick();
            }
            catch (Exception e)
            {
                ReportUpdateFailure(e);
            }

            try
            {
                SyncRecording();
            }
            catch (Exception e)
            {
                ReportUpdateFailure(e);
            }

            if (!_settings.EnableMod.Value)
            {
                _lastDecision = "BLOCKED";
                _lastBlockReason = SpawnBlockReason.Disabled;
                return;
            }

            try
            {
                UpdateDirector();
            }
            catch (Exception e)
            {
                ReportUpdateFailure(e);
            }

            LogDirectorDiagnostics();
        }

        private void UpdateDirector()
        {
            GameManager gm = StaticInstance<GameManager>.Instance;
            if (_seenGeneration != _levelGeneration)
            {
                _seenGeneration = _levelGeneration;
                ResetLevelState("New level");
                _inLevelTransition = false;
            }

            // The host owns the world in co-op; a client's own director would add enemies only
            // that client can see.
            if (_coopSync.Role == CoopRole.Client)
            {
                if (_levelStarted)
                {
                    ResetLevelState("Joined a co-op session");
                }

                _lastDecision = "BLOCKED";
                _lastBlockReason = SpawnBlockReason.CoopClient;
                return;
            }

            if (Time.time < _nextScanTime)
            {
                return;
            }

            _nextScanTime = Time.time + ScanIntervalSeconds;

            if (!_levelStarted && gm != null && CanBeginLevel(gm))
            {
                BeginLevel(gm);
            }
            else if (_levelStarted && (_appliedStyle != _settings.EffectiveStyle || TuningChanged()))
            {
                _appliedStyle = _settings.EffectiveStyle;
                _appliedTuning = _settings.Tuning;
                _director.ChangeStyle(CurrentPreset, _appliedTuning);
            }

            Snapshot snapshot = ScanWorld(gm);
            _snapshot = snapshot;

            float delta = _lastTickTime > 0f ? Time.time - _lastTickTime : 0f;
            _lastTickTime = Time.time;

            DirectorOrders orders = _director.Tick(new DirectorInput
            {
                Now = Time.time,
                DeltaSeconds = delta,
                SpawningAllowed = snapshot.spawningAllowed,
                Engaged = snapshot.engaged,
                PlayerHealthFraction = snapshot.hasPlayer ? snapshot.playerHp : 1f,
                RouteRoomIndex = snapshot.routeRoomIndex,
                WeightedThreat = snapshot.weightedThreat,
                ModAliveCost = snapshot.modAliveCost
            });

            CarryOutOrders(orders);
            RecordTick(snapshot, delta);
        }

        private bool TuningChanged()
        {
            DirectorTuning current = _settings.Tuning;
            return !Mathf.Approximately(current.Reinforcement, _appliedTuning.Reinforcement)
                || !Mathf.Approximately(current.Rest, _appliedTuning.Rest);
        }

        private void CarryOutOrders(DirectorOrders orders)
        {
            if (!_snapshot.spawningAllowed)
            {
                _pendingSurgeBudget = 0f;
                return;
            }

            if (orders.SurgeWarning)
            {
                ShowAlert(OverlayText.Get("alert.surge", "Something is coming..."));
                AddEvent("Surge warned");
            }

            // The player was warned, so a surge waits out a batch still placing rather than vanish.
            _pendingSurgeBudget += orders.SurgeBudget;

            if (_spawnInProgress)
            {
                return;
            }

            if (_pendingSurgeBudget > 0f)
            {
                float surgeBudget = _pendingSurgeBudget;
                _pendingSurgeBudget = 0f;
                _ = SpawnBatchAsync(surgeBudget, SurgeMaxUnits, true, false, "Surge");
            }
            else if (orders.TrickleBudget > 0f)
            {
                _ = SpawnBatchAsync(orders.TrickleBudget, TrickleMaxUnits, false, false, "Trickle");
            }
        }

        private bool CanBeginLevel(GameManager gm)
        {
            // The old player object is destroyed before generation starts, so a live player after
            // a transition belongs to the new level.
            return !_inLevelTransition
                && gm.PlayerUnit != null
                && gm.graphContext != null
                && gm.orderedRooms != null
                && gm.orderedRooms.Count > 0
                && gm.gameState.ToString() == "Running"
                && !gm.awaitingStartLevel;
        }

        private void BeginLevel(GameManager gm)
        {
            List<Room> rooms = gm.orderedRooms;
            int spawnableRooms = 0;
            int routeRooms = 0;

            for (int i = 0; i < rooms.Count; i++)
            {
                Room room = rooms[i];
                if (room == null)
                {
                    continue;
                }

                routeRooms = Mathf.Max(routeRooms, room.lodRoomIndex + 1);

                if (room.roomSize != RoomSize.Corridor && !room.disallowEnemySpawn)
                {
                    spawnableRooms++;
                }
            }

            bool measured = _capturedGeneration == _levelGeneration && _capturedPackBudget > 0f;
            _packBudget = measured ? _capturedPackBudget : FallbackPackBudget;
            float density = measured ? _capturedPackDensity : FallbackPackDensity;

            _lateGame = IsLateGame(gm);
            _appliedStyle = _settings.EffectiveStyle;
            _appliedTuning = _settings.Tuning;
            PressurePreset preset = CurrentPreset;

            List<UnitSO> eligible = new List<UnitSO>();
            Roster.CollectEligible(gm, preset, _packBudget, _lateGame, _settings.EliteMultiplier.Value, eligible);

            float averageCost = eligible.Count > 0 ? (float)eligible.Average(u => u.SpawnCost) : 0f;

            _director.StartLevel(
                new LevelCalibration
                {
                    PackBudget = _packBudget,
                    PackDensity = density,
                    SpawnableRooms = spawnableRooms,
                    RouteRooms = routeRooms,
                    AverageUnitCost = averageCost
                },
                preset,
                _appliedTuning);

            _levelStarted = true;
            _lastTickTime = 0f;

            // Load them now so the first surge does not hitch in the middle of a fight.
            for (int i = 0; i < eligible.Count; i++)
            {
                eligible[i].FetchAndLoadUnitLoader();
            }

            RememberLevelCalibration(density, spawnableRooms, routeRooms, averageCost, measured);
            RecordLevelStart(gm, false);

            if (_pendingMigrationNotice)
            {
                _pendingMigrationNotice = false;
                ShowAlert(OverlayText.Get(
                    "alert.migrated",
                    "Dynamic Pressure: your old tuning values were replaced by the new director."));
            }

            AddEvent("Level start: budget " + _director.TotalBudget.ToString("0") + (measured ? string.Empty : " (estimated)"));

            if (_settings.LogSpawnDecisions.Value)
            {
                Logger.LogInfo(
                    "Level start | style " + _appliedStyle
                    + " | packBudget " + _packBudget + (measured ? string.Empty : " (fallback)")
                    + " | density " + density
                    + " | rooms " + spawnableRooms + "/" + routeRooms
                    + " | eligible " + eligible.Count + " avg " + averageCost.ToString("0.0")
                    + " | total budget " + _director.TotalBudget.ToString("0")
                    + " | target " + _director.TargetThreat.ToString("0.0")
                    + " | late " + _lateGame);
            }
        }

        private static bool IsLateGame(GameManager gm)
        {
            if (gm.environmentsInOrder == null || gm.currentEnvironment == null)
            {
                return false;
            }

            List<WorldEnvironmentIds> order = gm.environmentsInOrder.environmentsOnOrder;
            int current = order.IndexOf(gm.currentEnvironment.id);
            int firstLate = order.IndexOf(WorldEnvironmentIds.Act_02_Forest);
            return current >= 0 && firstLate >= 0 && current >= firstLate;
        }

        private static void ShowAlert(string text)
        {
            UIManager ui = StaticInstance<UIManager>.Instance;
            if (ui != null)
            {
                ui.ShowSmallAlert(text);
            }
        }

        private void HandleKeys()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (_settings.OverlayToggleKey.Value != Key.None
                && keyboard[_settings.OverlayToggleKey.Value].wasPressedThisFrame)
            {
                _settings.EnableOverlay.Value = !_settings.EnableOverlay.Value;
            }

            if (_settings.EnableOverlay.Value
                && _settings.ManualSpawnKey.Value != Key.None
                && keyboard[_settings.ManualSpawnKey.Value].wasPressedThisFrame
                && !_spawnInProgress
                && _coopSync.Role != CoopRole.Client)
            {
                _ = SpawnBatchAsync(0f, 1, false, true, "Manual debug spawn");
            }

            if (_settings.ExportCombatDataKey.Value != Key.None
                && keyboard[_settings.ExportCombatDataKey.Value].wasPressedThisFrame)
            {
                ExportCombatData();
            }
        }

        private Snapshot ScanWorld(GameManager gm)
        {
            Snapshot s = new Snapshot();

            if (gm == null)
            {
                s.blockReason = SpawnBlockReason.NoGameManager;
                return s;
            }

            s.hasGameManager = true;
            s.gameState = gm.gameState.ToString();
            s.inSafeZone = gm.InSafeZone;
            s.currentEnvironment = gm.currentEnvironment != null ? gm.currentEnvironment.id.ToString() : "null";

            if (gm.PlayerUnit == null || gm.PlayerObject == null)
            {
                s.blockReason = SpawnBlockReason.NoPlayer;
                return s;
            }

            Unit player = gm.PlayerUnit;
            Room playerRoom = GetUnitRoom(player);

            s.hasPlayer = true;
            s.playerAlive = player.IsAlive;
            s.playerHp = SafeGetPlayerHp(player);
            s.playerRoomName = playerRoom != null ? playerRoom.name : "null";
            s.routeRoomIndex = playerRoom != null ? playerRoom.lodRoomIndex : 0;

            bool bossFight = false;
            List<Npc> alive = gm.aliveNpcs;

            if (alive != null)
            {
                for (int i = 0; i < alive.Count; i++)
                {
                    Npc npc = alive[i];
                    if (!IsActiveThreat(npc))
                    {
                        continue;
                    }

                    bool targeting = SafeTargetIsPlayer(npc);
                    bool knows = SafeHasKnownPlayerPosition(npc, player);

                    if (npc.IsBoss)
                    {
                        bossFight |= targeting || npc.hasBeenAggroedOnce;
                        continue;
                    }

                    float distance = Vector3.Distance(npc.transform.position, gm.PlayerPosition);
                    DynamicPressureSpawnMarker marker = npc.GetComponent<DynamicPressureSpawnMarker>();
                    bool isMod = marker != null;

                    if (isMod && targeting && marker.firstTargetTime < 0f)
                    {
                        marker.firstTargetTime = Time.time;
                    }

                    int cost = npc.unitSO.SpawnCost;

                    if (isMod && (distance <= ModUnitTrackedDistance || targeting))
                    {
                        s.modAliveCost += cost;
                        s.modAlive++;
                    }

                    bool present = IsSameOrConnectedRoom(GetUnitRoom(npc), playerRoom)
                        || (targeting && distance <= NearbyThreatDistance);

                    if (!present)
                    {
                        continue;
                    }

                    s.weightedThreat += isMod ? cost : cost * OriginalThreatWeight;
                    s.threatsPresent++;
                    s.engaged |= targeting || knows;
                }
            }

            s.blockReason = FindBlockReason(gm, player, playerRoom, bossFight);
            s.spawningAllowed = s.blockReason == SpawnBlockReason.None;

            if (!s.spawningAllowed)
            {
                _lastDecision = "BLOCKED";
                _lastBlockReason = s.blockReason;
            }

            return s;
        }

        private SpawnBlockReason FindBlockReason(GameManager gm, Unit player, Room playerRoom, bool bossFight)
        {
            if (_inLevelTransition)
            {
                return SpawnBlockReason.LevelTransition;
            }

            if (!_levelStarted)
            {
                return SpawnBlockReason.WaitingForLevel;
            }

            if (gm.gameState.ToString() != "Running" || gm.awaitingStartLevel)
            {
                return SpawnBlockReason.GameStateNotRunning;
            }

            if (gm.InSafeZone || gm.IsPlayerAtChurch || gm.IsOnboarding)
            {
                return SpawnBlockReason.SafeZone;
            }

            if (gm.IsEndless || CryptUI.IsRunning)
            {
                return SpawnBlockReason.ChallengeMode;
            }

            if (!player.IsAlive)
            {
                return SpawnBlockReason.PlayerDead;
            }

            if (bossFight)
            {
                return SpawnBlockReason.BossFight;
            }

            if (playerRoom != null && playerRoom.IsEndRoom)
            {
                return SpawnBlockReason.EndRoomBlocked;
            }

            return SpawnBlockReason.None;
        }

        private static bool IsActiveThreat(Npc npc)
        {
            return npc != null
                && npc.IsAlive
                && npc.unitSO != null
                && npc.gameObject.activeInHierarchy
                && !npc.unitSO.isCivilian
                && npc.unitSO.id.value != UnitIds.BlackGuildMachinegunEmplacement.value
                && IsHostileToPlayer(npc);
        }

        private static bool IsHostileToPlayer(Npc npc)
        {
            try
            {
                return npc.IsHostileTo(FactionIds.Player);
            }
            catch
            {
                return false;
            }
        }

        private static bool SafeTargetIsPlayer(Npc npc)
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

        private static bool SafeHasKnownPlayerPosition(Npc npc, Unit playerUnit)
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

        private static float SafeGetPlayerHp(Unit playerUnit)
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

        private static Room GetUnitRoom(Unit unit)
        {
            if (unit == null)
            {
                return null;
            }

            return unit.currentRoom != null ? unit.currentRoom : unit.lastValidCurrentRoom;
        }

        private static bool IsSameOrConnectedRoom(Room npcRoom, Room playerRoom)
        {
            if (npcRoom == null || playerRoom == null)
            {
                return false;
            }

            if (npcRoom == playerRoom)
            {
                return true;
            }

            return playerRoom.connectedRooms != null && Array.IndexOf(playerRoom.connectedRooms, npcRoom) >= 0;
        }

        /// <summary>
        /// A game update that removes or renames something read every frame used to take the whole
        /// plugin down with it and bury the player's log. Report a given failure once, count the
        /// repeats, and leave the overlay and the keys alive so the cause is still visible in game.
        /// </summary>
        private void ReportUpdateFailure(Exception e)
        {
            string signature = e.GetType().Name + ": " + e.Message;

            if (signature != _lastUpdateFailure)
            {
                _lastUpdateFailure = signature;
                _updateFailureCount = 0;
                AddEvent("Director update failed: " + e.GetType().Name);
                Logger.LogError("Director update failed. Repeats of this same failure are counted, not logged.");
                Logger.LogError(e);
            }

            _updateFailureCount++;
            _lastDecision = "FAILED";
            _lastBlockReason = SpawnBlockReason.UpdateFailed;
        }

        /// <summary>
        /// Rate-limited summary of what the director is doing. Purely observational: nothing here
        /// feeds a decision, and with the setting off the method costs one bool read per frame.
        /// </summary>
        private void LogDirectorDiagnostics()
        {
            if (!_settings.LogSpawnDecisions.Value)
            {
                _nextDiagLogTime = Time.time + DiagLogIntervalSeconds;
                _diagWindowBatches = 0;
                _diagWindowSpawned = 0;
                return;
            }

            if (Time.time < _nextDiagLogTime)
            {
                return;
            }

            _nextDiagLogTime = Time.time + DiagLogIntervalSeconds;

            Logger.LogInfo(
                "Director " + _director.Phase
                + " | intensity " + _director.Intensity.ToString("0.00")
                + " | pace " + _director.Pace.ToString("0.00")
                + " | threat " + _snapshot.weightedThreat.ToString("0.0") + "/" + _director.TargetThreat.ToString("0.0")
                + " | mod alive " + _snapshot.modAliveCost + "/" + _director.AliveCap.ToString("0")
                + " | spent " + _director.SpentThreat.ToString("0") + "/" + _director.UnlockedBudget.ToString("0")
                + "/" + _director.TotalBudget.ToString("0")
                + " | batches " + _diagWindowBatches + " placed " + _diagWindowSpawned
                + " | last " + _lastDecision + "/" + _lastBlockReason);

            _diagWindowBatches = 0;
            _diagWindowSpawned = 0;
        }

        private void ResetLevelState(string reason)
        {
            RecordLevelEnd();
            _recentEvents.Clear();
            _waveId = 0;
            _levelStarted = false;
            _lastTickTime = 0f;
            _pendingSurgeBudget = 0f;
            _lastDecision = "Reset";
            _lastBlockReason = SpawnBlockReason.None;
            _lastSpawnSummary = "None";
            AddEvent("Reset: " + reason);
        }

        private static void OnSpawnEnemiesExecutePrefix(SpawnEnemiesNode __instance)
        {
            if (__instance == null)
            {
                return;
            }

            // Kept against the generation it belongs to: a level with no enemy generation must not
            // inherit the numbers of the level before it.
            _capturedGeneration = _levelGeneration;
            _capturedPackBudget = __instance.packBudget;
            _capturedPackDensity = __instance.packDensity;
        }

        /// <summary>
        /// Dynamic Pressure enemies are ambient pressure additions, not scripted encounter
        /// objectives. AllDeadTrigger can capture later-spawned NPCs from GameManager.aliveNpcs,
        /// including NPCs outside the encounter room. Remove marked NPCs immediately before the
        /// original completion check.
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
        /// AllDeadTrigger subscribes RegisterDeath to NPCs captured after Start. Ignore a stale
        /// callback from a Dynamic Pressure NPC after it has already been excluded from the
        /// encounter's tracked list.
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

        /// <summary>
        /// Counts levels. GameManager keeps one graphContext for the whole session and reuses it,
        /// so object identity cannot tell two levels apart; this routine is where every level
        /// change converges, right before the new level is generated.
        /// </summary>
        private static void OnStartLevelRoutinePrefix()
        {
            _levelGeneration++;

            if (_instance != null)
            {
                _instance._bossHealth.BeginLevel(_instance.ConfiguredBossHealth());
                _instance._coopSync.OnNewLevel();
            }
        }

        /// <summary>The boss health this machine's own settings ask for; the master switch turns it off.</summary>
        private float ConfiguredBossHealth()
        {
            return _settings.EnableMod.Value ? _settings.BossHealthMultiplier.Value : 1f;
        }

        private static void OnUnitSpawnPostfix(Unit __instance)
        {
            if (_instance == null || __instance == null)
            {
                return;
            }

            try
            {
                if (_instance._bossHealth.OnUnitSpawned(__instance) && _instance._settings.LogSpawnDecisions.Value)
                {
                    _instance.Logger.LogInfo(
                        "Boss health | " + __instance.unitSO.name
                        + " x" + _instance._bossHealth.Effective.ToString("0.##")
                        + (_instance._bossHealth.FollowingHost ? " (host)" : string.Empty)
                        + " | max " + __instance.Stats.GetAttribute(EntityAttributes.Stat_MaxHealth).ToString("0"));
                }
            }
            catch (Exception e)
            {
                _instance.ReportUpdateFailure(e);
            }
        }

        private static void OnGoToMainMenuPrefix()
        {
            if (_instance != null)
            {
                _instance.RecordLevelEnd();
            }
        }

        private static void OnLevelTransitionPrefix()
        {
            if (_instance == null)
            {
                return;
            }

            _instance.ResetLevelState("Level transition");
            _instance._inLevelTransition = true;
        }

        private static void OnPlayerDamagePrefix(Unit __instance, DamageSourceData sourceData, out HitState __state)
        {
            // Damage can nest (a linked enemy, reflected damage), so each hit restores what the
            // one around it had set.
            __state = new HitState { Health = -1f, OuterFromPlayer = _hitIsFromPlayer, OuterSource = _hitSource };

            if (__instance == null || _instance == null)
            {
                return;
            }

            if (__instance.isPlayer)
            {
                __state.Health = __instance.GetCurrentHealth();
            }
            else if (_instance.IsRecording && __instance is Npc)
            {
                __state.Health = __instance.GetCurrentHealth();
                _hitIsFromPlayer = IsFromPlayer(sourceData);
                _hitSource = sourceData;
            }
        }

        /// <summary>
        /// Feeds the director what a hit actually took off the player's health, after armour and
        /// the game's own damage scaling, which is what the player feels.
        /// </summary>
        private static void OnPlayerDamagePostfix(Unit __instance, bool __result, HitState __state, DamageSourceData sourceData)
        {
            _hitIsFromPlayer = __state.OuterFromPlayer;
            _hitSource = __state.OuterSource;

            if (!__result || __state.Health < 0f || _instance == null || __instance == null)
            {
                return;
            }

            float lost = __state.Health - __instance.GetCurrentHealth();

            if (!__instance.isPlayer)
            {
                if (IsFromPlayer(sourceData))
                {
                    _instance.RecordDamageDealt(lost, sourceData);
                }

                return;
            }

            float maxHealth = __instance.Stats.GetAttribute(EntityAttributes.Stat_MaxHealth);
            if (maxHealth > 0f)
            {
                _instance._director.ReportPlayerHit(lost / maxHealth, sourceData.states.isPeriodicModifier);
                _instance.RecordPlayerHit(__instance, lost, maxHealth, sourceData);
            }
        }

        private void TryPatchTransitions()
        {
            MethodInfo prefix = AccessTools.Method(typeof(DynamicPressurePlugin), nameof(OnLevelTransitionPrefix));

            TryPatchMethod(typeof(NextLevelTrigger), "MakeTransition", prefix);
            TryPatchMethod(typeof(GameManager), "CompleteLevel", prefix);
            TryPatchAllNamedMethods(typeof(GameManager), "GoToLevel", prefix);
            TryPatchAllNamedMethods(typeof(GameManager), "GoToChurchHub", prefix);
            TryPatchAllNamedMethods(typeof(GameManager), "GoToCarHub", prefix);

            TryPatchAllNamedMethods(
                typeof(GameManager),
                "GoToMainMenu",
                AccessTools.Method(typeof(DynamicPressurePlugin), nameof(OnGoToMainMenuPrefix)));

            MethodInfo levelRoutine = AccessTools.Method(typeof(GameManager), "StartLevelRoutineGraph");
            MethodInfo levelPrefix = AccessTools.Method(typeof(DynamicPressurePlugin), nameof(OnStartLevelRoutinePrefix));
            if (levelRoutine == null || levelPrefix == null)
            {
                Logger.LogError("GameManager.StartLevelRoutineGraph was not found. The director cannot tell levels apart and will not add enemies after the first transition.");
                return;
            }

            _harmony.Patch(levelRoutine, prefix: new HarmonyMethod(levelPrefix));
            Log("Patched GameManager.StartLevelRoutineGraph (level counter).");
        }

        /// <summary>
        /// Patches the single damage entry point every hit funnels into. The other overload builds
        /// a DamageSourceData and calls this one, and Npc's override ends in base.ReceiveDamage, so
        /// one patch on Unit sees every unit's damage; the patch filters down to the player.
        /// </summary>
        private void TryPatchPlayerHits()
        {
            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(Unit),
                    "ReceiveDamage",
                    new[] { typeof(float), typeof(DamageSourceData), typeof(Hitmesh.Data), typeof(Vector3?) }
                );
                MethodInfo prefix = AccessTools.Method(typeof(DynamicPressurePlugin), nameof(OnPlayerDamagePrefix));
                MethodInfo postfix = AccessTools.Method(typeof(DynamicPressurePlugin), nameof(OnPlayerDamagePostfix));

                if (target == null || prefix == null || postfix == null)
                {
                    Logger.LogWarning("Failed to patch Unit.ReceiveDamage: method not found. The director will not feel incoming damage.");
                    return;
                }

                _harmony.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
                _hitPatchActive = true;
                Log("Patched Unit.ReceiveDamage (player hits).");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Failed to patch Unit.ReceiveDamage: " + e.Message + " | The director will not feel incoming damage.");
            }
        }

        private static void OnNpcDiePostfix(Npc __instance)
        {
            if (_instance != null && __instance != null && _instance.IsRecording)
            {
                _instance.RecordDeath(__instance);
            }
        }

        private void TryPatchNpcDeath()
        {
            try
            {
                MethodInfo target = AccessTools.Method(typeof(Npc), "Die");
                MethodInfo postfix = AccessTools.Method(typeof(DynamicPressurePlugin), nameof(OnNpcDiePostfix));

                if (target == null || postfix == null)
                {
                    Logger.LogWarning("Failed to patch Npc.Die: method not found. Combat data will not include kills.");
                    return;
                }

                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                Log("Patched Npc.Die (combat data).");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Failed to patch Npc.Die: " + e.Message + " | Combat data will not include kills.");
            }
        }

        private void TryPatchLevelBudget()
        {
            try
            {
                MethodInfo target = AccessTools.Method(typeof(SpawnEnemiesNode), "Execute");
                MethodInfo prefix = AccessTools.Method(typeof(DynamicPressurePlugin), nameof(OnSpawnEnemiesExecutePrefix));

                if (target == null || prefix == null)
                {
                    Logger.LogWarning("Failed to patch SpawnEnemiesNode.Execute: method not found. Budgets will use estimates.");
                    return;
                }

                _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                Log("Patched SpawnEnemiesNode.Execute (level budget).");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Failed to patch SpawnEnemiesNode.Execute: " + e.Message + " | Budgets will use estimates.");
            }
        }

        /// <summary>
        /// Every unit gets its starting health in Unit.Spawn: level generation, runtime spawns and
        /// the game's own spawn helpers all call SetStats and then Spawn, and Npc's override calls
        /// the base first.
        /// </summary>
        private void TryPatchBossSpawn()
        {
            try
            {
                MethodInfo target = AccessTools.DeclaredMethod(typeof(Unit), "Spawn", Type.EmptyTypes);
                MethodInfo postfix = AccessTools.Method(typeof(DynamicPressurePlugin), nameof(OnUnitSpawnPostfix));

                if (target == null || postfix == null)
                {
                    Logger.LogWarning("Failed to patch Unit.Spawn: method not found. Boss Health Multiplier will have no effect.");
                    return;
                }

                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                Log("Patched Unit.Spawn (boss health).");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Failed to patch Unit.Spawn: " + e.Message + " | Boss Health Multiplier will have no effect.");
            }
        }

        private void TryPatchAllDeadTrigger()
        {
            try
            {
                MethodInfo checkAllDead = AccessTools.Method(typeof(AllDeadTrigger), "CheckAllDead", Type.EmptyTypes);
                MethodInfo registerDeath = AccessTools.Method(typeof(AllDeadTrigger), "RegisterDeath", new[] { typeof(Unit) });
                MethodInfo checkPrefix = AccessTools.Method(typeof(DynamicPressurePlugin), nameof(OnAllDeadTriggerCheckAllDeadPrefix));
                MethodInfo registerPrefix = AccessTools.Method(typeof(DynamicPressurePlugin), nameof(OnAllDeadTriggerRegisterDeathPrefix));

                if (checkAllDead == null || registerDeath == null || checkPrefix == null || registerPrefix == null)
                {
                    Logger.LogWarning("Failed to patch AllDeadTrigger: expected methods were not found.");
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

        private sealed class Snapshot
        {
            public bool hasGameManager;
            public bool hasPlayer;
            public bool playerAlive;
            public bool inSafeZone;
            public string gameState = "Unknown";
            public string currentEnvironment = "Unknown";

            public float playerHp;
            public string playerRoomName = "null";
            public int routeRoomIndex;

            public int threatsPresent;
            public bool engaged;
            public float weightedThreat;
            public int modAlive;
            public float modAliveCost;

            public bool spawningAllowed;
            public SpawnBlockReason blockReason;
        }
    }

    internal struct HitState
    {
        public float Health;
        public bool OuterFromPlayer;
        public DamageSourceData OuterSource;
    }

    public sealed class DynamicPressureSpawnMarker : MonoBehaviour
    {
        public UnitSO sourceUnitSo;
        public float spawnTime;
        public Vector3 spawnPosition;
        public Room spawnRoom;
        public string spawnReason;
        public int waveId;
        public float firstTargetTime = -1f;
    }

    public enum SpawnBlockReason
    {
        None,
        Disabled,
        NoGameManager,
        NoPlayer,
        PlayerDead,
        SafeZone,
        ChallengeMode,
        GameStateNotRunning,
        WaitingForLevel,
        LevelTransition,
        BossFight,
        EndRoomBlocked,
        NoCandidateUnits,
        NoFairSpawnPoint,
        SpawnAsyncFailed,
        UpdateFailed,
        CoopClient
    }
}
