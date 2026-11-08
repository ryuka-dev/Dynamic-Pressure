# Dynamic Pressure - source

BepInEx plugin for SULFUR. A pacing director that adds extra enemies on top of
vanilla spawns, without touching them. The player-facing description lives in
`Thunderstore/README.md`; this file is for building it.

Published as [ryuka_labs/DynamicPressure](https://thunderstore.io/c/sulfur/p/ryuka_labs/DynamicPressure/).

## Layout

```text
Dynamic Pressure.csproj    references game assemblies through properties only
Directory.Build.props      every machine-specific path, all overridable
Plugin.cs                  lifecycle, world scan, Harmony patches
Director.cs                pacing and budgets; plain C#, no Unity types
Spawner.cs                 unit placement
Roster.cs                  the enemies the director may add
Settings.cs                player settings
ConfigMigration.cs         one-time move from the 0.1.x config layout
Overlay.cs                 debug overlay
OverlayLocalization.cs     language resolution for overlay text
Thunderstore/              the release package, flattened - this is the zip
  manifest.json            name / version / dependencies
  README.md                store page (players)
  CHANGELOG.md             newest version on top
  icon.png                 256x256
  lang/*.json              SULFUR Config localization, 15 languages
  nexusmods.txt            generated BBCode, committed, not shipped in the zip
```

Build output (`bin/`, `obj/`, the DLL, the zip) is not committed.

## Build

```powershell
dotnet build "Dynamic Pressure.csproj" -c Release
```

Paths come from `Directory.Build.props` and can be overridden per machine
without editing anything tracked:

```powershell
dotnet build "Dynamic Pressure.csproj" -c Release -p:SulfurGameDir=E:\Games\SULFUR
```

## Deploy for a playtest

```powershell
dotnet build "Dynamic Pressure.csproj" -c Release -p:DeployToSulfurProfile=true
```

Copies the DLL into `Thunderstore/` and into `<Gale profile>/BepInEx/plugins/DynamicPressure`
along with `lang/`. Pick a profile that does **not** already have this mod
installed from Thunderstore: two copies of one plugin GUID make BepInEx load
whichever it likes and log `Skipping [...]`, and a log like that proves nothing.

## Release

Keep the version identical in three places - `Thunderstore/manifest.json`, the
top entry of `Thunderstore/CHANGELOG.md`, and `PluginVersion` in `Plugin.cs` -
then build the package with the shared packaging script. It re-generates
`nexusmods.txt`, so commit that if it changed.

## Game version

Targets SULFUR 1.0.x (last verified on 1.0.5). The director reads
`SpawnEnemiesNode.packBudget` and patches `GameManager.StartLevelRoutineGraph`;
if a game update removes the latter the director stops after the first level
transition and logs an error.
