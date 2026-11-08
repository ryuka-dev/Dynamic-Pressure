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
    public sealed partial class DynamicPressurePlugin : BaseUnityPlugin
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
