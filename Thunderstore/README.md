# Dynamic Pressure

You started an arms race.  
Now the enemies are joining it too.

Dynamic Pressure adds a director that paces extra enemies around how your fights are going. It only ever adds enemies. Vanilla spawns aren't reduced or replaced.

## How It Plays

While you're on top of things, it keeps sending more enemies. Once you've been hurt badly enough, it stops and gives you a moment to recover. Push two rooms ahead and that break ends early.

Every so often you'll get a surge, a bigger group from one faction, with a short warning first.

If nothing's really hurting you, the breaks get shorter and surges come sooner. A fight that does hurt resets that, and so does the next level.

Extra enemies only come while you're fighting, so a cleared room stays clear until the next fight.

The number of extra enemies depends on how many the level already has, so later areas get more.

## Settings

Three settings control the game and one helps with balance. They're all in the General section.

### Enable Mod

```ini
EnableMod = true
```

Turns the mod on or off.

### Pressure Style

```ini
PressureStyle = Light
```

Higher styles send extra enemies more often and bring tougher ones earlier.

* **Light:** long breaks, rare surges, no mutated or heavy enemies.
* **Heavy:** shorter breaks, regular surges, mutated and heavy enemies from Act 2 on.
* **Nightmare:** short breaks, frequent surges, mutated and heavy enemies from the start.

### Spawn Position

```ini
SpawnPosition = Balanced
```

Balanced puts them mostly ahead of you and to the sides, and rarely behind. Ahead never puts them behind you. Anywhere can use any direction, and they can appear in plain sight.

### Record Combat Data

```ini
RecordCombatData = false
```

Off by default. See Helping With Balance below.

### Fine Tuning

Turn on the Advanced filter in SULFUR Config to get three multipliers. They work with any style and all default to 1.

```ini
[Tuning]
ReinforcementMultiplier = 1
RestMultiplier = 1
EliteMultiplier = 1
```

* **Reinforcement Multiplier:** how many extra enemies a level can get, from 0.5 to 2.
* **Rest Multiplier:** how long the breaks last, from 0.5 to 2.
* **Elite Multiplier:** the chance of mutated enemies, from 0 to 2. At 0 you won't get heavy enemies either. It does nothing on Light, since Light doesn't use them.

## Where Enemies Appear

Unless you pick Anywhere, they spawn where you can't see them and at least 15 meters away. If they spawn behind you, it's at least 25 meters back. They come from the enemies that belong in the area, one faction at a time, so they go after you instead of each other.

No extra enemies appear in safe zones, during boss fights, in the final room, in Endless or Crypt modes, or while you're nearly dead.

## Helping With Balance

I can't test every build myself, so if you'd like to help, the mod can record your fights.

1. Turn on Record Combat Data.
2. Play normally.
3. Pause the game or go to the hub, then press F10.
4. Your file manager opens with the file selected. Send it to me on GitHub or Nexus Mods.

The file stays on your computer unless you send it. It has your gear, how your fights went and your mod list. It doesn't have your name, your Steam account or your save.

## Upgrading From 0.1.x

Your Pressure Style carries over. The old tuning values (target pressure, cooldowns and so on) are gone, so any changes you made to them won't apply. Your old settings are backed up next to the config as `ryuka.sulfur.dynamicpressure.cfg.pre-0.2.bak`.

If you had Enable Auto Spawn off, the mod is now off through Enable Mod instead. Turn that back on to use it.

## Debug Overlay

Press F9 to see what the director is doing: its phase, intensity, threat and budget. The overlay settings are under the Advanced filter in SULFUR Config.

## Progression

Extra enemies never count toward encounters, so they can't keep a gate locked once the required enemies are dead.

## Known Limitations

Balance is still being worked on, so let me know how it feels.

Enemies that spawn far ahead can take a while to reach you.

In very open areas there may be no hidden spot nearby. On Ahead, the director waits instead of spawning anything behind you.

I haven't tested this version with SULFUR Together.

## Localization

Settings and the overlay follow the game's language. Most of the newer text is machine translated, so corrections are welcome.

## Source Code

The source is on my GitHub if you want to read it or use it as a reference for your own mod. It's only my own code and packaging text, with no SULFUR game files, Unity assemblies, BepInEx binaries, paid assets or decompiled game source.

[ryuka-sulfur-mods](https://github.com/ryuka-dev/ryuka-sulfur-mods)
