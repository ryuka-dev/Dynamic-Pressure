using System;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Utilities;
using UnityEngine;

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

        private static string TintReason(SpawnBlockReason reason)
        {
            if (reason == SpawnBlockReason.None)
            {
                return OverlayPalette.Tint(OverlayPalette.Good, reason);
            }

            if (reason == SpawnBlockReason.NoFairSpawnPoint
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
            if (_overlayStyle == null)
            {
                _overlayStyle = new GUIStyle(GUI.skin.label);
                _overlayStyle.richText = true;
            }

            DrawFeedbackMessage();

            if (!_settings.EnableOverlay.Value)
            {
                return;
            }

            const float margin = 20f;
            const float top = 120f;
            float width = Mathf.Min(680f, Screen.width - margin * 2f);
            float height = Mathf.Max(240f, Screen.height - top - margin);

            Rect rect = new Rect(margin, top, width, height);
            GUI.Box(rect, OverlayText.Get("overlay.title", "Dynamic Pressure Debug"));

            GUILayout.BeginArea(new Rect(rect.x + 10f, rect.y + 25f, rect.width - 20f, rect.height - 35f));
            _overlayScroll = GUILayout.BeginScrollView(_overlayScroll);

            Row(Label("overlay.enabled", "Enabled") + OverlayPalette.Flag(_settings.EnableMod.Value, true)
                + Separator() + Label("overlay.style", "Style") + OverlayPalette.Tint(OverlayPalette.Accent, _settings.EffectiveStyle)
                + Separator() + Label("overlay.position", "Position") + OverlayPalette.Tint(OverlayPalette.Accent, _settings.Position.Value));

            Row(Label("overlay.gameState", "GameState")
                + OverlayPalette.Tint(_snapshot.gameState == "Running" ? OverlayPalette.Good : OverlayPalette.Warn, _snapshot.gameState)
                + Separator() + Label("overlay.safeZone", "SafeZone") + OverlayPalette.Flag(_snapshot.inSafeZone, false)
                + Separator() + Label("overlay.env", "Env") + OverlayPalette.Tint(OverlayPalette.Value, _snapshot.currentEnvironment));

            Row(Label("overlay.player", "Player")
                + (_snapshot.hasPlayer
                    ? OverlayPalette.Tint(OverlayPalette.Good, OverlayText.Get("overlay.ok", "OK"))
                    : OverlayPalette.Tint(OverlayPalette.Bad, OverlayText.Get("overlay.missing", "Missing")))
                + Separator() + Label("overlay.hp", "HP")
                + OverlayPalette.Tint(_snapshot.playerHp < 0.5f ? OverlayPalette.Warn : OverlayPalette.Good, Mathf.RoundToInt(_snapshot.playerHp * 100f) + "%")
                + (_hitPatchActive ? string.Empty : Separator() + OverlayPalette.Tint(OverlayPalette.Bad, OverlayText.Get("overlay.noHitSensor", "no hit sensor"))));

            if (_lastUpdateFailure != null)
            {
                Row(Label("overlay.updateFailure", "Update Error")
                    + OverlayPalette.Tint(OverlayPalette.Bad, _lastUpdateFailure)
                    + OverlayPalette.Tint(OverlayPalette.Muted, " x" + _updateFailureCount));
            }

            Row(Label("overlay.room", "Room") + OverlayPalette.Tint(OverlayPalette.Value, _snapshot.playerRoomName)
                + Separator() + Label("overlay.route", "Route") + OverlayPalette.Tint(OverlayPalette.Value, _snapshot.routeRoomIndex));

            GUILayout.Space(8f);
            Row(Header("overlay.section.director", "Director"));
            Row(Label("overlay.phase", "Phase") + OverlayPalette.Tint(OverlayPalette.Accent, _director.Phase)
                + Separator() + Label("overlay.intensity", "Intensity") + OverlayPalette.Tint(OverlayPalette.Value, _director.Intensity.ToString("0.00"))
                + Separator() + Label("overlay.pace", "Pace") + OverlayPalette.Tint(OverlayPalette.Value, _director.Pace.ToString("0.00")));
            Row(Label("overlay.threat", "Threat")
                + OverlayPalette.Tint(OverlayPalette.Value, _snapshot.weightedThreat.ToString("0.0"))
                + OverlayPalette.Tint(OverlayPalette.Muted, " / " + _director.TargetThreat.ToString("0.0"))
                + Separator() + Label("overlay.engaged", "Engaged") + OverlayPalette.Flag(_snapshot.engaged, true)
                + Separator() + Label("overlay.present", "Present") + OverlayPalette.Count(_snapshot.threatsPresent));
            Row(Label("overlay.modAlive", "Mod Alive")
                + OverlayPalette.Tint(OverlayPalette.Value, _snapshot.modAlive + " (" + _snapshot.modAliveCost.ToString("0") + "/" + _director.AliveCap.ToString("0") + ")")
                + Separator() + Label("overlay.budget", "Budget")
                + OverlayPalette.Tint(OverlayPalette.Value, _director.SpentThreat.ToString("0") + " / " + _director.UnlockedBudget.ToString("0") + " / " + _director.TotalBudget.ToString("0")));

            GUILayout.Space(8f);
            Row(Header("overlay.section.spawnDecision", "Spawn Decision"));
            Row(Label("overlay.lastDecision", "Last Decision") + TintDecision(_lastDecision)
                + Separator() + Label("overlay.reason", "Reason") + TintReason(_lastBlockReason));
            Row(Label("overlay.lastSpawn", "Last Spawn") + OverlayPalette.Tint(OverlayPalette.Value, _lastSpawnSummary));

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
