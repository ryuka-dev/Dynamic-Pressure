# Changelog

## 2.0.0

The mod has been rebuilt around a pacing director.

* Extra enemies now come and go in a cycle based on the damage you actually take. If nothing's really hurting you, the breaks get shorter and surges come sooner.
* Added surges, bigger groups from one faction with a short warning first.
* Fixed the mod stopping as soon as a couple of enemies noticed you. Every enemy counted the same no matter how dangerous it was, so busy fights and later levels got almost no extra enemies.
* The number of extra enemies now depends on how many the level already has, so later areas get more.
* Extra enemies now spawn out of sight and further away, and more often ahead of you than behind. The new Spawn Position setting can stop them spawning behind you at all.
* Extra enemies now come from a hand-picked list, so you won't get ghosts, immortal hunters, vehicles or turrets.
* Light no longer adds mutated or heavy enemies. Heavy adds them from Act 2, Nightmare from the start.
* No more extra enemies in boss fights, Endless or Crypt modes, or while you're nearly dead.
* Settings have been reduced to Enable Mod, Pressure Style and Spawn Position. Your Pressure Style carries over. Other old values no longer apply, and there's a backup next to the config.
* Added multipliers for extra enemies, break length and elites, under the Advanced filter in SULFUR Config.
* Added Record Combat Data, off by default. Press F10 while paused or in the hub to get a file you can send me to help with balance.
* Added Traditional Chinese.

## 0.1.9

* Fixed the mod doing nothing at all on SULFUR 0.19.7. That version removed a property the mod read every frame to tell how long ago the player was last hurt, which threw on the first frame of every session: the overlay stayed empty, no enemies were ever detected, and a manual spawn reported a spawn exception. The mod now keeps that timer itself, so it no longer depends on that property.
* The overlay reports it when a game update breaks something the mod reads, and the failure is logged once instead of once per frame. Previously a single missing member could write tens of thousands of identical errors into a run's log.
* Removed a startup warning about a level transition method that has never existed. Level transitions were already detected through the methods the game does have.

## 0.1.8

* The debug overlay now follows the game language, with translations for every language the game ships.
* The debug overlay is now colour coded, so values that are holding spawning back stand out from normal ones.
* The debug overlay now sizes itself to the window and scrolls, so nothing is cut off at the bottom.
* Language files are now also read from beside the plugin DLL, for mod managers that flatten a package into one folder.
* Added an optional Log Spawn Decisions setting that writes a periodic spawn wave summary to the BepInEx log. Off by default.

## 0.1.7

* Fixed a large frame rate drop that could persist while standing in one area. Once a room had taken its full share of extra enemies, the mod retried a spawn that could never succeed on every single frame.
* Extra enemies now spill over into neighbouring rooms once a room is full, instead of the wave giving up.
* Updated the spawn point search for SULFUR 0.19.

## 0.1.6

* Fixed scripted encounter gates remaining locked when Dynamic Pressure enemies were still alive.
* Fixed player-activated gates and fight triggers sometimes failing after additional enemies were spawned.
* Removed the unnecessary player GameObject lookup used during enemy aggro initialization.
* Dynamic Pressure enemies no longer count as required enemies for vanilla `AllDeadTrigger` progression.
* Removed temporary diagnostics and compatibility workarounds used while investigating these issues.

## 0.1.0 - 0.1.1

Released before this changelog was kept; see the Thunderstore version history.
