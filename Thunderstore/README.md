# Dynamic Pressure

You started an arms race.  
Now the enemies are joining it too.

Dynamic Pressure adds extra enemy pressure on top of the original game without replacing or reducing vanilla spawns. The mod watches the current combat state in real time and only adds enemies when the surrounding pressure is considered too low.

## What It Does

Dynamic Pressure monitors the current level and calculates a pressure value based on nearby hostile NPCs, engaged enemies, enemies targeting the player, and enemies that know the player position.

When the pressure is below the configured target, the mod can spawn additional enemies using the current level's existing enemy pool. Vanilla enemy spawning is not reduced or replaced.

The goal is not to turn every room into an endless monster flood. The goal is to reduce low-pressure gaps and make the run feel more aggressive.

## Current Balance Status

Balance is still experimental.

The pressure calculation itself appears to be working, but the default balance is not fully tuned yet. In particular, the current Nightmare style may not feel as nightmarish as expected under the default settings.

You can enable the debug overlay to see the real-time pressure value, spawn decisions, block reasons, and current mod impact. Use those values to tune the difficulty to your preference.

## Pressure Styles

The mod has three configurable pressure styles:

### Style 1 - Light

A small pressure increase.  
Intended to fill low-pressure gaps without changing the overall feel too much.

### Style 2 - Heavy

A stronger pressure increase.  
Low-pressure rooms should feel more active, and combat downtime should be reduced.

### Style 3 - Nightmare

The highest pressure setting.  
This is intended to create a more oppressive experience, but the current default values may still be too conservative. Increase the recommended values below if you want a harsher version.

## Important Config

