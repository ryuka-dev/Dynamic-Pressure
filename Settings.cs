using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace Ryuka.Sulfur.DynamicPressure
{
    /// <summary>
    /// The player-facing settings. One difficulty, one placement choice, and a master switch;
    /// everything numeric lives in <see cref="PressurePreset"/> so it stays tuned together.
    /// </summary>
    internal sealed class Settings
    {
        // SULFUR Config keeps entries with this tag out of the default list.
        private const string AdvancedTag = "Advanced";
        private const string GeneralSection = "General";
        private const string OverlaySection = "Debug Overlay";
        private const string TuningSection = "Tuning";

        public ConfigEntry<bool> EnableMod { get; private set; }
        public ConfigEntry<PressureStyle> Style { get; private set; }
        public ConfigEntry<SpawnPosition> Position { get; private set; }
        public ConfigEntry<bool> RecordCombatData { get; private set; }

        public ConfigEntry<float> ReinforcementMultiplier { get; private set; }
        public ConfigEntry<float> RestMultiplier { get; private set; }
        public ConfigEntry<float> EliteMultiplier { get; private set; }
        public ConfigEntry<float> BossHealthMultiplier { get; private set; }

        public ConfigEntry<bool> EnableOverlay { get; private set; }
        public ConfigEntry<Key> OverlayToggleKey { get; private set; }
        public ConfigEntry<Key> ManualSpawnKey { get; private set; }
        public ConfigEntry<bool> LogSpawnDecisions { get; private set; }
        public ConfigEntry<Key> ExportCombatDataKey { get; private set; }

        public DirectorTuning Tuning
        {
            get
            {
                return new DirectorTuning
                {
                    Reinforcement = ReinforcementMultiplier.Value,
                    Rest = RestMultiplier.Value
                };
            }
        }

        /// <summary>
        /// The configured style, with anything outside the named values pulled to the nearest one.
        /// A hand-edited file can hold such a value, because BepInEx keeps any number for an enum.
        /// </summary>
        public PressureStyle EffectiveStyle
        {
            get { return ClampStyle(Style.Value); }
        }

        public static PressureStyle ClampStyle(PressureStyle style)
        {
            if ((int)style < (int)PressureStyle.Light)
            {
                return PressureStyle.Light;
            }

            return (int)style > (int)PressureStyle.Nightmare ? PressureStyle.Nightmare : style;
        }

        public static Settings Bind(ConfigFile config)
        {
            Settings settings = new Settings();

            settings.EnableMod = config.Bind(GeneralSection, "EnableMod", true, "Turns Dynamic Pressure on or off.");

            // Same section and key as the old numeric setting: BepInEx reads a stored 1, 2 or 3 as
            // the enum member with that value, so existing players keep their difficulty.
            settings.Style = config.Bind(
                GeneralSection,
                "PressureStyle",
                PressureStyle.Light,
                "How hard the director pushes. Higher styles send reinforcements more often and introduce tougher enemies earlier.");

            settings.Position = config.Bind(
                GeneralSection,
                "SpawnPosition",
                SpawnPosition.Balanced,
                "Where reinforcements appear. Balanced: mostly ahead and to the sides, rarely behind. Ahead: never behind you. Anywhere: any direction, and they can appear in plain sight.");

            settings.RecordCombatData = config.Bind(
                GeneralSection,
                "RecordCombatData",
                false,
                "Saves detailed combat data on your computer to help balance the mod. Press the export key (F10 by default) while paused or in the hub to create a file you can send to the mod author. It contains your gear, combat statistics and installed mod list, and nothing that identifies you.");

            settings.ReinforcementMultiplier = config.Bind(
                TuningSection,
                "ReinforcementMultiplier",
                1f,
                new ConfigDescription("Scales how many reinforcements a level can receive. 1 is the style's normal amount.", new AcceptableValueRange<float>(0.5f, 2f), AdvancedTag));
            settings.RestMultiplier = config.Bind(
                TuningSection,
                "RestMultiplier",
                1f,
                new ConfigDescription("Scales how long the director backs off after a peak. Lower values mean less time to recover.", new AcceptableValueRange<float>(0.5f, 2f), AdvancedTag));
            settings.EliteMultiplier = config.Bind(
                TuningSection,
                "EliteMultiplier",
                1f,
                new ConfigDescription("Scales the chance of mutated reinforcements. 0 also removes heavy units. Has no effect on styles that do not use them.", new AcceptableValueRange<float>(0f, 2f), AdvancedTag));
            settings.BossHealthMultiplier = config.Bind(
                TuningSection,
                "BossHealthMultiplier",
                1f,
                new ConfigDescription("Multiplies boss health. 1 is normal health. Changes take effect on the next level. In co-op the host's setting applies.", new AcceptableValueRange<float>(BossHealthScaler.MinMultiplier, BossHealthScaler.MaxMultiplier), AdvancedTag));

            settings.EnableOverlay = config.Bind(
                OverlaySection,
                "EnableOverlay",
                false,
                new ConfigDescription("Show the director's debug overlay.", null, AdvancedTag));
            settings.OverlayToggleKey = config.Bind(
                OverlaySection,
                "OverlayToggleKey",
                Key.F9,
                new ConfigDescription("Key that shows or hides the debug overlay.", null, AdvancedTag));
            settings.ManualSpawnKey = config.Bind(
                OverlaySection,
                "ManualSpawnKey",
                Key.F8,
                new ConfigDescription("Spawns one enemy for testing. Only works while the debug overlay is showing.", null, AdvancedTag));
            settings.LogSpawnDecisions = config.Bind(
                OverlaySection,
                "LogSpawnDecisions",
                false,
                new ConfigDescription("Write the director's decisions to the BepInEx log. Diagnostics only; does not affect spawning.", null, AdvancedTag));

            settings.ExportCombatDataKey = config.Bind(
                OverlaySection,
                "ExportCombatDataKey",
                Key.F10,
                new ConfigDescription("Key that exports recorded combat data. Works while paused or in the hub.", null, AdvancedTag));

            return settings;
        }
    }
}
