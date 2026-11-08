using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Items;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;

namespace Ryuka.Sulfur.DynamicPressure
{
    /// <summary>
    /// Everything that turns play into feedback data. All of it is gated on the player's opt-in
    /// and none of it is read back by the director.
    /// </summary>
    public sealed partial class DynamicPressurePlugin
    {
        private const float SampleIntervalSeconds = 5f;
        private const float FeedbackMessageSeconds = 6f;

        private CombatRecorder _recorder;
        private LevelRecord _levelRecord;
        private float _nextSampleTime;
        private string _feedbackMessage;
        private float _feedbackMessageUntil;
        private bool _sessionRecorded;
        private float _levelDensity;
        private int _levelSpawnableRooms;
        private int _levelRouteRooms;
        private float _levelAverageCost;
        private bool _levelMeasured;

        // Set while a hit from the player is being applied, so a death that hit causes can be
        // credited to it: the game runs Die inside ReceiveDamage, and last-damaged-by is not
        // updated for burns, poison or weaponless explosions.
        private static bool _hitIsFromPlayer;
        private static DamageSourceData _hitSource;

        private sealed class WeaponRecord
        {
            public float Damage;
            public int Hits;
            public int Kills;
        }

        private sealed class LevelRecord
        {
            public float StartTime;
            public readonly float[] PhaseSeconds = new float[4];
            public float DamageTaken;
            public float DamageTakenFromAdded;
            public int Hits;
            public float LowestHealth = 1f;
            public bool Died;
            public int AddedKills;
            public int OriginalKills;
            public int Added;
            public int Surges;
            public readonly Dictionary<string, WeaponRecord> Weapons = new Dictionary<string, WeaponRecord>();
            public readonly HashSet<int> DeadUnits = new HashSet<int>();
        }

        private bool IsRecording
        {
            get { return _recorder != null && _recorder.IsRecording; }
        }

        private void CreateRecorder()
        {
            _recorder = new CombatRecorder(Path.Combine(Paths.BepInExRootPath, "DynamicPressure", "feedback"), Logger);
        }

        /// <summary>Follows the opt-in setting, which the player can change at any time.</summary>
        private void SyncRecording()
        {
            bool wanted = _settings.EnableMod.Value && _settings.RecordCombatData.Value;

            if (wanted && !_recorder.NeedsStop)
            {
                _recorder.Start();
                _sessionRecorded = false;
                ResumeLevelRecord();
            }
            else if (!wanted && _recorder.NeedsStop)
            {
                RecordLevelEnd();
                _recorder.Stop();
            }
        }

        // Recording switched on during a level would otherwise lose everything until the next one.
        private void ResumeLevelRecord()
        {
            GameManager gm = StaticInstance<GameManager>.Instance;
            if (IsRecording && gm != null && gm.PlayerUnit != null && _levelStarted && !_inLevelTransition)
            {
                RecordLevelStart(gm, true);
            }
        }

        private void RecordSession()
        {
            List<object> plugins = new List<object>();
            foreach (PluginInfo info in Chainloader.PluginInfos.Values)
            {
                plugins.Add(new Dictionary<string, object>
                {
                    { "guid", info.Metadata.GUID },
                    { "name", info.Metadata.Name },
                    { "version", info.Metadata.Version.ToString() },
                });
            }

            _recorder.Record(
                "session",
                Time.time,
                "mod", PluginVersion,
                "game", Application.version,
                "plugins", plugins);
        }

        private void RememberLevelCalibration(float density, int spawnableRooms, int routeRooms, float averageCost, bool measured)
        {
            _levelDensity = density;
            _levelSpawnableRooms = spawnableRooms;
            _levelRouteRooms = routeRooms;
            _levelAverageCost = averageCost;
            _levelMeasured = measured;
        }

        private void RecordLevelStart(GameManager gm, bool joinedPartway)
        {
            if (!IsRecording)
            {
                return;
            }

            // Written here rather than when recording starts, so a launch that never reaches a
            // level leaves no file to take up a place in the export.
            if (!_sessionRecorded)
            {
                _sessionRecorded = true;
                RecordSession();
            }

            _levelRecord = new LevelRecord { StartTime = Time.time };
            _nextSampleTime = Time.time + SampleIntervalSeconds;

            _recorder.Record(
                "level_start",
                Time.time,
                "env", gm.currentEnvironment != null ? gm.currentEnvironment.id.ToString() : null,
                "level", gm.currentLevelIndex,
                "partial", joinedPartway,
                "difficulty", SaveDifficulty(),
                "style", _appliedStyle.ToString(),
                "position", _settings.Position.Value.ToString(),
                "reinforcement", _settings.ReinforcementMultiplier.Value,
                "rest", _settings.RestMultiplier.Value,
                "elite", _settings.EliteMultiplier.Value,
                "pack_budget", _packBudget,
                "density", _levelDensity,
                "rooms", _levelSpawnableRooms,
                "route_rooms", _levelRouteRooms,
                "avg_cost", _levelAverageCost,
                "measured", _levelMeasured,
                "late", _lateGame,
                "budget", _director.TotalBudget,
                "target", _director.TargetThreat,
                "gear", ReadGear(gm));
        }