```ini
[General]
EnableMod = true
EnableAutoSpawn = true
PressureStyle = 1
````

`EnableAutoSpawn` must be `true` for the mod to spawn enemies automatically.

If it is `false`, the mod will still load and show debug information, but it will not actively spawn enemies except through manual debug testing.

## Debug Overlay

The debug overlay is strongly recommended while tuning the mod.

It shows information such as:

* Current pressure
* Target pressure
* Pressure deficit
* Original hostile count
* Mod-spawned hostile count
* Spawn decision
* Block reason
* Last spawned enemy
* Whether mod-spawned enemies are targeting the player

Useful block reasons include:

```text
PressureAlreadyHigh
NoEngagedOriginalHostiles
NoValidNpcSpawnPoint
RecentDamage
LowHealth
MaxModSpawnedAlive
NoOriginalKillProgress
SpawnBudgetPerOriginalExceeded
```

If the mod is not spawning, check the overlay first. It usually explains why.

## Recommended Settings

The following settings are recommended as a better starting point than the early debug defaults.

### Light

```ini
[Style 1 - Light]
TargetPressure = 6
SpawnCooldown = 16
MaxSpawnPerWave = 1
MaxModSpawnedAlive = 2
MaxModSpawnedPerRoom = 3
MaxModSpawnedPerLevel = 8
```

### Heavy

```ini
[Style 2 - Heavy]
TargetPressure = 9
SpawnCooldown = 10
MaxSpawnPerWave = 1
MaxModSpawnedAlive = 4
MaxModSpawnedPerRoom = 5
MaxModSpawnedPerLevel = 16
```

### Nightmare

```ini
[Style 3 - Nightmare]
TargetPressure = 13
SpawnCooldown = 7
MaxSpawnPerWave = 2
MaxModSpawnedAlive = 6
MaxModSpawnedPerRoom = 8
MaxModSpawnedPerLevel = 28
```

If Nightmare still feels too mild, try:

```ini
[Style 3 - Nightmare]
TargetPressure = 16
SpawnCooldown = 5
MaxSpawnPerWave = 2
MaxModSpawnedAlive = 8
MaxModSpawnedPerRoom = 10
MaxModSpawnedPerLevel = 36
```

For a more extreme test:

```ini
[Style 3 - Nightmare]
TargetPressure = 18
SpawnCooldown = 4
MaxSpawnPerWave = 3
MaxModSpawnedAlive = 10
MaxModSpawnedPerRoom = 14
MaxModSpawnedPerLevel = 45
```

## Anti-Loop Protection

The mod includes safeguards to avoid endless spawning.

```ini
[Anti Loop]
MinOriginalEngagedHostilesForSpawn = 1
CooldownAfterModKillOnly = 8
MaxSecondsWithoutOriginalKill = 30
MaxModSpawnsPerOriginalEngaged = 2
```

These settings prevent the mod from repeatedly spawning enemies when only a stuck or unreachable original enemy remains.

For Nightmare, you can make the mod more aggressive with:

```ini
[Anti Loop]
MaxModSpawnsPerOriginalEngaged = 3
MaxSecondsWithoutOriginalKill = 40
CooldownAfterModKillOnly = 5
```

Do not raise these too much unless you specifically want a chaotic or unstable experience.

## Spawn Distance

If the mod often shows `NoValidNpcSpawnPoint`, increase the spawn distance range:

```ini
[Spawn]
SpawnDistanceMin = 8
SpawnDistanceMax = 60
```

For testing:

```ini
[Spawn]
SpawnDistanceMin = 6
SpawnDistanceMax = 120
```

A larger value makes it easier for the mod to find valid spawn points, but enemies may spawn farther away and take longer to apply pressure.

## Notes

Dynamic Pressure does not reduce vanilla enemy spawns.

It does not delete vanilla enemies.

It does not replace the original level generation.

It only adds extra enemies when the pressure system decides that the current situation is below the configured target.

## Progression Safety

Mod-spawned enemies are treated as additional combat pressure, not as required objectives for scripted encounters.

They are excluded from vanilla `AllDeadTrigger` progression checks, preventing extra enemies from keeping encounter gates locked after the original required enemies have been defeated.

Enemy aggro initialization now uses the known player unit directly instead of performing an unnecessary GameObject-to-Unit lookup. This prevents Dynamic Pressure from corrupting the game's internal player lookup cache and breaking player-activated gates or scripted fight triggers.

These fixes preserve the original encounter logic. The mod does not force gates open, skip required enemies, or modify vanilla player triggers.


## Known Issues / Current Limitations

Balance is not final.

Nightmare mode may still be too mild under the default settings.

Some rooms may not have valid spawn points close enough to the player.

If the current pressure is already high, the mod may not spawn anything for long periods. This is intentional.

If the player is low on health or was recently damaged, the mod may stop spawning temporarily.

## Suggested Tuning Process

1. Enable the debug overlay.
2. Play one or two levels.
3. Watch the average pressure and the block reasons.
4. If pressure is often below target, raise `TargetPressure`.
5. If enemies do not appear often enough, lower `SpawnCooldown`.
6. If too few enemies remain alive, raise `MaxModSpawnedAlive`.
7. If rooms feel overcrowded, lower `MaxModSpawnedPerRoom`.
8. If the mod stops too easily in Nightmare, raise `MaxModSpawnsPerOriginalEngaged` from `2` to `3`.

## SULFUR Config Localization Support

This mod includes localization files for SULFUR Config.

This localization support is only for the in-game configuration page provided by SULFUR Config. It localizes the mod name, config sections, setting names, and setting descriptions shown in the config UI.

It does not change the game’s own text, item names, dialogue, or gameplay content.

Supported SULFUR Config languages:

- English
- Simplified Chinese
- Swedish
- French
- Italian
- German
- Spanish
- Portuguese
- Russian
- Polish
- Japanese
- Korean
- Turkish
- Arabic

## Source Code

The source code for this mod is available in my GitHub repository.

It is shared for learning, reference, and transparency.
The repository only contains my original mod source code and packaging text. It does not include SULFUR game files, Unity assemblies, BepInEx binaries, paid assets, or decompiled game source.

If you are another modder, feel free to study how the mod works or use it as a reference for your own implementation.

[ryuka-sulfur-mods](https://github.com/ryuka-dev/ryuka-sulfur-mods)
