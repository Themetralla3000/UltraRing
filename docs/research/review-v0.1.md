# UltraRing v0.1 guest review (never run yet)

Scope: `src/UltraRing.Ultrakill`, `src/UltraRing.Link`, `Launch.ps1`, `Install.ps1`, `tools/Build.ps1`, read against
`docs/research/host-contract.md`, `docs/research/ultrakill-internals.md` and the decompile
(`$D` = `.../scratchpad/uk-decomp`). Nothing was run or edited. Only problems that follow from the code (and the decompile
where an ULTRAKILL API is involved) are listed; items I could not settle offline are in section 5 and labelled as such.

Line numbers are per file, as in the working tree.

## Summary

| # | Severity | Where | Problem |
|---|----------|-------|---------|
| B1 | blocker (very likely) | `BridgeSession.cs:74-90` | `OnSceneLoaded` ignores `LoadSceneMode`; ULTRAKILL's own additive "Footsteps" scene resets `InBridgeScene` to false right after the sandbox loads, so the bridge never starts |
| M1 | major | `LevelShell.cs:19,53-61` | The runtime-instantiated `EventSystem(Clone)` root is not on the keep list and gets `SetActive(false)`: all UI clicking dies |
| M2 | major | `BridgeSession.cs:122-129`, `TerrainManager.cs:313-325` | Terrain starts sampling before the recall and its ground reference only ever moves down; V1 recalled to a higher spot never gets terrain |
| M3 | major | `FrameCapture.cs:222-238`, `BridgeSession.cs:325-333`, `CaptureRig.cs:226-279` | After control is released or the host disappears the capture rig stays live: the HUD canvases stay on layer 3 and the overlay canvas stays bound to a disabled camera, so ULTRAKILL shows no HUD/pause menu |
| M4 | major | `BridgeSession.cs:122`, `V1.cs:62-69` | A dead V1 is never revived by a recall (`nm.activated` is false), the death screen is invisible, and keyboard focus stays on the invisible ULTRAKILL window |
| m1-m8 | minor | see section 4 | |

Verified correct (no finding): all struct layouts and offsets against `bridge_protocol.h` as documented in the contract
(header 0xC0, state 0x114, control 0x64, hunter 0x30, entity 0x80, damage 0x20, frame header 0x38, slot size 0x7E90100,
file size 0x17BB1300, rays/hits offsets `0x20` / `0x30020`); seqlock ordering in `ReadState`/`WriteControl`/`ReadEntities`;
damage conversion (`mc = clamp(20*sqrt(maxHp/100),10,300)`, `amount = fraction*mc`, spec 6.2); host yaw (`psi+180`, spec 3.4)
and the inverse for recalls; unit conversion at every use of a host value (`Drive`, recall, terrain, proxies, near/far,
`HasGroundBelow`); the three Harmony targets exist exactly once with matching parameter names
(`GameBuildSettings.GetInstance`, `StatsManager.Restart`, `EnemyIdentifier.DeliverDamage`); `PostProcessV2_Handler.OnPreRenderCallback`
and `PortalManagerV2.OnPreRenderCallback` only act on the main/hud/virtual cameras, so the extra capture cameras do not trigger
`SetupRTs`; Player, Main Camera, HUD Camera, `PostProcessV2_Handler`, `GunControl`, `PlayerActivatorRelay`, `CanvasController`
all live under the Player root, which `LevelShell` never disables (`GroundCheck.OnEnable` unparents the Player from "FirstRoom Pit",
`GroundCheck.cs:70-73`); `Launch.ps1` argument quoting, `ERMC_DIR`/`ERBRIDGE` inheritance, Doorstop 4.x switches and PowerShell 5.1
syntax.

---

## 1. Blocker

### B1. `OnSceneLoaded` treats ULTRAKILL's additive "Footsteps" scene as a scene change

`src/UltraRing.Ultrakill/BridgeSession.cs:74-90` (subscription at `:71`)

```csharp
private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
{
    bool bridgeScene = scene.name == LevelShell.SceneName;
    if (bridgeScene == InBridgeScene && !bridgeScene) return;
    InBridgeScene = bridgeScene;
    ...
```

