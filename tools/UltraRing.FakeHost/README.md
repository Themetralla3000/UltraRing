# UltraRing.FakeHost

A fake "Elden Ring" host so the ULTRAKILL guest can be tested without the real game. It speaks the same
`bridge.shm` / `frames.shm` protocol as the native host (see `docs/research/host-contract.md`) through
`UltraRing.Link.HostLink` / `HostFrames`.

## Run

```
dotnet run --project tools/UltraRing.FakeHost -- --dir D:/homelab/bridge_mod/runtime
```

Options: `--dir <path>` (sets `ERMC_DIR` for this process; default `%TEMP%\ermc`), `--spawn x,y,z`
(default `0,0,0`), `--exit-after <seconds>` (auto-close, for smoke tests). Start the guest with the same `ERMC_DIR`.
Logs go to the side panel and to `<dir>\fakehost.log`.

## What it simulates

- Window: the main window is the host window. Its client rect is published as `winX/Y/W/H`, `bbW/H`,
  `WINDOW_VALID`, `WINDOW_FOCUSED`. Resize it freely; the guest should follow `winW/winH`.
- 60 Hz tick: heartbeat, state (zone `0x3C000000`, 1 unit = 1 m, camera, player pos/quat, flags
  `CAMERA_VALID | PLAYER_VALID | WINDOW_VALID`, `CAM_OVERRIDDEN`, `COMPOSITING`).
- Life: `SETTLING` 1.5 s, then `ALIVE` and `hostLife++` (the guest must recall). Death = `DEAD` 3 s
  (`HOST_BUSY | PLAYER_DEAD`), `SETTLING` at spawn, `hostLife++`.
- Stand-in: control active (seq changed within 1000 ms) + `MOVE_HUNTER` pins the Tarnished to `hunterPos` with the
  spec's yaw-to-quaternion formula. Without it, W/S/A/D walk it.
- Terrain through the ray mailbox (only while ALIVE, 2 ms budget per tick, batches can span ticks): ground plane with a
  pit, walls, a 2 m step, a 6 m pillar, a 20 m ramp up to a platform at y=4, and a door at (0,0,10). Normals are
  synthesised like the real host (downward ray => (0,1,0), else -dir), `attr = 0`.
- Entities: 5 fake enemies (kinds 1/2/3) walking in circles, world-aligned boxes, hp/maxHp, names like `c4300`.
  Damage ring: `erHp = ceil(amount*maxHp/clamp(20*sqrt(maxHp/100),10,300))` (min 1), only for hostile, alive entities
  published last tick; at 0 hp an enemy is dead (flag bit0) for 3 s and then respawns. Every hit is logged.
- Hunter events: Hit me / `H` simulates the Tarnished losing HP (max 1200, `lastHitFrom` = nearest enemy). Enemy melee
  within 2 m (toggle in the panel).
- Deaths: `mcDeaths` change while stood in kills the Tarnished; "Kill plane" / `K` bumps `hostDeaths`.
- F8 in the host window: `mcSwitchReq++`. `hostFocusReq` change: the window is activated.
- Action: `mcActionReq` change is acked with result 1 near the door (<= 2 m, toggles open/closed), else 0.
  Prompt "Open"/"Close" is published when stood in near the door.
- Rendering: software wireframe of the scene from the CONTROL camera (camPos/camTarget/camUp/fovYDeg at the window
  aspect), entity boxes, and the Tarnished (unless `HIDE_HUNTER`). When `COMPOSITE` is set and `frames.shm` is mapped, the
  guest frame is composited on top like the memory-path shader: BGRA premultiplied, bottom-up,
  `scene = hand + world*(1-hand.a)`, `out = gui + scene*(1-gui.a)`. Slot choice uses the real pose history
  (`poseLag`, `pick_slot`). Depth occlusion is not implemented (the depth layer is ignored).
- Overlay: guest alive (mcHeartbeat moving), control flags/pose, guest frame fps, ray batches/s, damage events, entity hp.

## Controls

Host window: `H` hit me, `K` kill plane, `R` respawn, `W/S/A/D` walk (when not driven), `F8` switch request.
Side panel: buttons for the same, enemy reset, melee toggle, spawn point (applies with a respawn).

## Limitations

No collision on the free-walk mode, no depth test against the guest depth layer, no GPU frame path, passages/platform
tables are not published, the debug command mailbox and the environment block are ignored.
