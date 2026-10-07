# Toolchain setup (2026-10-07)

All paths under `D:/homelab/bridge_mod`. No game folders were modified, no game launched.

## Pinned host source
- `external/minecraft-ring` = https://github.com/siddoff/Minecraft-Ring @ `711015afa67ab25c7b9597c68038718d1cef322e` ("Release Minecraft Ring 0.3.0"), detached HEAD.
  `git clone <url> external/minecraft-ring && git -C external/minecraft-ring checkout 711015a...`

## LLVM-MinGW
- Release `20261006` (latest stable), asset `llvm-mingw-20261006-ucrt-x86_64.zip` (Windows x86_64 host, UCRT),
  sha256 `317492c456aa27ee607a5919f1d2d38dcdc1112516a24d0bf4b00d078f52d17a`. clang 23.1.3.
- Archive: `.tools/downloads/llvm-mingw/`; extracted to `.tools/compiler/llvm-mingw-20261006-ucrt-x86_64/`.
- `build_native.py` globs `<repo>/.tools/compiler/*/bin/clang++.exe` (first match), so there must be exactly one
  compiler folder. Junction (no admin needed):
  `New-Item -ItemType Junction -Path external/minecraft-ring/.tools/compiler -Target D:\homelab\bridge_mod\.tools\compiler`

## Native build
- `build_native.py` reviewed: only compiles 10 C++ files + 4 MinHook C files with clang to `build/objects/`, links
  `dist/dinput8.dll` and `dist/erbridge_core.dll` (static, stripped). No network, no file deletion, no installs, no
  game-folder access.
- `cd external/minecraft-ring && python tools/build_native.py` -> success, no warnings/errors.
  Outputs: `dist/dinput8.dll` (331,264 B), `dist/erbridge_core.dll` (435,712 B).
- Note: `Install.ps1`/`Launch.ps1`/`start.bat` etc. of upstream were NOT run (they touch the game folder).

## BepInEx 5
- v5.4.23.5, `BepInEx_win_x64_5.4.23.5.zip`, sha256 `82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4`.
- Archive in `.tools/downloads/bepinex5/`; extracted to `.tools/bepinex5-x64/` (winhttp.dll, doorstop_config.ini,
  changelog.txt, BepInEx/core/{BepInEx.dll, BepInEx.Preloader.dll, 0Harmony*.dll, Mono.Cecil*.dll, MonoMod.*.dll,
  HarmonyXInterop.dll, BepInEx.Harmony.dll}).
- Doorstop 4.x (changelog: "Upgrade Doorstop to version 4.5.0"). winhttp.dll is the proxy loader.
- Shipped `doorstop_config.ini`: `[General] enabled=true`, `target_assembly=BepInEx\core\BepInEx.Preloader.dll`,
  `redirect_output_log=false`, `boot_config_override=`, `ignore_disable_switch=false`; `[UnityMono]` search path /
  debugger options.
- Command-line overrides present in winhttp.dll (strings): `--doorstop-enabled`, `--doorstop-target-assembly`,
  `--doorstop-redirect-output-log`, `--doorstop-boot-config-override`, `--doorstop-mono-dll-search-path-override`,
  `--doorstop-mono-debug-enabled|address|suspend`, `--doorstop-clr-runtime-coreclr-path`, `--doorstop-clr-corlib-dir`.
  Env vars: `DOORSTOP_DISABLE` (ignored if `ignore_disable_switch=true`).
- The RoR2 copy (r2modman-managed) of `winhttp.dll` is an older Doorstop 4.x (no `--doorstop-boot-config-override`).

## r2modman reference (Risk of Rain 2, read-only)
`doorstop_config.ini` in the game folder has `enabled=true`, `target_assembly=BepInEx/core/BepInEx.Preloader.dll`
(forward slashes), `ignore_disable_switch=false`, empty `[UnityMono]`/`[Il2Cpp]` options. So the file is
enabled by default there; r2modman additionally launches the game with
`--doorstop-enabled true --doorstop-target-assembly <path>` (per its known behaviour; not verified from this machine).
For UltraRing: ship `enabled=false` in the game folder's ini and pass `--doorstop-enabled true
--doorstop-target-assembly <path>` as Steam launch options / launcher arguments.