What happens: `SceneHelper.OnSceneLoaded` (`SceneHelper.cs:519-531`) calls `SetUpFootstepPhysicsScene` for every `Single` load, which does
`SceneManager.CreateScene(scene.name + " - Footsteps", new CreateSceneParameters(LocalPhysicsMode.Physics3D))`
(`SceneHelper.cs:604-611`). ULTRAKILL's own `PresenceController.SceneManagerOnsceneLoaded` starts with
`if (mode == LoadSceneMode.Additive) return;` (`PresenceController.cs:40-44`), and nothing else in the whole decompile loads a scene
additively, so the game itself evidently receives `sceneLoaded` for that created scene. `BridgeSession` subscribes in
`Plugin.Awake`, long before `SceneHelper.OnEnable` subscribes, so it is called first for `uk_construct` (sets `InBridgeScene = true`,
starts `LevelShell.Prepare`), then `SceneHelper` runs and creates "uk_construct - Footsteps", and `BridgeSession` is called again with
`bridgeScene == false`. The early-out only covers false->false, so `InBridgeScene` becomes false, `ReleaseControl()`, `_terrain.Reset()`,
`_enemies.Clear()`, `_capture.Teardown()` run and `Map = null`. From then on `LateUpdate` returns at `:105` every frame: no anchor,
no recall, no driving, `RestartInPlace` is a no-op, F8 does nothing. The shell still gets prepared (the coroutine is already running),
so the user sees V1 falling through an empty void and ER is never driven. On every later reload the same sequence repeats.

Even if Unity turned out not to raise the event for `CreateScene`, filtering is free and correct.

Fix:

```csharp
private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
{
    if (mode != LoadSceneMode.Single) return;     // "<scene> - Footsteps" is created additively by SceneHelper
    bool bridgeScene = scene.name == LevelShell.SceneName;
    ...
```

---

## 2. Major

### M1. `LevelShell` disables the EventSystem that `SceneHelper` instantiates at scene load

`src/UltraRing.Ultrakill/LevelShell.cs:19` (keep list), `:53-61` (disable loop). Evidence: `SceneHelper.cs:519-526`.

`SceneHelper.OnSceneLoaded` destroys the scene's `EventSystem` and `Instantiate`s its own prefab into the freshly loaded scene
(and again for the additive Footsteps event, see B1). The keep list matches `root.name` exactly against `"EventSystem"`; the live root is a
clone, i.e. named `<prefab name>(Clone)`. It is not a singleton host (`HostsSingleton` is false), so line 61 `root.SetActive(false)`
turns it off. Consequence: no pointer/UI input module, so the pause menu, options, weapon wheel, spawn menu etc. cannot be clicked
(`FirstPersonInputModule`/`StandaloneInputModule` are gone). I could not read the prefab name offline (the uk_construct bundle's scene root
list shows the original root as `EventSystem`, which `SceneHelper` destroys); treat the exact name as "verify in the log line
`Bridge shell: disabled N sandbox objects: ...`", but a name equal to the bare `"EventSystem"` is not possible for an `Instantiate` result.

Also not safe from the same loop: other roots that the game creates at runtime in the active scene before `Prepare` runs
(`new GameObject(...)` without parent, e.g. `GoreZone.ResolveGoreZone` -> "Automated Gore Zone" `GoreZone.cs:47-55`,
`CausticVolumeManager` `CausticVolume.cs:26`, `PortalAwarePlayerCollider` -> "Player Collider Clone" `PortalAwarePlayerCollider.cs:388`).

Fix: keep by component, not by name.

```csharp
using UnityEngine.EventSystems;
...
if (Keep.Contains(root.name) || root.GetComponent<BridgeMarker>() != null) continue;
if (root.GetComponentInChildren<EventSystem>(true) != null) continue;               // SceneHelper's "(Clone)"
if (root.name.EndsWith("(Clone)") || root.name == "Automated Gore Zone") continue;   // runtime-created helpers
```

More robust still: only disable roots that carry a `MeshRenderer`, `Collider`, `Light` or `AudioSource` somewhere in their subtree and
have no `MonoSingleton`, `Canvas` or `EventSystem` in it.

### M2. Terrain ground reference: sampling starts before the recall and the reference never rises

`src/UltraRing.Ultrakill/BridgeSession.cs:122-129`, `src/UltraRing.Ultrakill/Terrain/TerrainManager.cs:313-325`, `DoRecall` `BridgeSession.cs:199-212`.

1. `_terrain.Tick(...)` runs whenever `Map != null && hostAlive && nm != null` (`:124`), i.e. also while `_recallPending` is still true and
   `nm.activated` is still false (the first frames after `sceneLoaded`, before `LevelShell.Prepare` has activated V1; `DoRecall` at `:122`
   is blocked by `nm.activated`). In uk_construct V1 starts at roughly UK (-82, -6, 341) ("FirstRoom Pit" `(-82,-6,341)` in the scene bundle), so
   `UpdateGroundRef` seeds `_groundRef` from that spot (`:315-319`) and a ray batch is cast around host coordinates that mean nothing.
