# Changelog

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
