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

        public ConfigEntry<bool> EnableMod { get; private set; }
        public ConfigEntry<PressureStyle> Style { get; private set; }
        public ConfigEntry<SpawnPosition> Position { get; private set; }

        public ConfigEntry<bool> EnableOverlay { get; private set; }
        public ConfigEntry<Key> OverlayToggleKey { get; private set; }
        public ConfigEntry<Key> ManualSpawnKey { get; private set; }
        public ConfigEntry<bool> LogSpawnDecisions { get; private set; }

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

            return settings;
        }
    }
}
