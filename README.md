# ksPlugPatcher_NRE

English / [日本語](README_ja.md)

A BepInEx plugin for Koikatsu Sunshine that works around bugs in **ksPlug / ksPlugVR** (by Katarsys) and in related game code, mainly `NullReferenceException`s.
It does not change ksPlug's files; it patches the running code with Harmony.

**For the ksPlug author:** [docs/KSPLUG_ISSUES.md](docs/KSPLUG_ISSUES.md) lists every issue with the exception stack, the cause, and a suggested fix inside ksPlug.
The patch and the document are MIT-licensed, so any part of them can be taken into ksPlug.

- Games: `KoikatsuSunshine` / `KoikatsuSunshine_VR` (not CharaStudio)
- ksPlug versions: 1.6.7 to 1.7.3 (normal and VR)
- Tested in game: ksPlug 1.7.0 (normal, with measurements), ksPlug 1.7.3 (normal), ksPlugVR 1.7.3 (patches applied, invite list)
- Version: 0.6.0

## What it fixes

| Problem | Workaround |
|---|---|
| Orgasm repeats in H while the gauge is empty (game `ParentObjectCtrl` touches destroyed H items) | Skip destroyed items (game patch) |
| NRE every frame in H from ksPlug `ColDefsManager`, `BellyCtrl`, `CtrlBones` (references to a rebuilt body); belly inflation and bone offsets stop | Remove destroyed entries / find the bones again |
| Several GB of memory per free H (ksPlug's invite menu loads every card fully) | Keep a light card in the list and load the full card when inviting |
| Frame rate drops with many invited girls (DynamicBone) | Stop invited girls' DynamicBones and cap updates to 1 per frame |
| After a ksPlug swap, faces do not match the lines (face list not reloaded) | Reload the face list together with the voice list |
| NRE on facial expressions for characters without a face material | Skip only the material call |

With ksPlug 1.7.0 (normal mode): no more orgasm loop and 0 ksPlug exceptions (thousands per H before); invite menu Mono +7.6 GB → +77 to 117 MB; 7 girls 137 ms → 25 ms per frame.

## Install

1. Install ksPlug (optional: without ksPlug only the game-side fixes are active) and BepInEx 5.
2. Copy `ksPlugPatcher_NRE.dll` from the [Releases](../../releases) ZIP to `BepInEx\plugins\ksPlugPatcher_NRE\`.

Settings are in `BepInEx\config\kkttspj.kks.ksplugpatcher.nre.cfg` (each workaround can be turned off). See [README_ja.md](README_ja.md) §3 for the full list.
The settings file of 0.5.x (`com.your.kks.ksplugpatcher.nre.cfg`) is copied to the new name on the first start of 0.6.0.

## Build

Place this folder directly under the game folder and run `build_and_deploy.ps1` (needs the .NET SDK).
The project references `..\..\BepInEx\core\` and `..\..\KoikatsuSunshine_Data\Managed\`. It does not reference ksPlug; ksPlug types are found by reflection.
The DLL is copied to `..\Deploy\ksPlugPatcher_NRE\`.

## License

MIT ([LICENSE](LICENSE)). No warranty.
ksPlug is a separate work by its author and is not included here. Game files, BepInEx and other plugins are not included either.
