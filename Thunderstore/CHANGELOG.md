# Changelog

## 0.2.0

The mod has been rebuilt around a pacing director.

* Reinforcements now follow a cycle of build-up, peak, fade and rest, driven by the damage you actually take. Staying in control of a level shortens the rests and brings surges sooner.
* Added surges: larger groups from one faction, with a short warning.
* Fixed the mod stopping as soon as a couple of enemies noticed you. Every enemy counted the same no matter how dangerous it was, so busy fights and later levels got almost no reinforcements.
* Budgets now come from each level's own enemy generation, so later environments get more reinforcements.
* Reinforcements now appear out of sight and away from you, and are more likely ahead than behind. The new Spawn Position setting can keep them from ever spawning behind you.
* Reinforcements are now picked from a checked list of enemies. Ghosts, immortal hunters, vehicles and turrets no longer appear as reinforcements.
* Light no longer adds mutated or heavy enemies. Heavy adds them from Act 2, Nightmare from the start.
* The director now stays out of boss fights, Endless and Crypt modes, and stops adding enemies while you are nearly dead.
* Settings have been reduced to Enable Mod, Pressure Style and Spawn Position. Your Pressure Style carries over. Other old values no longer apply and are kept in a backup file next to the config.
* Added three optional multipliers for reinforcements, rest length and elites, found under the Advanced filter in SULFUR Config.
* Added an optional Record Combat Data setting, off by default. Press F10 while paused or in the hub to export a file you can send to help with balance.
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
