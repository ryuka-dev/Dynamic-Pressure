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

    }
}
