# Risk of Rain 2 host for UltraRing: implementation-grade design

Goal: a BepInEx 5 plugin inside Risk of Rain 2 (RoR2) that implements the HOST side of the bridge protocol in `host-contract.md`, so the unchanged ULTRAKILL guest (V1) plays inside RoR2's stages, fights RoR2's monsters, and RoR2 draws the final frame (RoR2 world + guest layers composited on top).

Sources: decompile of the installed game (read-only), game asset files read with UnityPy (read-only), `host-contract.md`, `src/UltraRing.Link/HostLink.cs` (+ `HostFrames`), `tools/UltraRing.FakeHost/HostSim.cs`, the guest's `Terrain/TerrainManager.cs`. Nothing in the game folder or the r2modman profile was modified; no game was started.

Confidence markers: **[verified]** = read in the decompile / asset / binary; **[inferred]** = deduced from code; **[UNVERIFIED]** = needs an in-game test.

Path legend (decompile root `$D`):

| Short | Full path |
| --- | --- |
| `$D` | `C:/Users/arnau/AppData/Local/Temp/claude/D--homelab-bridge-mod/ecb58167-b098-41fe-8519-c9cf212289b9/scratchpad/ror2-decomp` |
| `R/` | `$D/RoR2/RoR2/` (namespace `RoR2`, ~1100 files; `R/CharacterBody.cs` etc.) |
| `K/` | `$D/KCC/KinematicCharacterController/` |
| `$D/RoR2/` | project root: also holds sub-namespace folders such as `RoR2.Networking/` |

Citation form `file:line` (line numbers of the decompile). `ilspycmd 8.2.0.7535 -p`, from `RoR2.dll` (5,888,512 B, 2026-02-24; contains DLC3 content), `KinematicCharacterController.dll`, `Assembly-CSharp.dll` (the latter holds only misc. shader/visual helpers, `$D/ACSharp/`).

---

## 0. Headline decisions

