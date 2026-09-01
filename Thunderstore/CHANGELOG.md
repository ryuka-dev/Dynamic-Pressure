# Changelog

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
