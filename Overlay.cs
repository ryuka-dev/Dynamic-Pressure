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
    }
}
