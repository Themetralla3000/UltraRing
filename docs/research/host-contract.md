# UltraRing host <-> guest contract (derived from Minecraft Ring 0.3.0)

Source of truth: checkout of Minecraft Ring at commit `711015afa67ab25c7b9597c68038718d1cef322e` ("Release Minecraft Ring 0.3.0"), read-only, never executed.
Purpose: the ULTRAKILL BepInEx plugin (C#) must act as a drop-in replacement for the Minecraft Fabric guest, talking to the unchanged Elden Ring native host DLL.

Path legend for citations (all relative to the checkout root):

| Short | Full path |
| --- | --- |
| `H/` | `bridge-base/elden-ring/er-bridge/` (host, C++); files cited by bare name: `bridge_protocol.h`, `terrain_contacts.h`, `support_surface.h`, `player_facing.h`, `world_environment.h` (all in `H/include/`); `game.cpp`, `compositor.cpp`, `gpu_transport.h`, `shm.cpp`, `core.cpp`, `proxy.cpp`, `frame.cpp`, `log.cpp`, `depth_tracking.h`, `memutil.cpp` (all in `H/src/`) |
| `J/` | `bridge-base/elden-ring/mc-bridge/src/main/java/dev/ermc/bridge/` (guest, server/shared side) |
| `K/` | `bridge-base/elden-ring/mc-bridge/src/client/java/dev/ermc/bridge/client/` (guest, client side) |

Citation form `file:line` or `file:a-b`. "Host" = native DLL inside eldenring.exe. "Guest" = the Minecraft mod (to be replaced by our plugin). Everything is little-endian. Statements labelled **(UNVERIFIED)** or **(SPECULATION)** are not backed by the source.

---

## 0. Headline findings (read first)

1. **The terrain-contacts pipeline (`OFF_CONTACTS` 0x320000, `OFF_COLLISION_CONTROL` 0x350000) is dead code in this commit, on both sides.** The host never calls `shm_contacts()` or `shm_collision_control()` (defined only in `shm.cpp:66-67`; `terrain_contacts.h` is included by no host `.cpp`, only by `tools/test_native_terrain_collision.py:12`). The guest never calls `writeCollision` (defined `BridgeShm.java:181-196`, `ErLink.java:90-96`, never invoked) and never calls `NativeTerrainCollision.refresh` (defined `NativeTerrainCollision.java:70`; only `reset()` is called, `ErBridgeMod.java:67`). `docs/development.md:51-56` states "Additional native colliders, wall projection and landing position corrections are disabled." Terrain really works through the **ray mailbox** (`OFF_RAYS`). A guest must not depend on contacts. See section 5.
2. **Damage conversion is relative to target max HP, not absolute**: `erHp = ceil(mcAmount * maxHp / clamp(20*sqrt(maxHp/100), 10, 300))`, minimum 1 (`game.cpp:1434-1439`, `1633-1634`). A guest can send a "fraction of mob health" scaled by the same divisor to get exact control (section 6.2).
3. **The compositor compares guest depth to host depth in metres** after linearising guest depth with `mcNear`/`mcFar` from the slot header, assuming **OpenGL window depth in [0,1]** (not Unity reversed-Z) (`compositor.cpp:256-259, 296-300`). Guest must publish OpenGL-style depth and scale `mcNear/mcFar` into host metres (section 9.5).
4. **Memory path pixel order is BGRA bytes (B,G,R,A) with `swapRB=0`; GPU path is RGBA bytes with `swapRB=1`**, decided by slot flag bit3, not by a heuristic (`compositor.cpp:999, 1086`; `FramePassthrough.java:266, 382`).
5. **The compositor does NOT apply the 1000 ms control timeout**: it reads the control block with `control_snapshot` (no staleness check) (`compositor.cpp:950-952`). A guest that dies leaving `COMPOSITE` set keeps its last frame on screen forever. Clear the flags on exit.
6. **`totalStatusDamage` (hunter events 0x2C) is never written by the host** (no reference in `game.cpp`); `view[]`/`proj[]`/`STATE_MATRICES_VALID` are never filled; `depthIndex`, `CAPTURE_DEPTH`, `hostPresentPage` are unused on this Windows build. Do not rely on them.
7. Unity (and ER) are both left-handed, Y-up, +Z forward. **No Z mirror is needed for ULTRAKILL** (the Minecraft guest mirrors Z, `CoordMap.java:378-379, 398`). The host's yaw convention is Minecraft's though; conversion in section 3.4.
8. Host side effects to know about: it patches the Elden Ring FPS cap to 144 (`game.cpp:2386-2397`), patches the "poke" AtkParam row in memory (`game.cpp:1494-1507`), takes over the world clock/weather only when the guest writes `OFF_ENVIRONMENT` with flags (section 8).

---

## 1. Transport

### 1.1 Files and directories

| Item | Value | Source |
| --- | --- | --- |
| Runtime dir (host) | env `ERMC_DIR` if set (and < 32768 chars); else `%TEMP%\ermc` (`GetTempPathA` + `"ermc"`); created with `CreateDirectoryA` (single level only) | `log.cpp:8-22` |
| Runtime dir (Java guest) | system property `erbridge.dir`, else env `ERMC_DIR`, else `<java.io.tmpdir>/ermc` | `BridgePaths.java:5-9` |
| Runtime dir (Launch.ps1) | `$env:ERMC_DIR = <repo>\runtime`, created `-Force` | `Launch.ps1:6,10` |
| Control file | `<dir>\bridge.shm`, **8 MiB = 0x800000** (`ERMC_SHM_SIZE`) | `bridge_protocol.h:25`, `Protocol.java:13` |
| Frame file | `<dir>\frames.shm`, **0x17BB1300 = 398,136,064 bytes** (0x1000 + 3 * 0x7E90100) | `bridge_protocol.h:384-391` |
| Log | `<dir>\er-bridge.log` (append) | `log.cpp:39` |
| Crash | `<dir>\eldenring-crash.txt/.dmp` | `docs/development.md` (log table) |

Our C# guest MUST resolve the directory the same way: `ERMC_DIR` env, else `Path.GetTempPath() + "ermc"`. Note ULTRAKILL is not launched by `Launch.ps1`, so set `ERMC_DIR` explicitly in the launcher or rely on both sides using the identical temp fallback. (`GetTempPathA` includes a trailing backslash; the host appends `ermc` to it; equals `%TEMP%\ermc`.)

### 1.2 Who creates / maps each file

**bridge.shm** (either side may start first):

- Host: `shm_open()` runs twice per process: from the dinput8 loader thread (`proxy.cpp:192`) and again from core init (`core.cpp:58`); idempotent (`shm.cpp:9`). Steps: `CreateDirectoryA(dir)`, `CreateFileA(OPEN_ALWAYS, share R|W)`, `CreateFileMappingA(PAGE_READWRITE, size 8 MiB)` (extends the file), `MapViewOfFile(FILE_MAP_ALL_ACCESS)` (`shm.cpp:8-28`).
- Initialisation: if `magic != 0x434D484D` or `version != 1`, host does `memset(base, 0, 0x100000)` (i.e. everything below `OFF_RAYS`), writes `version=1`, `size=8 MiB`, then (after a barrier) `magic` (`shm.cpp:29-38`). The Java guest does the identical thing with a release store on magic (`BridgeShm.java:44-52`). **Zeroing only covers [0, 0x100000)**; rays/entities/damage/passages/platforms/contacts/collision-control regions are not cleared by init (stale data from a previous session survives, e.g. a stale damage ring `write/read`; see 5.1 and 6.3 for the consequences).
- After mapping, host does `respSeq = reqSeq` on the debug-command mailbox to drop an unanswered request (`shm.cpp:40`). It does NOT do this for the ray mailbox (see 5.1).
- Host sets `hostPid`, `hostStartMs`, resets `coreGeneration=0`, `coreStatus=0` when `hostPid` differs from the current pid (`shm.cpp:41-46`).
- Guest sets `mcPid` and `mcStartMs` at every attach (`BridgeShm.java:53-54`).
- Guest side file creation: `FileChannel.open(READ, WRITE, CREATE)`, if size < 8 MiB write one byte at offset 8 MiB-1 (extends/sparse), `map(READ_WRITE, 0, 8 MiB)`, little-endian (`BridgeShm.java:29-42`).

**frames.shm** (guest creates; host only opens an existing file):

- Guest: create dir, create/extend to `FILE_SIZE`, map R/W, then **invalidate**: release-store `magic=0`, copy `frameCounter = max(frameCounter, latestFrameId@0x10)`, zero the 0x100-byte header of each of the 3 slots, write `version=3`, finally release-store `magic=0x524D484D` (`FramePassthrough.java:163-195`).
- Host: opens lazily from the Present hook, at most every 2000 ms: `CreateFileA(OPEN_EXISTING)` (fails silently if the guest has not created it: "Minecraft creates it"), requires file size >= `ERMC_FRAMES_FILE_SIZE`, maps the whole 398 MB, then requires `magic == 0x524D484D && version == 3`, else unmaps and logs once (`compositor.cpp:141-173`). The host maps once; it never re-validates magic after mapping.
- Therefore the guest must create the file with the **full 0x17BB1300 size** before the host first tries; a smaller file is rejected (the host retries every 2 s).

### 1.3 Activation gate on the host (proxy loader)

`proxy.cpp` is the `dinput8.dll` proxy dropped next to `eldenring.exe`. Sequence:

1. `DllMain(PROCESS_ATTACH)`: only if the exe path (lower-cased) contains `eldenring.exe` (`proxy.cpp:84-89, 229-235`) it spawns the loader thread.
2. Loader thread: `shm_open()` (creates/maps bridge.shm even if later refusing) (`proxy.cpp:192`), bridge dir = `<dll dir>\erbridge` (`proxy.cpp:194-198`).
3. `launch_allowed()` (`proxy.cpp:92-118`): env var `ERBRIDGE` must exist and equal exactly `"1"` (8-byte buffer, `strcmp`). Then a module snapshot of the process; if any loaded module name (case-insensitive) contains `easyanticheat` or `eosac` it refuses. **The EAC check is a one-shot snapshot at attach time**, not a continuous watch. If refused the loader thread returns and the DLL stays passive (it still forwards DirectInput calls).
4. Waits until `d3d12.dll` and `dxgi.dll` are loaded and a visible window of the process has a client area >= 320x200 (timeout 10 min -> returns 1) (`proxy.cpp:202-210`), then `Sleep(1500)`.
5. `coreReloadAck = coreReloadReq`; `load_core()` (`proxy.cpp:214-215`): copies `<Game>\erbridge\erbridge_core.dll` to `<Game>\erbridge\loaded\core_<pid>_<gen>.dll`, `LoadLibraryA`, resolves `erb_core_init`/`erb_core_shutdown`, calls init; writes `coreGeneration=gen`, `coreStatus = 1` (ok) or `-4` (init returned false); load failures give `coreStatus` -1 (copy), -2 (LoadLibrary), -3 (exports) (`proxy.cpp:153-188`).
6. Then loops every 50 ms: if `coreReloadReq != coreReloadAck`, unload + reload the core and ack (hot reload; nothing in the Java guest triggers this; dev tooling only) (`proxy.cpp:216-225`).
7. `erb_core_init` (`core.cpp:56-72`): `shm_open`, `crash_init`, `MH_Initialize`, `game_init()` (returns false on **any** signature mismatch of CastRay/RegisterTask/Kill, i.e. any Elden Ring build other than 2.7.1.0 / App Ver 1.17.1, `game.cpp:2398-2408`; returns false if the task page can't be made), then optional `compositor_init()`, then starts a worker thread (1 ms loop) and a pacer thread (publishes state at ~16 ms until the game tick task claims the frame source, `core.cpp:33-52`).

`steam_appid.txt`: Install.ps1 writes `1245620` (ASCII) into the game dir so that `eldenring.exe` started directly (without Steam/EAC launcher) can init Steam (`Install.ps1:29`). Launch.ps1 starts `eldenring.exe` directly (see 1.4), so EAC (`start_protected_game.exe`) is never involved. The host source never reads steam_appid itself.

### 1.4 Deployment and launch scripts

`Install.ps1 -GameDir <dir>` (`Install.ps1:1-31`):

1. Requires `eldenring.exe` present and no running `eldenring` process.
2. Backs up `%APPDATA%\EldenRing` to `<repo>\backups\<timestamp>\saves`.
3. Moves any of these names out of the game dir into the backup: `dinput8.dll, dxgi.dll, d3d11.dll, winmm.dll, version.dll, modengine2.dll, modengine2, mods, mod, SeamlessCoop, ersc_launcher.exe, ersc.dll, ersc_settings.ini, mod_loader_config.ini, ReShade.ini, ReShadePreset.ini, reshade-shaders, erbridge` (line 15).
4. Copies `steam_appid.txt` backup, creates `<Game>\erbridge\`, copies `dist\dinput8.dll` -> `<Game>\dinput8.dll` and `dist\erbridge_core.dll` -> `<Game>\erbridge\erbridge_core.dll`, writes `1245620` to `<Game>\steam_appid.txt` (lines 26-29).
5. Writes `<repo>\installation.json` (`game_dir, backup, moved_mods, installed_at, native_sha256`).

`Launch.ps1` (`Launch.ps1:1-74`): reads `installation.json`; sets `ERMC_DIR=<repo>\runtime`, `ERBRIDGE=1`; if no matching `eldenring` process, `Start-Process eldenring.exe -WorkingDirectory <game_dir>`; polls the first 128 bytes of `bridge.shm` every 500 ms for up to 90 s: requires `magic==0x434D484D`, `hostPid(0x20) == launched pid`, `coreStatus(0x44)==1`; `coreStatus<0` aborts. Then, if `mcPid(0x24)` names a live `java.exe` whose start time is within 120 s of `mcStartMs(0x30)`, it assumes the guest is already running. Otherwise starts Minecraft via Gradle `runClient`. **Our plugin is not started by this script; our launcher must set `ERMC_DIR` and (if it owns elden ring startup) `ERBRIDGE=1` and spawn `eldenring.exe` directly with working dir = game dir.**

Build: `python tools/build_native.py` compiles `proxy,log,shm,core,crash,memutil,debugcmd,frame,game,compositor` with clang (LLVM-MinGW) into `dist/dinput8.dll` and `dist/erbridge_core.dll`; the repo ships **no binaries** (`tools/build_native.py:1-30`). To reuse the host "unchanged" we must build it from this commit (or obtain a binary of it) **(note: the contacts pipeline in section 5.2 is absent from this source, so a differently built binary could behave differently; UNVERIFIED)**.

---

## 2. bridge.shm layout, field ownership, heartbeats, liveness

### 2.1 Region map (all absolute offsets in bridge.shm)

| Offset | Region | Writer | Notes |
| --- | --- | --- | --- |
| 0x000000 | `ErmcHeader` (0xC0 bytes) | both (per field) | `bridge_protocol.h:96-138` |
| 0x000100 | `ErmcGameState` (0x114) | host | seqlock, section 2.3 |
| 0x000800 | `ErmcControl` (0x64) | guest | seqlock, section 4 |
| 0x000A00 | `ErmcHunterEvents` (0x30) | host | seqlock, section 6.4 |
| 0x000B00 | `ErmcEnvironment` (0x18) | guest | seqlock, section 8 |
| 0x001000 | `ErmcCmdBlock` (0x1000) | dev tools | debug read/write/scan mailbox; not needed |
| 0x002000 | cmd response (up to 0xFE000) | host | |
| 0x100000 | Ray mailbox | guest requests / host answers | section 5.1 |
| 0x200000 | `ErmcEntityTable` | host | 0x10 + 256*0x80; section 6.1 |
| 0x280000 | `ErmcDamageQueue` | guest | 0x10 + 256*0x20; section 6.2 |
| 0x300000 | `ErmcPassageTable` | host | 0x10 + 64*0x20 |
| 0x310000 | `ErmcPlatformTable` | host | 0x10 + 169*0x20 |
| 0x320000 | `ErmcTerrainContacts` | (host, unimplemented) | 0x28 + 4096*0x20; section 5.2 |
| 0x350000 | `ErmcCollisionControl` (0x30) | (guest, unimplemented) | section 5.2 |

Struct packing: everything after `#pragma pack(push, 4)` (`bridge_protocol.h:83`) packs to 4; the earlier structs (contacts, collision control, platform) are plain 4-byte-field structs, so their offsets are the same.

### 2.2 `ErmcHeader` fields (0xC0 bytes)

| Off | Field | Type | Writer | Semantics | Source |
| --- | --- | --- | --- | --- | --- |
| 0x00 | magic | u32 | whoever initialises first | `0x434D484D` ("MHMC", kept from the MH:World bridge) | `bridge_protocol.h:23` |
| 0x04 | version | u32 | init | 1 | :24 |
| 0x08 | size | u32 | init | 8 MiB | |
| 0x0C | reserved0 | u32 | - | unused | |
| 0x10 | hostHeartbeat | u64 | host | = `on_frame` counter; +1 per Elden Ring game frame (from the tick task, group 117) or per ~16 ms from the pacer thread until the tick task claims the source | `frame.cpp:109`, `game.cpp:2319-2320`, `core.cpp:46-52` |
| 0x18 | mcHeartbeat | u64 | guest | +1 per guest render frame. **Host reads it only for the environment 2000 ms rule** | `Overlay.java:171-172`, `game.cpp:237-245` |
| 0x20 | hostPid | u32 | host | | `shm.cpp:41-45` |
| 0x24 | mcPid | u32 | guest | | `BridgeShm.java:53` |
| 0x28 | hostStartMs | u64 | host | unix ms | `shm.cpp:43` |
| 0x30 | mcStartMs | u64 | guest | unix ms | `BridgeShm.java:54` |
| 0x38 | coreReloadReq | u32 | dev tool | hot-reload request | `proxy.cpp:217` |
| 0x3C | coreReloadAck | u32 | host loader | | |
| 0x40 | coreGeneration | u32 | host loader | | `proxy.cpp:184` |
| 0x44 | coreStatus | i32 | host loader | 1 running, 0 not loaded, <0 error | `proxy.cpp:149,185` |
| 0x48 | mcSwitchReq | u32 | **host** | +1 when F8 is pressed in Elden Ring (ER->guest "take control back") | `game.cpp:2263-2267` |
| 0x4C | hostFocusReq | u32 | **guest** | +1 = "bring the ER window to the front" | `BridgeShm.java:75-78`, `game.cpp:2244-2262` |
| 0x50 | hostTaskPage | u64 | host | RWX page address, survives reloads | `game.cpp:2380` |
| 0x58 | hostLife | u32 | host | +1 each time the Tarnished becomes usable at a place the guest did not choose (after load, respawn, game warp); guest must recall before driving | `game.cpp:1203` |
| 0x5C | mcDeaths | u32 | guest | +1 each time the guest's player dies | `BridgeShm.java:114-117` |
| 0x60 | hostDeaths | u32 | host | +1 each time the standing-in Tarnished died in ER | `game.cpp:1216` |
| 0x64 | debugFlags | u32 | dev tool | bit0 compositor test pattern (`compositor.cpp:945`), bit1 host depth is standard-Z not reversed (`:1072`), bit2 spawn test soldier, bit3 remove it (`game.cpp:1734-1741`), bit4 press Esc (`core.cpp:19`). Leave 0. | |
| 0x68 | hostPresentPage | u64 | - | declared, never referenced on this build | |
| 0x70 | mcActionReq | u32 | guest | +1 = perform the action ER offers now | `BridgeShm.java:88-92` |
| 0x74 | hostActionAck | u32 | host | = mcActionReq once handled | `game.cpp:1898` |
| 0x78 | hostActionResult | i32 | host | 1 done, 0 nothing/blocked/grayed, -1 unsupported build, -2 needs ER input (ladders) | `game.cpp:1792-1810` |
| 0x7C | hostPromptSeq | u32 | host | +1 before and +1 after each prompt rewrite (odd = writing) | `game.cpp:1874-1878` |
| 0x80 | hostPrompt[64] | char | host | UTF-8 NUL-terminated prompt text | `game.cpp:1861-1880` |

Seqlock convention: writer bumps `seq` to odd, writes payload, bumps to next even; readers retry if `seq` odd or changed. **A writer must never leave `seq == 0` after publishing**: the host treats `seq==0` as "never published" (`control_snapshot` returns `s1 != 0`, `shm.cpp:96`; environment skips `!seq`, `game.cpp:248`; guest `readState` returns `s1 != 0`, `BridgeShm.java:151`). The host uses plain stores + compiler barriers (TSO assumption, `shm.cpp:70-72`); C# must use `Volatile.Read/Write` or `Interlocked` + `Thread.MemoryBarrier` (x86 also, but the JIT may reorder).

### 2.3 `ErmcGameState` (host -> guest), offset 0x100, size 0x114

Published once per host game frame by `on_frame` using a local copy then `memcpy` of bytes [4, 0x114) between two `seq` bumps (`frame.cpp:77-112`).

| Off | Field | Type | Meaning | Source |
| --- | --- | --- | --- | --- |
| 0x00 | seq | u32 | seqlock | |
| 0x04 | flags | u32 | see below | |
| 0x08 | frame | u64 | host frame counter (same as heartbeat) | `frame.cpp:86` |
| 0x10 | camPos[3] | f32 | ER camera eye, **stable frame** | `game.cpp:2486-2488` |
| 0x1C | camTarget[3] | f32 | `pos + camera forward` (1 unit ahead), stable frame | `game.cpp:2486` |
| 0x28 | camUp[3] | f32 | camera up row | `game.cpp:2489` |
| 0x34 | fovYDeg | f32 | vertical FOV in degrees | `game.cpp:2490` |
| 0x38 | nearZ | f32 | camera near | `game.cpp:2492`; also feeds compositor `hostNear` |
| 0x3C | farZ | f32 | camera far | feeds compositor `hostFar` |
| 0x40 | aspect | f32 | camera aspect | |
| 0x44 | unitsPerMeter | f32 | **always 1.0** (Elden Ring: metres) | `game.cpp:2466` |
| 0x48 | playerPos[3] | f32 | Tarnished **feet** (physics position), stable frame; while standing in, the reach-assist offset is subtracted so it reports "where the guest player is" | `game.cpp:2500-2503` |
| 0x54 | playerQuat[4] | f32 | (x,y,z,w) physics rotation (rotation about +Y; local forward = -Z) | `game.cpp:2504`, `player_facing.h:6-7` |
| 0x64 | winX,winY,winW,winH | i32 | ER client area in screen coordinates | `frame.cpp:91-98` |
| 0x74 | bbW,bbH | u32 | "back buffer size" = client area size | `frame.cpp:96-97` |
| 0x7C | stageId | u32 | the **zone id** (not a "stage"); 0 when unknown / not alive; header comment about 504/101 is stale (MH:World leftover) | `game.cpp:2475`, `bridge_protocol.h:170` |
| 0x80 | view[16] | f32 | never filled (zeros) | |
| 0xC0 | proj[16] | f32 | never filled (zeros) | |
| 0x100 | supportEpoch | u32 | changes when support contact is lost/reacquired | `game.cpp:2476` |
| 0x104 | supportTravelY | f32 | cumulative vertical travel at the tracked support point (m) | `game.cpp:2479` |
| 0x108 | supportPos[3] | f32 | tracked floor position, stable frame | `game.cpp:2480` |

State flags (`bridge_protocol.h:141-152`):

| Bit | Name | Set when | Source |
| --- | --- | --- | --- |
| 0 | CAMERA_VALID | camera object found (and life ALIVE) | `game.cpp:2494` |
| 1 | PLAYER_VALID | main player found **and life == ALIVE and frame valid** | `game.cpp:2505` |
| 2 | WINDOW_VALID | window client rect fetched | `frame.cpp:98` |
| 3 | CAM_OVERRIDDEN | host applied a guest camera since the previous publish | `game.cpp:2496` |
| 4 | MATRICES_VALID | **never set** | |
| 5 | WINDOW_FOCUSED | ER window is foreground | `frame.cpp:99` |
| 6 | COMPOSITING | a composite was drawn within the last 500 ms | `compositor.cpp:378`, `game.cpp:2497` |
| 7 | PLAYER_DEAD | life == DEAD | `game.cpp:2472` |
| 8 | HOST_BUSY | life == DEAD, or not ALIVE within 60 s of the last ALIVE/DEAD tick (loading screen, settling) | `game.cpp:2472-2473` |
| 9 | SUPPORT_VALID | tracked moving/flat support (needs MOVE_HUNTER) | `game.cpp:2477-2478` |

When life != ALIVE the early return at `game.cpp:2474` leaves cam*, player*, stageId, support* at **zero**; only window fields, `unitsPerMeter=1.0` and flags remain.

### 2.4 Liveness / timeout master table

Host-side rules about the guest:

| Rule | Value | Effect | Source |
| --- | --- | --- | --- |
| Control `seq` unchanged | **1000 ms** | `control_active()` false: no camera override, stand-in released, platform tracking off. Last good snapshot is reused on torn reads (never drops for a torn read) | `game.cpp:546-565` |
| Control never published (`seq==0`) | - | no control at all | `shm.cpp:85-99` |
| Compositor reads control | **no timeout** | stale `COMPOSITE` flag keeps the last frame drawn | `compositor.cpp:950-952` |
| `mcHeartbeat` unchanged | **2000 ms** | environment override released (clock/weather returned to ER) | `game.cpp:236-244` |
| `mcDeaths` change honoured only if | Tarnished stood in within the last **2000 ms**, life ALIVE, not already dead | kill Tarnished (`Kill(chr,0)`) | `game.cpp:1170-1183` |
| Ray mailbox processing budget | ~**2 ms** per game frame, clock checked every 16 rays | batch finished across frames via `processed` | `game.cpp:2214-2222` |
| Platform cell scan | every **40 ms** while standing in | | `game.cpp:2103` |
| Support tracking | previous sample must be **<= 250 ms** old; ray window `0.1 + 24*min(dt,250ms)/1000` m; travel jump limit `0.05 + 24*dt` m | else support lost, `supportEpoch++` | `game.cpp:2002, 2013` |
| Moving-cell memory | 1500 ms (`old.moved`), 250 ms (re-trace) | | `game.cpp:2126, 2137` |
| Reach scan (doors) | worker, 1000 ms period, <= 50 ms per scan, 800-ish nodes; passages republished every 30 ticks | | `game.cpp:813-819, 748, 921` |
| World strike rate limit | 150 ms | | `game.cpp:1920` |
| Poke per-target rate | 250 ms | | `game.cpp:1619` |
| Pending kill retry | 2000 ms | | `game.cpp:1590` |
| Poke row repatch retry | 5000 ms | | `game.cpp:1907` |
| Action press latch | cleared after 500 ms if not consumed | | `game.cpp:1827` |
| Life settling | after load/respawn: >= 90 ticks and 45 still ticks; game warp (>10 m jump): 30/15; zone change or hot reload: 10/0; "still" = moved < 0.15 m, same zone, same Havok offset | | `game.cpp:1137-1153, 1190-1191, 1226-1229` |
| NoDead clamp | HP==1 for 3 consecutive ticks -> let it die | | `game.cpp:1208-1213` |
| HOST_BUSY tail | 60 s after last ALIVE/DEAD | | `game.cpp:2473` |
| `frames.shm` map retry | 2000 ms | | `compositor.cpp:144` |
| Fence wait before reuse of a ring slot | 50 ms (then skips the composite) | | `compositor.cpp:995` |
| Depth buffer candidate | must have been used within 2000 ms | | `depth_tracking.h:92` |
| Compositor hook discovery | worker retries every 1 s, up to 180 tries | | `compositor.cpp:1268-1275` |
| Loader wait for window | 10 min; +1.5 s settle | | `proxy.cpp:206, 211` |
| Core shutdown waits | 2 s for worker threads, up to ~2 s for in-flight hooks | | `core.cpp:75-92` |

Guest-side rules (Minecraft reference implementation; our guest should keep equivalents):

| Rule | Value | Source |
| --- | --- | --- |
| Host alive iff `hostHeartbeat` changed within | **2000 ms**; first sample after attach never counts as live (stale counter) | `ErLink.java:14, 44-53` |
| Reopen bridge.shm attempt | 1000 ms | `ErLink.java:32` |
| Recall settled | recall served **and >= 400 ms ago** (`recallSettled(400)`) before driving camera/hunter, before overlay shows, before platform logic | `TerrainManager.java:220`, `CameraSync.java:70`, `Overlay.java:214` |
| Ray batch stall log | 3000 ms (only logs; mailbox ownership is **kept** until the host answers) | `TerrainManager.java:88, 594-598` |
| Overlay "in world" grace | 3000 ms after last PLAYER_VALID; deactivate only after 1500 ms of not wanting | `Overlay.java:198, 208` |
| Action request pending | 2000 ms | `ErBridgeClient.java:293` |
| Terrain column refresh | 2000 ms (`REFRESH_MS`) | `TerrainPrefetch.java:7` |
| Status damage drain | every 20 server ticks | `CombatBridge.java:108` |
| Native contacts validity | 2000 ms (dead path) | `NativeTerrainCollision.java:295` |
| Frame capture gating | at most one capture per host frame value | `FramePassthrough.java:248-252` |

---

## 3. Coordinate frames

### 3.1 Handedness and units

- Host world = Elden Ring physics space: **metres, left-handed (D3D), +Y up, +Z forward** (`CoordMap.java:359-360`; the host builds the camera matrix with `right = up x forward`, `game.cpp:599-604`).
- `unitsPerMeter = 1.0` (`game.cpp:2466`). All lengths in the protocol are metres.
- Camera matrix stored by the game: rows right, up, forward, position (`game.cpp:90`).
- ULTRAKILL/Unity: also left-handed, +Y up, +Z forward. A same-handedness guest only needs a translation (and unit scale) between guest world and host stable frame. (Minecraft's right-handed world needed `flipZ`.) **(UNVERIFIED for ULTRAKILL: world unit size. Do not assume 1 unit = 1 m; measure the player height/walk speed and pick a scale. All scale-dependent host constants are listed in 10.4.)**

### 3.2 The "stable frame" and `stageId`/zone

Elden Ring's Havok space re-centres as the world shifts, so every position that crosses the protocol is in a **stable frame per zone** (`game.cpp:11-15`):

- Open world (area 60 or 61, `blockId >> 24`): global = `blockRelative + 256 * (gridX, 0, gridZ)` with `gridX = (blk>>16)&0xFF`, `gridZ = (blk>>8)&0xFF`; zone = `area << 24` (0x3C000000 / 0x3D000000) (`game.cpp:409-420`).
- Everything else (legacy dungeons, caves): block-relative coordinates; zone = full block id (u32) (`game.cpp:419`).
- `blockId` is read at player+0x6D0, position at +0x6C0 (`game.cpp:67-68`). Block ids `0` and `0xFFFFFFFF` mean loading screen / no block: `block_valid()` false, frame invalid, life NONE (`game.cpp:389-392, 2289-2295`).
- `Havok = stable + offset`. `offset = HavokPos - blockPos(-bias)` is re-derived only by whole multiples of 8 m (Havok re-centre) once a zone has started (`game.cpp:443-529`). A new zone (loading screen, new character) starts fresh; **a seamless block change keeps the old zone id** and shifts coordinates by `g_bias` so the guest does not see a zone jump (`game.cpp:394-406, 504-507`).
- The guest must treat `stageId` (state 0x7C) as an opaque key: when it changes (and is not 0/-1) the guest must re-anchor (Minecraft: new region = 32768 blocks away per zone, `CoordMap.java:376`, `TerrainManager.java:816-856`). All guest->host positions are interpreted in the **current** zone's stable frame (`to_havok` adds the current offset, `game.cpp:531-536`); the guest has no way to name a zone in control, so on a zone change it must stop sending (flags = 0) until `stageId` matches its anchor (Minecraft: `CameraSync.ready` requires `map.zone() == STATE.stageId`, `CameraSync.java:61`). Ray results, entity table, passages and platforms are likewise only valid for the zone in force (`ErmcPassageTable.zone`, `ErmcPlatformTable.zone`).

Guest-chosen anchor (Minecraft): first valid state per zone pins `playerPos` (host stable) to a fixed guest point; `toMc = (x-ax)*s + originX, (y-ay)*s + 100, +/-(z-az)*s + 0.5` (`CoordMap.java:395-399`). For Unity the equivalent is a pure offset; anchors persisted per world (`TerrainManager.java:913-977`) - optional.

### 3.3 Camera fields (control block)

`camPos` = eye position; `camTarget` = look-at point (host only uses `normalize(camTarget - camPos)` as forward, so any distance works); `camUp` = up vector (roll honoured) all in stable frame metres; `fovYDeg` = **vertical** FOV, applied only if `5 < fov < 170` else the game's FOV is left (`game.cpp:589-609`). Host computes `fwd = norm(target-pos)`, `right = norm(cross(up, fwd))`, `up = cross(fwd, right)` and writes the 4x4 (rows right/up/fwd/pos) into the game's camera at `+0x10`, FOV (rad) at `+0x50`. **Aspect, near, far stay the game's** (`game.cpp:91-94`): the guest frame must be rendered at ER's window aspect (`winW/winH` from state) or it will be stretched (the compositor stretches the guest image over the full back buffer, `compositor.cpp:248-255, 1122-1128`).

The Minecraft guest sends `target = eye + lookVector` and `up = camera.getUpVector()`, then maps all three through `toHost` / `dirToHost` (`CameraSync.java:147-169`).

### 3.4 `hunterPos` and `hunterYawDeg`

- `hunterPos` = the guest player's **feet** (bottom of the body) in stable frame. Evidence: the host writes it directly into the Tarnished's physics origin (`game.cpp:1034-1044`), entity `pos` from the same origin is documented "origin (feet)" and boxes are built upward from it (`bridge_protocol.h:301`, `game.cpp:1381`), platform/support logic compares `hunterPos.y` with floor ray hits (`game.cpp:2018, 2048`: feet-floor must lie in [-0.45, +0.4] m when GROUNDED, up to +4 m otherwise). The Minecraft guest sends the interpolated feet (`CameraSync.java:173-179`).
- The host may move the stand-in off `hunterPos` ("reach assist"): when the guest faces an animated map object (door, lever, chest, lift) within 2 m the Tarnished is placed 0.45 m short of the surface, squared to it (`game.cpp:660-901`). `state.playerPos` is reported with that offset removed.
- `hunterYawDeg`: **Minecraft yaw in degrees**. Exact host formula (`player_facing.h:12-16`, used at `game.cpp:1050-1053`):

  ```
  y        = hunterYawDeg * pi/180
  forward  = (-sin y, -cos y)            // (x, z) in the host stable frame; MC yaw 0 faces host -Z
  theta    = atan2(-forward.x, -forward.z) = y     // identity: theta == the yaw in radians
  q        = (0, sin(theta/2), 0, cos(theta/2))     // written to the physics quat and the interpolated quat
  ```
  `tools/test_player_facing.py` verifies that rotating the character's local forward `(0,0,-1)` by that quaternion yields `(-sin y, -cos y)`.
  For a **Unity-handed guest whose frame equals the host frame up to translation**: if the guest's horizontal facing is `f=(fx,fz)` (unit, host axes), send `hunterYawDeg = deg(atan2(-fx, -fz))`. Equivalently, with Unity `eulerAngles.y = psi` (forward `(sin psi, cos psi)`), send `hunterYawDeg = psi + 180`. If the guest frame is rotated relative to the host frame by `r` about +Y (anchor rotation) add that rotation to the facing before converting.
  Guest-side counterpart for recall: Minecraft turns `playerQuat` into a yaw with `a = 2*atan2(qy, qw)`, `forward = (-sin a, -cos a)` (`TerrainManager.java:867-872`).
- The pose is applied to physics (`kPhysPos` 0x70, `kPhysPosLast` 0x80 as `vec4 w=1`, `kPhysProxyUpdate` 0x91=1, `game.cpp:80-86, 1042-1044`).

### 3.5 `poseLag`, `mcFrame`, `poseId` bookkeeping

- Guest assigns a monotonically increasing frame id per captured frame (Minecraft seeds it with `currentTimeMillis()*1024` and `max(...,latestFrameId@frames.shm+0x10)` so ids survive guest restarts, `FramePassthrough.java:118, 181`).
- It writes pixels into a slot with `frameId = poseId = id`, and **only afterwards** publishes the control block with `mcFrame = id` (`FramePassthrough.java:385-398`).
- Each game frame the host camera task calls `compositor_note_applied_pose(c.mcFrame)` (only when a camera override was actually applied) pushing the id into an 8-entry ring (`game.cpp:607`, `compositor.cpp:183-198`).
- At Present: `poseId = history[(pos-1-lag) & 7]` where `lag = ctrl.poseLag`, clamped to `<= pos-1` and `<= 6`; `0` lag = newest applied pose; no history -> 0 (`compositor.cpp:192-198, 983`). The guest default is `poseLag = 1` (`ControlState.java:233`, `CameraSync.java:42`). Meaning (header): "game frames between applying a pose and presenting it" (`bridge_protocol.h:199`).
- `pick_slot(poseId)` (`compositor.cpp:200-213`): scans the 3 slots; skips odd `seq`, zero/oversized width/height; returns the slot with `poseId == wanted` immediately; otherwise the slot with the greatest `poseId` **less than** wanted; null if none. Stats logged as "exact / older / missing" every 300 presents (`:217-226`).
- If no slot qualifies and the compositor already holds a frame, it re-draws the previous textures (`:990, 1004`). A slot is only uploaded when `slot->frameId > g_lastUploaded` ("fresh", `:1000`).

---

## 4. Control flags and the driving sequence

### 4.1 `ErmcControl` (guest -> host), offset 0x800, size 0x64

| Off | Field | Type | Host use | Source |
| --- | --- | --- | --- | --- |
| 0x00 | seq | u32 | liveness: must change at least every 1000 ms | `game.cpp:553-562` |
| 0x04 | flags | u32 | below | |
| 0x08 | mcFrame | u64 | pose id for compositing (== slot `poseId`) | `game.cpp:607` |
| 0x10 | camPos[3] | f32 | | |
| 0x1C | camTarget[3] | f32 | | |
| 0x28 | camUp[3] | f32 | | |
| 0x34 | fovYDeg | f32 | | |
| 0x38 | hunterPos[3] | f32 | stand-in feet | |
| 0x44 | poseLag | u32 | compositor | |
| 0x48 | depthIndex | u32 | **unused** | |
| 0x4C | lightGain | f32 | relight: `k = clamp(lightMin + lum*lightGain, 0, 1.15)`; 0 = default 2.6 | `compositor.cpp:1079` |
| 0x50 | lightMin | f32 | 0 = default 0.18 | `:1080` |
| 0x54 | fogStrength | f32 | 0 = default 0.55 | `:1081` |
| 0x58 | hunterYawDeg | f32 | when MOVE_HUNTER | |
| 0x5C | supportEpoch | u32 | echo of `state.supportEpoch` used by this pose | |
| 0x60 | supportTravelY | f32 | platform travel already included in `hunterPos` (m) | |

The Minecraft guest does not write 0x4C-0x57 (zeros -> defaults) (`BridgeShm.java:158-179`).

Flags (`bridge_protocol.h:179-188`):

| Bit | Name | Host behaviour |
| --- | --- | --- |
| 0 | OVERRIDE_CAMERA | camera task (task group 107, after the game resolved its camera) writes the guest camera if life==ALIVE && control fresh && frame valid (`game.cpp:589-591, 2326-2335`) |
| 1 | MOVE_HUNTER | stand-in active: Tarnished pinned to `hunterPos`, NoMove, gravity off, NoDead, HP refilled every tick (`game.cpp:1029-1033, 1092-1093`); also enables support/platform tracking (`:2303-2304`) |
| 2 | HIDE_HUNTER | clears render flag bit3 of the character (`game.cpp:987-994`); restored on release |
| 3 | CAPTURE_DEPTH | reserved |
| 4 | COMPOSITE | host draws guest frames from frames.shm into the ER frame (requires `frames_open()`) (`compositor.cpp:952`) |
| 5 | NO_DEPTH_TEST | debug: no occlusion |
| 6 | DEBUG_DEPTH | debug: show host depth bands (10 m) |
| 7 | NO_RELIGHT | debug: no ER relighting of guest pixels |
| 8 | GROUNDED | guest stands on terrain; enables platform carrying of `hunterPos.y` when `supportEpoch` matches (`game.cpp:1035-1036`); also tightens the support feet-gap to 0.4 m instead of 4.0 m (`:2048`) |
| 9 | FLYING | creative/spectator flight: do not track a platform (`:1992`) |

Minecraft sets: `OVERRIDE_CAMERA | (standIn&&player ? MOVE_HUNTER|HIDE_HUNTER) | (passthrough ? COMPOSITE) | debug bits`, `GROUNDED` if `onGround && !flying`, `FLYING` if `flying` (`CameraSync.java:154-160`).

### 4.2 Start-driving sequence used by the Minecraft guest

1. Map `bridge.shm`; every render frame call `poll()` (reads `hostHeartbeat`, snapshot of `ErmcGameState` if alive) then `bumpMcHeartbeat()` (`Overlay.java:170-172`). Also poll `mcSwitchReq` (`:173-179`).
2. World server tick (`TerrainManager.onServerTick`, `TerrainManager.java:318-343`): if host alive and state readable: compare `hostLife` with the last seen one; **any change (including the first read) calls `requestRecall()`** (`:325-332`).
3. `updateAnchor`: ignore zone 0/-1; when `PLAYER_VALID` and no non-provisional mapping for this `stageId`, create `Mapping(anchor = state.playerPos, unitsPerMeter, flipZ, zone, region)`; teleport any player not in the zone's region to the Tarnished (`TerrainManager.java:816-856`). Before the first valid state a provisional mapping at host origin lets rendering be tested (`:851-855`).
4. Recall served when `RECALL_GEN != recallDone && PLAYER_VALID && mapping != null && players exist`: teleport each player to `toMc(playerPos)` (feet rounded up to 1/16 + 0.01) with yaw from `playerQuat`, `recallDone = gen`, `recallDoneAtMs = now` (`TerrainManager.java:334-342, 858-875`). A temporary floor is placed first (`:864`). Recall requests are issued: world start (`:298`), overlay activation (`Overlay.java:258`), respawn (`TerrainManager.java:302-305`), `hostLife` change.
5. `CameraSync.ready()` (all must hold, else control is released): mapping non-provisional, `PLAYER_VALID`, `map.zone() == state.stageId`, `mc.player != null && map.contains(player.x)` (player already inside the zone's region), **`recallSettled(400)`** (`CameraSync.java:60-71`). Also overlay active, not in host mode (F8), not host-busy (`:118-119`).
6. Per camera setup it fills control and writes it: directly if not compositing, else the pose is saved with the capture and written after the pixels are in the slot (`CameraSync.java:182-185`, `FramePassthrough.java:398`). `controlling=true`.
7. Release: any "not ready" condition writes one control with `flags=0, mcFrame++` (`CameraSync.java:194-201`), which also stops compositing on the host (COMPOSITE flag drops, `compositor.cpp:957-963`).
8. While `HOST_BUSY` (death, load, settling) the overlay draws nothing and suspends control (`Overlay.java:214-227`).

### 4.3 F8 handling

- **Guest F8 (Minecraft -> ER)**: `Overlay.switchToHost` (`Overlay.java:88-103`): `hostMode = true`, `CameraSync.suspend()` (writes `flags=0` control once), pause game, release the mouse, `requestHostFocus()` (`hostFocusReq++`), hide its window. Host: on a `hostFocusReq` change it restores/foregrounds the ER window, sets `g_f8Down = down` to prevent the same key press counting as a return press, `return`s (`game.cpp:2249-2262`).
- **ER F8 (ER -> Minecraft)**: host each tick polls `GetAsyncKeyState(VK_F8)` (even when unfocused); if ER is foreground and F8 went down (edge) it does `mcSwitchReq++` (`game.cpp:2263-2268`). Guest sees `mcSwitchReq` change in `Overlay.onFrame` (first sample is baseline) and calls `switchToMc`: clears `hostMode`, re-arms host preparation, shows/focuses its window (`Overlay.java:106-121`). It then drives again after a new recall (overlay is re-activated -> `requestRecall`, `Overlay.java:258`) and `recallSettled(400)`.
- While control flags are 0 the host releases the stand-in (restores NoMove/gravity/NoDead/render, `game.cpp:996-1021`): ER's own input then works. The host does not otherwise gate F8; the key is hard-wired to F8 (`VK_F8`).
- Compositor on F8: when `COMPOSITE` clears it retires GPU captures and treats frames up to `latestFrameId@0x10` as consumed (`compositor.cpp:953-963`).

---

## 5. Terrain

### 5.1 Ray mailbox (`OFF_RAYS` 0x100000) - the working terrain mechanism

Layout (`bridge_protocol.h:255-288`): `ErmcRayHeader` (0x20 bytes) then `ErmcRay rays[8192]` (24 B: `f32 start[3]; f32 end[3]`) at +0x20, then `ErmcRayHit hits[8192]` (32 B) at `+0x20 + 8192*24 = +0x30020`.

| Off | Field | Writer | |
| --- | --- | --- | --- |
| 0x00 | reqSeq | guest | +1 to submit |
| 0x04 | respSeq | host | set to reqSeq when the whole batch is done |
| 0x08 | count | guest | <= 8192 (clamped host-side) |
| 0x0C | flags | guest | `ERMC_RAYS_CAMERA_FILTER` bit0 (**not implemented in the host mailbox path**), `CUSTOM_FILTER` bit1, `HIT_SELF` bit2 (debug command path only) |
| 0x10 | processed | host | progress (resumable across game frames) |
| 0x14 | filterA | guest | used as the collision filter when CUSTOM_FILTER |
| 0x18,0x1C | filterB/C | guest | **ignored by the host** |

Hit (`ErmcRayHit`, 32 B): `pos[3]` (stable frame) at +0, `normal[3]` at +12, `hit u32` at +24, `attr u32` at +28.

Protocol and host behaviour:

- Guest writes rays, `count`, `flags` (+filterA) **then** bumps `reqSeq` with release semantics (`BridgeShm.java:210-230`). One batch in flight: guest must not touch the region until `respSeq == reqSeq`; Java refuses (`-1`) if `reqSeq != respSeq` (`:214-215`). **Important**: because the zeroing at init stops at 0x100000, a stale `reqSeq != respSeq` from an aborted previous session makes the host process that old batch first (and the Java guest never submits until it completes; the host rereads `count`/`flags`/rays at processing time, `game.cpp:2201-2208`). A new guest should read both counters after attach and, if they differ, wait for `respSeq` to catch up (or not submit anything until it does).
- Host services the mailbox **only while life == ALIVE** (inside `if (alive)`, `game.cpp:2307-2308`), once per game tick (task group 117). Per tick it processes rays from `processed` until ~2 ms elapsed (clock sampled every 16 rays) (`:2214-2222`), writes `processed`, and when finished sets `respSeq = reqSeq` then `processed = 0` (`:2224-2228`). So a batch needs >= 1 game frame round trip and may span several; throughput is time-budgeted, not count-budgeted (**exact rays/frame unmeasured; the Minecraft guest uses 2048-ray batches**, `TerrainManager.java:69`).
- Default filter when not CUSTOM: `kTerrainRayFilter = 0x5D` ("map geometry and props, but not characters"; verified live per the comment, `game.cpp:102-109, 2210`). The ray ignores the player character (`ignore = p.ins`) (`:2216`).
- `raycast()` (`game.cpp:1960-1986`): `start/end` in the stable frame -> Havok (`+offset`); calls `CSPhysWorld::CastRay(world, filter, origin(w=1), delta(w=0), hit, ignore)`. On hit: `hit = 1` (**always 1, not the raw return value as the header comment claims**), `pos = to_stable(hit)`, `normal` **synthesised, not measured**: if `-delta.y/|delta| > 0.7` (a downward ray) `normal = (0,1,0)`; otherwise `normal = -delta/|delta|` (facing the ray origin). `attr` is **always 0** (never filled). On miss everything is zero. No per-ray distance is returned: derive it from `pos`.
- Known engine behaviour (comment in the unused sampler): CastRay "can miss when starting inside solid geometry" (`terrain_contacts.h:8-9`).

How the Minecraft guest uses it (reference for column terrain): per 1x1 column it submits `COARSE_RAYS = 2 + 3*4*2 = 26` rays: a downward ray from `y+2.2` to `y-40` at the column centre; a "high" downward ray from `y+40` to `y+2.2`; and for heights `{0.35, 1.0, 1.875}` above the sample feet, 4 wall segments (two axis-centre lines and two diagonals) cast in both directions (`TerrainManager.java:60-73, 464-473`, `TerrainClearance.java:30-35`). Consumption: `lowHit = hit != 0`; ground = `hit.pos.y`; the high ray only counts if `normal.y > 0.3` (always true for hits); horizontal hits count as obstacles when `|normal.y| < 0.7` and the wall is taller than the ground by 0.25 m (`TerrainManager.java:550-560, 646-651`, `TerrainClearance.java:38-45`). Because the host synthesises normals, a guest cannot detect slopes from the normal; derive slope from neighbouring hit heights.

### 5.2 `OFF_CONTACTS` / `OFF_COLLISION_CONTROL` (native contacts): documented, **unwired**

Status: both regions exist in the protocol and the sampler class `TerrainContactSampler` is fully written (`terrain_contacts.h`), but **no host code instantiates it or reads `OFF_COLLISION_CONTROL`**, and the guest neither writes collision control nor calls `NativeTerrainCollision.refresh` (see section 0.1). Treat as inert. If a different host build does implement it, the intended contract is:

`ErmcCollisionControl` (0x350000, 48 B): `seq u32 @0`, `flags u32 @4` (0 = off; bit1 = full previous simulation feet valid; Java ORs `2` into any nonzero flags, `BridgeShm.java:191`), `zone u32 @8`, `previousFeetX f32 @0xC`, `feet[3] @0x10`, `previousFeetY @0x1C`, `velocity[3] @0x20`, `previousFeetZ @0x2C` (`bridge_protocol.h:63-70`). Guest writes "actual simulation feet", independent of frame poses, units metres in the stable frame, velocity m/s.

`ErmcTerrainContacts` (0x320000): header 0x28 bytes (`seq @0, count @4, zone @8, valid @0xC` (bit0 complete, bit1 40-byte header with trajectory; sampler sets 3), `origin[3] @0x10`, `previousFeetY @0x1C`, `previousFeetX @0x20`, `previousFeetZ @0x24`), then `count <= 4096` contacts of 32 B: `min[3], max[3], kind u32, reserved` (`bridge_protocol.h:42-60`; Java reads a 32-byte header if `flags&2==0` else 40, `BridgeShm.java:347`). Kinds: `1 FLOOR`, `2 WALL`, `3 CEILING`, `4 CLEAR` (ray-verified air only).

Intended sampling (`terrain_contacts.h`): origin = feet; `STEP = 0.125 m`; `EDGE = 13` (1.625 m footprint), `HEIGHTS = 21` rows (2.625 m); guard rays 7x7x3 floor + 7x7x3 ceiling + 13x27x4 walls (1698) + 2 x 169 footprint floors = 2036 rays, `2*2036 <= 4096` contacts. Floor rays from `feet.y+0.625` (or `previousY+0.125` when descending <= 16 m) to `feet.y-24`; ceiling rays `feet.y+0.65 -> +2.75`; wall reach `clamp(12 + 4*travel, 12, 64)` m. Contact geometry: FLOOR box `xz = ray +/- 0.0625`, `y in [hit.y-0.125, hit.y]`; CEILING `y in [hit.y, hit.y+0.125]`; WALL cross-section 0.125 x 0.125 around the ray, extending along the axis from the hit to 0.0625 behind it on the blocked side only; CLEAR = the box between the ray start and its hit padded 0.0625 on the cross axes. All in the stable frame of `zone`.

**Guest implication**: do not wait for contacts. Build colliders from rays (5.1) or from your own sweeps of the mailbox.

### 5.3 Passages and platforms / support

- **Passages** (`OFF_PASSAGES`, host-written, 64 max; entry 0x20 B: `pos[3]` (corridor centre at floor level), `yaw` (through-direction `(sin yaw, 0, cos yaw)` in ER), `halfWidth` 0.75, `halfDepth` 1.8, `height` 3.0, `id`; header `seq, count, zone`) (`bridge_protocol.h:344-368`, `game.cpp:903-976`). Found through animated map objects (doors) whose centre line at +1 m is clear while lines 1.4 m to each side hit a wall; rescanned every 30 ticks while standing in; published with `zone`; cleared (count 0) when not standing in. The Minecraft guest uses them to avoid placing wall blocks in 1 m door openings at an angle to its grid. **A guest with continuous collision (Unity colliders) can ignore them entirely.**
- **Platforms table** (`OFF_PLATFORMS`): moving-floor cells on a 0.5 m grid within +/-3.25 m of `hunterPos`, 13x13 = up to 169 cells, rewritten every 40 ms while standing in (`game.cpp:2101-2169`): `x,z,floor,previousFloor,clearLow,clearHigh,flags` (bit0 floor present, bit1 old floor verified empty, bit2 static landing). Only cells observed moving at the same world point qualify. **Ignorable** unless the guest wants to ride lifts.
- **Support tracking** (`state.supportEpoch/supportTravelY/supportPos`, flag SUPPORT_VALID): `update_support` casts 5 rays (centre + 4 corners at +/-0.3 m, +/-0.5 m vertical) each tick while standing in and !FLYING. If `GROUNDED` and `control.supportEpoch == state.supportEpoch`, the host moves the pinned Tarnished by `state.supportTravelY - control.supportTravelY` in Y (`game.cpp:1035-1036`). **Minimal guest**: send `supportEpoch = 0` (the host's epoch is never 0, `game.cpp:1990`) and `supportTravelY = 0` -> no adjustment ever happens; ignore the state fields. Optional: implement platform riding by applying `supportTravelY` deltas to the guest player and echoing epoch/travel (Minecraft: `MovingPlatformClient.copyControl`, `K/MovingPlatformClient.java:50-57`).
- Cost note: while MOVE_HUNTER is active the host casts ~5 support rays + up to 169 cell rays (more for moving cells) every 40 ms on the game thread (`game.cpp:2101-2160`) in addition to the mailbox. Unavoidable unless MOVE_HUNTER is off.

---

## 6. Entities, combat, life

### 6.1 Entity table (`OFF_ENTITIES` 0x200000)

Header: `seq u32 @0`, `count u32 @4`, `frame u64 @8`, entries at +0x10, 0x80 B each, max 256 (`bridge_protocol.h:290-316`). Published every game tick while life == ALIVE (`game.cpp:2310`), emptied (`count=0`) otherwise (`:2315`). Seqlock: odd while writing (`game.cpp:1397-1403`).

| Off | Field | Production (source `game.cpp:1323-1406`) |
| --- | --- | --- |
| 0x00 | id u64 | `FieldInsHandle` of the ChrIns (chr+0x08): stable while it lives; also what damage targets use |
| 0x08 | kind u32 | hostile (`CanTarget(player, chr)` real game call, or team type 6/7 fallback): `LARGE_MONSTER=1` if team==7 or capsule height > 3.0 m, else `SMALL_MONSTER=2`; non-hostile (NPCs etc.) `OTHER=3` |
| 0x0C | emId u32 | NpcParamId (chr+0x60) |
| 0x10 | pos[3] | `to_stable(physics pos)` = **feet** |
| 0x1C | quat[4] | physics rotation (x,y,z,w), copied (not used because flag bit1 is always set) |
| 0x2C | boxCenter[3] | `(pos.x, pos.y + h/2, pos.z)` |
| 0x38 | boxHalf[3] | `(r, h/2, r)` with the capsule radius `r` and height `h` read from the physics module (+0x2E4, +0x2E0); fallback `h=1.8, r=0.4` when out of (0.05, 40)/(0.05, 15) |
| 0x44 | hp f32 | `max(hp, 0)` (ints in the game) |
| 0x48 | maxHp f32 | |
| 0x4C | flags u32 | **always `2` (bit1: box world-aligned, ignore quat) | `1` if dead/dying (`hp<=0` or render flag bit7)** |
| 0x50 | name[48] | `"c%04d"` model id (e.g. `c4300`) |

Selection: candidates = game's "characters by distance" list (up to 1024) + the debug character set; skipped: the player, squared distance (chr+0x3FC) > 80 m, models 1000/100 (invisible helpers), `maxHp <= 0`, duplicate handles; cap 256 (`game.cpp:1264, 1333-1396`). Dead characters stay in the list with `flags&1`. Boxes are **world-aligned vertical capsule boxes** (not oriented); the ordered-box path (`flags` bit1 == 0, `quat`) exists in the guest but the host never emits it.

Guest handling (reference): one invisible proxy per id with the AABB (`EntityBridge.java:130-168`): `center = toMc(boxCenter)`, halves scaled by `1/unitsPerMeter`, entity feet = `center.y - hy`. Proxies of dead entities are removed (`:77-82`). Enemies = kind 1 or 2.

### 6.2 Guest -> host damage ring (`OFF_DAMAGE` 0x280000)

`ErmcDamageQueue`: `write u32 @0` (producer), `read u32 @4` (host consumer), `reserved[2]`, `ring[256]` at +0x10 each `ErmcDamage` 0x20 B: `id u64 @0`, `amount f32 @8`, `hitPos[3] @0xC`, `flags u32 @0x18`, `reserved @0x1C` (`bridge_protocol.h:318-342`). Producer: write entry `ring[write % 256]`, then release-store `write+1`; refuse when `write - read >= 256` (`BridgeShm.java:389-405`). Consumer (host): `r = read; if (w - r > 256) r = w - 256` (drops oldest), applies each, stores `read = r` (`game.cpp:1911-1951`). **The init zeroing does not cover this region (>= 0x100000)**, so `write/read` may be stale from an earlier session; they remain consistent with each other, so a new guest must **continue from the existing `write`** value (do not reset to 0).

Host processing (`service_damage`, only while ALIVE, before entities are republished so ids refer to last tick's table):

- `id == 0`: "strike the world": limited to once per 150 ms; `hitPos` is a point in the stable frame; if `flags & WORLD_RAY (1<<3)` the host first casts from the Tarnished's eye `feetPos + 1.62 m` toward `hitPos` (filter 0, i.e. unfiltered) and uses the hit point instead; if `0.3 < len < 8` m from eye to target it spawns the player's "poke" bullet (Ruin Fragment param row 10176000, patched to dmgLevel 1, 12 poise) 0.25 m before the target along that direction; breaks crates/pots (`game.cpp:1918-1942, 1459-1541`).
- `id != 0`: must match an entity published in the previous tick (`find_published`, re-validates vtable and handle, `game.cpp:1312-1318`), `0 < amount < 10000`, and `can_target(player, chr)` must be true (hostile) else dropped (`:1944-1948`). Then `apply_hit`.
- `apply_hit` (`game.cpp:1629-1661`): skip if dead; `dmg = ceil(er_damage(amount, maxHp))`, min 1; if not NOT_BY_PLAYER, sets the target's `last_hit_by` to the player handle (kill credit/aggro); calls the game's `ModifyHp(data, -dmg, ...)` (respects invincibility; if HP unchanged logs "invincible"); if alive after and by player (rate limited 250 ms per target) fires a poke bullet from behind the target for hit reaction and aggro; if HP <= 0 calls `Kill(chr, 0)` (death animation, drops, runes to the player), retried for 2 s if refused.

**Exact conversion** (`game.cpp:1434-1439`):

```
mcHealth = clamp(20 * sqrt(maxHp / 100), 10, 300)
erHp     = amount * maxHp / mcHealth      // then ceil, min 1 (apply_hit)
```
Examples: maxHp 100 -> mcHealth 20 -> 1 point = 5 HP; maxHp 221 (soldier) -> 29.73 -> 1 point = 7.43 HP; maxHp 6000 -> 154.9 -> 1 point = 38.7 HP; maxHp <= 25 -> mcHealth floor 10; maxHp >= 22500 -> 300.
**Recommended UltraRing scheme**: the guest knows `maxHp` from the entity table, so send `amount = fractionOfMaxHp * mcHealth(maxHp)` (compute `mcHealth` with the same clamp) and the host yields `ceil(fraction * maxHp)` HP. Do **not** rely on Minecraft's hurt cooldown: the Minecraft proxy applies a 20-tick/half-cooldown rule before sending (`ErEntity.java:hurt`, the host does no throttling except the 250 ms poke).

Flags (`bridge_protocol.h:330-335`): `CRITICAL (1<<0)` only appears in the host log (`game.cpp:1636`) - no damage multiplier; `OUTWARD (1<<1)` **not read by the host source** (comment mentions a slinger shot, MH:World legacy); `NOT_BY_PLAYER (1<<2)` skips kill credit/aggro/poke (use for guest-world environmental or friendly-fire-ish damage); `WORLD_RAY (1<<3)` only with `id == 0`.

Hit-test is the guest's job: use `boxCenter/boxHalf` as a world-aligned AABB in host coordinates (convert to the guest frame). The Minecraft proxy fixes the AABB bottom-centre at the feet and applies `+/- boxHalf` (`ErEntity.java:makeBoundingBox`).

### 6.3 Host -> guest damage (`OFF_HUNTER` 0xA00, `ErmcHunterEvents`, 0x30 B)

| Off | Field | Production |
| --- | --- | --- |
| 0x00 | seq | odd while writing (`game.cpp:1064-1089`) |
| 0x04 | hitCount u32 | +1 per detected HP-loss event |
| 0x08 | totalDamage f32 | running sum of ER HP lost |
| 0x0C | lastDamage f32 | HP lost in the last event |
| 0x10 | lastHitFrom[3] | position (stable frame, **feet**) of the attacker: the entity the game recorded in the Tarnished's `last_hit_by` (chr+0x180, reset to -1 after each hit), else the nearest hostile of the last publish, else `hunterPos` |
| 0x1C | hunterMaxHp f32 | Tarnished max HP; **only written when a hit is reported** (0 until the first hit) |
| 0x20 | lastHitFrame u64 | state frame at the hit |
| 0x28 | lastHitKind u32 | `ERMC_ENT_*` of the blamed attacker (default LARGE when unknown) |
| 0x2C | totalStatusDamage f32 | **never written (always 0)** |

Detection (`game.cpp:1055-1093`): each tick while standing in, `hp = data.hp`, `mx = data.maxHp`; if `lastHp > 0 && hp < lastHp` and not the 3-tick NoDead clamp, `dmg = lastHp - hp` (integer HP) is accumulated; then HP is rewritten to `mx` ("never faint while standing in") and `lastHp = mx`. So events are per game tick (multiple hits in one tick merge), and the Tarnished's armor/defences have already been applied to the HP loss. Poison/rot etc. would show as many small events (each tick <1 HP changes appear only if the integer HP drops).

Guest consumption (reference `CombatBridge.java:74-119`): keep `lastHits`, `lastTotal`; first read only establishes the baseline; each `hits != lastHits` gives `hostDamage = totalDamage - lastTotal`; `share = hostDamage / hunterMaxHp`; damage mapped to Minecraft: `amount = min(20, (attackerKind==SMALL ? 3 : 6) + share*30)`; attacker blamed = nearest enemy proxy to `lastHitFrom` within 32 m, knockback away from `lastHitFrom` with extra `min(1.1, share*3)`. **Recommended for UltraRing**: use `share = hostDamage/hunterMaxHp` as the fraction of the guest player's max HP to remove (units-free), reset baselines if `hitCount` decreases (host restart zeroes this region only if the file header was re-initialised, `shm.cpp:33`/`BridgeShm.java:46`). Status damage: the guest handles `totalStatusDamage` but the host never produces it, so DoT shows only as ordinary small events.

### 6.4 Life and death counters

State machine (`game.cpp:1096-1237`): `NONE` (no character/loading) -> `SETTLING` (wait until the character stops moving) -> `ALIVE` (published, may stand in) -> `DEAD`.

- `hostLife` (+1): when SETTLING completes with `settleMin >= 30` ticks, i.e. after a load (90), a respawn (90) or a game warp (30), **not** after hot reload or a seamless zone/block change (10) (`:1203`). Guest reaction: `requestRecall()` = teleport the guest player to `state.playerPos` with `playerQuat` yaw and refrain from driving for 400 ms after (4.2 step 4-5). ER, not the guest, decides the spawn place (last Site of Grace).
- `mcDeaths` (+1 by the guest when its player dies, except deaths caused by `hostDeaths`): if ALIVE and the Tarnished stood in within 2 s and is not dead, the host calls `ChrIns::Kill(chr, 0)` (the game's own debug kill: death animation, "YOU DIED", runes dropped, respawn at grace) and sets `g_expectDeath` so it does not echo back (`game.cpp:1170-1183, 1219`). The guest then respawns immediately (`doImmediateRespawn`), draws nothing while `HOST_BUSY`, and is moved by the next recall when `hostLife` ticks (`LifeBridge.java` doc, `:13-30`).
- `hostDeaths` (+1 by the host): the standing-in Tarnished died in ER without the guest asking (e.g. below the map: NoDead produces "HP=1" every tick for 3 ticks, then the host lets it die) (`:1208-1217`). Guest reaction: kill the player with damage that bypasses creative/invulnerability (`LifeBridge.java:478-503`: baseline on first read, then `player.hurt(erbridge:host_death, Float.MAX_VALUE)` on change).
- `keepInventory` is forced on in the bridge world (`TerrainManager.java:293`).
- If the host `life != ALIVE`: state's PLAYER_VALID clears; control must not be applied (the camera task requires ALIVE) and the stand-in is released (`stand_in(active=false)`).

---

## 7. Action / prompt (interact) protocol

Header fields 0x70-0xBF (section 2.2). Flow (`game.cpp:1756-1900`; guest `ErBridgeClient.java:272-296`):

1. Guest: on its action key (Minecraft: `R`), if overlay active, host not busy and host alive: `mcActionReq += 1` (remember the new value as pending).
2. Host (`service_action`, in the ALIVE block each tick): first call only latches `g_actionSeen = mcActionReq` (requests made before the host's first service are ignored, `:1888-1892`). On change: `perform_action`: requires the build's code signatures to match (`g_actionOk`), the game's `CSActionButtonMan` to have a selected prompt (`+0x20` non-null, text id `+0x2C >= 0`), `+0x29` canExec and not grayed (`+0x2B`); ladder prompts (ActionButtonParam 5000/5010) return -2; else sets the "Event Action tapped" latch byte (`+0x81 = 1`) so the game's own handler consumes it in the next event update; result 1. No input injection. The latch is cleared after 500 ms if not consumed.
3. Host writes `hostActionResult` then `hostActionAck = req` (release order).
4. Guest waits for `hostActionAck == pending` (give up after 2 s): `1` -> resample terrain around the player (door/lever moves collision), `0` "nothing to do here", `-2` "ladders need ER controls", `<0` unsupported.
5. Prompt text: while ALIVE **and standing in** the host publishes the current prompt's FMG string (ActionButtonText category 0x20, DLC 0x16D / 0x1D1) as UTF-8 (<= 63 bytes) in `hostPrompt` with `hostPromptSeq` incremented before and after; empty string = none; unknown text -> `"Action <id>"` (`game.cpp:1861-1880`). Java reads it without the seqlock (`BridgeShm.java:102-112`); a C# guest should re-read until `hostPromptSeq` is even and unchanged.

The prompt depends on the **Tarnished's** position/facing, i.e. on `hunterPos`/`hunterYawDeg` fidelity (reach assist squares it to doors within 2 m of its look ray).

---

## 8. Environment block (time / weather), briefly

`OFF_ENVIRONMENT` 0xB00, guest-written, `ErmcEnvironment` (seq, flags, timeRevision, dayTicks, weatherRevision, weather; 24 B) (`bridge_protocol.h:85-94`). `flags`: `ENV_TIME=1`, `ENV_WEATHER=2`; 0 releases. `dayTicks` in [0, 24000), **0 = 06:00, 6000 = noon**; host converts `seconds = ((dayTicks % 24000) * 18/5 + 21600) % 86400` and requests that clock time (`world_environment.h:7`; it also sets the game's time rate to 0 while owned). `weather`: 0 clear -> WeatherParam suffix 1, 1 rain -> 20, 2 thunder -> 30 (windy rain) (`world_environment.h:9-15`). Only honoured while ALIVE, fresh `mcHeartbeat` (< 2 s), no scripted native time/weather in effect (`game.cpp:232-335`). **UltraRing: leave the block zeroed (never publish) so ER keeps its own sky and clock.** If published, the guest must republish `seq` consistently (even, nonzero) and clear flags when exiting.

---

## 9. frames.shm in detail

### 9.1 File header (`ErmcFramesHeader` + GPU extension), offset 0 of frames.shm

| Off | Field | Writer | Notes |
| --- | --- | --- | --- |
| 0x00 | magic u32 `0x524D484D` ("MHMR") | guest (last) | host checks only at map time |
| 0x04 | version u32 = 3 | guest | |
| 0x08 | latestSlot u32 | guest | **not read by the host** (it scans all 3 slots) |
| 0x0C | reserved | | |
| 0x10 | latestFrameId u64 | guest (release) after each publish | host reads it with an interlocked read **only when compositing is off**, to retire everything up to that id: `g_lastUploaded = max(g_lastUploaded, latestFrameId)` (`compositor.cpp:957-963`); guest also reads it at startup to keep its counter monotonic. |
| 0x40 | GPU magic u32 `0x47504D43` | host (last, interlocked) | present iff shared textures exist; `gpu_release` writes 0 (`gpu_transport.h:9, 37`) |
| 0x44 | width u32 | host | |
| 0x48 | height u32 | host | |
| 0x4C | generation u32 | host | `GetTickCount()` at creation, part of the resource names |
| 0x50 | host pid u32 | host | |
| 0x54 | slot count u32 = 6 | host | |
| 0x60 + s*8 | ack u64 (s = 0..5) | host | highest frame id of GPU slot `s` the host has finished copying |
| 0xA0 + s*8 | published id u64 (s = 0..5) | guest | frame id the guest published in GPU slot `s` |

`memset(g_frames+0x40, 0, 0xA0)` clears 0x40..0xDF when the host (re)creates GPU textures (`gpu_transport.h:33`). **A memory-path-only guest must never write 0x40..0xDF** and must treat them as host-owned.

### 9.2 Slots

3 slots of `ERMC_FRAME_SLOT_SIZE = 0x100 + 3840*2160*16 = 0x7E90100` bytes; slot `i` at `0x1000 + i*0x7E90100`; the Minecraft guest uses `slot = frameId % 3`, and the host indexes by position, not by that formula (`compositor.cpp:175-177`, `FramePassthrough.java:360-361`). Max frame size 3840x2160 (`ERMC_FRAME_MAX_W/H`).

Slot header (0x100 bytes reserved; host-used fields marked):

| Off | Field | Type | Host use |
| --- | --- | --- | --- |
| 0x00 | seq | u32 | guest sets odd while writing, then next even; host skips odd and revalidates after copying (`copy_slot` returns false if changed) |
| 0x04 | width | u32 | 1..3840; must equal the current texture size else the copy is skipped and (on a new size) textures are recreated |
| 0x08 | height | u32 | 1..2160 |
| 0x0C | flags | u32 | bit0 world valid, bit1 GUI valid (**both ignored by the host**), **bit2 hand layer present**, **bit3 GPU slot** (frame lives in shared textures) |
| 0x10 | frameId | u64 | freshness: uploaded only if `> g_lastUploaded`; also the fence value for GPU frames |
| 0x18 | poseId | u64 | == `ErmcControl.mcFrame` of the pose used for this frame; slot selection key |
| 0x20 | mcNear | f32 | guest projection near (see 9.5 for units) |
| 0x24 | mcFar | f32 | guest projection far |
| 0x28 | fovYDeg | f32 | **not read by the host** |
| 0x2C | aspect | f32 | **not read by the host** |
| 0x30 | gpuSlot | u32 | GPU path: which of the 6 shared texture sets holds the frame |
| 0x34 | gpuGeneration | u32 | must equal the host's `generation` or the frame is rejected |
| 0x38-0xFF | unused | | |

The Minecraft publish writes `flags = (hand ? 7 : 3) | (gpu ? 8 : 0)` (`FramePassthrough.java:382`).

### 9.3 Layers, formats, order

After the 0x100 header, layers are contiguous with `layerBytes = width*height*4` and **no row padding** (`copy_slot`, `compositor.cpp:765-779`):

| Layer | Offset in slot | Content | Format |
| --- | --- | --- | --- |
| 0 | 0x100 | world color | **BGRA8: bytes B,G,R,A** (memory path) |
| 1 | 0x100 + L | world depth | float32 per pixel, **OpenGL window depth [0,1]** |
| 2 | 0x100 + 2L | GUI/HUD | BGRA8 |
| 3 | 0x100 + 3L | hand + screen effects (optional, flags bit2) | BGRA8 |

- **Row order**: bottom-up (OpenGL): row 0 of the memory is the bottom scanline. The shader samples with `uvMc = (u, 1-v)` (`compositor.cpp:291`), so memory row `height-1` is the top of the screen.
- **Premultiplied alpha** for all colour layers: composite is `gui + scene*(1-gui.a)`, `scene = hand + world*(1-hand.a)`, blended onto the back buffer with `ONE, INV_SRC_ALPHA` (`compositor.cpp:312-315, 533-537`). Background must be alpha 0.
- Textures created as `DXGI_FORMAT_B8G8R8A8_UNORM` (layers 0,2,3) and `R32_FLOAT` (layer 1) (`compositor.cpp:694-696`). In the memory path the upload buffer is copied raw, so memory bytes must already be B,G,R,A. Java gets this from `glReadPixels(GL_BGRA, GL_UNSIGNED_INT_8_8_8_8_REV)` (`FramePassthrough.java:266`).
- **`swapRB`** = `g_texGpu ? 1 : 0` where `g_texGpu` is the GPU flag of the **last uploaded frame** (`compositor.cpp:1033, 1086`); applied to world, hand and GUI (`:295, 310, 314`). GPU path: the guest blits RGBA8 GL textures into D3D `B8G8R8A8` memory so the host swaps. Memory path never swaps.
- Alpha handling in the shader: world pixels with `a > 0 && depth < 1.0` are depth-tested/relit/hazed; **world pixels with `a > 0` and `depth == 1.0` are drawn unlit and un-occluded** (`:297-307`), as is anything in the hand and GUI layers (hand relit, never occluded; GUI unmodified).

### 9.4 How the host picks and uses slots

1. Present hook `composite()` each frame (`compositor.cpp:902-1166`): `mc = control_snapshot && (flags & COMPOSITE) && frames_open()`. If not `mc`: retire everything and return.
2. `poseId = pose_for_present(ctrl.poseLag)` -> `slot = pick_slot(poseId)` (3.5).
3. `ensure_frame_resources(slot.width, slot.height)` recreates textures, upload ring (3x), and the GPU shared textures when the size changes (`wait_idle`, expensive: avoid resizing).
4. Waits up to 50 ms on the ring slot's fence; `fresh = slot->frameId > g_lastUploaded`.
5. Memory path (`flags & 8 == 0`): `copy_slot` into the upload heap (3 or 4 layers; the hand layer only if flags bit2 and the slot is big enough), `CopyTextureRegion` into the persistent textures; `g_lastUploaded = frameId`.
6. Depth: host copies its tracked scene depth buffer (`depth_tracking.h`), reversed-Z, linearised with state `nearZ/farZ` (defaults 0.05 / 10000 if invalid) (`compositor.cpp:1066-1075`). `useDepth` only if a host depth buffer is tracked and `NO_DEPTH_TEST` is clear. Relight is on when the scene copy exists and `NO_RELIGHT` is clear; fog start 40, end 400 (guest units == metres), strength 0.55 default (`:1077-1084`).
7. The frame is drawn full-screen into the swapchain back buffer (supported formats `R8G8B8A8_UNORM`, `B8G8R8A8_UNORM`, `R10G10B10A2_UNORM`, else compositing is permanently off, `:935-940`), only when the swapchain's queue is a direct queue (`:921-926`).
8. "Freshness": a frame counts as new only when its `frameId` exceeds the last uploaded id. If the guest ever restarts with a lower counter, nothing uploads until ids exceed the old maximum - hence the time-based seed.

### 9.5 Making the memory path the one that is used; depth rules

To force the memory path the guest must:

1. Never set slot `flags` bit3; leave `0x30/0x34` zero; never open the `Local\ERMCGPU_*` objects. (The host still allocates 6x4 shared textures at the frame size each time resolution changes, `compositor.cpp:738` -> `gpu_init`; about 199 MB of VRAM at 1920x1080 and 797 MB at 3840x2160 by arithmetic: 24 textures x w*h*4 B. It is harmless but not free.)
2. Write each layer as raw bytes at `slot + 0x100 + layerBytes*i` in B,G,R,A (colour) / float32 (depth), bottom-up rows, premultiplied.
3. Publish with the slot `seq` odd->even, set `frameId/poseId/mcNear/mcFar/width/height/flags`, then `frames[0x08]=slot`, release-store `frames[0x10]=frameId`, then publish the control block with `mcFrame == poseId`.
4. Capture at most once per distinct host frame (`hostHeartbeat` changed since the last capture, `FramePassthrough.java:248-252`), because the host consumes one pose per game frame and the 3-slot ring would otherwise overwrite poses it still needs.
5. Keep the resolution stable and aspect == ER's `winW/winH`.

Depth encoding expected by `linMc(d)` (`compositor.cpp:256-259`):
`z = 2d-1; eyeDist = 2*n*f / (f + n - z*(f - n))` with `n = mcNear`, `f = mcFar` from the slot header: i.e. a standard OpenGL perspective depth. The result is compared with the host's linear depth in metres: occluded if `eyeDist > hostDist + 0.03 + 0.004*eyeDist` (`:299`).
**Unity/ULTRAKILL consequences** (derived, **UNVERIFIED** against the game): Unity on D3D11/12 uses reversed-Z; either convert, or write the layer from a pass that outputs `d = f*(zEye - n) / (zEye*(f - n))` (this inverts the formula above; zEye = view-space distance along the camera forward axis; d is invariant when n and f are scaled together). To compare in host metres when one guest unit = `k` metres, publish `mcNear = n*k`, `mcFar = f*k` (and keep `d` computed from the guest's own n,f). Clear depth value must be `1.0` where nothing was drawn (`md < 1.0` test).

### 9.6 GPU shared-texture path (for later)

Host creation (`gpu_transport.h:16-39`, called from `ensure_frame_resources`): for each of `kGpuSlots = 6` and each of 4 layers, a committed D3D12 texture (layer 1 `R32_FLOAT`, others `B8G8R8A8_UNORM`), flags `ALLOW_RENDER_TARGET | ALLOW_SIMULTANEOUS_ACCESS`, heap `SHARED`, shared by name `Local\ERMCGPU_<hostPid>_<generation>_<slot>_<layer>` (`%lu_%u_%d_%d`); a shared fence `Local\ERMCGPU_<hostPid>_<generation>_ready`. Then the frames.shm GPU header (9.1) is filled and the magic `0x47504D43` stored last. Guest import (Java: EXT_memory_object_win32 + EXT_semaphore_win32, `GpuTransport.java:432-490`): requires magic, matching host pid, width/height equal to the frame, slot count 6, and a new generation; failures fall back to the memory path (`failedGeneration`).
Per frame (guest): choose capture set `cur = frameCounter % 6`; only if `available(cur)` (`used==0 || ack[cur] >= used[cur]`, `GpuTransport.java:428`); `begin(cur)`: wait semaphore at value `used[cur]`; blit colour layers (RGBA bytes into the BGRA textures, hence `swapRB`), draw depth as float into layer 1; `finish(cur, id)`: signal the fence with value `frameId`, `used[cur] = id`; then fill the slot header with `flags |= 8`, `0x30 = cur`, `0x34 = generation`, and write `frames[0xA0 + cur*8] = frameId` (`published`).
Host: `gpu_frame_ready(slot)` requires `index<6`, `generation` match, and `fence.GetCompletedValue() >= frameId`; copies the shared textures into the persistent ones, records `g_gpuAckFence/Frame[index]`; when the host's own fence passes that value (`gpu_acknowledge`), it stores the ack frame id at `frames[0x60 + index*8]`. It also acks (releases) stale published ids and unselected slot captures so one unconsumed slot cannot stall the ring (`gpu_transport.h:40-65`). Requires monotonic `frameId`s (they are fence values). GPU path **should be postponed**; start memory-only.

---

## 10. Minimal guest checklist and Minecraft-specific hazards

### 10.1 Common prerequisites (lifecycle)

1. Resolve `ERMC_DIR` (env, else `%TEMP%\ermc`); create the dir; open/extend `bridge.shm` to 8 MiB; map R/W; if `magic/version` mismatch, zero `[0,0x100000)`, set version=1,size=8 MiB, then publish magic (release). Set `mcPid`, `mcStartMs`.
2. Poll `hostHeartbeat` (0x10): alive = changed within 2 s (ignore the first sample). Read `ErmcGameState` with the seqlock. Increment `mcHeartbeat` (0x18) every guest render frame (only matters for the environment rule, but keep it).
3. Read ray mailbox `reqSeq/respSeq`; if unequal wait for them to match before the first submit. Read damage ring `write/read` and continue from `write`.
4. Create `frames.shm` at full size (398,136,064 B) with version 3 and magic last, **before** setting `COMPOSITE`.
5. Zone handling: anchor each `stageId` (ignore 0/-1); on `stageId` change stop driving (flags=0) until re-anchored. React to `hostLife` changes (recall), `mcSwitchReq` changes (F8 from ER), `hostDeaths` changes (kill the player).
6. On guest shutdown/crash handler: write a control block with `flags = 0` (and bump `seq`) so the host stops compositing and releases the Tarnished; the host does not time out `COMPOSITE`.

### 10.2 (A) Camera driving + hidden stand-in walking

- Gate: host alive, `PLAYER_VALID`, `stageId` anchored and equal, no HOST_BUSY/PLAYER_DEAD, `recallSettled` (>= 400 ms after teleporting the guest player to `state.playerPos`), and F8 handoff not active.
- Every guest render frame write a control block (seq odd -> payload -> next even; also required at >= 1 Hz): `flags = OVERRIDE_CAMERA | MOVE_HUNTER | HIDE_HUNTER | (COMPOSITE) | (GROUNDED xor FLYING as applicable)`, camera = eye/target/up/vertical FOV (degrees, 5..170) in host stable metres, `hunterPos` = feet, `hunterYawDeg` per 3.4, `poseLag = 1`, `mcFrame` = pose id, `supportEpoch = 0`, `supportTravelY = 0`, lightGain/lightMin/fogStrength = 0 (defaults).
- Render ULTRAKILL at ER's `winW x winH` aspect with the same FOV.
- Consider `HIDE_HUNTER` mandatory (host otherwise shows the Tarnished).
- With COMPOSITE: write the control only after the corresponding frame is in the slot (9.5 step 3).
- F8: on guest hotkey write flags=0, `hostFocusReq++`, release input; on `mcSwitchReq` change resume (recall + 400 ms).

### 10.3 (B) Terrain collision (needs a guest-side design; host provides rays only)

- Submit batches through the ray mailbox (<= 8192 rays per batch, one in flight, results in stable-frame metres, `hit` 0/1, synthesised normals, attr 0) and build guest colliders from the hit points; sample around the player (Minecraft: radius 8 m near, 24 m far, ~26 rays per 1 m column, floor ray `+2.2 -> -40` m from feet).
- Resample after any `mcActionReq` result 1, after world strikes (`id==0` damage; wait ~0.9 s), and around opened passages (door changes arrive as new `OFF_PASSAGES` ids).
- Do not use `OFF_CONTACTS` / `OFF_COLLISION_CONTROL`. Platform/passage tables are optional.
- Rays are only answered while the host is ALIVE and are budgeted ~2 ms/game frame: expect latency of 1+ frames per batch.

### 10.4 (C) Combat both ways

- ER -> guest: enemies from the entity table (id, kind, world-aligned AABB `boxCenter +/- boxHalf`, hp/maxHp, flags bit0 dead); host damage via `OFF_HUNTER` counters (baseline on first read; `hitCount`, `totalDamage`, `hunterMaxHp`, `lastHitFrom`, `lastHitKind`); convert `share = hostDamage/hunterMaxHp` into guest HP; kill/respawn via `hostDeaths`/`mcDeaths`.
- Guest -> ER: compute hits against the AABBs yourself, push to the ring `{id, amount, hitPos, flags}` after writing the entry, then bump `write`; ring capacity 256 (check `write - read < 256`). Amount via the section 6.2 formula (send `fraction * clamp(20*sqrt(maxHp/100),10,300)`). `id == 0` + `WORLD_RAY` strikes world props.
- Player death -> `mcDeaths++` (only while standing in; the host kills the Tarnished). Remember the host reacts at most once per counter change within 2 s of the last stand-in tick.

### 10.5 (D) Compositing through the memory path

See 9.5. Order per frame: render world (alpha 0 background, premultiplied) -> read back colour (BGRA bytes) + OpenGL-style depth (float32) + GUI layer (+ optional hand layer) bottom-up; write slot; set `latestSlot`, `latestFrameId`; then write control with `mcFrame == poseId`; set `COMPOSITE`. Never write 0x40..0xDF. Keep ids strictly increasing and > any previously published id (seed from `time*1024` and `max(.., latestFrameId)`).

### 10.6 Minecraft-specific assumptions in the host (could misbehave with ULTRAKILL)

| # | Assumption | Where | Risk / mitigation |
| --- | --- | --- | --- |
| 1 | `hunterYawDeg` is Minecraft yaw; facing = `(-sin, -cos)` in host axes | `player_facing.h` | Convert as in 3.4; a wrong sign mirrors the Tarnished's facing and breaks door prompts (reach assist uses the same vector, `game.cpp:846-847`) |
| 2 | Damage scale is "Minecraft damage points" (a 20-point mob) | `game.cpp:1434-1439` | Use the fraction trick; amounts outside (0,10000) are dropped; every hit is at least 1 HP |
| 3 | Host-to-guest damage is raw ER HP lost + `hunterMaxHp` | `game.cpp:1055-1093` | Use `share`; `hunterMaxHp` is 0 until the first hit |
| 4 | Eye height 1.62 m for world-strike rays; strike reach 0.3-8 m | `game.cpp:1928-1936` | A guest whose eye height differs sends `hitPos` consistent with that eye; world strikes only fire if target within 8 m of ER's `feet+1.62` |
| 5 | Body dimensions: platform corner offsets +/-0.3 (support) / 0.3125 (cells); clearance up to `floor+2.05..2.1` m; feet-floor gap `[-0.45, +0.4]` grounded (4.0 otherwise); reach chest height 1.0, knee 0.4, gap 0.45 | `game.cpp:2031-2048, 2084, 2147-2151, 689-694` | Only matter for support/platform/door-assist; the Tarnished capsule is ER's own (comment: 1.5 x 0.4, `game.cpp:86`), not scaled by guest body size |
| 6 | Positions are feet; 1 guest block = 1 m | `CoordMap.java` | Our guest needs its own scale (ULTRAKILL units unknown **UNVERIFIED**) and consistent scaling of rays, camera, hitboxes, `supportTravelY`, `mcNear/mcFar`, hit points |
| 7 | Block-grid artifacts: 0.5 m platform cells, 1/16 block heights, 1 m door passage sizing | `game.cpp:2111-2119`, `bridge_protocol.h:344-350` | Ignore for continuous colliders |
| 8 | `mcNear/mcFar` in "blocks" == metres; fog 40..400 m | `compositor.cpp:1082-1083` | Scale near/far into metres (9.5) |
| 9 | Depth is OpenGL [0,1], world alpha>0 with depth==1 is drawn un-occluded | `compositor.cpp:256-259, 297-307` | Provide GL-style depth; translucent depth-less effects will never be occluded or relit |
| 10 | Memory-path pixel order BGRA vs GPU RGBA | `compositor.cpp:999, 1086` | Match flag bit3 |
| 11 | Guest FOV must equal the camera FOV it sends; aspect equals ER back buffer aspect | `game.cpp:589-609` | Render to `winW:winH` |
| 12 | Stand-in forced invulnerable, HP refilled each tick, "NoMove" (ER input ignored while driving) | `game.cpp:1029-1033, 1092` | Guest owns player health |
| 13 | Time/weather owner: Minecraft's `world.getDayTime()` when `ErmcEnvironment` is written | `WorldEnvironmentBridge.java:44-49` | Do not write the block |
| 14 | Host patches ER FPS cap to 144 and restores 60 at unload; poke param row patched | `game.cpp:2386-2397, 2454-2457, 1494-1507` | Informational |
| 15 | Hot reload: after core reload `hostLife` is not bumped; but zone continuity relies on the page-persisted bias | `game.cpp:394-406, 508-516` | Dev only |
| 16 | `playerPos` returned minus the reach-assist offset, so a stationary guest near a door can see `playerPos != hunterPos` | `game.cpp:2501-2503` | Do not use `playerPos` as feedback while driving except for recalls |
| 17 | Host expects exactly one guest process; two guests would race the control block and ray mailbox (no ownership field). Launch.ps1 prevents a second Minecraft via the pid/start-time check (`Launch.ps1:48-58`). | | Implement an equivalent guard |
| 18 | Entity table ids are ER object handles, entities are only those within 80 m, max 256; dead ones persist briefly | `game.cpp:1264, 1385` | Remove proxies when `flags&1` or `hp<=0 && maxHp>0` or id gone |

### 10.7 Unwired / unreliable fields (do not depend on)

`ErmcContacts`, `ErmcCollisionControl`, `hunter.totalStatusDamage`, `state.view/proj`, `STATE_MATRICES_VALID`, `ErmcControl.depthIndex`, `CTRL_CAPTURE_DEPTH`, `ErmcRayHit.attr`, ray flag `CAMERA_FILTER`, ray `filterB/filterC`, damage flags `OUTWARD`/`CRITICAL` (effect-less), frames header `latestSlot`, `frames fovYDeg/aspect`, `hostPresentPage`.

---

## Appendix A. Host per-frame order (task groups)

Camera task (group 107, "DrawParamUpdate", right after the game's camera step): `if (life==ALIVE && control fresh) apply_camera` (`game.cpp:2326-2335`).
Tick task (group 117, "WorldChrManPostPhysics", `task_tick`, `game.cpp:2276-2324`): `handle_switching` -> player lookup -> `update_frame` -> `update_life` -> `control_active` -> `service_environment` -> `update_support` -> `update_platform_cells` -> `stand_in` (reach assist, pin, HP report) -> `update_passages` -> if ALIVE: `service_rays`, `service_damage`, `publish_entities`, `service_test_enemy`, `service_action`, `check_action_press`; else `clear_entities` -> `publish_prompt` -> `frame_claim_source` + `on_frame` (publishes state + heartbeat).
Present hook (compositor): composite guest frames over the finished ER frame.
Worker thread (1 ms): debug mailbox, compositor hook discovery, depth scans, door scan.

## Appendix B. Quick offset reference

bridge.shm: header 0x0, state 0x100, control 0x800, hunter events 0xA00, environment 0xB00, cmd 0x1000, cmd resp 0x2000, rays 0x100000 (rays +0x20, hits +0x30020), entities 0x200000 (+0x10), damage 0x280000 (ring +0x10), passages 0x300000 (+0x10), platforms 0x310000 (+0x10), contacts 0x320000 (entries +0x28), collision control 0x350000.
frames.shm: header 0x0 (GPU extension 0x40-0xDF), slot i at `0x1000 + i*0x7E90100`, layers at slot+0x100 + i*(w*h*4).
Dev-only: `tools/status.py` reads these offsets to print host pid, mc pid, core status, heartbeat movement, state flags (`tools/status.py:1-25`).
