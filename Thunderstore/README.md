# Dynamic Pressure

You started an arms race.  
Now the enemies are joining it too.

Dynamic Pressure adds a pacing director on top of the original game. It watches how a fight is going and sends reinforcements when you have room to breathe, then backs off when you have had enough. It only ever adds enemies. Vanilla spawns are not reduced or replaced.

## How It Plays

The director runs in a cycle:

1. **Build-up:** reinforcements arrive while you are in control.
2. **Peak:** taking real damage pushes the director to its peak.
3. **Fade and relax:** it stops adding enemies and gives you time to recover. Pushing two rooms forward ends the rest early.

Every so often a **surge** arrives: a larger group from one faction, with a short warning first.

If you stay in control of a level without ever reaching a peak, the director notices. Rests get shorter and surges come sooner, so a strong build keeps being tested. A hard enough fight resets this, and so does the next level.

Reinforcements only come while you are fighting, so a cleared room stays cleared until the next fight.

Budgets are measured from each level's own enemy generation, so later environments automatically get more reinforcements.

## Settings

Three settings control the game, and one helps with balance. All four are in the General section.

### Enable Mod

```ini
EnableMod = true
```

Turns the mod on or off.

### Pressure Style

```ini
PressureStyle = Light
```

Light, Heavy or Nightmare. Higher styles send reinforcements more often and bring tougher enemies earlier.

* **Light:** the longest rests, rare surges, no mutated enemies or heavy units.
* **Heavy:** shorter rests, regular surges, mutated enemies and heavy units from Act 2.
* **Nightmare:** the shortest rests, frequent surges, mutated enemies and heavy units from the start.

### Spawn Position

```ini
SpawnPosition = Balanced
```

Balanced: mostly ahead and to the sides, rarely behind. Ahead: never behind you. Anywhere: any direction, and they can appear in plain sight.

### Record Combat Data

```ini
RecordCombatData = false
```

Off by default. See Helping With Balance below.

### Fine Tuning

Turn on the Advanced filter in SULFUR Config for three multipliers that apply on top of any style. All of them default to 1.

```ini
[Tuning]
ReinforcementMultiplier = 1
RestMultiplier = 1
EliteMultiplier = 1
```

* **Reinforcement Multiplier:** how many reinforcements a level can receive, from 0.5 to 2.
* **Rest Multiplier:** how long the director backs off after a peak, from 0.5 to 2.
* **Elite Multiplier:** the chance of mutated reinforcements, from 0 to 2. At 0, heavy units are removed too. It does nothing on Light, which has none to begin with.

## Fair Spawns

Unless you choose Anywhere, reinforcements appear out of your sight and at least 15 meters away. Anything spawning behind you has to be at least 25 meters back. They are drawn from the enemies that belong in the current environment, one faction at a time, so they fight you and not each other.

The director will not add enemies in safe zones, during boss fights, in the final room, while you are nearly dead, or in Endless and Crypt modes.

## Helping With Balance

Balance depends on how people really play, so the mod can record your fights if you choose to.

1. Turn on Record Combat Data.
2. Play normally.
3. Pause the game or go to the hub, then press F10.
4. Your file manager opens with the file selected. Send that file to me on GitHub or Nexus Mods.

The file is only stored on your computer until you send it. It contains your gear, how your fights went, and the list of mods you have installed. It does not contain your name, your Steam account or your save file.

## Upgrading From 0.1.x

Your Pressure Style carries over. The old tuning values (target pressure, cooldowns, per-style limits and so on) no longer exist, so if you changed any of them they will not apply. A copy of your old settings is saved next to the config file as `ryuka.sulfur.dynamicpressure.cfg.pre-0.2.bak`.

If you had Enable Auto Spawn turned off, the mod is now turned off through Enable Mod. Turn it back on to use the director.

## Debug Overlay

Advanced players can show the director's state with F9: current phase, intensity, threat and budget. In SULFUR Config these settings are under the Advanced filter.

## Progression Safety

Mod-spawned enemies are extra pressure, not objectives. They are excluded from vanilla `AllDeadTrigger` checks, so they can never keep an encounter gate locked after the required enemies are dead.

## Known Limitations

Balance is still being tuned, and feedback is welcome.

Enemies placed far ahead can take a while to reach you.

In a very open area there may be no hidden spawn point nearby. With Ahead, the director then waits rather than spawning behind you.

Co-op through SULFUR Together has not been tested with this version.

## Localization

Settings and the debug overlay follow the game language, in all 15 languages the game ships. Most of the newer translations are machine translated, and corrections are welcome.

## Source Code

The source code for this mod is available in my GitHub repository.

It is shared for learning, reference, and transparency.
The repository only contains my original mod source code and packaging text. It does not include SULFUR game files, Unity assemblies, BepInEx binaries, paid assets, or decompiled game source.

If you are another modder, feel free to study how the mod works or use it as a reference for your own implementation.

[ryuka-sulfur-mods](https://github.com/ryuka-dev/ryuka-sulfur-mods)
