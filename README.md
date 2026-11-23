# Blue Sky Mod (Schedule I)

A MelonLoader (IL2CPP) mod for **Schedule I** that adds a full **Blue Sky Meth** production chain, custom **99.1% Pure** effect, synced multiplayer held-item/avatar visuals, and a built-in save cleaner for safe mod uninstallation.

- **Author:** J1gno
- **Target Branch:** Schedule I IL2CPP (main Steam branch)
- **Dependency:** [S1API (Forked)](https://www.nexusmods.com/schedule1/mods/1194) (`S1API.Il2Cpp.MelonLoader.dll`) + MelonLoader

---

## Features

- **Pure Ephedrine (`pseudo_pristine`):** Heavenly-quality precursor sold alongside vanilla Pseudo (Standard build inherits vanilla Pseudo's unlock rank; Instant Unlock build removes the rank gate).
- **Liquid Meth (Blue) (`liquid_meth_blue`):** Crafted at the Chemistry Station (`Blue Meth` recipe: Pure Ephedrine + Acid + Phosphorus).
- **Blue Sky Meth (`meth_bluesky`):** Baked from Liquid Meth (Blue) in the Lab Oven with blue crystal shards, custom shatter particles, packaged icons, and synced third-person visuals (unpackaged, baggie, jar, and brick).
- **`99.1% Pure` Effect (`bluesky_purity`):** Custom effect carried over into mixes, boosting mix base value and applying player/NPC visuals and speed effects cleanly across singleplayer and multiplayer.
- **Safe Uninstallation:** Includes an in-game `bluesky_uninstall` console command and a `MelonPreferences.cfg` `UninstallOnNextLoad` toggle that backs up the save and scrubs all mod items/mixes/recipes/contracts without touching vanilla data or other mods.

---

## Building From Source

### Prerequisites

1. **Schedule I** (IL2CPP Steam version) with **MelonLoader** and **[S1API (Forked)](https://www.nexusmods.com/schedule1/mods/1194)** installed (launch the game once so MelonLoader generates `MelonLoader\Il2CppAssemblies\*.dll`).
2. **.NET 6 SDK** (for building the mod DLLs) and **.NET 8 SDK** (optional, for running the standalone `SaveCleanerTests`).

### Configure Your Game Path

By default, `Directory.Build.props` points to `C:\Program Files (x86)\Steam\steamapps\common\Schedule I`.

If your game is installed elsewhere, pick any one of these options (no tracked files need to be edited):

1. **Recommended (local override file):** Copy `Directory.Build.props.user.example` to `Directory.Build.props.user` (gitignored) and set `<ScheduleIPath>`.
2. **Environment variable:** Set `SCHEDULE_I_PATH` to your Schedule I folder.
3. **CLI flag:** Pass `-p:ScheduleIPath="D:\SteamLibrary\steamapps\common\Schedule I"` to `dotnet build`.

To build without copying the DLL into your game's `Mods` folder, set `<CopyToMods>false</CopyToMods>` or pass `-p:CopyToMods=false`.

### Build Commands

```bash
# Standard build (outputs bin/Release/net6.0/BlueSkyMod.dll)
dotnet build BlueSkyMod.csproj -c Release

# Instant Unlock build (outputs bin/Release/net6.0/BlueSkyMod_InstantUnlock.dll)
dotnet build BlueSkyMod_InstantUnlock.csproj -c Release

# Run standalone save-cleaner unit tests
dotnet run --project Tests/BlueSkySaveCleaner.Tests.csproj -c Release
```

VS Code users can also run the preconfigured tasks in `.vscode/tasks.json` (`build standard`, `build instant unlock`, `build all`, `run tests`).

---

## Modding & Customization Map ("Where to Edit")

Quick reference for the main places to customize values, items, visuals, or hooks:

| What you want to change | File | Section / Method |
|---|---|---|
| Mod name, author, version, Nexus ID | `Main.cs` | Assembly attributes & `ModVersion` |
| MelonPreferences entries | `Main.cs` | `OnInitializeMelon` |
| Harmony patches (e.g. after a game update) | `Main.cs` | `OnInitializeMelon` (`TryPatch` calls) |
| Item IDs, effect ID, recipe IDs | `BlueSkySaveCleaner.cs` | `BlueSkyIds`, `ModTokens`, `IsBaseModItemId` / `IsModEffectId` / `IsModRecipeId` |
| Blue Sky prices, mix base price, addictiveness, NPC speed | `ItemDefinitions.cs` | Top balance constants |
| Pure Ephedrine price, stack limit, quality, rank unlock | `ItemDefinitions.cs` | `CreatePristinePseudo` |
| Liquid Meth (Blue) & Lab Oven yield (`ProductQuantity`) | `ItemDefinitions.cs` | `CreateLiquidBlue` |
| Blue Sky Meth name, crystal material & appearance | `ItemDefinitions.cs` | `CreateBlueSky` |
| `99.1% Pure` effect name, description, tier, value bonus | `ItemDefinitions.cs` | `GetOrCreateBlueSkyEffect` |
| Player consumption effects (speed, FoV, camera shake, post-FX) | `ItemDefinitions.cs` | `ApplyBlueSkyToNativePlayer` |
| NPC consumption effects (eyes, emotion, VO, walk speed) | `ItemDefinitions.cs` | `ApplyBlueSkyToNativeNpc` |
| Chemistry Station recipe title, ingredients, cook time | `ItemDefinitions.cs` | `CreateAndRegisterRecipes` |
| Shop / supplier stock & delivery settings | `ItemDefinitions.cs` | `TryInjectPristinePseudoIntoShops` |
| Color palette (pills, liquid, crystals, eyes, particles) | `VisualsAndAvatars.cs` | Color constants (`BlueLiquidColor`, `CyanCrystalColor`, etc.) |
| Lab Oven shatter particles & shard visuals | `StationInjection.cs` | `LabOven_Shatter_Patch` |
| Uninstall backup folder location | `UninstallWorkflow.cs` | `GetBackupRoot` |

---

## Project Architecture

```text
├── Main.cs                        # Entry point, lifecycle hooks, Harmony patch wiring, IL2CPP helpers
├── ItemDefinitions.cs             # Custom items, '99.1% Pure' effect, recipes, shop injection
├── StationInjection.cs            # Chemistry Station UI, MP RPC safety nets, Lab Oven shatter visuals
├── VisualsAndAvatars.cs           # Palette, runtime icon tinting, stored/held/MP avatar visuals
├── UninstallWorkflow.cs           # In-game `bluesky_uninstall` command & `UninstallOnNextLoad` hook
├── BlueSkySaveCleaner.cs          # Pure-C# ID registry (`BlueSkyIds`) & JSON save scrubber
├── Tests/
│   ├── BlueSkySaveCleaner.Tests.csproj
│   └── SaveCleanerTests.cs        # Automated regression tests for save scrubbing
├── Directory.Build.props          # Shared build config & ScheduleIPath resolution
├── Directory.Build.props.user.example
├── BlueSkyMod.csproj              # Standard build
└── BlueSkyMod_InstantUnlock.csproj# Instant Unlock build (`INSTANT_UNLOCK`)
```

---

## Safe Uninstallation (For Players & Testers)

- **In-game (Host / Singleplayer):** Open the developer console and run `bluesky_uninstall`. The mod saves progress, creates a backup in `UserData/BlueSkyMod_Backups/`, removes all Blue Sky Mod data from the save, and returns to the Main Menu so you can close the game and delete the DLL.
- **Pre-load:** Set `UninstallOnNextLoad = true` under `[BlueSkyMod]` in `UserData/MelonPreferences.cfg`, then load the save once.

In multiplayer, the **host** runs `bluesky_uninstall`, then both host and clients remove the DLL.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development guidelines, multiplayer safety rules, and testing requirements.

## License

This project is licensed under the **GNU General Public License v3.0 (GPL-3.0)** — see [LICENSE](LICENSE) for details. Any forks or derivative mods distributed publicly must remain open source under GPL-3.0.