        private void RecordLevelEnd()
        {
            LevelRecord level = _levelRecord;
            _levelRecord = null;

            if (level == null || !IsRecording)
            {
                return;
            }

            Dictionary<string, object> weapons = new Dictionary<string, object>();
            foreach (KeyValuePair<string, WeaponRecord> weapon in level.Weapons)
            {
                weapons[weapon.Key] = new Dictionary<string, object>
                {
                    { "damage", weapon.Value.Damage },
                    { "hits", weapon.Value.Hits },
                    { "kills", weapon.Value.Kills },
                };
            }

            GameManager gm = StaticInstance<GameManager>.Instance;

            _recorder.Record(
                "level_end",
                Time.time,
                "seconds", Time.time - level.StartTime,
                "build_up", level.PhaseSeconds[(int)DirectorPhase.BuildUp],
                "sustain", level.PhaseSeconds[(int)DirectorPhase.Sustain],
                "fade", level.PhaseSeconds[(int)DirectorPhase.Fade],
                "relax", level.PhaseSeconds[(int)DirectorPhase.Relax],
                "hits", level.Hits,
                "damage_taken", level.DamageTaken,
                "damage_from_added", level.DamageTakenFromAdded,
                "lowest_health", level.LowestHealth,
                "died", level.Died,
                "added", level.Added,
                "surges", level.Surges,
                "added_kills", level.AddedKills,
                "original_kills", level.OriginalKills,
                "spent", _director.SpentThreat,
                "budget", _director.TotalBudget,
                "pace", _director.Pace,
                "weapons", weapons,
                "gear", gm != null ? ReadGear(gm) : null);

            _recorder.Flush();
        }

        private void RecordTick(Snapshot snapshot, float delta)
        {
            if (!IsRecording || _levelRecord == null || StaticInstance<GameManager>.Instance == null)
            {
                return;
            }

            _levelRecord.PhaseSeconds[(int)_director.Phase] += delta;

            if (snapshot.hasPlayer)
            {
                _levelRecord.LowestHealth = Mathf.Min(_levelRecord.LowestHealth, snapshot.playerHp);
                _levelRecord.Died |= !snapshot.playerAlive;
            }

            if (Time.time < _nextSampleTime)
            {
                return;
            }

            _nextSampleTime = Time.time + SampleIntervalSeconds;

            _recorder.Record(
                "sample",
                Time.time,
                "phase", _director.Phase.ToString(),
                "intensity", _director.Intensity,
                "pace", _director.Pace,
                "hp", snapshot.playerHp,
                "room", snapshot.routeRoomIndex,
                "engaged", snapshot.engaged,
                "present", snapshot.threatsPresent,
                "threat", snapshot.weightedThreat,
                "target", _director.TargetThreat,
                "added_alive", snapshot.modAlive,
                "added_cost", snapshot.modAliveCost,
                "spent", _director.SpentThreat,
                "unlocked", _director.UnlockedBudget,
                "blocked", snapshot.spawningAllowed ? null : snapshot.blockReason.ToString());
        }

        private void RecordSpawn(UnitSO unit, SpawnPointChoice point, string reason, string mutation)
        {
            if (!IsRecording || _levelRecord == null)
            {
                return;
            }

            _levelRecord.Added++;

            if (reason == "Surge")
            {
                _levelRecord.Surges++;
            }

            _recorder.Record(
                "spawn",
                Time.time,
                "unit", unit.name,
                "cost", unit.SpawnCost,
                "kind", reason,
                "zone", point.zone.ToString(),
                "distance", point.distanceToPlayer,
                "mutation", mutation,
                "wave", _waveId);
        }

        private void RecordPlayerHit(Unit player, float lost, float maxHealth, DamageSourceData source)
        {
            if (!IsRecording || _levelRecord == null || lost <= 0f)
            {
                return;
            }

            Npc attacker = source.sourceUnit as Npc;
            bool fromAdded = attacker != null && attacker.GetComponent<DynamicPressureSpawnMarker>() != null;

            _levelRecord.Hits++;
            _levelRecord.DamageTaken += lost;
            if (fromAdded)
            {
                _levelRecord.DamageTakenFromAdded += lost;
            }

            _recorder.Record(
                "hit",
                Time.time,
                "damage", lost,
                "hp", maxHealth > 0f ? player.GetCurrentHealth() / maxHealth : 0f,
                "periodic", source.states.isPeriodicModifier,
                "from", attacker != null && attacker.unitSO != null ? attacker.unitSO.name : null,
                "from_added", fromAdded);
        }

        private void RecordDamageDealt(float damage, DamageSourceData source)
        {
            if (_levelRecord == null || damage <= 0f)
            {
                return;
            }

            if (!IsRecording)
            {
                return;
            }

            WeaponRecord weapon = WeaponEntry(source);
            weapon.Damage += damage;
            weapon.Hits++;
        }

