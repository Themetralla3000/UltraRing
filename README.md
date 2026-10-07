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
| `docs/research/` | Host contract, ULTRAKILL internals and toolchain notes |
| `external/` | Pinned Minecraft Ring checkout (not committed; fetched by tooling) |

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
