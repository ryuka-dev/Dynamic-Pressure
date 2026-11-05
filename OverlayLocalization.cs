using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using PerfectRandom.Sulfur.Core;
using UnityEngine;

namespace Ryuka.Sulfur.DynamicPressure
{
    /// <summary>
    /// Resolves which language the game is currently displaying, as a plain language code that
    /// matches the lang/&lt;code&gt;.json files shipped in the package.
    ///
    /// This is the only place that touches I2.Loc. Everything above it works with the project's
    /// own language code strings, so replacing the localization backend stays a one-file change.
    /// </summary>
    internal static class GameLanguage
    {
        public const string FallbackCode = "en";

        /// <summary>
        /// The language codes the game's localisation source ships, which are also the names of the
        /// lang/ files in this package.
        /// </summary>
        private static readonly string[] ShippedCodes =
        {
            "en", "sv", "fr", "it", "de", "es", "pt", "ru", "pl", "ja", "ko", "zh-CN", "tr", "ar",
        };

        /// <summary>
        /// The language code the game is currently displaying ("en", "zh-CN", …), or null when
        /// localisation has not finished loading.
        /// </summary>
        public static string CurrentCode()
        {
            try
            {
                string code = I2.Loc.LocalizationManager.CurrentLanguageCode;
                if (string.IsNullOrEmpty(code))
                {
                    return null;
                }

                // The Traditional Chinese font set is its own I2 source, coded "zh". At startup it
                // comes before the main source and wins the code lookup; after an in-game change it
                // comes after it and the code is "zh-TW". Same language, so report one code.
                return code == "zh" ? "zh-TW" : code;
            }
            catch (Exception)
            {
                // Localisation loads asynchronously during startup; before it is up this can throw
                // rather than return empty. The overlay stays on its English fallbacks until then.
                return null;
            }
        }

        /// <summary>True while the current language is written right to left.</summary>
        public static bool IsRightToLeft()
        {
            try
            {
                return I2.Loc.LocalizationManager.IsRight2Left;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Reorders a right-to-left string into the visual order IMGUI draws in. The game runs the
        /// same fixer over its own translations before handing them out.
        /// </summary>
        public static string FixRightToLeft(string text)
        {
            try
            {
                return I2.Loc.LocalizationManager.FixRTL_IfNeeded(text);
            }
            catch (Exception)
            {
                return text;
            }
        }

        /// <summary>
        /// Picks the lang/ file for a language code: the exact code, else the same language under a
        /// different region, else English. "zh-TW" is closer to the file filed under "zh-CN" than to
        /// English, and a code carrying an unknown region is the likeliest shape of a language that
        /// has not been asked for yet.
        /// </summary>
        public static string ResolveCode(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode))
            {
                return FallbackCode;
            }

            for (int i = 0; i < ShippedCodes.Length; i++)
            {
                if (string.Equals(ShippedCodes[i], languageCode, StringComparison.OrdinalIgnoreCase))
                {
                    return ShippedCodes[i];
                }
            }

            string wanted = LanguageOf(languageCode);
            for (int i = 0; i < ShippedCodes.Length; i++)
            {
                if (string.Equals(LanguageOf(ShippedCodes[i]), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return ShippedCodes[i];
                }
            }

            return FallbackCode;
        }

        /// <summary>A code without whatever follows its first hyphen: "zh-CN" and "zh-Hans" are both "zh".</summary>
        private static string LanguageOf(string languageCode)
        {
            int hyphen = languageCode.IndexOf('-');
            return hyphen > 0 ? languageCode.Substring(0, hyphen) : languageCode;
        }
    }

    /// <summary>
    /// The overlay's string table. Reads the same lang/&lt;code&gt;.json files the package already
    /// ships for the config UI, so all of a language's text lives in one file.
    ///
    /// Every lookup carries the English text as its own fallback, so a missing file or a missing
    /// key degrades to readable English rather than to a key name or a blank line.
    /// </summary>
    internal static class OverlayText
    {
        // Read with DataContractJsonSerializer rather than Unity's JsonUtility: these files are
        // the same ones the config UI library reads, and that is the reader proven against them.
        // Assigned by the serializer, which the compiler cannot see.
#pragma warning disable 0649
        [DataContract]
        private sealed class LangEntry
        {
            [DataMember(Name = "key")]
            public string Key;

            [DataMember(Name = "value")]
            public string Value;
        }

        [DataContract]
        private sealed class LangFile
        {
            [DataMember(Name = "entries")]
            public LangEntry[] Entries;
        }
#pragma warning restore 0649

        private static readonly Dictionary<string, string> Strings =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The lang/ file currently loaded.</summary>
        public static string LoadedCode { get; private set; }

        /// <summary>The language code the game reported, before it was resolved onto a file.</summary>
        public static string GameCode { get; private set; }

        /// <summary>How many strings the last load ended up with.</summary>
        public static int LoadedCount { get { return Strings.Count; } }

        /// <summary>
        /// Why the last load fell short, when it did. Diagnostics only: a language file that will
        /// not load is invisible otherwise, because every lookup silently falls back to English.
        /// </summary>
        public static string LastLoadNote { get; private set; }

        /// <summary>The directory the strings were actually read from.</summary>
        public static string LoadedFrom { get; private set; }