2. `DoRecall` moves V1 to the Tarnished (UK ~ (0,0,0) for the first anchor, anywhere for later recalls) but does not touch the terrain.
   `UpdateGroundRef` only ever lowers the reference (`feetY < _groundRef - 12`, `:323`) or adopts a floor that is already sampled within 1 m
   of the feet (`:321`). With a stale reference that is more than about 1.2 m below the real floor, the LOW ray starts inside the ground
   (spec 5.1: CastRay misses from inside solid), the HIGH ray only reaches `ref + 13.2 m` (`LowHeadroom + HighReach`), and nothing re-seeds
   the reference. For any recall that lands more than about 13 m higher than the stale reference (respawn at another grace in the same
   open-world zone: the whole of areas 60/61 is one `stageId`; or an earlier hostLife recall) no floor is ever found,
   `HasGroundBelow` stays false, `HoldForGround` times out after 4 s (`:222`) and V1 falls into the void with the temp floor
   destroyed 1 s later.

Fix:

```csharp
// TerrainManager
public void ReseedGroundReference() { _haveRef = false; }   // call from DoRecall after V1.Teleport
// UpdateGroundRef: also follow V1 upward when no known floor is near it
else if (feetY < _groundRef - 12.0 || feetY > _groundRef + LowHeadroom + HighReach * 0.5)
    _groundRef = (float)feetY;
// BridgeSession.LateUpdate
if (Map != null && hostAlive && nm != null && !_recallPending) { _terrain.Tick(...); ... }
// BridgeSession.DoRecall()
_terrain.ReseedGroundReference();
```

### M3. The capture rig outlives the control: HUD and pause menu stay redirected away from ULTRAKILL's own window

`src/UltraRing.Ultrakill/Render/FrameCapture.cs:222-238` (`Cancel`/`Teardown`), `BridgeSession.cs:325-333` (`ReleaseControl`),
`CaptureRig.cs:201-247` (`EnsureHudLayers`, `EnsureOverlay`) and `:251-279` (`Destroy`).

`CaptureRig.Render` moves every HudController canvas tree to layer 3 and switches the Player `Canvas` to `ScreenSpaceCamera` bound to a
camera that is `enabled = false` (only drawn by `UiCam.Render()`). The only way back is `CaptureRig.Destroy()`, which runs from
`DestroyRig` (scene load, size change, `Disable`, `Shutdown`). `ReleaseControl()` only calls `FrameCapture.Cancel()`, which does not touch the
rig. So after the first successful drive, whenever control is released for good (ER closed, host stopped, `WindowOverlay` falls back to a
normal window via `RestoreNormal`), ULTRAKILL's own window is left without HUD, crosshair and pause/options menu (nothing renders them any
more, so a paused game shows an empty frame). While the host is merely busy or V1 is dead the same state is harmless (ULTRAKILL's window
is invisible then), but it is indistinguishable from "host gone".

Fix: tear the rig down (restoring canvas mode and layers) when the host has been unavailable for a moment, and let `Submit` rebuild it:

```csharp
// FrameCapture
public void ReleaseRig() { Cancel(); DestroyRig(); _haveHostFrame = false; }
// BridgeSession.LateUpdate, in the "!_alive || !haveState" branch (and optionally after N s of !ready):
if (_hostGoneSince == 0) _hostGoneSince = Link.NowMs;
else if (Link.NowMs - _hostGoneSince > 2000) _capture.ReleaseRig();
```

Also call it from `HostMode`'s `EnterHostMode` (the ULTRAKILL window is hidden then, so nothing is lost but state stays clean).

### M4. A dead V1 is never revived by the recall, and nothing tells the player

`src/UltraRing.Ultrakill/BridgeSession.cs:122` (`... && nm.activated) DoRecall();`), `V1.cs:62-69` (revive branch), `StartupPatches.cs:23`.

`NewMovement.GetHurt` sets `activated = false` on death (`NewMovement.cs:2026-2027`), so the recall is blocked until
something calls `Respawn`; the `revive: true` argument in `DoRecall` is therefore dead code. The only revive path is
`StatsManager.Update` -> `Restart()` on `R` or Fire1 (`StatsManager.cs:236-241`) -> `RestartInPlace`. But with V1 dead and not driving:
nothing is composited (the death screen is not drawn in ER), the ULTRAKILL window is alpha 0 yet still the foreground window, so ER gets no
keyboard input, and Fire1 clicks fall through to ER. The host spec (6.4) wants an immediate guest respawn.

Fix:

```csharp
// BridgeSession.LateUpdate
if (_recallPending && hostAlive && Map != null && nm != null && (nm.activated || nm.dead)) DoRecall();
```

(`V1.Teleport(..., revive: true)` then runs `Respawn`/`ActivatePlayer` as in `CheckPoint.ResetRoom`.) Optionally also release the foreground to the host
when V1 is dead and not driving.

---

## 3. Contract notes that are fine but worth knowing

* `FrameCapture` publishes control only after the slot is written and the pixels exist (`Land`, `FrameCapture.cs:363-389`), and republishes
  the last control every 300 ms while `Submit` is being called (`:200-206`), so the host's 1 s `seq` timeout is respected. `ReleaseControl` writes
  `flags = 0` first and `Cancel()` clears `_haveLast`, so a stale COMPOSITE control is not resurrected. COMPOSITE is never set without a frame.
  No finding.
