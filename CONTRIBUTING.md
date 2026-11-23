# Contributing to Blue Sky Mod

Thanks for your interest in improving or forking Blue Sky Mod!

## Development Setup

1. Clone the repository.
2. Copy `Directory.Build.props.user.example` to `Directory.Build.props.user` and set `<ScheduleIPath>` to your Schedule I installation folder (if it differs from the default Steam path).
3. Verify both mod variants and the test suite build cleanly:
   ```bash
   dotnet build BlueSkyMod.csproj -c Release
   dotnet build BlueSkyMod_InstantUnlock.csproj -c Release
   dotnet run --project Tests/BlueSkySaveCleaner.Tests.csproj -c Release
   ```

## Guidelines & Safety Checklist

### 1. Keep `BlueSkySaveCleaner` in Sync
- If you add or rename any custom item, effect, or recipe ID, update:
  - `BlueSkyIds`, `ModTokens`, `IsBaseModItemId`, `IsModEffectId`, and `IsModRecipeId` in `BlueSkySaveCleaner.cs`.
  - Regression test coverage in `Tests/SaveCleanerTests.cs`.
- Keep `BlueSkySaveCleaner.cs` free of Unity/IL2CPP dependencies so the test suite can always run standalone with `dotnet run`.

### 2. Preserve Multiplayer Safety
- **Host vs. Client authority:** Only mutate server-owned state (like `NPCSpeedController`) when `IsServerInitialized || !IsClientInitialized`.
- **Local vs. Remote players:** Guard camera, post-processing, and `PlayerMovement` overrides behind `if (target != Player.Local) return;`.
- **Late-joining clients & RPC timing:** Ensure any new custom items/recipes are registered before `ProductManager.OnStartClient` and protected in the relevant `RpcLogic___*` prefixes in `StationInjection.cs`.
- **Session cleanup:** Clear any new per-session static collections inside `Main.ResetLoadState()` so returning to the menu and loading another save/lobby never leaks state.

### 3. Pull Requests
- Keep changes focused and explain *why* (especially for Harmony patches or IL2CPP workarounds).
- Confirm both `BlueSkyMod.csproj` and `BlueSkyMod_InstantUnlock.csproj` compile with zero warnings and all `SaveCleanerTests` pass before opening a PR.
- By contributing, you agree that your contributions will be licensed under the repository's **GPL-3.0** license.
