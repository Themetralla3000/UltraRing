# UltraRing

**ULTRAKILL's V1 in the Lands Between — both games running at once.**

UltraRing runs real ULTRAKILL alongside Elden Ring. ULTRAKILL drives the player —
movement, dashes, slides, weapons, parries and HUD — while Elden Ring keeps its own
world, enemies and saves and draws the combined frame. A Risk of Rain 2 host is planned
on the same protocol.

> **Status: early development.** Nothing is playable yet. See the roadmap below.

## How it works

UltraRing follows the architecture of [Minecraft Ring](https://github.com/siddoff/Minecraft-Ring):
two complete games connected through shared memory.

| Role | Game | Responsibilities |
| --- | --- | --- |
| Host | Elden Ring (native DLL from Minecraft Ring, unchanged) | World, enemies, camera override, hidden stand-in that takes enemy hits, terrain ray/contact queries, D3D12 compositing |
| Guest | ULTRAKILL (BepInEx plugin, this repo) | V1 movement and weapons, invisible collision built from host terrain, enemy proxies, frame capture, input window |

The guest speaks the exact `bridge_protocol.h` layout used by Minecraft Ring's Fabric mod,
so the Elden Ring host DLL needs no changes. Because the protocol is host-agnostic, a
Risk of Rain 2 host can later be written as a BepInEx plugin implementing the same contract.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/UltraRing.Link/` | C# mirror of the shared-memory protocol (bridge.shm, frames.shm) |
| `src/UltraRing.Ultrakill/` | ULTRAKILL guest plugin (BepInEx 5) |
| `tests/UltraRing.Link.Tests/` | Protocol layout checks against the C header's static_asserts |
| `tools/` | `Build.ps1`, `UltraRing.FakeHost` (fake Elden Ring host for tests) |
| `Install.ps1`, `Launch.ps1`, `Restore.ps1` | Game-folder install, launcher for both games, undo |
| `docs/research/` | Host contract, ULTRAKILL internals and toolchain notes |
| `external/` | Pinned Minecraft Ring checkout (not committed; fetched by tooling) |

## Getting started

All commands run in PowerShell from the repository root. Nothing is installed into a game folder until `Install.ps1`.

1. **Build** (`tools\Build.ps1`): fetches the pinned Minecraft Ring source and the LLVM-MinGW / BepInEx 5.4.23.5 toolchains if
   missing (needs `git`, Python 3 and the .NET 8 SDK; ULTRAKILL must be installed because the plugin compiles against its
   assemblies), builds the Elden Ring host DLLs, builds `UltraRing.sln`, and stages a BepInEx runtime for ULTRAKILL at
   `runtime\ultrakill-bepinex\` (BepInEx core + `plugins\UltraRing\`).
2. **Install** (`.\Install.ps1`, optional `-EldenRingDir "...\ELDEN RING\Game" -UltrakillDir "...\ULTRAKILL"`; the defaults are the Steam paths):
   - Elden Ring: like Minecraft Ring, it refuses to run while the game is open, backs up `%APPDATA%\EldenRing` and moves
     conflicting mod files (Seamless Coop, ReShade, ...) into `backups\<timestamp>`, copies `dinput8.dll` and
     `erbridge\erbridge_core.dll`, and writes `steam_appid.txt`.
   - ULTRAKILL: copies only BepInEx's `winhttp.dll` and a `doorstop_config.ini` with `enabled=false`, so a normal Steam launch stays
     vanilla. BepInEx and the plugin live in `runtime\ultrakill-bepinex` and are switched on by the launcher only.
   - Everything is recorded in `installation.json`. Never take a modded Elden Ring online.
3. **Launch** (`.\Launch.ps1`, or `start.bat` / `Play.bat`): starts `eldenring.exe` directly (no EAC) with
   `ERBRIDGE=1` and `ERMC_DIR=runtime`, waits for the host bridge (90 s), then starts ULTRAKILL with
   `--doorstop-enabled true --doorstop-target-assembly <repo>\runtime\ultrakill-bepinex\BepInEx\core\BepInEx.Preloader.dll -screen-fullscreen 0 -popupwindow`.
   In Elden Ring choose Continue. ULTRAKILL's window sits borderless and almost invisible over Elden Ring's, owned by it, and receives
   the keyboard and mouse. **F8** hands control to Elden Ring and back (F8 in either game).
4. **Restore** (`.\Restore.ps1`): removes the bridge files from both game folders and puts back whatever Install moved away.
   Saves are never deleted or overwritten (the save backup is only a copy).

### Testing without Elden Ring

`.\Launch.ps1 -FakeHost` starts `tools\UltraRing.FakeHost` (a fake host speaking the same protocol: window, terrain rays, enemies,
a software-rendered scene with the guest frames composited) in place of Elden Ring, then ULTRAKILL as above. Only the ULTRAKILL side of
Install is needed (`.\Install.ps1 -SkipEldenRing`). Press F8 in the FakeHost window to switch control; resize it to test the window glue.

### Window overlay notes

- `[Rendering] WindowMode` in `BepInEx\config\dev.ultraring.ultrakill.cfg` (or the environment variable `ULTRARING_WINDOW_MODE`): `Layered`
  (default, constant opacity 1/255 like Minecraft Ring), `Region` (full-size window clipped to one pixel) or `Tiny` (a 1x1 window).
  Switch to `Region` if the layered window shows ULTRAKILL opaque over Elden Ring; the plugin falls back to it by itself if Windows rejects the opacity.
- Set `InputOverlay = false` to keep ULTRAKILL as a normal window (debugging).

## Roadmap

1. **v0.1 – Walk:** launcher for both games, input window above Elden Ring, Elden Ring camera driven by V1, hidden stand-in, terrain collision.
2. **v0.2 – Fight:** enemy proxies, damage both ways, shared life.
3. **v0.3 – Look right:** viewmodel/effects/HUD composited with depth, F8 control switch.
4. **v0.4:** interactions (doors, graces, items); Risk of Rain 2 host.

## Requirements

- Windows x64, Elden Ring app ver. 1.17.1 (`eldenring.exe` 2.7.1.0), ULTRAKILL (Steam).
- .NET 8 SDK, Python 3, LLVM-MinGW (for the host DLL), BepInEx 5.4.23.x.

Offline single-player only. The host DLL refuses to run with Easy Anti-Cheat; never take
a modded Elden Ring online.

## Credits

Built on [Minecraft Ring](https://github.com/siddoff/Minecraft-Ring) by siddoff, itself based on
[minecraft-crossover-bridge](https://github.com/justbustin/minecraft-crossover-bridge) by justbustin
(both MIT). UltraRing is an unofficial fan project, unaffiliated with New Blood Interactive,
Arsi "Hakita" Patala, FromSoftware, Bandai Namco or Hopoo Games. No game files are distributed.
