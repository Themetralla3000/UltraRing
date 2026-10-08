# Changelog

## Unreleased / 0.3.0

Split into two repositories. The ULTRAKILL side now lives in the reusable
[ULTRAKILL Crossover Bridge](https://github.com/Themetralla3000/ultrakill-crossover-bridge) kit (v0.1.0), consumed here as the git submodule `bridge/`.
UltraRing is now the Elden Ring host integration: Minecraft Ring's host DLLs, Elden Ring install/launch/restore, docs.

- **Removed** `src/UltraRing.Link`, `src/UltraRing.Ultrakill`, `tools/UltraRing.FakeHost`, `tests/UltraRing.Link.Tests` and `UltraRing.sln`
  (now `UltrakillBridge.Protocol`, `UltrakillBridge.Guest`, the kit's fake host and tests).
- **Build.ps1** builds the host DLLs, then calls `bridge\scripts\Build.ps1`; the guest is staged at `bridge\dist\guest`
  (was `runtime\ultrakill-bepinex`). Needs `git clone --recursive` / `git submodule update --init`.
- **Install/Restore/Launch/Stop** delegate the ULTRAKILL part to the kit's `Install-Guest`, `Uninstall-Guest`, `Launch-Guest`, `Stop-Guest`;
  `-FakeHost` uses the kit's `Run-FakeHost.ps1`. An `installation.json` from 0.2.0 keeps working.
- **Renamed:** plugin GUID `dev.ukbridge.guest`, config `dev.ukbridge.guest.cfg` (Build.ps1 copies your old config over once),
  `ULTRARING_WINDOW_MODE` -> `UKBRIDGE_WINDOW_MODE`, bridge dir variable `UKBRIDGE_DIR` (`ERMC_DIR` is still read).
- Research notes in `docs/research/` are kept as history; current docs are in the kit.

## 0.2.0 — 2026-10-08

First version played in Elden Ring (only lightly tested so far).

- **Camera and stand-in:** Elden Ring renders from V1's eyes; the invisible Tarnished follows V1's feet and facing and takes enemy aggro.
- **Terrain:** host raycasts rebuilt as ULTRAKILL colliders — floors, slopes, stairs, walls from knee/chest/head probes, ceilings;
  look-ahead sampling along V1's velocity; per-zone terrain cache on disk; F10 debug view inside Elden Ring.
- **Combat:** every ULTRAKILL weapon, punch and slam hits Elden Ring enemies through invisible proxies; damage converted to a share of
  max HP; style, freshness and blood healing; coins; whiplash pulls V1 to enemies; timed punch parries host attacks.
- **Damage in and shared life:** stand-in HP loss becomes V1 damage (dash i-frames dodge it); deaths shared both ways; recall to the
  Site of Grace after respawns.
- **Interactions:** Elden Ring's prompts on V1's HUD, performed with V.
- **Compositing:** V1's viewmodel, effects and HUD captured with dedicated cameras and drawn into Elden Ring's frame.
- **Window and focus:** borderless, almost transparent input window owned by Elden Ring; F8 control switch; Elden Ring forced to
  borderless mode; `Focus.bat` and `Stop.bat` recovery helpers.
- **Tooling:** `Build.ps1`, `Install.ps1`, `Launch.ps1` (incl. `-FakeHost`), `Restore.ps1`; a WinForms fake host for development.

## 0.1.0 — 2026-10-07

Initial scaffold: protocol mirror of Minecraft Ring's `bridge_protocol.h`, guest plugin skeleton, layout tests, research notes.
