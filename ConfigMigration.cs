using System;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace Ryuka.Sulfur.DynamicPressure
{
    /// <summary>
    /// Moves a pre-2.0 configuration onto the simplified settings, once.
    /// </summary>
    /// <remarks>
    /// The difficulty carries over by itself because PressureStyle keeps its section and key. The
    /// old per-style tuning numbers have no equivalent in the director, so they are not read into
    /// it; they are removed from the file after a backup, and the player is told if any of them
    /// had been changed from the defaults.
    /// </remarks>
    internal static class ConfigMigration
    {
        private const string LegacyMarker = "[Style 1 - Light]";
        private const string BackupSuffix = ".pre-2.0.bak";
        private const string AbsentValue = "__dynamic_pressure_absent__";
        private const string AutoSpawnKey = "EnableAutoSpawn";
        private const string GeneralSection = "General";

        private struct LegacyEntry
        {
            public readonly string Section;
            public readonly string Key;
            public readonly string Default;

            public LegacyEntry(string section, string key, string defaultValue)
            {
                Section = section;
                Key = key;
                Default = defaultValue;
            }
        }

        private static readonly LegacyEntry[] LegacyEntries = BuildLegacyEntries();

        private static LegacyEntry[] BuildLegacyEntries()
        {
            string[] style1 = { "Style 1 - Light", "6", "16", "1", "2", "3", "8" };
            string[] style2 = { "Style 2 - Heavy", "9", "10", "1", "4", "5", "16" };
            string[] style3 = { "Style 3 - Nightmare", "13", "7", "2", "6", "8", "28" };

            System.Collections.Generic.List<LegacyEntry> entries = new System.Collections.Generic.List<LegacyEntry>
            {
                new LegacyEntry(GeneralSection, AutoSpawnKey, "true"),
                new LegacyEntry("Pressure", "ScanInterval", "1"),
                new LegacyEntry("Pressure", "EnemyScanRadius", "25"),
                new LegacyEntry("Pressure", "CloseEnemyDistance", "8"),
                new LegacyEntry("Safety", "LowHealthStopThreshold", "0.35"),
                new LegacyEntry("Safety", "RecentDamageStopWindow", "4"),
                new LegacyEntry("Anti Loop", "MinOriginalEngagedHostilesForSpawn", "1"),
                new LegacyEntry("Anti Loop", "CooldownAfterModKillOnly", "8"),
                new LegacyEntry("Anti Loop", "MaxSecondsWithoutOriginalKill", "30"),
                new LegacyEntry("Anti Loop", "MaxModSpawnsPerOriginalEngaged", "2"),
                new LegacyEntry("Candidates", "AllowEnvironmentFallbackCandidates", "false"),
                new LegacyEntry("Spawn", "SpawnDistanceMin", "8"),
                new LegacyEntry("Spawn", "SpawnDistanceMax", "60"),
            };

            foreach (string[] style in new[] { style1, style2, style3 })
            {
                entries.Add(new LegacyEntry(style[0], "TargetPressure", style[1]));
                entries.Add(new LegacyEntry(style[0], "SpawnCooldown", style[2]));
                entries.Add(new LegacyEntry(style[0], "MaxSpawnPerWave", style[3]));
                entries.Add(new LegacyEntry(style[0], "MaxModSpawnedAlive", style[4]));
                entries.Add(new LegacyEntry(style[0], "MaxModSpawnedPerRoom", style[5]));
                entries.Add(new LegacyEntry(style[0], "MaxModSpawnedPerLevel", style[6]));
            }

            return entries.ToArray();
        }

        /// <summary>
        /// Runs the migration if the file still holds the old layout.
        /// </summary>
        /// <returns>True when the player had changed old tuning values that no longer apply.</returns>
        public static bool Run(ConfigFile config, Settings settings, ManualLogSource log)
        {
            string path = config.ConfigFilePath;
            if (!NeedsMigration(path) || !TryBackup(path, log))
            {
                return false;
            }

            bool hadCustomValues = false;
            bool savedOnSet = config.SaveOnConfigSet;
            config.SaveOnConfigSet = false;

            try
            {
                foreach (LegacyEntry legacy in LegacyEntries)
                {
                    ConfigEntry<string> entry = config.Bind(legacy.Section, legacy.Key, AbsentValue, string.Empty);
                    string stored = entry.Value;
                    config.Remove(entry.Definition);

                    if (stored == AbsentValue)
                    {
                        continue;
                    }

                    if (legacy.Key == AutoSpawnKey)
                    {
                        // A player who had automatic spawning off did not want extra enemies.
                        if (IsFalse(stored))
                        {
                            settings.EnableMod.Value = false;
                        }

                        continue;
                    }

                    hadCustomValues |= !SameValue(stored, legacy.Default);
                }

                settings.Style.Value = Settings.ClampStyle(settings.Style.Value);
                config.Save();
            }
            finally
            {
                config.SaveOnConfigSet = savedOnSet;
            }

            log.LogInfo("Moved an older configuration onto the simplified settings. Backup: " + path + BackupSuffix);
            if (hadCustomValues)
            {
                log.LogWarning("Custom tuning values from the older version no longer apply. They are kept in the backup file.");
            }

            return hadCustomValues;
        }

        private static bool NeedsMigration(string path)
        {
            try
            {
                return File.Exists(path) && File.ReadAllText(path).Contains(LegacyMarker);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryBackup(string path, ManualLogSource log)
        {
            string backup = path + BackupSuffix;

            try
            {
                if (!File.Exists(backup))
                {
                    File.Copy(path, backup);
                }

                return true;
            }
            catch (Exception e)
            {
                // Without a backup the old values would be lost for good, so leave them in place
                // and try again next launch.
                log.LogWarning("Could not back up the older configuration; leaving it untouched: " + e.Message);
                return false;
            }
        }

        private static bool IsFalse(string value)
        {
            return string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameValue(string stored, string expected)
        {
            double a;
            double b;
            if (double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out a)
                && double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out b))
            {
                return Math.Abs(a - b) < 0.0001;
            }

            return string.Equals(stored.Trim(), expected, StringComparison.OrdinalIgnoreCase);
        }
    }
}
