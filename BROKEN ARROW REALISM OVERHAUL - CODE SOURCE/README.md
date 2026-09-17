# Broken Arrow Realism Overhaul

Source code of the **Broken Arrow Realism Overhaul** mod for *Broken Arrow* (1.2.0.3), by **adrien74200**.

The mod is a single-player / campaign realism overhaul: real weapon ranges, real calibres and
armour, real optics and sensors, real ballistics, reworked helicopter flight and countermeasures,
terrain cover, and two campaign-only divisions per side. It is not made for multiplayer and refuses
to run when the game is started with the anti-cheat enabled.

- Nexus Mods page: *(add link)*
- Discord: https://discord.gg/hNUBQhXW8Z
- Steam: https://steamcommunity.com/profiles/76561199350352727/

## What this repository contains

| Path | Content |
|---|---|
| `*.cs` | The complete mod source (C#, namespace `RealismOverhaul`). |
| `reel/*.csv` | The real-world stat tables, embedded in the DLL as resources at build time. |
| `BrokenArrowRealismOverhaul.csproj` | The build project. |

Nothing else is needed to build. The released archive also ships
[MelonLoader 0.7.3](https://github.com/LavaGang/MelonLoader) unmodified (`version.dll` +
`MelonLoader/`), taken as-is from its official release; it is not part of this repository.

## How it works

The mod is a standard MelonLoader mod. It uses Harmony patches on the game's own systems
(damage calculation, shooting distance, sensors, AI orders, UI) and rewrites unit statistics
through the game's own database override API. It writes one log file
(`MelonLoader/Latest.log`) and one preferences file (`UserData/MelonPreferences.cfg`).

It does not touch, disable or interact with the anti-cheat in any way, it does not read or write
anything outside the game folder, and it makes no network connection of any kind.

## Requirements to build

- **.NET SDK 6.0 or newer** — https://dotnet.microsoft.com/download
- **Broken Arrow** installed (Steam), with **MelonLoader 0.7.3** installed into the game folder
  and the game launched once, so that MelonLoader generates the interop assemblies in
  `<game>\MelonLoader\Il2CppAssemblies\`.

The project references those game assemblies directly. They belong to Steel Balalaika / Slitherine
and are **not** redistributed here, so a copy of the game is required to compile.

## Build

From the folder containing `BrokenArrowRealismOverhaul.csproj`:

```
dotnet build BrokenArrowRealismOverhaul.csproj -c Release -o out -p:Public=true -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\broken_arrow"
```

- `-p:GameDir=...` — path to your Broken Arrow installation. Omit it if the game is in the default
  Steam location.
- `-p:Public=true` — builds the released configuration (compact log and short UI texts).
  Without it you get the development configuration (verbose log).
- `-p:NetRuntime=...` — optional, path to a `Microsoft.NETCore.App\6.x.x` folder. By default the
  project references the installed .NET 6 runtime if it is present, otherwise it falls back to the
  SDK's normal framework reference.

The result is `out\BrokenArrowRealismOverhaul.dll`, which is the file shipped in `Mods\` of the
released archive. The build is deterministic (`Deterministic`, `PathMap`, no PDB), so the released
DLL of version 0.24.0 is reproduced byte for byte:

```
BrokenArrowRealismOverhaul.dll   1 454 080 bytes   MD5 f1c0dea433838b0893acd2f49bbeb540
```

## Install (for players)

Copy `version.dll`, `MelonLoader\` and `Mods\BrokenArrowRealismOverhaul.dll` into the Broken Arrow
folder, then start the game from Steam with the **"Anti-Cheat Disabled"** launch option.

## License

Free to use and to learn from. Please do not reupload the compiled mod elsewhere without asking.