* The published camera/stand-in pose lags the live V1 by the readback latency (typically 2-3 render frames) and updates only at the capture
  rate (one capture per host frame). Consistent with the frame it belongs to, but ER's camera will feel slightly behind. Design trade-off, no bug.

## 4. Minor

| # | Where | Problem / fix |
|---|-------|---------------|
| m1 | `BridgeSession.cs:356-361` | `Shutdown()` calls `ReleaseControl()` then `_capture.Teardown()` then `_overlay.Restore()` with no try/finally; an exception in the first two skips the window restore. Wrap in `try { ... } finally { _overlay?.Restore(); }`. |
| m2 | `BridgeSession.cs:56` | `_controlReleased` starts true, so no `flags = 0` control is written when the guest attaches. If a previous ULTRAKILL died with COMPOSITE set, the host keeps drawing its last frame (spec 0.5: compositor has no timeout) until the new guest first drives. Write one `flags=0` control (new `seq`) the first time `Link.Poll()` reports alive. |
| m3 | `FrameCapture.cs:363-378`, `GuestFrames.cs:466-515` | Alpha repair loops (MaxRgb world + MaxRgb gui + Opaque hand, 3 x 2 Mpx at 1080p) run on the main thread in Mono, per captured frame (~15-30 ms). Expect ULTRAKILL near 30-40 fps while bridged. Move the repair into a blit shader on the way into the RT, or use `CaptureScale` < 1 until then. |
| m4 | `Launch.ps1:71-76,81-92` | If Elden Ring is already running (started outside the script, no `ERMC_DIR`/`ERBRIDGE`), the script waits 90 s for `bridge.shm` in `runtime\` and fails with a generic message. Detect an `eldenring` process whose `bridge.shm` is not in `$RuntimeDir` (check `er-bridge.log` mtime or just warn when a pre-existing host is reused) and tell the user to restart it through the script. |
| m5 | `FrameCapture.cs:55-57,190-196` | A readback counts as timed out after 750 ms of wall clock, measured in `LateUpdate` before ULTRAKILL's player loop pumps the callbacks. A single long frame (first-use shader compilation of the new cameras, scene load) can mark every queued job as an error; 8 in a row permanently disable capture (`Disable`). Count timeouts in frames as well, or raise the limit to ~2 s and decay `_errors`. |
| m6 | `LevelShell.cs:44-62` | Before the first recall V1 free-falls (the shell disables "FirstRoom Pit" and every floor) at up to -100 u/s; if ER is not in the world yet this lasts as long as the wait (thousands of units below the origin by the time the recall arrives). Pin V1 (`nm.rb.isKinematic = true`, restored in `V1.Teleport`) until the first recall. |
| m7 | `FrameCapture.cs:116-122,242-256` | `GuestFrames.Open()` (creating a 398 MB file) happens synchronously inside the first `Drive`; control `seq` is not yet running then, but the main thread stalls for the file creation. Open it at attach instead (the host retries every 2 s anyway). |
| m8 | `WindowOverlay.cs:414-432` | Click-through while "drawing nothing" relies on constant-alpha 0 on a layered window; `NativeMethods.WS_EX_TRANSPARENT` is defined but unused. See section 5. |

## 5. Unverified risks (could not be settled offline, no code evidence either way)

* Alpha-0 `WS_EX_LAYERED` windows pass mouse messages through only for per-pixel/colour-key transparency per MSDN; for
  `LWA_ALPHA` with 0 I could not confirm click-through. If it does not, ER's mouse is dead while V1 is not driving (keyboard is fine because
  `TryFocus` does not take focus then). Fix if so: add `WS_EX_TRANSPARENT` in `UpdateAlpha` when `drawNothing`, remove it when driving.
* DPI: nothing sets or queries DPI awareness. The host's `winX/winY/winW/winH` are in the host process's coordinate space, the window
  is positioned from this process. At 100% scaling it is identical; at 125-150% the overlay is only right if both processes share awareness.
* Layered window + D3D11 flip-model swapchain (`useFlipModelSwapchain = True`): may render black or opaque; the code only falls back on API failure
  (`OnLayeredFailed`), not on a visual failure. `WindowMode=Region` is the manual escape hatch.
* `AsyncGPUReadback` row order on D3D11 (`FlipRows` default false) and ULTRAKILL shader output with single-RT cameras (alpha, `_CameraDepthTexture`
  soft particles) need a first visual check; config flags exist.
* The host window owning ULTRAKILL's window (`GWLP_HWNDPARENT`): destroying the owner (closing ER) destroys owned windows, so ULTRAKILL's main
  window, and with it the player, probably closes together with ER before `RestoreNormal` can unparent it.