1. **Zero Harmony patches are needed for a first working host.** Everything required is reachable through public API and public static events: `ICameraStateProvider` (camera + input lock + HUD), `GlobalEventManager.onServerDamageDealt` (measure and neutralise damage to the stand-in), `GlobalEventManager.onCharacterDeathGlobal`, `Stage.onStageStartGlobal`, `MapZone.onBodyTeleportGlobal`, `CharacterBody.fakeActorCounter`, `CharacterModel.invisibilityCount`, `KinematicCharacterMotor.SetPosition`, `Interactor.AttemptInteraction`. No R2API, no MMHOOK/HookGen. Harmony (0Harmony in BepInEx core) stays available for fallbacks.
2. **Stand-in = the local player's own `CharacterBody`**, pinned each `FixedUpdate` to `hunterPos` through the KCC motor with all collision solving disabled, hidden, non-colliding with characters (fake actor), kept alive by restoring health inside `onServerDamageDealt` (which fires before RoR2's death check). Damage actually lost (after armor/shield) feeds the hunter-event counters. This keeps kill credit, gold, XP, item procs, enemy aggro and AI targeting natural.
3. **Camera = a custom `ICameraStateProvider` installed with `CameraRigController.SetOverrideCam(provider, 0f)`.** The same provider returns `IsUserControlAllowed = false`, which makes `PlayerCharacterMasterController` stop feeding player input into the body (`CanSendBodyInput`), so no separate input hook is needed. Releasing the override (`SetOverrideCam(null, t)`) hands the camera back smoothly (`cameraMode.MatchState`).
4. **Frame of reference**: RoR2 = Unity = left-handed, +Y up, +Z forward, 1 unit = 1 m (Commando capsule 1.82 m x 0.5 m radius, gravity -30), no floating origin. The host "stable frame" is simply Unity world space. Zone id = (scene index, per-stage-load counter).
5. **Rays**: `Physics.Raycast` on `LayerIndex.world.mask` only, `QueryTriggerInteraction.Ignore`, ~2 ms budget per frame via the existing `HostLink.ServiceRays` (synthesised normals kept for contract parity; the guest only reads `hit` and `pos.y`, so real normals would also be safe).
6. **Compositing**: a `ScreenSpaceOverlay` Canvas with 2-3 `RawImage` layers (world, optional hand, GUI) fed from `HostFrames`. Premultiplied alpha is handled by RoR2's own shader `Hopoo Games/UI/Custom Blend` (has `_SrcBlend/_DstBlend`, addressable key `Assets/RoR2/Base/Shaders/UI/HGUICustomBlend.shader`), i.e. `Blend One OneMinusSrcAlpha`; CPU un-premultiply is the fallback. Depth occlusion against RoR2 geometry is a later milestone and needs a custom shader (Unity Editor 2021.3.33f1 AssetBundle) or an async depth readback.
7. **Launch**: own staged BepInEx (like the ULTRAKILL side), not the r2modman profile: `Risk of Rain 2.exe --doorstop-enabled true --doorstop-target-assembly <staged Preloader.dll>` (the game folder's r2modman `winhttp.dll` contains exactly these switches [verified by byte search]). RoR2 already has `runInBackground = true` [verified: PlayerSettings].
8. **Biggest risks**: death/overkill path inside `HealthComponent.TakeDamageProcess`, other code taking the camera override (pod, cutscenes), premultiplied blend shader availability at runtime, per-frame texture upload cost, DPI/window-rect mismatch between the two processes, direct-exe launch without the Steam client. Full list in section 16.

---

## 1. Verified environment facts

| Item | Value | Source |
| --- | --- | --- |
| Unity | 2021.3.33f1, Mono (`MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll`), built-in render pipeline + Post-Processing v2 (`Unity.Postprocessing.Runtime.dll`) | `LogOutput.log:2`, Managed list |
| BepInEx in profile | 5.4.21 (profile log line 1), Doorstop 4.x `winhttp.dll` (24,576 B, 2023-05-28) in the game folder, `doorstop_config.ini` `enabled=true`, `target_assembly=BepInEx/core/BepInEx.Preloader.dll` (relative, i.e. dead without command line) | game folder |
| Doorstop switches in the game's `winhttp.dll` (UTF-16 strings) | `--doorstop-enabled`, `--doorstop-redirect-output-log`, `--doorstop-target-assembly`, `--doorstop-mono-dll-search-path-override`, `--doorstop-mono-debug-*`, `--doorstop-clr-*` | byte search of `winhttp.dll` |
| PlayerSettings | `runInBackground = True`, `visibleInBackground = True`, `fullscreenMode = 1` (FullScreenWindow), `activeInputHandler = 0` (legacy `UnityEngine.Input`), `m_ActiveColorSpace = 1` (**Linear**), `useFlipModelSwapchain = True`, default window 1024x768 | `globalgamemanagers` via UnityPy |
| Physics | `Physics.gravity = (0,-30,0)`, `queriesHitTriggers = false`, `queriesHitBackfaces = false`, **`autoSyncTransforms = false`**, fixed timestep **1/60 s**, max timestep 0.333 | `globalgamemanagers` PhysicsManager / TimeManager |
| Commando body prefab | root layer 0 (team layer is assigned at spawn, `CharacterBody.cs:3534`), `CapsuleCollider radius 0.5 height 1.82 center (0,0,0) direction Y`, `Rigidbody mass 100` | `ror2-base-commando_static_assets_all_*.bundle` via UnityPy |
| Main Camera prefab (`Assets/RoR2/Base/Core/Camera/Main Camera.prefab`) | child `Scene Camera`: near **0.04**, far **4000**, FOV 60, depth 0, `m_RenderingPath -1` (player setting), `AllowMSAA false`, culling mask 0x3A7F17; child `UI Camera, Worldspace` (depth 1, mask layer 21); child `UI Camera` (depth 2, mask layer 5) | same |
| Script API level | `mscorlib.dll` has `ReadOnlySpan`, `Encoding.GetBytes(ReadOnlySpan<char>, Span<byte>)`, `Buffer.MemoryCopy`; `netstandard.dll` present => the existing `UltraRing.Link` (netstandard2.1, unsafe) loads in RoR2 unchanged | `ilspycmd -t` on `mscorlib.dll` |
| Floating origin | none: no `FloatingOrigin/OriginShift/WorldOffset` anywhere in `RoR2.dll` | grep of the decompile |
| Shaders loadable at runtime (see 12.3) | `Hopoo Games/UI/Custom Blend` (props `_SrcBlend`, `_DstBlend`, `_MainTex`, `_Color`), `Legacy Shaders/Particles/Alpha Blended Premultiply` (in `calmwater_unitybuiltinshaders` bundle), `UI/Default`, `Sprites/Default`, `Unlit/Color`, ... | UnityPy over `StreamingAssets/aa/StandaloneWindows64/*shader*.bundle` |

---

## 2. Responsibility map (contract -> RoR2)

| Host responsibility (host-contract.md) | RoR2 mechanism | Section |
| --- | --- | --- |
| Create/own `bridge.shm`, header, seqlocks, heartbeat | `HostLink.Open()`, `BumpHeartbeat()` once per `Update` | 15 |
| `state.stageId`, zone re-anchoring | `(SceneIndex+1)<<16 \| (loadCounter & 0xFFFF)` | 3 |
| `PLAYER_VALID / CAMERA_VALID / PLAYER_DEAD / HOST_BUSY`, `hostLife / hostDeaths` | life state machine over `LocalUser`, `Run`, `Stage`, `HealthComponent` | 4 |
| Stand-in (`MOVE_HUNTER`, `HIDE_HUNTER`) | KCC pin + `fakeActorCounter` + `invisibilityCount` | 5 |
| Hunter damage events (`OFF_HUNTER`) | `GlobalEventManager.onServerDamageDealt` | 5.4 |
| Camera override (`OVERRIDE_CAMERA`), camera fields, window rect | `ICameraStateProvider`, `Win32 GetClientRect/ClientToScreen` | 6 |
| Ray mailbox | `Physics.Raycast`, `HostLink.ServiceRays` | 7 |
| Entity table | `CharacterBody.readOnlyInstancesList` + hurtbox bounds | 8 |
| Damage ring (guest -> enemies) | `DamageInfo` + `HealthComponent.TakeDamage` + `GlobalEventManager.OnHitEnemy/OnHitAll` | 9 |
| Action / prompt | `InteractionDriver` / `Interactor` / `IInteractable.GetContextString` | 10 |
| F8 (both directions), `hostFocusReq`, `mcSwitchReq` | `GetAsyncKeyState`, `SetForegroundWindow`, override release | 11 |
| Compositor (`COMPOSITE`) | Canvas overlay fed from `HostFrames` | 12 |
| Environment block, passages, platforms, contacts | not implemented (leave zero / count 0) | 2.1 |

### 2.1 Not implemented on purpose

- `OFF_ENVIRONMENT` (time/weather): never write; RoR2 keeps its own sky.
- `OFF_PASSAGES`, `OFF_PLATFORMS`: guest has continuous colliders and ignores them (contract 5.3, 10.3). They are **not cleared** by the shm init, so on `Open()` also write `count = 0` into both headers once (stale data from an Elden Ring session would otherwise survive). `supportEpoch/supportTravelY` in state stay 0, `SUPPORT_VALID` never set.
- `OFF_CONTACTS`, `OFF_COLLISION_CONTROL`: dead on the protocol side (contract 0.1).

---

## 3. (a) Units, coordinates, zone id

- **Handedness/up/forward**: Unity left-handed, +Y up, +Z forward = host frame. No Z mirror. Positions cross the protocol unchanged (stable frame == Unity world space).
- **Units**: 1 unit = 1 m. Evidence: Commando capsule 1.82 x 0.5 (a person), gravity -30 m/s^2, `CharacterBody.footPosition = transform.position - (0, capsuleHeight*0.5, 0)` (`R/CharacterBody.cs:2035`). Publish `unitsPerMeter = 1.0`.
- **Guest scale**: the guest picks `scale` (m per ULTRAKILL unit); `ultrakill-internals.md` suggests 0.5 (V1 3.5 u -> 1.75 m, close to Commando's 1.82 m, walk 16.5 u/s -> 8.25 m/s vs Commando 7 m/s). Nothing host-side depends on it.
- **No floating origin**: world coordinates are fixed for the lifetime of a loaded scene. A scene re-load (stage change, or the same stage re-entered after looping) produces different geometry and spawn points, so it must be a new zone.
- **`stageId` (state 0x7C)**: must be non-zero while valid and change on every scene load:

```csharp
// updated in Stage.onStageStartGlobal (R/Stage.cs:187, invoked at the end of Stage.Start, :189-210)
_loadCounter++;
int si = (int)(Stage.instance.sceneDef.sceneDefIndex);        // SceneIndex enum, R/SceneDef.cs:139
StageId = (uint)((si + 2) << 16) | (_loadCounter & 0xFFFFu);  // never 0, never 0xFFFFFFFF
```

  `SceneCatalog.mostRecentSceneDef` (`R/SceneCatalog.cs:37`, `onMostRecentSceneDefChanged` :39) and `SceneDef.baseSceneName`/`cachedName` (`R/SceneDef.cs:141,194`) give the readable name for logs ("golemplains", "blackbeach", ...). The guest treats the value as opaque and re-anchors when it changes; it stops sending (flags=0) until it has anchored, which is exactly the SETTLING window below.
- **Entity ids** include the load counter (section 8) so an id cannot alias across stage loads.
- **Yaw convention** (contract 3.4): the guest sends `hunterYawDeg = psi + 180` for a Unity heading `psi` (forward `(sin psi, cos psi)`); the host therefore converts back with `psi = hunterYawDeg - 180`. `state.playerQuat` is published as `(0, sin(t/2), 0, cos(t/2))` with `t = (psi + 180) deg`, so the guest's `a = 2*atan2(qy,qw)`, `forward = (-sin a, -cos a)` yields `(sin psi, cos psi)` again. Verified algebraically: `(-sin(psi+180), -cos(psi+180)) = (sin psi, cos psi)`.

---

## 4. (b) Life state machine

### 4.1 Signals (all main-thread, polled once per `Update`)

| Signal | API |
| --- | --- |
| Local user | `LocalUserManager.GetFirstLocalUser()` (`R/LocalUserManager.cs:133`); splitscreen/other users ignored |
| Master / body | `LocalUser.cachedMaster`, `cachedBody`, `cachedBodyObject` (`R/LocalUser.cs:77-85`); event `LocalUser.onBodyChanged` (:112) |
| Camera rig | `LocalUser.cameraRigController` (`R/LocalUser.cs:89`), `CameraRigController.sceneCam/uiCam` (`R/CameraRigController.cs:39,42`) |
| Run alive | `Run.instance != null` (`R/Run.cs:592`), `Run.instance.isGameOverServer` (:648), events `Run.onRunStartGlobal/onRunDestroyGlobal/onServerGameOver` (:801-809) |
| Server | `NetworkServer.active` (single-player host == server, section 14) |
| Stage | `Stage.instance` (`R/Stage.cs:50`), `Stage.instance.sceneDef.sceneType` (`SceneType.Stage`, `Intermission`, `TimedIntermission`, `UntimedStage` are playable; `Menu`, `Cutscene`, `Invalid`, `Junk` are not; `R/SceneType.cs`) |
| Leaving the stage | `SceneExitController.isRunning` (`R/SceneExitController.cs:47`), events `onBeginExit/onFinishExit` (:49,51) |
| Pod / vehicle | `CharacterBody.currentVehicle` (`R/CharacterBody.cs:1662`); stage-1 pod uses `SurvivorPodController` -> `cameraRigController.SetOverrideCam(this, 0f)` (`R/SurvivorPodController.cs:66,70`) |
| Body alive | `body.healthComponent.alive` (`R/HealthComponent.cs:503`), body destroyed |
| Pause | `PauseStopController.instance.isPaused` (`R/PauseStopController.cs:36,46`); single-player pause sets `Time.timeScale = 0` (`:208, :254`) |
| Other camera users | `rig.hasOverride` (`R/CameraRigController.cs:196`), `rig.IsOverrideCam(x)` (:327) |

### 4.2 States

```
NONE      no Run / no server / no LocalUser / scene is Menu or Cutscene / game over
SETTLING  playable stage, body may exist, but not yet usable (pod, falling in, other camera override,
          stage just loaded, respawned this second, teleported by RoR2)
ALIVE     body exists, alive, no vehicle, no foreign camera override, grounded-and-still after the settle time
DEAD      the player's body died (or is dying) and RoR2 has not produced a new one yet
```

Transitions (host side counters in brackets):

| From -> to | Condition | Effect |
| --- | --- | --- |
| any -> SETTLING | `Stage.onStageStartGlobal` (also bump `_loadCounter`); `LocalUser.onBodyChanged` with a new live body; `MapZone.onBodyTeleportGlobal` (`R/MapZone.cs:80`) for the stand-in body; `currentVehicle != null`; a foreign override appears | release control (section 5.5), clear entity table, `settleMin = 60` fixed ticks (1 s) after a new body/stage, `10` after an RoR2 teleport |
| SETTLING -> ALIVE | body alive, `currentVehicle == null`, `!rig.hasOverride` (or ours), `settleMin` elapsed, and body "still" for 0.5 s (`characterMotor.velocity.sqrMagnitude < 0.05`, or `isGrounded`) | **`hostLife++`** (the guest recalls to `state.playerPos`), `_everAlive = true` |
| ALIVE -> DEAD | body null/destroyed or `!healthComponent.alive` after being alive (RoR2 death states keep the body for seconds) | if not requested by the guest: **`hostDeaths++`** |
| DEAD -> SETTLING | new body appears (extra life `RespawnExtraLife`, `CharacterMaster.Respawn`, next stage) | |
| any -> NONE | `Run.instance == null`, `isGameOverServer`, scene type not playable, `!NetworkServer.active` | zero camera/player in state |

Published flags (same bit semantics as the fake host, `HostSim.PublishState`):

- `HOST_BUSY` = life == DEAD, or (`life != ALIVE` and `_everAlive` and `< 60 s` since last ALIVE/DEAD tick), **or `Time.timeScale == 0` (RoR2 paused)**. The guest then draws nothing and releases control, which is exactly what we want while the RoR2 pause menu is up.
- `PLAYER_DEAD` = DEAD.
- `CAMERA_VALID | PLAYER_VALID` only while ALIVE; while not ALIVE leave cam/player/stageId/support fields at zero (contract 2.3).
- `WINDOW_VALID | WINDOW_FOCUSED` from Win32 (section 6.4).

### 4.3 Counters

- **`hostLife`**: +1 on every SETTLING -> ALIVE. Guest reaction (recall) is unchanged. Not bumped on F8 return (the guest handles that itself, contract 4.3) and not bumped while the guest is simply walking.
- **`hostDeaths`**: +1 when the stand-in dies without the guest asking. Because all damage is neutralised (5.4), this only happens for non-damage kills (`HealthComponent.Suicide`, `Networkhealth = 0` by other code, mods). Detect with `GlobalEventManager.onCharacterDeathGlobal` (`R/GlobalEventManager.cs:172`): `damageReport.victimBody == standIn && !_expectDeath`.
- **`mcDeaths`**: guest-written; `HostLink.PollGuestDeath()` (first poll latches). Honour only if ALIVE, stood in within 2000 ms, not dead (contract 6.4). Policy `GuestDeathPolicy` (plugin config):
  - `SoftRespawn` (recommended default until the whole loop is trusted): heal to full, `TeleportHelper.TeleportBody(body, Stage.instance.GetPlayerSpawnTransform().position)` (`R/TeleportHelper.cs:172`, `R/Stage.cs:295`), go through SETTLING (so `hostLife++` triggers the guest respawn). RoR2 never sees a death, the run continues.
  - `Kill`: `body.healthComponent.Suicide(null, null, default)` (`R/HealthComponent.cs:2085`), set `_expectDeath = true` first so it is not echoed as `hostDeaths`. This is the real RoR2 death: if the player has Dio's Best Friend the body respawns (`CharacterMaster.RespawnExtraLife`, `R/CharacterMaster.cs:1577`); otherwise `Run.BeginGameOver` ends the run (`R/Run.cs:2294`): life goes DEAD -> NONE and the guest sees HOST_BUSY then no PLAYER_VALID.

### 4.4 Stage 1 pod and other cutscene-like states

On stage 1 the player starts in a `SurvivorPodController` vehicle with its own override camera (`R/Stage.cs:33` `usePod`). Two options: (a) wait it out (SETTLING while `currentVehicle != null`, ~6 s); (b) disable the pod: the ConVar is `stage1_pod` (`R/Stage.cs:35`, flag `Cheat`) and can be set directly: `RoR2.Console.instance.FindConVar("stage1_pod")?.SetString("0")` (direct `SetString` does not check the cheat flag [inferred]). Recommended: (b) behind a config switch `SkipPod = true`.

Teleporter events, boss intros, the Mithrix/Commencement cutscenes and the Bazaar all use `SetOverrideCam`/`ForcedCamera` (`R/ForcedCamera.cs:29,42`): treat `rig.hasOverride && !rig.IsOverrideCam(ours)` as SETTLING/HOST_BUSY, never fight another provider.

---

## 5. (c) The stand-in

### 5.1 Pinning the body to `hunterPos` / `hunterYawDeg`

`hunterPos` is the guest's feet. RoR2's body transform is the capsule centre: `footPosition = transform.position - (0, capsuleHeight*0.5, 0)` (`R/CharacterBody.cs:2035-2045`), so

```csharp
Vector3 centre = feet + Vector3.up * (m.capsuleHeight * 0.5f);   // exact inverse of CharacterBody.footPosition
```

The body is driven by `KinematicCharacterMotor` (`CharacterMotor : BaseCharacterController`, `R/CharacterMotor.cs:15`; KCC API in `K/KinematicCharacterMotor.cs`):

- `Motor.SetPosition(pos, bypassInterpolation: true)` (`:376`), `SetPositionAndRotation` (`:398`), `MoveCharacter(pos)` (`:412`).
- RoR2's own teleport recipe (`R/TeleportHelper.cs:132-170`, `OnTeleport`): `Motor.MoveCharacter(p); Motor.SetPosition(p); characterMotor.rootMotion = 0; velocityAuthority = (0, min(vy,0), 0)`; it is local only (no network message), so it is the right call for a per-tick pin. `TeleportHelper.TeleportBody(body, footPos)` (`:172`, adds 0.1 m up, converts foot to centre, sends `TeleportMessage` 68) is the right call for one-shot moves (soft respawn, recall).

Per `FixedUpdate` (RoR2 fixed step is 1/60 s; the pin must happen before the physics step and KCC's simulate, so do it from `RoR2Application.onFixedUpdate` or a plugin `FixedUpdate`; with `Physics.autoSyncTransforms == false` the new transform reaches colliders/hurtboxes at that step, which is what enemy attack queries then see):

```csharp
var cm = body.characterMotor;            // R/CharacterBody.cs:1784
var kcc = cm.Motor;                      // K/BaseCharacterController.cs: Motor property
kcc.SetPosition(centre, true);
cm.velocity = Vector3.zero;              // R/CharacterMotor.cs:122 (public field)
cm.rootMotion = Vector3.zero;
cm.disableAirControlUntilCollision = false;
body.GetComponent<CharacterDirection>().yaw = psiDeg;   // R/CharacterDirection.cs:169 (model/forward)
body.inputBank.aimDirection = guestForward;             // R/InputBankTest.cs:119, see 10 and 6.3
```

One-time setup when control starts (store the originals for restore, 5.5):

| What | API | Why |
| --- | --- | --- |
| No gravity/fall damage | `cm.gravityScale = 0f` (`R/CharacterMotor.cs:60`); `velocity` zeroed each tick | gravity is `Physics.gravity.y * gravityScale` in `PreMove` (`:432-475`) |
| No KCC collision response / depenetration against RoR2 geometry that the guest does not know | `kcc.SetCapsuleCollisionsActivation(false)`, `SetMovementCollisionsSolvingActivation(false)`, `SetGroundSolvingActivation(false)` (`K/KinematicCharacterMotor.cs:361,366,371`) | the guest's terrain is a ray-built approximation of RoR2's; letting KCC "fix" the position would fight the pin. The body becomes a pure kinematic point; monsters still hit it through its hurtboxes (layer `entityPrecise`), not the capsule |
| Monsters do not push or block it | `body.fakeActorCounter++` (`R/CharacterBody.cs:1962`, effect `SetFakeActor` :5079 -> `LayerIndex.GetAppropriateFakeLayerForTeam`, rebuilds KCC collidable layers) | same facility the Drifter bag uses |
| Hidden | `modelLocator.modelTransform.GetComponent<CharacterModel>().invisibilityCount++` (`R/CharacterModel.cs:602`; hides the model and shadows, `:1014-1016`, `:2006-2013`). RoR2 precedent: `R/DrifterBagController.cs:281-293` | `HIDE_HUNTER` |
| Do NOT deactivate hurtboxes | (`HurtBoxGroup.hurtBoxesDeactivatorCounter` exists but would make monsters ignore the player) | the stand-in must remain targetable and hittable |

Skills/inputs: with the camera provider's `IsUserControlAllowed == false` the player controller pushes all buttons as released (`R/PlayerCharacterMasterController.cs:319-346 CanSendBodyInput`, `:409-450 PollButtonInput` falls into the `else { flag = true; }` branch, `moveVector` stays zero in `Update` :353-398). As belt and braces also zero `inputBank.moveVector` each fixed tick.

### 5.2 Gravity and "GROUNDED/FLYING"

The guest owns vertical motion (it runs V1's physics against ray-built terrain). The host ignores `GROUNDED/FLYING` (they only matter to ER's platform logic). `supportEpoch = 0` is echoed by the guest, so no platform adjustment is ever applied. RoR2 moving platforms are rare and are not attempted.

### 5.3 Rotation

`CharacterMotor.UpdateRotation` returns identity (`R/CharacterMotor.cs:582-585`), body orientation comes from `CharacterDirection.yaw`. Set it from `psi = hunterYawDeg - 180` (3). The model is hidden, so this only matters for consumers of the body forward (AI facing checks) and for `state.playerQuat`.

### 5.4 Keeping it alive and measuring damage

Design (**[inferred] from code; needs the overkill test in milestone 5**):

- All damage to the stand-in flows through `HealthComponent.TakeDamageProcess` (`R/HealthComponent.cs:1037`). Just before the death check it calls `GlobalEventManager.ServerDamageDealt(damageReport)` (`:1795`), i.e. the public static event `GlobalEventManager.onServerDamageDealt` (`R/GlobalEventManager.cs:182`, raised at `:2275`), and **then** evaluates `if (!alive)` (`:1796`). Armor, shield, barrier, blocks, one-shot protection are already applied at this point; `DamageReport.damageDealt` is the HP/shield actually lost (constructed from `num4` after armor `:1379-1386`).
- Handler (server side):

```csharp
GlobalEventManager.onServerDamageDealt += r =>
{
    if (r.victimBody != _standIn || !_driving) return;
    float lost = Mathf.Min(r.damageDealt, r.combinedHealthBeforeDamage);
    if (lost > 0f) ReportHunterHit(lost / HunterMaxHp ...);   // 5.6
    // neutralise: give everything back before RoR2's `if (!alive)` runs
    var hc = r.victim;                       // HealthComponent
    hc.Networkhealth = hc.fullHealth;        // public SyncVar setters, R/HealthComponent.cs:541,554,593
    hc.Networkshield = hc.fullShield;
    hc.Networkbarrier = 0f;
};
```

  Because `godMode` early-returns at the top of `TakeDamageProcess` (`:1069`), using `godMode = true` would skip everything (no number to measure, no armor math); that is only the fallback (mode B): Harmony prefix on `HealthComponent.TakeDamage(DamageInfo)` (`:1005`) that estimates `lost = damage * (armor>=0 ? 1-armor/(armor+100) : 2-100/(100-armor))` (same formula as `:1386`) and sets `godMode` for the duration of the control.
- `HealthComponent.Suicide`/`Kill` paths do not pass `ServerDamageDealt` and are therefore the only ways the stand-in dies (4.3 `hostDeaths`).
- Fall damage / lava / Void fog: fall damage cannot occur (velocity forced to zero); hazards still reach the handler and are reported as ordinary hunter damage.
- DoT ticks (burn, bleed, poison) produce many small events; acceptable (contract 6.3 has the same property).

### 5.5 Releasing control (F8, `flags = 0`, control timeout, SETTLING, guest gone)

On any release condition (control not active for > 1000 ms via `HostLink.UpdateControl()`, flags without `MOVE_HUNTER`, life != ALIVE, F8):

1. `cm.gravityScale = saved`, `kcc.SetCapsuleCollisionsActivation(true)`, `SetMovementCollisionsSolvingActivation(true)`, `SetGroundSolvingActivation(true)`, `body.fakeActorCounter--`, `CharacterModel.invisibilityCount--` (only if this host incremented it; track a bool each).
2. `rig.SetOverrideCam(null, 0.35f)`: RoR2's `SetOverrideCam` calls `cameraMode.MatchState(currentCameraState)` when an override is removed (`R/CameraRigController.cs:314-325`), so the normal camera resumes from the last guest pose without a jump.
3. Unregister damage neutralisation (`_driving = false`): from here real damage can kill the player again.
4. Optional: if the body ended inside geometry, `TeleportHelper.FindSafeTeleportDestination(footPos, body, rng)` (`R/TeleportHelper.cs:277`).

### 5.6 Hunter events

`HostLink.ReportHunterHit(damage, from, hunterMaxHp, frame, kind)`:

- `damage` = `lost` above (RoR2 HP + shield units); `hunterMaxHp` = `hc.fullCombinedHealth` (`R/HealthComponent.cs:513`) so the guest's `share = hostDamage / hunterMaxHp` is a fraction of the player's full bar. (Barrier is excluded.)
- `from` (`lastHitFrom`, feet of the attacker): `r.attackerBody != null ? r.attackerBody.footPosition : r.damageInfo.position`.
- `kind`: `attackerBody.isChampion || attackerBody.isBoss` -> `EntLargeMonster (1)`, else `EntSmallMonster (2)` (default LARGE when unknown, like ER).
- `frame` = current state frame (heartbeat value).

---

## 6. (d) Camera override

### 6.1 Mechanism

`CameraRigController.LateUpdate` (`R/CameraRigController.cs:504-555`): runs the camera mode, then if `overrideCam != null` calls `overrideCam.GetCameraState(this, ref state)`, lerps from `lerpCameraState` (only if a lerp is pending) and calls `SetCameraState` (`:643`), which sets `transform` and `sceneCam.fieldOfView` (vertical, degrees) and adds screen-shake displacement:

```csharp
base.transform.SetPositionAndRotation(position2, cameraState.rotation);
sceneCam.fieldOfView = cameraState.fov;
```

`LateUpdate` returns early when `Time.deltaTime == 0f || isCutscene` (`:511`): while RoR2 is paused the camera simply holds.

Provider (the same object also locks input and, optionally, hides the HUD):

```csharp
sealed class GuestCamera : ICameraStateProvider       // R/ICameraStateProvider.cs
{
    public bool Has; public CameraState Pose; public bool HideHud;
    public void GetCameraState(CameraRigController rig, ref CameraState s)
    { if (!Has) return; s = Pose; Applied = true; }
    public bool IsUserLookAllowed(CameraRigController r) => false;
    public bool IsUserControlAllowed(CameraRigController r) => false;   // -> PlayerCharacterMasterController input off
    public bool IsHudAllowed(CameraRigController r) => !HideHud;
}
// install (each Update, because rigs are recreated per stage):
var rig = LocalUserManager.GetFirstLocalUser()?.cameraRigController;
if (rig && !rig.IsOverrideCam(cam) && !rig.hasOverride) rig.SetOverrideCam(cam, 0f);   // 0 = no lerp
```

`rig.isControlAllowed` (`:198`) is `overrideCam.IsUserControlAllowed`; `isHudAllowed` (`:210`) is `overrideCam.IsHudAllowed`. Other users of the same facility: `SurvivorPodController`, `ForcedCamera`, `FireballVehicle`, `SojournVehicleBase`; install only when `!rig.hasOverride`.

### 6.2 Control -> `CameraState`

Contract 3.3: `camPos`, `camTarget` (only the direction matters), `camUp`, `fovYDeg` (vertical, applied only if `5 < fov < 170`). All in the stable frame = Unity world.

```csharp
var fwd = (target - pos).normalized;
Pose.position = pos;
Pose.rotation = Quaternion.LookRotation(fwd, up);     // roll honoured through `up`
Pose.fov      = (fovY > 5f && fovY < 170f) ? fovY : rig.sceneCam.fieldOfView;
```

`Camera.fieldOfView` is vertical, so no conversion. Aspect and clip planes remain RoR2's: publish them from `rig.sceneCam` (`aspect`, `nearClipPlane` 0.04, `farClipPlane` 4000). The guest must render at `winW:winH` aspect (it already does for ER).

First-person considerations: the camera is placed by the guest at V1's eye; the body model is invisible (5.1), so no self-occlusion; `CharacterModel` first-person fade (`R/CharacterModel.cs:1005-1013`) is harmless. Near plane 0.04 m is fine. RoR2 `uiCam`s keep working (HUD is not part of `sceneCam`).

### 6.3 Aim for the body (needed for interactions)

With `IsUserControlAllowed == false` the player controller no longer writes `bodyInputs.aimDirection` (it only assigns the previous value back, `R/PlayerCharacterMasterController.cs:353-398`), so set it yourself each frame from the guest camera: `body.inputBank.aimDirection = Pose.rotation * Vector3.forward` (setter normalises, `R/InputBankTest.cs:119-134`). `aimOrigin` is read-only (`aimOriginTransform`, `:135`), which is acceptable (a chest-height origin close to V1's eye).

### 6.4 Publishing camera fields and the window rect

- Camera fields in `ErmcGameState` = the pose actually applied this frame (written from `GetCameraState`, so no script-order issue): `camPos`, `camTarget = pos + fwd`, `camUp`, `fovYDeg`, `nearZ/farZ/aspect` from `sceneCam`; flag `CAM_OVERRIDDEN` if applied since the previous publish; `CAMERA_VALID` while ALIVE. When no override is applied (normal play) publish `rig.currentCameraState` (public field, `:100`).
- `HostFrames.NoteAppliedPose(control.mcFrame)` exactly when `GetCameraState` applied a guest pose (once per frame): this feeds the 8-entry pose history the compositor needs (12.4).
- **Window rect** (`winX/winY/winW/winH`, `bbW/bbH`, `WINDOW_VALID`, `WINDOW_FOCUSED`): the client area of RoR2's window in screen coordinates, via Win32. Reuse `src/UltraRing.Ultrakill/Platform/WindowFinder.cs` + `NativeMethods.cs` (class `UnityWndClass`, `EnumThreadWindows` on the main thread) by linking the two source files into the new project:

```csharp
IntPtr hwnd = WindowFinder.FindUnityWindow();          // cache, revalidate with IsWindow every ~1 s
GetClientRect(hwnd, out RECT c);                       // c = (0,0,w,h)
var p = new POINT { x = 0, y = 0 }; ClientToScreen(hwnd, ref p);
winX = p.x; winY = p.y; winW = c.right; winH = c.bottom;
bbW = (uint)winW; bbH = (uint)winH;                    // should equal Screen.width/Screen.height
focused = GetForegroundWindow() == hwnd;
```

  Sample at most every 100 ms. **DPI**: if the two processes differ in DPI awareness the numbers are virtualised differently; check `GetDpiForWindow` / `Screen.width` equality at startup and log a warning (**[UNVERIFIED]**, same risk exists with ER). RoR2 must be windowed or borderless: ConVars `window_mode Window|Fullscreen` (`R/SettingsConVars.cs:43`; `Fullscreen` = `FullScreenWindow`, borderless, fine; `FullscreenExclusive` is not usable), `resolution 1920x1080x60` (`:90`). Launch with `-screen-fullscreen 0 -popupwindow` like the ULTRAKILL launcher, but RoR2 re-applies its saved settings after load, so set them through the convars/config as well.
- **Screen shake**: `SetCameraState` adds `ShakeEmitter.ComputeTotalShakeAtPoint` scaled by `userProfile.screenShakeScale` (`R/UserProfile.cs:52`, used at `R/CameraRigController.cs:645-651`) to the transform only. To keep the RoR2 render exactly aligned with the guest pose set `LocalUser.userProfile.screenShakeScale = 0f` while driving and restore afterwards (do not persist: it is an archived profile field).

---

## 7. (e) Ray mailbox

### 7.1 What to cast against

- Mask: `LayerIndex.world.mask` (`R/LayerIndex.cs`, `public static readonly LayerIndex world`). Characters live on `playerBody/enemyBody` (`R/CharacterBody.cs:3534`, `LayerIndex.GetAppropriateLayerForTeam`), hurtboxes on `entityPrecise`, pickups on `pickups`, debris on `debris`, projectiles on `projectile`; none of those are in `world`, so the equivalent of ER's "map geometry and props, not characters" (`kTerrainRayFilter 0x5D`) is exactly `world`. Do **not** use `CommonMasks.bullet` (`world | entityPrecise`, `R/LayerIndex.cs:9`) or `interactable` (includes `defaultLayer`, i.e. chests/barrels *and* the cast of default-layer props); an optional second mask `world | defaultLayer` could include chests as obstacles later (config).
- `QueryTriggerInteraction.Ignore` (kill zones, MapZones, holdout zones are triggers and must not block V1). Project default is already `queriesHitTriggers = false`.
- Backface behaviour like ER: `queriesHitBackfaces = false`, so a ray starting inside a mesh collider misses it (same documented ER quirk).

### 7.2 Servicing and budget

Use the existing `HostLink.ServiceRays(cast, budgetMs = 2.0)` unchanged, only while ALIVE, once per `Update`:

```csharp
bool Cast(Vector3 s, Vector3 e, out Vector3 hit)
{
    Vector3 d = e - s; float len = d.magnitude;
    if (len < 1e-5f) { hit = default; return false; }
    if (Physics.Raycast(s, d / len, out RaycastHit h, len, WorldMask, QueryTriggerInteraction.Ignore))
    { hit = h.point; return true; }
    hit = default; return false;
}
```

`Physics.Raycast` costs a few microseconds, so the 2 ms budget serves ~500-1500 rays/frame (the guest batches 2048); a batch finishes over 2-4 frames at 60 fps. If that proves too slow, switch to `RaycastCommand.ScheduleBatch` (Unity.Collections is shipped; `RaycastCommand(from, direction, distance, layerMask, maxHits)` in 2021.3): 8192 rays is ~1 ms across worker threads; results are read next frame (the mailbox already tolerates latency). Also call `Physics.SyncTransforms()` once per frame before servicing when a pin moved this frame (autoSyncTransforms is false), mostly relevant for moving props.

Reset at attach: `ServiceRays` only answers while `reqSeq != respSeq`; a stale unanswered batch from a previous session is processed first, as in ER (contract 5.1) - nothing to add host-side.

### 7.3 Normals

The guest's `TerrainManager.ApplyResults` (`src/UltraRing.Ultrakill/Terrain/TerrainManager.cs:434-470`) reads only `hits[i].hit` and `hits[i].pos[1]`; a grep over `src/` shows `RayHits` is used nowhere else. So both "synthesised like ER" (what `HostLink.ServiceRays` does: downward => `(0,1,0)`, else `-dir`) and real `RaycastHit.normal` are safe. Recommendation: keep the synthesised normals (zero change to `HostLink`, strict parity); if real normals are wanted later, add an overload with `out Vector3 normal`.

---

## 8. (f) Entities

### 8.1 Enumeration

```csharp
foreach (CharacterBody b in CharacterBody.readOnlyInstancesList)      // R/CharacterBody.cs:1216
{
    if (b == standIn || !b || !b.healthComponent) continue;
    Vector3 core = b.corePosition;                                     // :2023
    if ((core - playerFeet).sqrMagnitude > 80f*80f) continue;          // ER: 80 m
    ...
}
```

Cap 256 (sort by distance if more). Run every frame or every 2nd frame (RoR2 can have 100+ bodies late game; `readOnlyInstancesList` includes players, drones, minions, items' summons).

### 8.2 Field mapping (`ErmcEntity`, `HostLink.FillEntity`)

| Field | RoR2 source |
| --- | --- |
| `id` (u64, non-zero, stable while alive) | `((ulong)_loadCounter << 32) \| b.netId.Value` (`networkIdentity.netId`, `R/CharacterBody.cs:1782`; `netId` is unique within the session in single-player). Fallback `(uint)b.gameObject.GetInstanceID()` if there is no `NetworkIdentity` |
| `kind` | hostility: `TeamManager.IsTeamEnemy(playerTeam, b.teamComponent.teamIndex)` (`R/TeamManager.cs:277`, just `a != b`) **and** team in {`Monster`, `Lunar`, `Void`} -> hostile; hostile & (`b.isChampion` (`:1654`) or `b.isBoss` (`:2085`, master flag) or half-extent y > 3 m) -> `EntLargeMonster (1)`; other hostile -> `EntSmallMonster (2)`; everything else (player-team drones/minions, `Neutral` NPCs such as shopkeepers) -> `EntOther (3)`. The guest treats only 1/2 as enemies. Optionally a config toggle hides non-hostiles entirely |
| `emId` | `(uint)b.bodyIndex` (`R/CharacterBody.cs:1146`) |
| `name[48]` | `BodyCatalog.GetBodyName(b.bodyIndex)` (`R/BodyCatalog.cs:102`, e.g. `"LemurianBody"`), truncated to 47 ASCII bytes |
| `pos` (feet) | `b.footPosition` (`:2035`), or for flyers `bounds.min.y` of the box below (flying bodies have no motor capsule; `footPosition` is then `transform.position`) |
| `boxCenter / boxHalf` (world-aligned AABB) | union of `Collider.bounds` over `b.hurtBoxGroup.hurtBoxes[i].collider` (`R/HurtBoxGroup.cs:49`, `R/HurtBox.cs:76`), skipping null/disabled; fallback when no hurtbox: capsule `(corePosition, (radius, max(height/2, radius), radius))` from `b.radius` (`:2069`) and `characterMotor.capsuleHeight`. Clamp half-extents to <= 40 m. Uses world-space AABBs, i.e. matches the contract's "boxes are world-aligned" (flag `EntityWorldBox` always set by `FillEntity`) |
| `hp` | `hc.health + hc.shield` clamped to `[0, maxHp]` (`R/HealthComponent.cs:357,362`) |
| `maxHp` | `hc.fullCombinedHealth` (`:513`) = `fullHealth + fullShield`. Must be exactly the value used in the damage formula (9.1) |
| `flags` bit0 (dead) | `!hc.alive` or `hc.health <= 0`. RoR2 destroys bodies shortly after death, so dead entries live only a few frames; keep an entry for 0.5 s after the body vanished with `flags |= Dead` so the guest removes its proxy cleanly |

`Collider.bounds` of a KCC-driven character lags at most one fixed step. Large bosses (Titan, Vagrant, Beetle Queen, Mithrix) get big, loose AABBs: consistent with the contract (AABB hit testing), only less precise.

### 8.3 Bookkeeping for damage

Keep `Dictionary<ulong, CharacterBody> _published` rebuilt on each publish (previous publish stays valid for one frame, like ER's `find_published`): the damage ring is serviced **before** entities are republished and may only reference ids from the last publish (contract 6.2).

---

## 9. (g) Damage application (guest -> RoR2)

### 9.1 Conversion

Contract 6.2: `hp = ceil(amount * maxHp / clamp(20*sqrt(maxHp/100), 10, 300))`, minimum 1, `0 < amount < 10000`, target must be a hostile published id:

```csharp
static int ErHp(float amount, float maxHp)      // identical to HostSim.ErHp (tools/UltraRing.FakeHost/HostSim.cs:386)
{ float mc = Mathf.Clamp(20f * Mathf.Sqrt(maxHp / 100f), 10f, 300f);
  return Mathf.Max(1, (int)Mathf.Ceil(amount * maxHp / mc)); }
```

With the published `maxHp` this makes "fraction f of target max HP" exact: `amount = f * mc(maxHp)` -> `hp = ceil(f*maxHp)`. Corrections on top of it (config):

- **Armor**: RoR2 reduces `damageInfo.damage` by the target's armor (`R/HealthComponent.cs:1379-1386`: `x * (1 - a/(a+100))` for `a >= 0`). To keep the fraction semantics exact: `damageInfo.damage = hp * (100f + max(armor,0))/100f` (use `target.armor`); option `CompensateArmor = true`.
- **Player damage stat**: RoR2's own skills multiply by `body.damage`. Default `DamageScale = 1` (hp as-is; items like Crowbar/Lens do not scale it). Optional `ItemDamageScale = body.damage / (body.baseDamage + body.levelDamage*(body.level-1))` (`R/CharacterBody.cs:1264,1290`) to let damage items matter.

### 9.2 Building the hit (exactly the sequence `BulletAttack.DefaultHitCallbackImplementation` uses, `R/BulletAttack.cs:298-360`)

```csharp
var dmg = new DamageInfo {
    damage          = hpWithCorrections,
    crit            = false,                         // or body.RollCrit() when cfg.RollCrit; crit multiplies by attackerBody.critMultiplier inside TakeDamage (HealthComponent.cs:1362)
    attacker        = playerBody.gameObject,         // kill credit, gold, XP, aggro, on-kill items
    inflictor       = playerBody.gameObject,
    position        = hitPosFromGuest,               // stable frame == world
    force           = (hitPos - playerBody.corePosition).normalized * cfg.Knockback,
    procChainMask   = default,
    procCoefficient = cfg.ProcCoefficient,           // see 9.3; default 0.5
    damageColorIndex= DamageColorIndex.Default,      // CRITICAL flag -> .WeakPoint / crit
    damageType      = new DamageTypeCombo(DamageType.Generic, DamageTypeExtended.Generic, DamageSource.Primary), // R/DamageTypeCombo.cs:66; skill-sourced so SkillMask items/buffs (e.g. SureProc) see it
    inflictedHurtbox= target.mainHurtBox,            // or the hurtbox whose bounds contain hitPos (weak points: HurtBox.damageModifier, R/HurtBox.cs:49)
};
HealthComponent hc = target.healthComponent;
bool proceed = FriendlyFireManager.ShouldDirectHitProceed(hc, playerBody.teamComponent.teamIndex);   // R/FriendlyFireManager.cs:26
if (proceed) {
    hc.TakeDamage(dmg);                                                    // R/HealthComponent.cs:1005 (server only: `NetworkServer.active`)
    GlobalEventManager.instance.OnHitEnemy(dmg, hc.gameObject);            // R/GlobalEventManager.cs:225 (on-hit items, procChain)
}
GlobalEventManager.instance.OnHitAll(dmg, hc.gameObject);                  // :1898 (items that proc on any hit)
```

Guest flags: `NOT_BY_PLAYER (1<<2)` -> `attacker = null` (no credit/aggro; `OnHitEnemy` then runs `HandleDamageWithNoAttacker`, `R/GlobalEventManager.cs:238`) ; `CRITICAL (1<<0)` -> `crit = true` + `DamageColorIndex.WeakPoint` when `cfg.CritFromFlag`; `OUTWARD` ignored (like ER); `WORLD_RAY`/`id == 0` (strike the world): RoR2 has no breakable-by-poke props, ignore (log once). Drop everything else exactly like `HostSim.ServiceDamage` (id not published last tick, not hostile/alive, amount out of range). Dead target: `!hc.alive` -> skip.

### 9.3 Items and procs (very desirable)

Calling `OnHitEnemy` + `OnHitAll` after `TakeDamage` is precisely what every RoR2 hitscan/overlap/projectile does (`BulletAttack.cs:357-360`, `:732-736`), so: on-hit items (Ukulele chain lightning, Behemoth, Tri-Tip, Runald's/Kjaro's, Sticky Bomb, Gasoline on kill, ...), on-kill items (Infusion, Harvester, Soldier's Syringe is attack-speed only), money and experience on kill, "hit counter" items all work with `attacker = player body`. Caveat: ULTRAKILL has high rates of fire and multi-hit shotgun pellets, so with `procCoefficient = 1` per guest hit the proc rate would be far above any RoR2 skill; expose `ProcCoefficient` (default 0.5, scaled per hit; also optional `ProcOncePerFrame`/rate limit) and keep proc chains (`ProcChainMask`) default so RoR2's chain-prevention (e.g. Ukulele lightning cannot re-proc) still applies to what the items spawn.

### 9.4 What the guest cannot do

RoR2 monsters are not stunned by the guest hit unless `damageInfo.force`/`DamageType.Stun1s` is used; add `DamageType.Stun1s` only on heavy hits if desired (config). Elite affix auras, armor plate (`itemCounts.armorPlate`, `:1388`: -5 flat per stack, min 1) and similar defences are honoured automatically because the hit goes through `TakeDamage`.

---

## 10. (h) Action / prompt (interact)

### 10.1 RoR2 mechanism

The local player's `InteractionDriver` (`R/InteractionDriver.cs`, component on the body, requires `InputBankTest` and `Interactor`) runs `MyFixedUpdate` each `FixedUpdate`: finds `currentInteractable = FindBestInteractableObject()` (`:140`) from `inputBank.aimOrigin/aimDirection` (cast `Interactor.FindBestInteractableObject(ray, maxRaycastDistance, overlapPosition, overlapRadius)`, `R/Interactor.cs:18`, mask `LayerIndex.CommonMasks.interactable`, distance `interactor.maxInteractionDistance`), and if `inputBank.interact` was pressed calls `interactor.AttemptInteraction(currentInteractable)` (`:149`; server -> `PerformInteraction`: for every `IInteractable` on the object, if `GetInteractability == Available` -> `OnInteractionBegin(interactor)` + `GlobalEventManager.OnInteractionBegin`; the result RPC tells whether anything succeeded, `R/Interactor.cs:~100-128`). Interactability enum: `Disabled | ConditionsNotMet | Available` (`R/Interactability.cs`).

### 10.2 Host implementation

Because 6.3 feeds `inputBank.aimDirection` from the guest camera, the stock `InteractionDriver` already tracks "what the guest crosshair is on" (and draws RoR2's outline highlight for it, which is visible since RoR2's world is the background). Host code:

1. **Prompt (publish every `Update` while ALIVE and stood in, else `""`)**: `go = driver.currentInteractable`; `i = go?.GetComponent<IInteractable>()` (multiple allowed; take the first with `GetInteractability != Disabled`); `text = i.GetContextString(driver.interactor)` (`R/IInteractable.cs`; e.g. `PurchaseInteraction` builds `"Open Chest <nobr>(<style=cShrine>$25</style>)</nobr>"`, `R/PurchaseInteraction.cs:245-257`; `GenericInteraction` returns the localised `contextToken`, `R/GenericInteraction.cs:85`). Clean up: strip `<...>` rich-text tags with a regex, replace `&nbsp;`, trim; `HostLink.SetPrompt()` already limits to 63 UTF-8 bytes and does the odd/even `hostPromptSeq` dance. `ConditionsNotMet` (not enough gold) -> still publish the text (ER's "grayed" -> result 0 on press).
2. **Action**: `HostLink.PollActionRequest(out req)` (first call latches); then:

```csharp
GameObject go = driver.currentInteractable;
int result = 0;
if (go) {
    var i = go.GetComponent<IInteractable>();
    var st = i?.GetInteractability(driver.interactor);
    if (st == Interactability.Available) { driver.interactor.AttemptInteraction(go); result = 1; }
}
Link.AckAction(req, result);     // writes hostActionResult then hostActionAck
```

   Result codes: 1 done, 0 nothing/unavailable/unaffordable, -1 unsupported (never), -2 not used. After result 1 the guest resamples terrain around the player (doors/lifts); in RoR2 interactions rarely change collision except teleporter/portals (handled as stage change) and the Bazaar; acceptable.
3. Interaction reach is `interactor.maxInteractionDistance` (prefab value, ~3 m [UNVERIFIED: prefab field not read]) from the body's aim origin (chest height), not from V1's eye; for pickup/barrel ergonomics add `cfg.ExtraReach` through `InteractionDriver.interactableOverride` (public field, `R/InteractionDriver.cs:39`): if the stock search finds nothing, run `interactor.FindBestInteractableObject(guestRay, 5f, eye, 2f)` yourself and assign the override; clear it after the press.
4. Chests/shrines/teleporter all go through the same path; the teleporter charge needs the stand-in inside the holdout zone, which it is (the stand-in position == V1's position).
5. Pings/equipment/skills/sprint are not mapped.

---

## 11. (i) F8 and handing control back

### 11.1 RoR2 -> guest (host F8, `mcSwitchReq++`)

- Detect with Win32, not Unity input, so it works regardless of Rewired (RoR2 uses legacy input handler 0 plus Rewired; Rewired ignores input when the app is unfocused by default): `GetAsyncKeyState(VK_F8) & 0x8000` edge-detect each `Update`; act only when `GetForegroundWindow() == RoR2 hwnd` (ER rule, contract 4.3). No `F8` binding exists in RoR2's code (`grep` of the decompile for key-code use found none relevant) [UNVERIFIED for Rewired's default keymap; verify no Rewired action uses F8].
- Then `Link.BumpSwitchRequest()`. The guest takes control again; the stand-in/camera are re-driven once the guest recalls (it teleports V1 to `state.playerPos` and waits 400 ms) and writes control flags again. While RoR2 has control (control inactive) `state.playerPos` = `body.footPosition`, `playerQuat` yaw from `inputBank.aimDirection` (where the player is looking; contract 4.2 step 4).

### 11.2 Guest -> RoR2 (`hostFocusReq`)

`HostLink.PollFocusRequest()`: on change bring RoR2's window to the front: `ShowWindow(hwnd, SW_RESTORE)` + `SetForegroundWindow(hwnd)`. The foreground lock may block a background process; workarounds in order: `AttachThreadInput` with the foreground thread, a synthetic `keybd_event(VK_MENU)` tap before `SetForegroundWindow`, or have the guest call `AllowSetForegroundWindow(hostPid)` (guest-side change, avoid). Latch `_f8Down = current key state` so the F8 that triggered the switch does not count as a return press (ER does the same, `game.cpp:2249-2262`). The guest writes `flags = 0` before bumping `hostFocusReq`, so our release path (5.5) is already running.

### 11.3 Normal control and camera back

Release path 5.5 runs when `flags` lacks `OVERRIDE_CAMERA/MOVE_HUNTER`, or the control seq stalls > 1000 ms (`HostLink.UpdateControl()` -> inactive): override removed (lerp 0.35 s), stand-in restored, input flows again as soon as RoR2 is focused (`CameraRigController.isControlAllowed` is true again). Compositing stops when `COMPOSITE` clears (12.5).

---

## 12. (j) Compositing guest frames inside RoR2

### 12.1 Data

`HostFrames` (`src/UltraRing.Link/HostLink.cs:529`) already does: lazy open of `frames.shm` every 2 s, magic/version/size check, pose history (`NoteAppliedPose`, `PoseForPresent(lag)`), `PickSlot(poseId)`, `ReadSlotHeader`, `CopySlot(slot, out hdr, world, depth, gui, hand)` with seq re-validation. Layers per slot: world BGRA (premultiplied, alpha 0 background), float depth (OpenGL window depth), GUI BGRA, optional hand BGRA; all bottom-up. **Unity `Texture2D` memory is also bottom-up (row 0 = bottom), so no flip is needed.**

### 12.2 Pipeline choice

RoR2 renders: `Scene Camera` (depth 0) -> `UI Camera, Worldspace` (1) -> `UI Camera` (2) -> `ScreenSpaceOverlay` canvases. Options:

| Option | Verdict |
| --- | --- |
| **ScreenSpaceOverlay Canvas + `RawImage`s** | **Chosen.** No camera hooks, drawn after every camera (including RoR2's HUD cameras), trivial; no depth. A canvas with `sortingOrder = short.MaxValue - 1` sits above RoR2's own overlays (pause menu would be covered, but HOST_BUSY hides the overlay while paused) |
| `CommandBuffer` at `CameraEvent.AfterImageEffects` on `sceneCam` + `Graphics.Blit` | Needed only for depth-aware compositing before the UI cameras (and in-world HUD ordering); later milestone |
| `OnRenderImage`/`Graphics.Blit` | Conflicts with Post-Processing v2 `PostProcessLayer` ordering; not needed |
| IMGUI `GUI.DrawTexture` | Straight alpha only, extra cost; no |

### 12.3 Premultiplied alpha

The data is premultiplied (`out = src + dst*(1-src.a)`). Built-in UI shaders are straight alpha. Runtime shader compilation is not possible in a player build, so a shader must already exist in the game or be supplied by an AssetBundle.

- **Best (verified present): `Hopoo Games/UI/Custom Blend`**, source `Assets/RoR2/Base/Shaders/UI/HGUICustomBlend.shader` (catalog internal id 12363). Properties found by parsing the shader: `_SrcBlend (Int)`, `_DstBlend (Int)`, `_InternalSimpleBlendMode (Int)`, `_MainTex`, `_Color`, `_Stencil*`, `_ColorMask`, `_UseUIAlphaClip` (UI-compatible, so it works on a `RawImage`). Material setup:

```csharp
Shader sh = Resources.FindObjectsOfTypeAll<Shader>().FirstOrDefault(s => s.name == "Hopoo Games/UI/Custom Blend");
if (!sh) sh = Addressables.LoadAssetAsync<Shader>("Assets/RoR2/Base/Shaders/UI/HGUICustomBlend.shader").WaitForCompletion(); // Unity.Addressables.dll, same call style RoR2 uses
var mat = new Material(sh);
mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);             // 1
mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);// 10
```
  Nothing in the managed code references this shader (grep of the decompile), so it is not guaranteed to be loaded; hence the explicit `Addressables` load (blocking is fine once, at overlay creation). Whether the blend is applied exactly as expected is **[UNVERIFIED]** (needs one visual test with a half-transparent test frame; `docs/` FakeHost can generate one).
- **Fallbacks**: `Legacy Shaders/Particles/Alpha Blended Premultiply` (`_MainTex`, `_InvFade`; shipped in `calmwater_unitybuiltinshaders_*.bundle`, loaded only if that bundle is) as a non-UI quad; or **CPU un-premultiply** (`rgb = rgb*255/a` for `a > 0`) on a worker thread then `UI/Default` (straight alpha, built-in and in the same bundle). A 1080p pass is ~2M pixels, a few ms on one core; do it during the shm->staging copy.
- `Hidden/BlitCopy` and `Hidden/Internal-GUITexture*` strings exist in `UnityPlayer.dll` but the shaders are not in any asset list we could read [UNVERIFIED availability at runtime]; do not depend on them.
- **Colour space**: RoR2 is **Linear** (`m_ActiveColorSpace = 1`). Create the textures as sRGB-flagged (`new Texture2D(w, h, TextureFormat.BGRA32, false, linear: false)`): sampling decodes, the sRGB back buffer re-encodes, so opaque guest pixels round-trip bit-exactly; only soft (partially transparent) edges blend in linear instead of gamma space (small visual difference) [UNVERIFIED visually].

### 12.4 Upload and per-frame flow

Per `Update` (or `WaitForEndOfFrame` of the previous frame; any point before the canvas renders):

```csharp
if (!ctrlCompositeFlag) { overlay.SetActive(false); frames.LastUploaded = max(frames.LastUploaded, frames.LatestFrameId); return; }
frames.TryOpen();                                              // 2 s retry, requires the guest to have created frames.shm
ulong pose = frames.PoseForPresent(control.poseLag);           // lag from control (guest default 1)
int slot   = frames.PickSlot(pose);
if (slot >= 0 && frames.ReadSlotHeader(slot, out var hdr) && hdr.frameId > frames.LastUploaded)
{
    EnsureTextures(hdr.width, hdr.height);                      // recreate only on size change
    // copy straight into the textures' native buffers (no managed intermediate):
    var wBuf = texWorld.GetRawTextureData<byte>(); var gBuf = texGui.GetRawTextureData<byte>();
    if (frames.CopySlot(slot, out hdr, Span(wBuf), default /*depth: skipped*/, Span(gBuf), Span(hBuf)))
    { texWorld.Apply(false); texGui.Apply(false); /* hand if flag */ frames.LastUploaded = hdr.frameId; }
}
overlay.SetActive(true);                                       // otherwise keep drawing the previous textures (compositor.cpp:990)
```

(`Texture2D.GetRawTextureData<byte>()` returns a NativeArray view of the CPU copy; wrap its pointer in a `Span<byte>` with `Unsafe`/`NativeArrayUnsafeUtility.GetUnsafePtr` and `new Span<byte>(ptr, len)`; `LoadRawTextureData(IntPtr, int)` also exists in 2021.3.) Skip the depth layer (pass an empty span: `CopySlot` skips it) until the depth milestone.

Cost at 1920x1080: 3 layers x 8.3 MB = 25 MB of memcpy (~3-5 ms) + 3 `Apply` uploads (~2-4 ms) per fresh frame; at 60 fps that is ~1.5 GB/s. Mitigations: skip the hand layer when `FrameHand` is clear; skip a layer when the guest signals it is empty (the guest can leave GUI alpha 0 but the bytes still copy); move `CopySlot` to a worker thread double-buffering (the frame is consistent after seq validation); reduce to one combined layer by pre-compositing GUI over world on the worker (premultiplied "over" is `gui + world*(1-gui.a)`), halving upload.

Pose bookkeeping: call `frames.NoteAppliedPose(control.mcFrame)` from `GuestCamera.GetCameraState` (6.4). The slot shown is the one whose `poseId` equals the pose applied `lag` frames ago, else the newest older one (contract 3.5).

Canvas objects (created once, `DontDestroyOnLoad`):

```csharp
var go = new GameObject("UltraRingOverlay"); Object.DontDestroyOnLoad(go);
var cv = go.AddComponent<Canvas>(); cv.renderMode = RenderMode.ScreenSpaceOverlay; cv.sortingOrder = short.MaxValue - 1;
// 3 children: RawImage (world), RawImage (hand), RawImage (gui); anchors stretch 0..1; raycastTarget = false; material = mat; no GraphicRaycaster, no CanvasScaler
```

### 12.5 Start/stop semantics

Draw only while `control.flags & COMPOSITE` and the overlay's frames are valid. Like the ER compositor, do **not** apply the 1000 ms control timeout to COMPOSITE (a dead guest leaves its last frame): better to deviate here and also hide the overlay when the guest heartbeat (`GuestAlive`, 2 s) is lost, otherwise a crashed guest leaves a frozen image over RoR2. Also hide while `HOST_BUSY`/paused. `state.COMPOSITING` = overlay drawn within the last 500 ms.

Guest sizing: the guest renders at `winW x winH` (state). If RoR2's resolution changes the guest recreates its targets; the overlay recreates textures when `hdr.width/height` change.

### 12.6 What is lost without depth (later milestone)

ER's compositor depth-tests guest world pixels against ER's depth and relights/fogs them. Our first overlay draws guest world pixels over everything: V1 projectiles/particles/weapons are visible through RoR2 walls and are never lit/fogged by RoR2. Needed later:

- Guest depth is OpenGL-style [0,1] with `mcNear/mcFar` in the slot header (contract 9.5) linearised in host metres; RoR2's depth is `_CameraDepthTexture` (reversed-Z on D3D11, `LinearEyeDepth`). Compare `eyeDist > hostDist + 0.03 + 0.004*eyeDist` -> discard.
- Option A (clean): a custom shader that samples the guest depth texture (R32F) and `_CameraDepthTexture`, drawn from a `CommandBuffer` on `sceneCam` at `CameraEvent.AfterForwardAlpha`/before UI cameras. Needs the shader compiled into an AssetBundle with **Unity Editor 2021.3.33f1** (not available here); RoR2 does render a depth texture for its Sobel outline/AO (`Hopoo Games/Internal/SobelBuffer`, SSAO convar `pp_ao`), so `_CameraDepthTexture` should exist [UNVERIFIED].
- Option B (CPU): `AsyncGPUReadback` of a 1/4-res copy of RoR2's depth (2-3 frame latency), worker thread zeroes alpha of occluded guest pixels before upload. Cheaper to build, latency artefacts.
- Option C: only occlude by raycasting sparse points (coarse, no).

---

## 13. (k) Input focus, background running, cursor

- The guest window sits over RoR2's with ~1/255 opacity and holds the keyboard focus; RoR2 is unfocused but must keep simulating.
- **RoR2 keeps running unfocused**: `PlayerSettings.runInBackground = True` [verified] and `NetworkManagerSystem.Init` also assigns `runInBackground = configurationComponent.RunInBackground` (`$D/RoR2/RoR2.Networking/NetworkManagerSystem.cs:650`). Set `Application.runInBackground = true` once in `Awake` as insurance. No code pauses on focus loss: `grep OnApplicationFocus|OnApplicationPause|Application.isFocused` finds only the audio listener (`R/AudioManager.cs:69, 108`). Single-player pause happens only through the pause screen (`PauseManager`/`PauseStopController`, `Time.timeScale = 0`).
- **Audio**: `audio_focused_only` (`R/AudioManager.cs:75`, Archive) mutes Wwise when unfocused if the user enabled it; the plugin should run `audio_focused_only 0` via `RoR2.Console.instance.SubmitCmd(null, "audio_focused_only 0")` once at startup (or document it).
- **Cursor**: `MPEventSystemManager.Update` (`R/MPEventSystemManager.cs:~90`) and `RoR2Application.UpdateCursorState` (`R/RoR2Application.cs:340-351`) lock the cursor (`CursorLockMode.Locked`) and hide it when no UI has a cursor, re-applying only when the mode value changes. Unity releases the OS clip when the window is not focused, so RoR2 does not steal the mouse from the guest. When RoR2 regains focus (F8 return) Unity re-locks by itself. No plugin action needed; if a stray lock appears, call `Cursor.lockState = CursorLockMode.None` while the guest controls.
- **Input**: RoR2 gets no OS key/mouse events while unfocused (Rewired also ignores unfocused input by default); plus `IsUserControlAllowed = false` (6.1) removes it at the source, so a gamepad still connected to RoR2 cannot move the stand-in.
- **Fixed update / frame rate**: RoR2 `fps_max 60` (`R/SettingsConVars.cs:136`, archived), `vsync_count`. Heartbeat is per rendered frame; nothing to change.

---

## 14. (l) Multiplayer

- **Supported: single player and "host of a lobby with the plugin only on the host"** (server == `NetworkServer.active`; the local player has authority, `TakeDamage` is server-only: `R/HealthComponent.cs:1005-1015` warns and returns on clients).
- **Not supported: running as a pure client.** `HealthComponent.TakeDamage` would need the `MsgType 53` damage message (`R/BulletAttack.cs:362-376`), items/gold run on the server, `HurtBox`/`TeamComponent` data is still readable but `Networkhealth` restores, `GlobalEventManager` server events, `Stage` server hooks and `MapZone` are server-side. Guard: `if (!NetworkServer.active) life = NONE`.
- **Side effects when hosting with others**: position pinning is sent as normal authority motion (the owner is the server, `Util.HasEffectiveAuthority`), remote players see a body teleporting around; teleports are local-only calls (no `TeleportMessage`), so the remote view of the body may interpolate strangely. Other players' bodies appear in the entity table as kind 3 (`Player` team). Splitscreen/local multiplayer: only `LocalUserManager.GetFirstLocalUser()` is driven; the camera rig of user 0 uses a sub-viewport in splitscreen (`RunCameraManager.ScreenLayouts`, `R/RunCameraManager.cs`), which breaks the full-window aspect assumption; unsupported.
- Death in MP: `Kill` policy kills only the local body; the others continue.

---

## 15. (m) Plugin skeleton, build, launch

### 15.1 Project layout (new)

```
src/UltraRing.Ror2/
  UltraRing.Ror2.csproj
  Plugin.cs                  BepInPlugin, config, Awake/Update/FixedUpdate/OnDestroy
  Ror2Host.cs                the HostSim analogue: per-frame Tick(), owns HostLink/HostFrames, publishes state
  Life.cs                    state machine (4)
  StandIn.cs                 pin/hide/fake-actor/restore + damage neutraliser (5)
  GuestCamera.cs             ICameraStateProvider (6)
  RayService.cs              Physics.Raycast delegate (7)
  EntityPublisher.cs         (8)
  DamageService.cs           guest->RoR2 damage (9)
  ActionService.cs           prompt + action (10)
  Win32Host.cs               window rect, F8, focus (links ../UltraRing.Ultrakill/Platform/{NativeMethods,WindowFinder}.cs)
  Overlay.cs                 Canvas/RawImage compositor (12)
```

### 15.2 csproj

Mirror `src/UltraRing.Ultrakill/UltraRing.Ultrakill.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <LangVersion>9.0</LangVersion>
    <Nullable>disable</Nullable>
    <AssemblyName>UltraRing.Ror2</AssemblyName>
    <Ror2Dir Condition="'$(Ror2Dir)' == ''">C:\Program Files (x86)\Steam\steamapps\common\Risk of Rain 2</Ror2Dir>
    <Ror2Managed>$(Ror2Dir)\Risk of Rain 2_Data\Managed</Ror2Managed>
    <BepInExCore Condition="'$(BepInExCore)' == ''">$(MSBuildThisFileDirectory)..\..\.tools\bepinex5-x64\BepInEx\core</BepInExCore>
    <NoWarn>$(NoWarn);CS0436</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="BepInEx.AssemblyPublicizer.MSBuild" Version="0.4.2" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="$(BepInExCore)\BepInEx.dll"  Private="false" />
    <Reference Include="$(BepInExCore)\0Harmony.dll" Private="false" />
    <Reference Include="$(Ror2Managed)\RoR2.dll" Private="false" Publicize="true" />
    <Reference Include="$(Ror2Managed)\KinematicCharacterController.dll" Private="false" />
    <Reference Include="$(Ror2Managed)\UnityEngine*.dll" Private="false" />
    <Reference Include="$(Ror2Managed)\com.unity.multiplayer-hlapi.Runtime.dll" Private="false" />  <!-- UnityEngine.Networking: NetworkServer, NetworkIdentity -->
    <Reference Include="$(Ror2Managed)\Unity.Addressables.dll" Private="false" />
    <Reference Include="$(Ror2Managed)\Unity.ResourceManager.dll" Private="false" />
    <Reference Include="$(Ror2Managed)\Unity.Collections.dll" Private="false" />
    <Reference Include="$(Ror2Managed)\Rewired_Core.dll" Private="false" />       <!-- types in LocalUser/NetworkUser signatures -->
    <Reference Include="$(Ror2Managed)\HGCSharpUtils.dll" Private="false" />
    <Reference Include="$(Ror2Managed)\Unity.TextMeshPro.dll" Private="false" />
    <Reference Include="$(Ror2Managed)\Unity.Postprocessing.Runtime.dll" Private="false" />
    <Reference Include="$(Ror2Managed)\Wwise*.dll;$(Ror2Managed)\AK.Wwise*.dll" Private="false" /> <!-- only if the compiler asks -->
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\UltraRing.Link\UltraRing.Link.csproj" />
    <Compile Include="..\UltraRing.Ultrakill\Platform\NativeMethods.cs" Link="Platform\NativeMethods.cs" />
    <Compile Include="..\UltraRing.Ultrakill\Platform\WindowFinder.cs" Link="Platform\WindowFinder.cs" />
  </ItemGroup>
</Project>
```

(BepInEx core from the repo's own `.tools/bepinex5-x64` (5.4.23.5) or from the r2modman profile's `BepInEx/core` (5.4.21); both expose the same API. `Publicize` is used for the few private members (`Stage.stage1PodConVar`, `CameraRigController.cameraModeContext`); everything else above is public. Add `UltraRing.Ror2` to `UltraRing.sln` and have `tools/Build.ps1` stage it like the ULTRAKILL plugin: `runtime\ror2-bepinex\BepInEx\{core,plugins\UltraRing\}`.)

Plugin shell:

```csharp
[BepInPlugin("dev.ultraring.ror2host", "UltraRing RoR2 Host", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    Ror2Host _host;
    void Awake() {
        Application.runInBackground = true;
        var cfg = new HostConfig(Config);                       // GuestDeathPolicy, SkipPod, ProcCoefficient, HideHud, ...
        _host = new Ror2Host(cfg, Logger);                      // opens bridge.shm (HostLink.Open), zeroes passages/platforms counts
        RoR2Application.onLoad += _host.OnGameLoaded;           // R/RoR2Application.cs:115: invoked once at :594-597, then nulled. BepInEx loads plugins before RoR2Application finishes loading, so Awake is early enough; subscribe to GlobalEventManager/Stage/MapZone events inside OnGameLoaded
    }
    void Update()      => _host.Update();                       // control, life, camera install, F8, rays, damage, entities, action, overlay, state publish
    void FixedUpdate() => _host.FixedUpdate();                  // pin, input zeroing
    void OnDestroy()   => _host.Shutdown();                     // release control, write coreStatus 0 (HostLink.Dispose)
}
```

Shutdown/crash: on `Application.quitting` and `AppDomain.ProcessExit` release the override and let `HostLink.Dispose()` clear `coreStatus`. (There is no `flags` to clear on the host side; the guest handles its own `COMPOSITE` flag, and our overlay disappears with the process.)

Directory resolution: `HostLink.Open()` uses `BridgePaths` (`ERMC_DIR`, else `%TEMP%\ermc`) like the guest.

### 15.3 Launching with the staged BepInEx

Do not touch the r2modman profile (it has ~80 plugins and its own Doorstop target). Stage `runtime\ror2-bepinex` and start the exe directly with the Doorstop overrides, same as `Launch.ps1` does for ULTRAKILL:

```powershell
# Launch.ps1 additions (-RoR2 switch replaces -FakeHost/Elden Ring host)
$Ror2Dir = $Install.ror2.game_dir      # C:\Program Files (x86)\Steam\steamapps\common\Risk of Rain 2
$Ror2Exe = Join-Path $Ror2Dir 'Risk of Rain 2.exe'
$Stage   = Join-Path $RuntimeDir 'ror2-bepinex'
$Pre     = Join-Path $Stage 'BepInEx\core\BepInEx.Preloader.dll'
$env:ERMC_DIR = $RuntimeDir                 # inherited by the game process (needs a direct launch, see below)
$Args = '--doorstop-enabled true --doorstop-target-assembly "' + $Pre + '" -screen-fullscreen 0 -popupwindow -screen-width 1920 -screen-height 1080'
$P = Start-Process -FilePath $Ror2Exe -ArgumentList $Args -WorkingDirectory $Ror2Dir -PassThru
# then wait for bridge.shm header: hostPid == $P.Id and coreStatus == 1 (the plugin sets both in Awake), exactly like the Elden Ring wait loop
```

Notes:

- The game folder already contains the r2modman `winhttp.dll` + `doorstop_config.ini` (`enabled=true`, relative target that does not exist); the command-line switches override the target. `Install.ps1` must **not** overwrite them (backup/restore semantics as for ULTRAKILL are unnecessary; leave as is). A plain Steam launch without the arguments would attempt `BepInEx/core/BepInEx.Preloader.dll` relative to the game folder (absent) and run vanilla, as it does today.
- Steam must be running (RoR2 initialises Facepunch.Steamworks). Direct `Risk of Rain 2.exe` launch without `steam_appid.txt` is **[UNVERIFIED]**; fallbacks: (1) create `steam_appid.txt` containing `632360` in the game folder (a game-folder write: needs explicit user consent), or (2) launch through Steam: `steam.exe -applaunch 632360 <same args>` - then the environment variable does not reach the game, so also let the plugin read the bridge directory from its own config entry `BridgeDir` (default empty = `ERMC_DIR`/temp fallback) and have the launcher write that file in the staged config before starting.
- The staged BepInEx root is derived from the Preloader path, so logs/config land in `runtime\ror2-bepinex\BepInEx\` (`LogOutput.log`), leaving the profile untouched. `BepInEx.cfg`: `[Chainloader] HideManagerGameObject = true` is safer for a long-lived plugin object; `[Logging.Console] Enabled = false`.
- r2modman route (if the user prefers it): install the built DLL into a *copy* of the profile and launch via r2modman; works because the plugin needs no R2API/MMHOOK, but 80 other plugins make debugging hard, so not recommended.
- Required RoR2 settings for the overlay: `window_mode Window` or borderless `Fullscreen`, and the same resolution as the guest's target (`resolution 1920x1080x60`).

---

## 16. Implementation plan

Order follows the requested list; compositing could be pulled forward (after M2) because it is the fastest way to see correctness.

**M0 - Scaffold (0.5 d).** `UltraRing.Ror2` project, plugin loads in a staged BepInEx, logs the RoR2 version/Unity version, `Launch.ps1 -RoR2` starts the game and waits for the bridge header. Exit: `runtime\ror2-bepinex\BepInEx\LogOutput.log` shows the plugin, `bridge.shm` has `hostPid` of RoR2 and `coreStatus == 1`.

**M1 - Connect + state (1 d).** `HostLink.Open`, heartbeat per frame, window rect/focus, life machine (4) with all signals, `stageId`, `hostLife`, `PLAYER_VALID/CAMERA_VALID/HOST_BUSY` flags, camera fields from the rig, passages/platforms counts zeroed. No control applied. Exit: the real ULTRAKILL guest attaches, sees PLAYER_VALID after the pod/landing, recalls V1 to the RoR2 spawn, re-anchors on a stage change (`stageId` changes); FakeHost-style status tool prints sane values.

**M2 - Stand-in + camera (1-2 d).** `GuestCamera` provider, override install/release, `StandIn` pin/hide/fake-actor/gravity/collision, input lock, `hostFocusReq` handling, control timeout release, state `CAM_OVERRIDDEN`, `NoteAppliedPose`. Exit: V1's camera drives the RoR2 view (RoR2 window shows the world from V1's eyes), stand-in follows `hunterPos`, no fall damage, mobs target it, releasing control returns a normal RoR2 camera without jump. Test with `tools/UltraRing.FakeHost` unchanged guest and a debug overlay.

**M3 - Rays (0.5-1 d).** `RayService` with `LayerIndex.world`, time budget, `SyncTransforms`, stats overlay. Exit: V1 walks and jumps on RoR2 terrain (guest builds colliders from hits); measure rays/frame; switch to `RaycastCommand` batching only if needed.

**M4 - Entities + damage (1-2 d).** `EntityPublisher` (8), `DamageService` (9) with conversion, armor compensation, `OnHitEnemy/OnHitAll`, hurtbox selection, flags. Exit: guest proxies appear at RoR2 monsters' positions; shots hurt them; kills give gold/XP; Ukulele-style items proc; the id/maxHp bookkeeping matches (guest fraction == RoR2 fraction).

**M5 - Hunter damage + life (1-2 d).** `onServerDamageDealt` neutraliser + `ReportHunterHit`, overkill test (one-shot a stand-in with a huge hit via console `kill`-style tests), `hostDeaths` via `onCharacterDeathGlobal`, `mcDeaths` policies (SoftRespawn/Kill), DEAD/SETTLING/ALIVE transitions, stage transitions via the teleporter, game over. Exit: RoR2 monsters damage V1 through the guest's HP bar (share semantic), V1 death -> soft respawn at the stage spawn with `hostLife++`; teleporting to the next stage re-anchors the guest.

**M6 - Compositing overlay (1-2 d).** Canvas/RawImage, shader load (`Custom Blend`), `HostFrames` flow with pose lag, async/threaded copy if the frame budget demands, `COMPOSITING` flag, hide on pause/guest loss. Exit: V1 viewmodel and GUI appear over RoR2 at 60 fps at 1080p; premultiplied edges look right; resolution change handled.

**M7 - Actions (0.5-1 d).** Prompt text, `AttemptInteraction`, result codes, optional extra reach. Exit: guest shows "Open Chest" style prompts and can open chests/shrines/teleporter with its action key.

**M8 - F8 + polish (1 d).** Both F8 directions, focus request, `audio_focused_only`, `stage1_pod` skip, HUD hiding option, screen shake neutralisation, Win32/DPI checks, config file, README section, `Launch.ps1`/`Install.ps1`/`Build.ps1` integration, MP guards. Exit: full loop playable for a whole stage.

**Later (not in scope now):** depth-aware compositing and RoR2 fog/relight (12.6); real normals; moving-platform support; per-hurtbox oriented hit boxes; guest `OFF_ENVIRONMENT` mapping to RoR2 time-of-day (none exists); GPU shared-texture path (D3D11 in RoR2 vs host contract's D3D12 is irrelevant here, the overlay is memory-path only).

---

## 17. Risk list

| # | Risk | Impact | Mitigation / test |
| --- | --- | --- | --- |
| 1 | Overkill/execute damage kills the stand-in before the `onServerDamageDealt` restore runs, or receivers earlier in `TakeDamageProcess` (`IOnTakeDamageServerReceiver`, `onIncomingDamageReceivers`, item behaviours) react to health <= 0 | Real RoR2 death, run ends | M5 overkill test with 1e6 damage and `VoidDeath`/execute flags; fallback mode B (`godMode` + prefix estimate, 5.4); `Buddha` body flag (`R/HealthComponent.cs:1798`) as a last-ditch (sets HP 1) |
| 2 | Another system takes `SetOverrideCam` (pod, teleporter cutscene, bazaar, mods) or removes ours when the rig is recreated | Camera fight, wrong view | Install only when `!rig.hasOverride`; re-check every frame; treat foreign override as SETTLING/HOST_BUSY (4.4) |
| 3 | `Hopoo Games/UI/Custom Blend` not loadable or blend not as assumed | Wrong edges/dark halos | Addressables load by key; fallback CPU un-premultiply + `UI/Default` (12.3); visual test with a half-transparent frame |
| 4 | Texture upload cost (25 MB/frame at 1080p) drops RoR2 frame rate | Stutter | Skip unused layers, worker-thread copy, pre-composite GUI+world, upload only fresh frames, lower guest resolution; measure in M6 |
| 5 | Pinned body vs KCC: depenetration, interpolation jitter, `Physics.autoSyncTransforms = false` leaves colliders one step stale | Stand-in jitter, missed enemy hits | Pin in `FixedUpdate` with `SetPosition(.., true)`, collisions off (5.1), `Physics.SyncTransforms()` after pinning; test enemy melee/projectile hits against a moving stand-in |
| 6 | Window rect / DPI mismatch between processes; RoR2 re-applies saved resolution/fullscreen | Overlay misaligned or stretched | Compare `Screen.width/height` to the Win32 client rect at start, log; set `window_mode`/`resolution` convars; windowed or borderless only |
| 7 | Direct launch of `Risk of Rain 2.exe` without Steam context (`steam_appid.txt`) | Game fails to start under our launcher | Test early (M0); fallbacks in 15.3 (steam_appid.txt with consent, `steam.exe -applaunch` + config-based `BridgeDir`) |
| 8 | Guest scale vs RoR2 sizes (monster hitbox AABBs for bosses are huge, Commando 1.82 m vs V1) | Hit tests feel off, V1 clips through RoR2 props | Scale is the guest's choice (0.5 m/u suggested); clamp AABB half-extents; tune per-body in the entity publisher; consider capsule-shaped test instead of AABB later |
| 9 | Proc balance: ULTRAKILL fire rates vs RoR2 proc chains; crit semantics | Item procs absurdly strong/weak | `ProcCoefficient`, rate limit, `RollCrit` toggles in config; start at 0.5 |
| 10 | Rewired/F8: F8 might be bound in RoR2's default Rewired map, or Rewired still reads a gamepad while unfocused | Accidental actions | Check the active Rewired maps; input is locked at the camera provider anyway |
| 11 | Death/game-over semantics: `Kill` ends the whole RoR2 run; `SoftRespawn` bypasses RoR2 rules | Surprising run loss / cheaty | Config, default SoftRespawn; document |
| 12 | RoR2 updates change APIs (this decompile is the 2026-02-24 build with DLC3) | Plugin breaks on update | Only public API used; version gate logging on `Application.version`; pin Steam build when testing |
| 13 | Multiplayer client sessions | Unsupported | `NetworkServer.active` guard (14) |
| 14 | Linear colour space makes semi-transparent blending differ slightly from ER's gamma blending | Slightly different glows | Accept; optionally pre-gamma-correct alpha edges on the worker thread |
| 15 | No depth test: guest world pixels visible through RoR2 walls | Visual inconsistency | Known limitation until the depth milestone (12.6) |
| 16 | `Physics.Raycast` budget too low for large guest batches | Slow terrain streaming | `RaycastCommand.ScheduleBatch`, bigger budget when the player stands still |
| 17 | RoR2 HUD and guest GUI overlap; RoR2 shows Commando skill icons | Clutter | `GuestCamera.HideHud` config (`IsHudAllowed`, `R/CameraRigController.cs:210`) or a partial HUD later |
| 18 | `stage1_pod` convar is `Cheat`-flagged and `Stage.stage1PodConVar` is private | Pod cannot be skipped | `SetString` through `Console.FindConVar` / publicizer; else wait out the pod (SETTLING) |
| 19 | Stale shm regions (rays/damage/entities/passages) from earlier sessions | Phantom batches/damage | Follow contract 10.1 (ray header catch-up, damage ring continues from `write`, zero passage/platform counts) |
| 20 | `HostLink` is single-writer by design; two plugin instances or FakeHost + RoR2 at once | Corrupted state | Launcher guard (same pid/start-time check as `Launch.ps1`'s guest guard) |
