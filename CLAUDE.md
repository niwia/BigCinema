# BigScreen (Big Walk mod) - agent notes

Read `docs/HANDOFF.md` first. It is the ordered task list for whoever picks this project
up on a machine that has Big Walk, BepInEx and the .NET SDK installed.

## What this is
A BepInEx 6 (IL2CPP) plugin for Big Walk (Unity 6, Mirror networking). It places a shared
screen in the world and plays a YouTube video on it for every player with the mod, in sync.
Design and rationale: `docs/ARCHITECTURE.md`. Background for engineers new to Unity/modding:
`docs/MODDING-PRIMER.md`.

## Current state
- All code in `src/BigScreen/` was written without access to the game. It parses as C# but
  has never been compiled against the game's interop assemblies nor run in-game.
- Expect a handful of interop naming fixes on the first build. `docs/HANDOFF.md` lists the
  likely suspects and how to check them.

## Build / deploy
```powershell
dotnet run --project tests/BigScreen.Tests   # pure-logic tests (no game needed, no NuGet)
.\scripts\build.ps1            # dotnet build against the auto-detected BepInEx folder
.\scripts\build.ps1 -Deploy    # + copy DLL into <BepInEx>\plugins\BigScreen (game must be closed)
.\scripts\launch.ps1           # build, deploy, then start the game modded through Gale
.\scripts\game-state.ps1       # is it running, is it modded, what did we log
.\scripts\package.ps1          # Thunderstore zip in dist\
```
`launch.ps1` runs Gale's CLI (`--game big-walk --profile Default --launch --no-gui`); launching
from Steam does not attach the loader. Big Walk opens on a mic-check screen that needs one click
before the main menu, which no script can do for you. Past that, `Dev.AutoHost` takes it into the
session on its own.

The tests in `tests/BigScreen.Tests` cover the part of the shared queue that is pure logic: the
host-side index rules, the state message's field order, and `SyncState.Clone`'s deep copy. They
compile against hand-written shims (`tests/BigScreen.Tests/Shim.cs`), so they need no BepInEx
install and no NuGet packages - but that also means the queue logic there is a COPY of
`BigScreenController`/`Net/Protocol.cs`. Change one, change both.

Per-machine tool paths come from `.env` (gitignored; copy `.env.template`). Real environment
variables override it. Every key is optional - the scripts auto-detect first.
BepInEx folder resolution: `/p:BepInExPath`, else `Config.Build.user.props`, else Gale profile,
r2modman profile, game folder. See `Directory.Build.props`.

## Conventions
- Only touch Unity/IL2CPP objects on the main thread; background work posts to `Util/MainThread`.
- Poll IL2CPP state (e.g. `VideoPlayer.isPrepared`) instead of subscribing to IL2CPP events.
- Wrap every game call in try/catch with a log line; a game update must degrade, not crash.
- Never create networked (Mirror-spawned) objects; the screen is local-only on modded clients.
- Call Mirror's `NetworkWriterExtensions` / `NetworkReaderExtensions` statically.
- Keep `Plugin.Version`, `<Version>` in the csproj, and `thunderstore/manifest.json` in sync.
- Do not commit anything from `BepInEx\interop` or decompiled game output.

## Logs
BepInEx log: `<BepInEx>\LogOutput.log`. Our lines are prefixed `[Info   :BigScreen]` etc.
Set `Debug.Diagnostics = true` in `BepInEx\config\dev.h223chen.bigscreen.cfg` for a status line
every 2 s.