        private WeaponRecord WeaponEntry(DamageSourceData source)
        {
            string name = source.sourceWeapon != null && source.sourceWeapon.weaponDefinition != null
                ? source.sourceWeapon.weaponDefinition.name
                : "other";

            WeaponRecord record;
            if (!_levelRecord.Weapons.TryGetValue(name, out record))
            {
                record = new WeaponRecord();
                _levelRecord.Weapons[name] = record;
            }

            return record;
        }

        private void RecordDeath(Npc npc)
        {
            if (!IsRecording || _levelRecord == null || !_levelRecord.DeadUnits.Add(npc.GetInstanceID()))
            {
                return;
            }

            bool byPlayer = _hitIsFromPlayer;
            DynamicPressureSpawnMarker marker = npc.GetComponent<DynamicPressureSpawnMarker>();

            if (byPlayer)
            {
                WeaponEntry(_hitSource).Kills++;
            }

            if (marker == null)
            {
                if (IsHostileToPlayer(npc))
                {
                    _levelRecord.OriginalKills++;
                }

                return;
            }

            _levelRecord.AddedKills++;
            _recorder.Record(
                "added_death",
                Time.time,
                "unit", marker.sourceUnitSo != null ? marker.sourceUnitSo.name : null,
                "alive_for", Time.time - marker.spawnTime,
                "reached_player_after", marker.firstTargetTime >= 0f ? (object)(marker.firstTargetTime - marker.spawnTime) : null,
                "by_player", byPlayer,
                "wave", marker.waveId);
        }

        private static bool IsFromPlayer(DamageSourceData source)
        {
            return source.isPlayer || (source.sourceUnit != null && source.sourceUnit.isPlayer);
        }

        private static List<object> ReadGear(GameManager gm)
        {
            List<object> gear = new List<object>();

            try
            {
                if (gm.EquipmentManager == null)
                {
                    return gear;
                }

                foreach (KeyValuePair<InventorySlot, InventoryItem> pair in gm.EquipmentManager.equippedItems)
                {
                    InventoryItem item = pair.Value;
                    if (item == null || item.itemDefinition == null)
                    {
                        continue;
                    }

                    List<object> enchantments = new List<object>();
                    foreach (EnchantmentDefinition enchantment in item.enchantments)
                    {
                        if (enchantment != null)
                        {
                            enchantments.Add(enchantment.name);
                        }
                    }

                    Dictionary<string, object> entry = new Dictionary<string, object>
                    {
                        { "slot", pair.Key.ToString() },
                        { "item", item.itemDefinition.name },
                    };

                    if (item.weaponDefinition != null)
                    {
                        entry["rank"] = item.GetRankLevel();
                    }

                    if (enchantments.Count > 0)
                    {
                        entry["enchantments"] = enchantments;
                    }

                    gear.Add(entry);
                }
            }
            catch (Exception)
            {
                // Gear is context, not the record itself; a game change here must not lose the level.
            }

            return gear;
        }

        private static string SaveDifficulty()
        {
            try
            {
                return SulfurSaveState.active.DIFFICULTY.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        // The game's own alert queue only drains while running, and export happens while paused.
        private void ShowFeedbackMessage(string text)
        {
            _feedbackMessage = text;
            _feedbackMessageUntil = Time.unscaledTime + FeedbackMessageSeconds;
        }

        private void DrawFeedbackMessage()
        {
            if (_feedbackMessage != null && Time.unscaledTime < _feedbackMessageUntil)
            {
                GUI.Label(new Rect(20f, 60f, Screen.width - 40f, 40f), OverlayPalette.Tint(OverlayPalette.Accent, _feedbackMessage), _overlayStyle);
            }
        }

        /// <summary>
        /// Saves the recorded sessions to one file and shows it in the file manager. Limited to
        /// moments when losing window focus is harmless.
        /// </summary>
        private void ExportCombatData()
        {
            // A player who never opted in has nothing to do with this key.
            if (!_settings.RecordCombatData.Value && !_recorder.HasRecordedData)
            {
                return;
            }

            GameManager gm = StaticInstance<GameManager>.Instance;
            if (gm != null && gm.gameState.ToString() != "Paused" && !gm.InSafeZone)
            {
                ShowFeedbackMessage(OverlayText.Get("alert.exportWhere", "Pause the game or go to the hub to export combat data."));
                return;
            }

            string path;
            if (!_recorder.TryExport(out path))
            {
                ShowFeedbackMessage(OverlayText.Get("alert.exportEmpty", "No combat data to export yet."));
                return;
            }

            Log("Combat data exported to " + path);
            ShowFeedbackMessage(OverlayText.Get("alert.exported", "Combat data exported. Send the highlighted file to the mod author."));

            try
            {
                using (Process.Start("explorer.exe", "/select,\"" + path + "\""))
                {
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not open the file manager (" + e.GetType().Name + "). The export is at the path logged above.");
            }
        }
    }
}