        /// <summary>
        /// Where a language file may sit, relative to the plugin DLL, most preferred first.
        ///
        /// The packaged layout keeps them in lang/, but some mod managers flatten a package and
        /// drop every file into one folder, which puts the language files directly beside the DLL.
        /// Both have to work, so both are searched.
        /// </summary>
        private static IEnumerable<string> CandidateDirectories(string pluginDirectory)
        {
            if (string.IsNullOrEmpty(pluginDirectory))
            {
                yield break;
            }

            // Packaged layout: Plugin.dll + lang/en.json
            yield return Path.Combine(pluginDirectory, "lang");

            // Flattened layout: Plugin.dll + en.json
            yield return pluginDirectory;

            // A manager that kept lang/ beside the plugin's own folder rather than inside it.
            string parent = null;
            try
            {
                DirectoryInfo info = Directory.GetParent(pluginDirectory);
                parent = info != null ? info.FullName : null;
            }
            catch (Exception)
            {
                parent = null;
            }

            if (!string.IsNullOrEmpty(parent))
            {
                yield return Path.Combine(parent, "lang");
            }
        }

        /// <summary>
        /// Loads the table for the game's current language. Safe to call repeatedly; it is a no-op
        /// when the resolved language has not changed, so the only file access happens on an actual
        /// language change. Returns true when the table was reloaded.
        /// </summary>
        public static bool Reload(string pluginDirectory, out string resolvedCode)
        {
            string gameCode = GameLanguage.CurrentCode();
            resolvedCode = GameLanguage.ResolveCode(gameCode);

            if (LoadedCode == resolvedCode && GameCode == gameCode)
            {
                return false;
            }

            Strings.Clear();
            LastLoadNote = null;
            LoadedFrom = null;

            // English first, so an incomplete translation falls through to it per key.
            if (resolvedCode != GameLanguage.FallbackCode)
            {
                LoadFirstMatch(pluginDirectory, GameLanguage.FallbackCode);
            }

            LoadFirstMatch(pluginDirectory, resolvedCode);

            LoadedCode = resolvedCode;
            GameCode = gameCode;
            return true;
        }

        /// <summary>
        /// Loads a language from the first candidate directory that actually has it, so a flattened
        /// package does not silently fall back to English.
        /// </summary>
        private static void LoadFirstMatch(string pluginDirectory, string code)
        {
            foreach (string directory in CandidateDirectories(pluginDirectory))
            {
                if (LoadInto(directory, code))
                {
                    LoadedFrom = directory;
                    return;
                }
            }

            LastLoadNote = "no readable " + code + ".json under " + (pluginDirectory ?? "<unknown>");
        }

        /// <summary>Returns true when the file existed and yielded at least one entry.</summary>
        private static bool LoadInto(string langDirectory, string code)
        {
            string path = Path.Combine(langDirectory, code + ".json");
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                LangFile parsed;
                using (MemoryStream stream = new MemoryStream(File.ReadAllBytes(path)))
                {
                    parsed = new DataContractJsonSerializer(typeof(LangFile)).ReadObject(stream) as LangFile;
                }

                if (parsed == null || parsed.Entries == null)
                {
                    LastLoadNote = "no entries in " + path;
                    return false;
                }

                // Right-to-left text is reordered once here rather than per draw: IMGUI lays out
                // whatever it is given left to right, and the game hands out its own translations
                // already reordered the same way.
                bool rightToLeft = GameLanguage.IsRightToLeft();

                int loaded = 0;
                for (int i = 0; i < parsed.Entries.Length; i++)
                {
                    LangEntry entry = parsed.Entries[i];
                    if (entry != null && !string.IsNullOrEmpty(entry.Key) && entry.Value != null)
                    {
                        Strings[entry.Key] = rightToLeft
                            ? GameLanguage.FixRightToLeft(entry.Value)
                            : entry.Value;
                        loaded++;
                    }
                }

                return loaded > 0;
            }
            catch (Exception e)
            {
                // A malformed or unreadable language file must never take the overlay down; the
                // caller's English fallback text covers whatever did not load. The note is what
                // tells the log the difference between "not found", "empty" and "would not parse".
                LastLoadNote = e.GetType().Name + " reading " + path;
                return false;
            }
        }

        /// <summary>
        /// Looks up a key, returning <paramref name="englishFallback"/> when it is not translated.
        /// </summary>
        public static string Get(string key, string englishFallback)
        {
            string value;
            if (Strings.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }

            return englishFallback;
        }
    }

    /// <summary>
    /// Colour vocabulary for the overlay. The overlay is read at a glance while the game is
    /// running, so a value's colour carries its meaning: green is nominal, amber is a value worth
    /// watching, red is the reason nothing is happening, grey is inert.
    /// </summary>
    internal static class OverlayPalette
    {
        public const string Header = "#7EC8FF";
        public const string Label = "#B9B9B9";
        public const string Value = "#FFFFFF";
        public const string Good = "#7BE07B";
        public const string Warn = "#FFC451";
        public const string Bad = "#FF6B6B";
        public const string Muted = "#8A8A8A";
        public const string Accent = "#D89CFF";

        public static string Tint(string hex, object value)
        {
            return "<color=" + hex + ">" + value + "</color>";
        }

        /// <summary>Green when the flag is in its nominal state, amber otherwise.</summary>
        public static string Flag(bool value, bool nominal)
        {
            return Tint(value == nominal ? Good : Warn, value);
        }

        /// <summary>Green while below the limit, amber on the last slot, red once saturated.</summary>
        public static string Budget(int used, int limit)
        {
            string hex = Good;
            if (limit > 0 && used >= limit)
            {
                hex = Bad;
            }
            else if (limit > 0 && used >= limit - 1)
            {
                hex = Warn;
            }

            return Tint(hex, used) + Tint(Muted, " / " + limit);
        }

        public static string Count(int value)
        {
            return Tint(value > 0 ? Value : Muted, value);
        }
    }
}
