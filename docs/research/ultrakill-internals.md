# ULTRAKILL internals for the bridge mod (BepInEx 5 / Harmony guest)

Research notes, factual, with file/line citations into a decompile of `Assembly-CSharp.dll`.

* Game: ULTRAKILL, Unity **2022.3.29f1**, **Mono** (not IL2CPP), **Direct3D 11**, built-in render pipeline (Player.log).
* Install: `C:/Program Files (x86)/Steam/steamapps/common/ULTRAKILL` (nothing there was modified).
* Decompile root (`$D`): `C:/Users/arnau/AppData/Local/Temp/claude/D--homelab-bridge-mod/ecb58167-b098-41fe-8519-c9cf212289b9/scratchpad/uk-decomp`
  (ilspycmd 8.2.0.7535, `-p` project mode; 1214 top-level .cs files plus namespace folders). All paths below are relative to `$D`.
* Serialized values (collider sizes, camera culling masks, layer names, physics matrix, scene contents) were read from the game's asset files with UnityPy
  (scratchpad venv, read-only). These are authored/default values; scripts can change them at runtime. Such facts are marked **[asset]**.
* Confidence markers: **[verified]** = read in code/assets, **[inferred]** = deduced from code, **[UNCERTAIN]** = needs an in-game test.

---

## 0. TL;DR (decisions the bridge should be built around)

| Topic | Fact |
|---|---|
| Units | V1 standing capsule: height 3.5, radius 0.5, centre y+0.25. Feet are 1.5 below the Player pivot. Camera is 1.4 above pivot = **2.9 units above the feet**. Gravity -40 u/s^2. Walk 16.5 u/s. No official metre mapping; V1 = 3.5 u. Pick `scale` (m/unit) from the host character (0.5 m/u gives a 1.75 m V1, 8.25 m/s walk). |
| Pipeline | Built-in RP, Forward. Main Camera -> MRT low-res RT (`PostProcessV2_Handler.mainTex`) + shared depth RT; HUD Camera draws layer 13 (viewmodel + world-space HUD canvases) into the same RT; "Virtual Camera" draws a full-screen Quad with shader `ULTRAKILL/PostProcessV2` to the back buffer; a Screen-Space-Overlay Canvas (crosshair, alt HUD, hurt screen...) is drawn last, not by any camera. |
| Capture | Do not scrape the back buffer. Add our own cameras (world/effects, viewmodel, UI) with ARGB32 RTs cleared to alpha 0; switch the overlay Canvas to `ScreenSpaceCamera` on the UI capture camera. Details in section B.6. |
| Geometry layers | Colliders on layer **8 `Environment`** (also valid: 6, 7, 24). Non-trigger, no `Slippery` tag. Layer collision matrix confirms player (layer 2) collides with 6/7/8/24. |
| Wall jump perf | `WallCheck` calls `ColliderUtility.FindClosestPoint`, which is a brute-force loop over **all triangles** of a non-convex `MeshCollider`. Use many small mesh colliders (or primitives / convex) not one giant terrain mesh. |
| Enemy proxy | Custom invisible GameObject: layer 10 (non-blocking) or 11, tag `Body`/`Head`/`Limb`, `EnemyIdentifierIdentifier` on the collider, `EnemyIdentifier` on the root, **Harmony-prefix `EnemyIdentifier.DeliverDamage`** (it silently returns when no `Enemy` component exists). Register an `ITarget` in `TargetTracker` for coin auto-targeting. |
| Best scene | `uk_construct` (sandbox): `sandboxTools=true`, cheats auto-enabled, no rank/story, `levelNumber 0` (LevelStats auto-hidden). Player starts in a "pit-falling" locked state: call `PlayerActivator.Activate()`. |
| Focus | `runInBackground = False` in PlayerSettings: **the whole game freezes when the window loses focus**. Set `Application.runInBackground = true`. Real keyboard/mouse will not drive V1 while ER has focus: inject through virtual Input System devices (Input System 1.7.0). No `OnApplicationFocus`/pause-on-focus code exists in the game scripts. |

---

## 1. Managed folder / packages

`ULTRAKILL_Data/Managed` (verified listing):

* Game code: `Assembly-CSharp.dll` (single assembly), `NewBlood.*` (`LegacyInput`, `EngineInterop`, `DomainReloading`), `plog`, `plog.unity`, `pcon.core`, `TriInspector`, `UnityUIExtensions`, `Vertx.Debugging.Runtime`, `Naelstrof.JigglePhysics`, `MaskedOcclusionCulling`, `Newtonsoft.Json`, `Facepunch.Steamworks.Win64`.
* **Input System 1.7.0** (`Unity.InputSystem`, `Unity.InputSystem.ForUI`). `activeInputHandler = 2` (both legacy and new) [asset: globalgamemanagers PlayerSettings].
* **Addressables** (`Unity.Addressables`, `Unity.ResourceManager`, `Unity.ScriptableBuildPipeline`): all scenes except the bootstrap scene are Addressable bundles.
* `Unity.TextMeshPro`, `UnityEngine.UI`, `Unity.Localization`, `Unity.Mathematics`, `Unity.Burst`, `Unity.Collections`, `Unity.Animation.Rigging`, `Unity.AI.Navigation` + `NavMeshComponents`, `Unity.Timeline`, ProBuilder/Polybrush, Recorder, Fbx, Unity Services (analytics).
* **Not present**: URP/HDRP (`Unity.RenderPipelines.*`), Post-Processing v2 (`UnityEngine.Rendering.PostProcessing`), Cinemachine. `GraphicsSettings.m_CustomRenderPipeline` is null [asset] => **built-in render pipeline**. All "post processing" is ULTRAKILL's own (`PostProcessV2_Handler` + shader `ULTRAKILL/PostProcessV2`).
* Plugin references you will want: `Assembly-CSharp`, `UnityEngine.CoreModule/PhysicsModule/InputLegacyModule/UI/...`, `Unity.InputSystem`, `Unity.Addressables`, `Unity.TextMeshPro`, `Unity.Mathematics`. Code uses C# 9+ features in the decompile but that does not affect plugin targets (Mono, net4.x profile).
* Note `Bootstrap.Start` (`Bootstrap.cs:6`) sets `Debug.unityLogger.filterLogType = LogType.Error` in non-debug builds: the game's own `Debug.Log` output is suppressed (BepInEx logging is unaffected).

Build facts: the player build contains only scene `Assets/Scenes/Bootstrap.unity` (BuildSettings). Every other scene is loaded with `Addressables.LoadSceneAsync(name)` in `SceneHelper.LoadSceneCoroutine` (`SceneHelper.cs:666`).

Time/physics settings **[asset]**: fixed timestep **0.008 s (125 Hz)**, max timestep 0.1, `Physics.gravity = (0,-40,0)`, broadphase SAP (world bounds ignored), default solver iterations 6 (player rb multiplies by 5), `queriesHitTriggers = true`, `autoSyncTransforms = true`.

---

## 2. A. Player (`NewMovement`)

`NewMovement.cs`: `[ConfigureSingleton(SingletonFlags.NoAutoInstance)] public class NewMovement : MonoSingleton<NewMovement>, ITarget, IPortalTraveller`.
`MonoSingleton<NewMovement>.Instance` returns null when no player is loaded (no auto-create). Use `TryGetInstance(out var nm)` or the `InstanceChanged` event to detect "level ready".

### A.1 Components on the `Player` GameObject (uk_construct, layer 2) [asset]

`Transform, CapsuleCollider, Rigidbody, NewMovement, AudioSource, CheatsController, ClimbStep, PlayerActivatorRelay, KeepInBounds, PlayerMovementParenting, VerticalClippingBlocker, PortalAwarePlayerCollider, WallCheckGroup`.

* `CapsuleCollider`: radius **0.5**, height **3.5**, center (0, **0.25**, 0), direction Y, not trigger. Height changes at runtime: **1.25** while sliding/forced-crouch (`NewMovement.cs:2270`, `:902`), restored to 3.5 (`:933`). Sliding also moves the pivot down by 0.5*(3.5-1.25) = 1.125.
* `Rigidbody`: mass **100**, drag 0, interpolate = Interpolate, collision detection = ContinuousDynamic, constraints = FreezeRotation (112), uses gravity (custom-gravity extension `rb.GetGravityDirection()/SetCustomGravity` in namespace `Gravity`).
* Eye/camera: child `Main Camera` at local (0, **1.4**, 0). `CameraController.Start` stores `originalPos = transform.localPosition` (`CameraController.cs`), slide lowers target by `0.625` (`NewMovement.cs HandleSlideState`). Feet = pivot.y + 0.25 - 1.75 = **pivot.y - 1.5**. So standing eye height above the floor = **2.9**; total height 3.5; slide height 1.25.
* Children: `GroundCheck` (layer 20, trigger CapsuleCollider r0.25 h1.3 at local y -1.256, scale 0.85/0.8/0.85), `SlopeCheck` (layer 20, trigger Capsule r0.45 h3.5 centre y0.25, `slopeCheck=true`), `WallCheck` (layer 20, trigger SphereCollider r0.5, localScale 1.8 => ~0.9 world radius), `Main Camera` (see B), `CameraCollisionChecker`, `CamPositioner`, `Canvas` (UI), `v1_combined` (V1 3rd-person model, layer 30 `Portal`, not seen by the camera).
* Note `GroundCheck.OnEnable` does `transform.parent.parent = null` (`GroundCheck.cs`): the Player detaches from its authored parent (`FirstRoom Pit`) at runtime.

### A.2 Position / velocity / state fields (public unless noted)

```csharp
public Rigidbody rb;                    // [HideInInspector]
public CapsuleCollider playerCollider;  // [HideInInspector]
public CameraController cc;
public GroundCheckGroup gc, slopeCheck; // gc.onGround, gc.touchingGround, gc.heavyFall, gc.canJump
public int hp = 100; public float antiHp; // "hard damage" (yellow bar)
public bool dead, activated, levelOver, endlessMode;
public bool boost;          // dashing (also true while sliding)
public bool sliding, jumping, falling, walking, standing, slowMode;
public float boostCharge = 300f;   // stamina, 100 per dash, regen 70/s (x difficulty factor)
public int currentWallJumps;       // 0..3, reset when grounded
public float slamForce, slamCooldown;   // ground slam; gc.heavyFall == slamming
public Vector3 dodgeDirection, pushForce, preDashSpeed;
public CustomGroundProperties groundProperties; // set by GroundCheck from the floor collider
public float windState;            // air-strafe boost timer
public float walkSpeed, airAcceleration, jumpPower, wallJumpPower; // serialized
public Vector3 Position (cachedPos), HeadPosition (cachedHeadPos); // ITarget, updated by UpdateCachedTransformData
```
Velocity is simply `nm.rb.velocity` (world units/s). `PlayerTracker.Instance.GetPlayerVelocity()` also exists.

### A.3 Speeds, gravity, jump [asset + code]

Serialized on `NewMovement`: `walkSpeed = 750`, `airAcceleration = 6000`, `jumpPower = 90`, `wallJumpPower = 150`.

* Walk (grounded): `targetVel = inputDir * (walkSpeed * Time.deltaTime * 2.75f)` in `FixedUpdate` => `750 * 0.008 * 2.75 = 16.5 u/s` (`NewMovement.cs Move()`, line 1281). 16.5 also appears as the air-strafe soft cap constant.
* Crouch/slowMode multiplier 1.25/2.75. `CustomGroundProperties.speedMultiplier` scales it.
* Jump (`NewMovement.cs:1551`): `rb.AddForce(up * jumpPower * 1500f * 2.6f)` for a normal jump => impulse over one fixed step = `90*1500*2.6*0.008/100 = 28.1 u/s` upward; apex about `28.1^2/80 = 9.9 u` (about 2.8 V1 heights). Slide-jump x2 force, dash-jump x1.5, super-jump (after slam) up to x12.5.
* Dash (`TryDash`, `:1053`): `boostLeft = 100`, decreases by 4 per fixed step, velocity `= dodgeDirection * 16.5 * 3 = 49.5 u/s` for 25 steps = 0.2 s (about 9.9 u). Costs 100 of 300 stamina. Dash/slide sets the player layer to **15 Invincible** (`NewMovement.cs Dodge`, `Move`: `base.gameObject.layer = 15` / back to `2`).
* Slide (`StartSlide`, `:2240`, velocity in `Dodge()` `:1433`): speed `walkSpeed*0.008*4*preSlideSpeed` = **24 u/s** x `preSlideSpeed` (1..3, built from pre-slide speed/24).
* Slam (`TryStartSlam`, `:1108`): sets `rb.velocity = (0,-100,0)`, `gc.heavyFall = true`; fall speed clamped to -100 in `Update`.
* Wall jump (`WallJump`, `:1714`): up to 3 per airtime (`currentWallJumps < 3`), force `2000 * wallJumpPower` along (wall normal + up).
* `ClimbStep` auto-steps ledges up to `step = 2.1` units (`ClimbStep.cs:13`).
* All values are tuned for g = 40; if the host uses a different gravity, scale `Physics.gravity` (reset by `GameStateManager.ResetGravity`), or better keep V1 physics unscaled and scale only the host mapping.

### A.4 Grounded check / wall check

* `GroundCheck.cs` (two instances in `GroundCheckGroup`): trigger-based. `OnTriggerEnter` adds a collider if `ColliderIsCheckable`:
```csharp
public bool ColliderIsCheckable(Collider col)   // GroundCheck.cs:431
{
  if (!col.isTrigger && !col.gameObject.CompareTag("Slippery")) {
     if (!LayerMaskDefaults.IsMatchingLayer(col.gameObject.layer, LMD.Environment)
         && col.gameObject.layer != 11 && col.gameObject.layer != 26) {
         if (col.gameObject.layer == 18) return col.gameObject.CompareTag("Floor");
         return false; }
     return true; }
  return false; }
```
  => ground = non-trigger collider on layers **6,7,8,24** (LMD.Environment), or 11 (BigCorpse), 26 (Armor), or layer 18 (PlayerOnly) with tag `Floor`. Tag `Slippery` is excluded (treated as frictionless: `NewMovement.Update` raycast branch).
* `GroundCheck.cs` also: `canJump` ("enemy step") from layer-12 `EnemyTrigger` colliders (`!CompareTag("Slippery") && layer == 12`); `heavyFall` slam damages layer-10/11 enemies via `EnemyIdentifierIdentifier` (`component2.eid.hitter = "ground slam"; DeliverDamage(..., instakillStomp ? 99999 : 2, tryForExplode:true)`).
* `WallCheck.cs:41`: wall = non-trigger collider on LMD.Environment or layer 11, tag != `Slippery`; `CustomGroundProperties.canWallJump` can veto. Closest-point query uses `ColliderUtility.FindClosestPoint(col, pos, ignoreVerticalTriangles)` (`ColliderUtility.cs:146`): non-convex `MeshCollider` => `Mesh.AcquireReadOnlyMeshData(sharedMesh)` + a Burst loop over every triangle; every other collider type uses `collider.ClosestPoint`. => **keep triangle count per mesh collider small**.
* `GroundCheck`'s `PlayerMovementParenting.AttachPlayer` is used for tag `Moving` (and layer 11/26) colliders that have an `attachedRigidbody`.

### A.5 Damage, death, respawn

```csharp
public void GetHurt(int damage, bool invincible, float scoreLossMultiplier = 1f, bool explosion = false,
                    bool instablack = false, float hardDamageMultiplier = 0.35f, bool ignoreInvincibility = false); // NewMovement.cs:1904
public void GetHealth(int health, bool silent, bool fromExplosion = false, bool bloodsplatter = true);               // :2090
public void FullHeal(bool silent = false); public void SuperCharge();   // hp = 200
public void Respawn();                                                   // :2159
public void ForceAntiHP(float amount, ...); public void ResetHardDamage();
public void Launch(Vector3 direction, float multiplier = 8f, bool ignoreMass = false); // :1798
public void LaunchFromPoint(Vector3 position, float strength, float maxDistance = 1f);
public void StopMovement(); public void ActivatePlayer(); public void DeactivatePlayer();
```
* Early-out in `GetHurt`: `if (dead || levelOver || (invincible && layer==15 && !ignoreInvincibility) || damage <= 0) return;`. `invincible:true` => i-frames (`hurtInvincibility`, layer 15) and hard-damage conversion. Cheat `Invincibility.Enabled` zeros damage. Assist `damageTaken` scales it.
* `hp` is `int`, starts 100 (200 on difficulty 0); `GetHealth` caps at 100 (200 when `difficulty == 0` or lenient-with-restarts), `SuperCharge` sets 200. `antiHp` (hard damage) clamps usable max hp.
* **Death**: `hp` reaches 0 inside `GetHurt` => `dead = true; activated = false; cc.enabled = false; rb.constraints = None;` weapons/fists off, `deathSequence.gameObject.SetActive(true)` (`DeathSequence.cs`: 2 s slow-down + "you died" screen; `PostProcessV2_Handler.DeathEffect(true)`).
* **Restart**: `StatsManager.Update` (`StatsManager.cs`) calls `Restart()` when `R` (legacy `Input.GetKeyDown`) or `Fire1` pressed while `nm.hp <= 0`. `StatsManager.Restart()` (`:261`): if `currentCheckPoint == null` => `SceneHelper.RestartSceneAsync()` (**full scene reload**, destroys all our scene objects); else `currentCheckPoint.OnRespawn()` (`CheckPoint.cs:370`) which ends in `ResetRoom()` (`:522`).
* In-place respawn recipe (mirrors the end of `CheckPoint.ResetRoom`, lines ~550-590):
```csharp
nm.transform.position = p; nm.rb.position = p; nm.rb.velocity = Vector3.zero;
nm.rb.SetCustomGravityMode(false); cc.gravityRotation = Quaternion.identity; cc.gravityVec = Physics.gravity.normalized;
cc.rotationOffset = Quaternion.identity; cc.transitionRotationZ = cc.tiltRotationZ = 0; cc.ResetCamera(yawDegrees); cc.ApplyRotations();
nm.gc.heavyFall = false; cc.activated = true; nm.enabled = true;
nm.Respawn();               // hp=100, stamina, antiHp reset, deathSequence off, weapons/fists on, dead=false
nm.GetHealth(0, silent: true); nm.cc.StopShake(); nm.ActivatePlayer();
```
  Patch `StatsManager.Restart` (prefix, return false) to do this instead of reloading the scene.

### A.6 Teleporting safely

* `KeepInBounds` is on the Player (`useColliderCenter=1, maxConsideredDistance=4.5, updateMode=Update, includePlayerOnly=1`) [asset]. Each Update it `Physics.Linecast`s from the previous traced position to the current one against Environment (+PlayerOnly layer 18); a hit snaps the player back. If the per-frame displacement is **> 4.5 units** it simply accepts the new position (`KeepInBounds.cs:91`). For short teleports through geometry call `GetComponent<KeepInBounds>().ForceApproveNewPosition()` (`:85`) right after setting the position.
* Set both `transform.position` and `rb.position`, zero `rb.velocity`, then fix the camera (A.5). Vanilla checkpoints set `player.transform.position = checkpoint.position + up * 1.25` (feet on the checkpoint plane means pivot 1.5 above floor; 1.25 works because the checkpoint trigger is slightly above floor).
* Triggers that can interfere: `OutOfBounds` (`OutOfBounds.cs`, teleports the player to `StatsManager.spawnPos`/checkpoint when entering its trigger) and `DeathZone`; disable these scene objects (see E.5).
* Floating-point: ER world coordinates are large; keep V1 near the Unity origin (floating origin) and offset host coordinates.

### A.7 Input actions the player reads (`PlayerInput.cs`, `InputActions.cs`)

`Move` (Vector2), `Look` (Vector2), `Jump`, `Slide`, `Dodge`, `Fire1`, `Fire2`, `Punch`, `Hook`, `ChangeFist`, `NextVariation/PreviousVariation`, `NextWeapon/PrevWeapon/LastWeapon`, `Slot1..6`, `SelectVariant1..3`, `Pause`, `Stats`. Access through `MonoSingleton<InputManager>.Instance.InputSource` (`InputManager.cs:77`, type `PlayerInput`; each member is an `InputActionState` with `.Action`, `.IsPressed`, `.WasPerformedThisFrame`, `.ReadValue<T>()`). `NewMovement.Update` reads `Move` only if `activated`; `HandleInputs` runs only if `!GameStateManager.Instance.PlayerInputLocked`.

---

## 3. B. Camera and rendering pipeline

### B.1 `CameraController` (`CameraController.cs`)

`[ConfigureSingleton(NoAutoInstance)] class CameraController : MonoSingleton<CameraController>` lives on `Player/Main Camera`.

* `public Camera cam;` (the Main Camera), `private Camera hudCamera;` (serialized), `nm`, `player`.
* Rotation storage: `public float rotationY` (yaw, degrees, stored on the **Player** transform), `public float rotationX` (pitch, degrees, **positive = look up**, clamped to +-90 in `LateUpdate`), `transitionRotationZ`, `tiltRotationZ` (roll from strafing when `cameraTilt` pref on), `Quaternion rotationOffset`, `gravityRotation`.
```csharp
public void ApplyRotations(bool debug = false) {      // :409
  player.transform.localRotation = gravityRotation * Quaternion.AngleAxis(rotationY, Vector3.up);
  MonoSingleton<NewMovement>.Instance.rb.rotation = player.transform.rotation;
  transform.localRotation = Quaternion.AngleAxis(-rotationX, Vector3.right)
        * Quaternion.AngleAxis(transitionRotationZ, Vector3.forward)
        * Quaternion.AngleAxis(tiltRotationZ, Vector3.forward) * rotationOffset; }
```
* Input: `LateUpdate` (`:218`) adds `InputSource.Look.ReadValue<Vector2>() * (opm.mouseSensitivity/10) * (zoom ? cam.fov/defaultFov : 1)` to rotationY/X when `activated && !GameStateManager.CameraLocked`. To drive look from the host, either write `rotationX/rotationY` from a script that runs after `CameraController.LateUpdate` and call `ApplyRotations()`, or inject a virtual-mouse delta (note sensitivity scaling).
* Final camera pose (what the host camera must copy) = `cc.cam.transform.position/rotation` **after** `CameraController.LateUpdate` (includes bob: `walking && standing` oscillates localPosition.y by 0.1; shake: `cameraShaking`; slide lowering; roll tilt). `cc.GetDefaultPos()` = bob/shake-free eye position (used by weapons for hitscan origin; `Revolver`/`Punch` use `cc.GetDefaultPos()` + `camera.forward`).
* FOV: `PrefsManager` key **`"fieldOfView"`** (float, default 105; this machine: 120, `Prefs.json`). Applied as `cam.fieldOfView` (Unity FOV = **vertical** FOV; no aspect compensation except `CheckAspectRatio`, which only scales the HUD camera's X). Runtime changes: dash forward `defaultFov - defaultFov/20`, dash back `+defaultFov/10`, `Zoom(float)`; `Mathf.MoveTowards(..., 300 deg/s)` back to `defaultFov`. Always read `cc.cam.fieldOfView` per frame. The pref change callback is `PrefsManager.onPrefChanged("fieldOfView", float)`.
* `CameraShake(float)`, `StopShake()`, `ResetCamera(float yaw, float pitch=0)`, `SetRotation(Quaternion, Vector3 gravity, ...)`, `Transform(Matrix4x4, Vector3?, Quaternion?)` (portal travel).

### B.2 Camera objects [asset: uk_construct `Player/Main Camera`]

| Object | Layer | Camera settings |
|---|---|---|
| `Main Camera` (tag `MainCamera`, `CameraController`, `AudioListener`, `CameraFrustumTargeter`, `AudioLowPassFilter`) | 2 | depth **-1**, clearFlags **Skybox** (bg alpha 0), near 0.1, far 4000, FOV from pref, **Forward**, HDR off, MSAA off, occlusion culling on, `targetTexture` null in asset (set to MRT at runtime). cullingMask `0x8FD2DFD7` (2412961751): Default, TransparentFX, IgnoreRaycast, Water, **OutdoorsBaked(6), EnvironmentBaked(7), Environment(8)**, Gib, Limb, BigCorpse, EnemyTrigger, Projectile, Invincible, BrokenGlass, GroundCheck, Item, Explosion, **Outdoors(24)**, OutdoorsNonSolid, Armor, GibLit, SpecialLighting. **Excludes** UI(5), AlwaysOnTop(13), Invisible(16), PlayerOnly(18), VirtualScreen(19), EnemyWall(21), VirtualRender(28), SandboxGrabbable(29), Portal(30). |
| `Main Camera/HUD Camera` | 2 | depth **0**, clearFlags **Depth only**, FOV **90** (reset to 90 each frame, scaled to zoom target), near 0.1 far 1000, cullingMask = **AlwaysOnTop (13) only**, Forward, no occlusion culling. Runtime: `SetTargetBuffers(mainTex.colorBuffer, depthBuffer.depthBuffer)`. |
| `Main Camera/Virtual Camera` | 2 | depth **1**, clearFlags **Nothing**, **orthographic size 0.5**, near 0.01 far 5, cullingMask = **VirtualScreen (19)** only, renders to the **back buffer**. Local position (-0.005,-2.8,-300). Child `Quad` (layer 19, scale 20x20x1) with material `PostProcessV2_VSRM` = shader **`ULTRAKILL/PostProcessV2`**. |
| `Main Camera/Shop Camera` | 2 | inactive; used by shop. |

Material `PostProcessV2_VSRM` properties [asset]: textures `_MainTex` (assigned at runtime = `mainTex`), `_Dither`, `_NoiseTex`, `_VignetteTex`, `_HudTex`, `_OutlineTex`; floats `_Deathness`, `_Sharpness`, `_RandomNoiseStrength`, `_UnderWaterScale/Speed/Strength`, `_JFADistance`; keywords toggled from code: `UNDERWATER`, `DEAD`, `WICKED`, `VIGNETTE`; vector `_VirtualRes` (set in `SetupRTs`). Palette/dither/colour-compression use globals/keywords `PALETTIZE`, `_ColorPrecision`, `_PaletteTex`.

### B.3 `PostProcessV2_Handler` (`PostProcessV2_Handler.cs`) - the render-target plumbing

`[ConfigureSingleton(NoAutoInstance)] class PostProcessV2_Handler : MonoSingleton<...>`; public fields `Camera mainCam, hudCam, virtualCam; RenderTexture mainTex, reusableBufferA, reusableBufferB, depthBuffer, viewNormal, portalMask; Material postProcessV2_VSRM; Texture ditherTexture, vignetteTexture; float downscaleResolution`.

`SetupRTs()` (`:338`), executed from `Camera.onPreRender` for the main camera:
```csharp
width = Screen.width; height = Screen.height;
if (downscaleResolution != 0f) { /* pixelization: keep aspect, short side = downscaleResolution (720/480/360/240/144/36) */ }
mainTex        = new RenderTexture(w, h, 0,  RenderTextureFormat.ARGB32)  { filterMode = Point, antiAliasing = 1 };
depthBuffer    = new RenderTexture(w, h, 32, RenderTextureFormat.Depth)   { filterMode = Point };
reusableBufferA= new RenderTexture(w, h, 0,  RenderTextureFormat.RG16);   // outline buffer
reusableBufferB= new RenderTexture(w, h, 0,  RenderTextureFormat.ARGB32); // scratch (blood/outline passes)
viewNormal     = new RenderTexture(w, h, 0,  RenderTextureFormat.RGB565); // view normals
buffers = { mainTex.colorBuffer, reusableBufferA.colorBuffer, viewNormal.colorBuffer };
mainCam.SetTargetBuffers(buffers, depthBuffer.depthBuffer);                 // :404  (3-target MRT)
hudCam.SetTargetBuffers(mainTex.colorBuffer, depthBuffer.depthBuffer);      // :407 and each frame in OnPreRenderCallback (:626)
postProcessV2_VSRM.SetTexture("_MainTex", mainTex);
```
* "Pixelization" is the `pixelization` pref (int 0..6 => 0 native, 720, 480, 360, 240, 144, 36 rows; `GraphicsSettings.GetPixelizationValue`, `GraphicsSettings.cs:199`). This machine: `pixelization 0`, `dithering 0`, `colorCompression 0`, `vertexWarping 0`, `textureWarping 0`, `outlineThickness 10`, `simplifyEnemies 1`, so it renders at native window resolution with outlines.
* Vertex warping / texture warping / dithering / palette are shader globals and keywords driven by prefs (`vertexWarping`, `textureWarping`, `dithering`, `colorCompression`, `colorPalette`), applied in the object shaders and the `PostProcessV2` shader; the shaders themselves were not decompiled.
* Outlines: `SetupOutlines` (`:461`) adds a `CommandBuffer "Outlines"` at `CameraEvent.AfterEverything` on the main camera that jump-floods `reusableBufferA` and blits the result into `mainTex`. `OnPreRenderCallback(Camera)` (`:543`) for the main camera also clears `reusableBufferA`, and builds a `"Blood and Oil"` command buffer at `BeforeForwardAlpha` that draws blood stains and gasoline decals (instanced) into `mainTex` using `_DepthBuffer`.
* `HeatWaves()` adds a command buffer at `AfterForwardAlpha` copying `mainTex`.
* Order per frame: Main Camera (depth -1) -> HUD Camera (depth 0, **clears depth** in the shared depth RT so the viewmodel is on top) -> Virtual Camera (depth 1): Quad samples `mainTex` through `PostProcessV2` (dither, palette, vignette, death/underwater) -> back buffer. Then Unity draws **Screen-Space-Overlay canvases** (not part of any camera).
* Consequence: `mainTex` after the frame = world + effects + outlines + viewmodel + world-space HUD, opaque-ish, low alpha reliability; `depthBuffer` after the frame is the **HUD camera's** depth (world depth is overwritten by `ClearFlags.Depth`).

### B.4 Canvases / HUD rendering [asset]

* `Player/Canvas` (layer 5, `CanvasController`, `OptionsMenuToManager`): **ScreenSpaceOverlay** (`m_RenderMode 0`), sortingOrder 40. Contains crosshair, `AltHud`/`AltHud (2)` (hudType 2/3), hurt screen, `BossBarManager` ("Boss Healths"), subtitles, `MessageHud`, `DeathSequence`, `BlackScreen`, `LevelStats` stuff, `PauseMenu`, `OptionsMenu`, `SpawnMenu`, cheat overlay, `Navmesh Warning`, `Level Name Popup`, `Weapon Wheel`, `Hellmap`, `CutsceneSkipText`, `Game Modifiers`, `ScanningStuff`.
* `HUD Camera/HUD` (`HudController`, `altHud=false`) with `GunCanvas` (weapon icon, `StatsPanel` with `HealthBar`, `StaminaMeter`, speedometer, railcannon meter) and `StyleCanvas` (`StyleHUD`, `StyleCalculator`) plus `FinishCanvas`: **World Space** canvases (`m_RenderMode 2`), layer **13**, drawn by the HUD Camera. `HUDPos` anchors them to screen corners. This is the default HUD (`hudType = 1`). `hudType` 2/3 => `HudController.CheckSituation()` (`HudController.cs:262`) disables the GunCanvas and enables the overlay `AltHud` (2 coloured, 3 colourless); 0 disables both (**[UNCERTAIN]** meaning of 0 in the UI).
* `ScreenBlood` (heal splash) is instantiated under the overlay canvas.

### B.5 Viewmodel layers [asset: gameprefabs bundle]

Weapon prefabs (`Revolver Pierce/Twirl/Ricochet`, `Shotgun Pump/Grenade`, `Nailgun Overheat`, `Railcannon Harpoon`, `Rocket Launcher ...`, `Arm Blue` punch) have every child on **layer 13 AlwaysOnTop** and are instantiated under `Main Camera/Guns` by `GunSetter` (`GunSetter.cs:70 ResetWeapons`). So the HUD Camera draws viewmodel + arms + the two world-space HUD canvases together. Projectiles are layer 14, explosions 23, gibs 9/27, enemies root 12 with limbs 10.

### B.6 Where to hook for capture (recommendation)

All three hook options exist; recommended is **our own cameras** because ULTRAKILL's RTs are low-reliability for alpha and shared depth is overwritten:

1. **World/effects (+depth)**: create `BridgeWorldCam` (child of Main Camera, `CopyFrom(cc.cam)` every `LateUpdate` after `CameraController`), `targetTexture` = our ARGB32 RT (alpha cleared 0), a depth `RenderTexture` via `SetTargetBuffers(colorRT.colorBuffer, depthRT.depthBuffer)` (the same pattern ULTRAKILL uses), `clearFlags = SolidColor (0,0,0,0)`, `cullingMask = main mask & ~(layers 6,7,8,24)` (the real level is hidden anyway) and keep Projectile 14, Explosion 23, Gib 9/27, Item 22, Default 0, TransparentFX 1, Armor 26, SpecialLighting 31. Disable or cull the original Main Camera (e.g. `cullingMask = 0`) to avoid double rendering; keep it enabled because `PostProcessV2_Handler`, command buffers and `Camera.onPreRender` depend on it. **[UNCERTAIN]** ULTRAKILL's shaders write MRT outputs and sample globals (`_DepthBuffer`, `_OutlineTex`, `_WorldNormal`); with a single-target camera extra outputs are dropped and soft-particle/outline shaders may sample stale textures. Verify visually. Alpha for opaque shaders must be verified (**[UNCERTAIN]**).
2. **Viewmodel**: clone of `HUD Camera` (`CopyFrom`, FOV 90, `cullingMask = 1<<13`, clear alpha 0, own RT). It will include `GunCanvas/StyleCanvas/FinishCanvas` because they are also layer 13; to split them, temporarily set those three canvas GameObjects to another layer in `OnPreRender` and restore in `OnPostRender`, or render the HUD separately by masking them.
3. **HUD (stock overlay)**: set the overlay `Player/Canvas` to `renderMode = ScreenSpaceCamera`, `worldCamera = BridgeUiCam` (orthographic, `cullingMask = 1<<5`, own RT, alpha 0). The world-space `GunCanvas/StyleCanvas` are covered by step 2.
4. **Cheap alternative / debug**: `PostProcessV2_Handler.Instance.mainTex` and `.depthBuffer` are public; grab `mainTex` with a `CommandBuffer` at `CameraEvent.AfterEverything` on the Main Camera (after the outline pass) for "world only", and disable `hudCam` for that frame so the viewmodel is not baked in. Alpha is not reliable (ARGB32 but alpha semantics unknown).
5. Transfer to the host: `RenderTexture.GetNativeTexturePtr()` (D3D11) to a native shared-surface plugin, or `AsyncGPUReadback.Request` (UnityEngine.CoreModule, supported in 2022.3) into shared memory. Depth: `RenderTextureFormat.Depth` RT is directly sample-able; non-linear, near 0.1, far 4000 (reversed-Z, `SystemInfo.usesReversedZBuffer`), see `PostProcessV2_Handler.ZBufferParams`.

### B.7 Related renderers that affect capture

`DoubleRender` (command buffers for radiance outlines at `BeforeForwardAlpha`), `CorrectCameraView`, `ScreenDistortionController`, `PortalRenderV2` (portal system v2, `ULTRAKILL.Portal*`; in uk_construct a `PortalManagerV2` exists; it re-targets cameras when portals are present), `LimboSkybox`, `SpaceSkybox` (skyboxes rendered via `RenderSkyboxes` when no portal manager). Hurt/low-hp tint is the global `_HurtScreenColor` plus the overlay `HurtScreen` image.

### B.8 Pose conventions

Unity left-handed, Y-up. Yaw `rotationY` rotates the **Player** (forward +Z at 0). Camera forward = `cc.cam.transform.forward`. Host handedness/axis mapping is not covered here.

---

## 4. C. Layers, tags and what geometry must provide

### C.1 Layer table [asset: TagManager]

```
0 Default        1 TransparentFX   2 Ignore Raycast (= PLAYER)   4 Water        5 UI
6 OutdoorsBaked  7 EnvironmentBaked 8 Environment               9 Gib         10 Limb (enemy hitboxes)
11 BigCorpse (big enemy hitboxes)  12 EnemyTrigger (enemy step trigger)       13 AlwaysOnTop (viewmodel/HUD)
14 Projectile    15 Invincible (player during dash/i-frames)    16 Invisible   17 BrokenGlass
18 PlayerOnly    19 Virtual Screen 20 GroundCheck               21 EnemyWall   22 Item
23 Explosion     24 Outdoors       25 Outdoors Non-solid        26 Armor       27 GibLit
28 VirtualRender 29 SandboxGrabbable 30 Portal                  31 SpecialLighting
```
Tags: `RoomManager, Body, Forward, Left, Right, ForwardR, ForwardL, Floor, Wall, Limb, Head, EndLimb, Spider, Door, Glass, GlassFloor, Enemy, StyleHUD, ChallengeManager, Piston, PlayerPosInfo, GunControl, Breakable, OptionsManager, Moving, MessageHud, Coin, Slippery, Armor, Metal, IgnorePushes, SoftFloor, PlayerTrigger` (+ builtin `Player`, `MainCamera`...).

### C.2 `LayerMaskDefaults` (`LayerMaskDefaults.cs:10`)

```
LMD.Environment = layers 6 | 7 | 8 | 24      (0x40|0x80|0x100|0x1000000)
LMD.Enemies     = layers 10 | 11             (0x400|0x800)
LMD.Player      = layer 2                    (4)
EnvironmentAndBigEnemies = Environment | 11, EnemiesAndEnvironment, EnemiesAndPlayer, EnvironmentAndPlayer, EnemiesEnvironmentAndPlayer, BigEnemiesEnvironmentAndPlayer
```
`PrefsManager`/`SceneHelper.footstepLayerMask = GetMask("Environment","Outdoors","EnvironmentBaked","OutdoorsBaked")` (same four).

### C.3 What each system collides with

* **Physics collision matrix** [asset: PhysicsManager.m_LayerCollisionMatrix]: Player layer 2 collides with Default, TransparentFX, IgnoreRaycast, Water, UI, **OutdoorsBaked, EnvironmentBaked, Environment, BigCorpse(11), EnemyTrigger(12)**, AlwaysOnTop, Projectile, Invisible, PlayerOnly, Item, Explosion, **Outdoors**, Armor. It does **not** collide with Limb(10), Gib, Invincible. Layer 15 (Invincible, dash) collides with Default, 6/7/8/24, BigCorpse, Item, Armor, PlayerOnly but not Limb or Projectile. `NewMovement.Update` also calls `Physics.IgnoreLayerCollision(2, 12, heavyFall || sliding)`.
* Layer 20 GroundCheck (the three check triggers) collides with Water, **6,7,8,24**, Limb, BigCorpse, EnemyTrigger, Invisible, PlayerOnly, Armor.
* Layers 6/7/8/24 collide with everything gameplay-relevant (Gib, Limb, BigCorpse, Projectile, Explosion, Item, GroundCheck, Invincible...).
* **Hitscan** (`RevolverBeam.cs`): `enemyLayerMask = 10 | 11 (+14 if canHitProjectiles) (+2 for enemy beams)` (`:145-151`), `pierceLayerMask = 6|7|8|24|26` (`:153-157`), plus `Water` raycast (`:453`). Environment hits use `LayerMaskDefaults.IsMatchingLayer(layer, LMD.Environment)`; ricochets require `Environment` or layer 0; tag `Armor` blocks/ricochets; tags `Glass/GlassFloor` + `Glass` component shatter; `Breakable` component shatters; `Coin` tag. **Geometry only needs a collider on an Environment layer.**
* **Projectiles**: `Projectile.cs:474` sweeps `LMD.EnemiesAndEnvironment | player`; `OnTriggerEnter` branches on tags (`Head/Body/Limb/EndLimb`), `LMD.Environment`, layer 0, layer 14, `Armor`, `Coin`.
* **Explosions** (`Explosion.cs`): trigger on layer 23; hit enemies when `other.layer == 10 || 11` (line ~299); `Breakable`, `Flammable`; player via tag `Player`. Layer 23 collides with Limb, BigCorpse, Environment layers, Projectile, Item.
* **Slide / air checks**: `NewMovement.cs` raycasts with `LMD.Environment` (ceiling checks, slide height, floor snap `TryFloorSnap` ray `num+1` below), `frictionlessSurfaceMask = Environment | layer 0`; `Cling` (wall scrape) with Environment; `FixedUpdate windState>0` `rb.SweepTestAll` breaks `Breakable`/`Glass` on layers 8/24.
* **Camera shake collision** and **`KeepInBounds`**: Environment (+layer 18 for the player).
* Footstep audio/surface type: `SceneHelper.TryGetSurfaceData` (`SceneHelper.cs`) only resolves for colliders that also have a `MeshRenderer` + material with `_SurfaceType` (it duplicates such objects into a hidden "footstep" physics scene at scene load); our collider-only geometry yields no surface data => default/none footsteps (**[inferred]**; `PlayerFootsteps` falls back when `TryGetSurfaceData` is false).

### C.4 Components/tags geometry may carry

* None are *required*. Recommended: layer 8, non-trigger, static (no Rigidbody), no `Slippery` tag.
* `CustomGroundProperties` (`CustomGroundProperties.cs`): `friction`, `speedMultiplier`, `push/pushForce`, `canJump`, `jumpForceMultiplier`, `canWallJump`, `canSlide`, `canDash`, `launchable`, `forceCrouch`, `overrideFootsteps/surfaceType` etc. Found on the same collider (or its attached rigidbody).
* Tag `Floor`/`Wall` are used by **blood stains** (`Bloodsplatter.CreateBloodstain`, tags Wall/Floor/Moving/Glass/GlassFloor required) and layer-18 ground. Tag `Slippery` = ice. Tag `Moving` + kinematic Rigidbody = moving platform (player parenting). `Glass` component for breakable glass, `Breakable` for destructibles, `Bleeder` for gore on hit.
* `PhysicMaterial`: the player capsule uses a shared physics material; friction is handled in code (`friction = modForcedFrictionMultip * groundProperties.friction`), geometry materials should be default.

---

## 5. D. Enemies and damage

### D.1 `EnemyIdentifier` (`EnemyIdentifier.cs`, `[DefaultExecutionOrder(-500)]`)

Key public members: `EnemyClass enemyClass` (Husk/Machine/Demon/Boss...), `EnemyType enemyType` (`EnemyType.cs`: Cerberus=0 ... Filth=3, Stray=13, Soldier=15, Swordsmachine=7, Drone=1, ...), `float health`, `bool dead`, `string hitter`, `List<string> hitterWeapons`, `List<HitterAttribute> hitterAttributes`, `GameObject weakPoint`, `Transform overrideCenter`, `float totalDamageTakenMultiplier`, `bool ignorePlayer`, `bool attackEnemies`, `bool blessed`, `bool puppet`, `bool dontCountAsKills`, `bool dontUnlockBestiary`, `bool checkingSpawnStatus`, `string[] weaknesses`, `float[] weaknessMultipliers`, `Enemy zombie/machine/statue` (the `Enemy` brain component), `GoreZone gz`.

* `Awake()`: `health = 999f; InitializeReferences(); ForceGetHealth()`. `Update()` calls `ForceGetHealth()` each frame which **overwrites `health` from the `Enemy` component** if present; with no `Enemy` it keeps whatever you set.
* `Start()`: registers in `MonoSingleton<EnemyTracker>.Instance.AddEnemy(this)`, `GetGoreZone()` (auto-creates and reparents under an "Automated Gore Zone" when the object has no parent, `GoreZone.cs:53`), `UpdateTarget()`, bestiary unlock (set `dontUnlockBestiary = true` to avoid touching saves).
* `EnemyIdentifierIdentifier` (`EnemyIdentifierIdentifier.cs`): put on **each hitbox collider**; `Awake` fills `eid = GetComponentInParent<EnemyIdentifier>()`; `Start` has `SlowCheck()` that destroys itself when `eid == null` and, if `eid.dead`, deactivates when far from start.

### D.2 `DeliverDamage` (`EnemyIdentifier.cs:1084`)

```csharp
public void DeliverDamage(GameObject target, Vector3 force, Vector3 hitPoint, float multiplier, bool tryForExplode,
        float critMultiplier = 0f, GameObject sourceWeapon = null, bool ignoreTotalDamageTakenMultiplier = false, bool fromExplosion = false)
```
Flow: if `target == gameObject` retarget to the child `EnemyIdentifierIdentifier`; if `sourceWeapon` set, enemy retargets the player; `multiplier *= totalDamageTakenMultiplier`, `/= totalHealthModifier`, boss difficulty scaling, `weaknesses` vs `hitter`, fire bonus, electricity/aftershock; then by `enemyType`/`enemyClass` it calls `Enemy.GetHurt(...)` (`zombie/machine/statue`), `Drone.GetHurt`, `MaliciousFace`, `Idol`, `Deathcatcher`, `Wicked`. **If the needed `Enemy` component is missing it `return`s without doing anything** (e.g. `if (!zombie) return;`).

`Enemy.GetHurt` (`Enemy.cs:1671`) does: damage `d = mult + mult * limbMult * crit` with `limbMult = Head 1, Limb/EndLimb 0.5, else 0` (`CalculateLimbMultiplier`), `health -= d`, blood (`HandleBloodSelection` -> `BloodsplatterManager.GetGore(GoreType.Head/Limb/Body/Small, eid)` + `Bloodsplatter.GetReady()` which enables healing), death handling, hurt sound, then **style**: `SendStyleInformation(hitLimb, killed, sourceWeapon)` (`Enemy.cs:2615`) -> `StyleCalculator.HitCalculator(string hitter, string enemyType, string hitLimb, bool dead, EnemyIdentifier eid, GameObject sourceWeapon)` (`StyleCalculator.cs:98`) with `enemyType` in {"zombie","machine","spider"} and limb in {"head","limb","body"}; `HitCalculator` calls `gc.AddKill()` on kills (kill-streak charge) and `StyleHUD.AddPoints(int points, string pointID, GameObject sourceWeapon, EnemyIdentifier eid, ...)`. Kills for non-`Enemy` identifiers: `EnemyIdentifier.ProcessDeath` (`:1522`) -> `GetGoreZone().AddDeath()` (no `StatsManager.kills++` except Idol/Deathcatcher).

### D.3 How each weapon finds enemies (all end in `eid.DeliverDamage`)

* **Hitscan** (`RevolverBeam.cs`): sphere/raycast with `enemyLayerMask` (layers 10, 11), then `hit.transform.GetComponentInParent<EnemyIdentifierIdentifier>()` and the **tag** check `CompareTag("Enemy"|"Body"|"Limb"|"EndLimb"|"Head")` (`:926`); sets `eid.hitter = "revolver"|"railcannon"`, appends to `eid.hitterWeapons`, calls `eid.DeliverDamage(gameObject, dir*bulletForce, hitPoint, damage, tryForExplode, critMultiplier, sourceWeapon)` (`:1016`); afterwards HitStop `MonoSingleton<TimeController>.Instance.HitStop(0.05f)` (`:753`), camera shake, headshot combo (`gc.headshots`, `tag == "Head"`), `quickDraw` style. Pierce logic differs for layer 11 (`!dead || layer == 11`).
* **Ricochet/coin auto-aim** `RicochetAimAssist` (`RevolverBeam.cs:1157`): `Physics.SphereCastAll(r=5, infinite, LMD.Enemies)`, picks nearest hit with `EnemyIdentifierIdentifier.eid && !eid.dead`, line-of-sight raycast vs Environment, and if `eid.weakPoint` set aims at `eid.weakPoint.transform.position` (head). **Coin** (`Coin.cs:167`): `enemyQuery = (TargetDataRef t) => t.target.Type == TargetType.ENEMY && !t.target.EID.dead && ...` via `vision.TrySee(...)`: enemies are found through **`TargetTracker`** (`ULTRAKILL.Enemy/TargetTracker.cs`) with `ITarget`s, not by colliders. Real enemies register in `Enemy.Start`: `targetTracker.RegisterTarget(this, deathTokenSource.Token)` (`Enemy.cs:551`, `PortalManagerV2.Instance.TargetTracker`). It then delivers `eid.DeliverDamage(weakPoint-or-first-EnemyIdentifierIdentifier, force, hitPoint, power, false, 1f, sourceWeapon)` after `eid.hitter = "coin"` (`Coin.cs:929-960`).
* **Projectiles/nails/saws** (`Projectile.cs:605`, `Nail.cs:278-295,444,510`): trigger/collision on layers 10/11 + tags, `GetComponentInParent<EnemyIdentifierIdentifier>`, `DeliverDamage(..., damage/4 or /10 * enemyDamageMultiplier ...)`.
* **Explosions** (`Explosion.cs:299-337,472,498`): `other.layer == 10 || 11` -> `componentInParent.eid.DeliverDamage(..., fromExplosion: true)`; also `OverlapSphereNonAlloc(LMD.EnemiesAndPlayer)` at `:602`.
* **Melee/parry/knuckleblaster** (`Punch.cs`): `Physics.Raycast/SphereCast` with `LMD.Enemies` (`:649`), `OverlapSphere` on layers 10/11, `BoxCastAll` on layer 12 for enemy-step; `Punch.cs:1084-1114`: tag check then `enemyIdentifier.DeliverDamage(target.gameObject, forward*force*1000f, point, damage, tryForExplode)`; parry of projectiles uses mask 16384 (layer 14).
* **Ground slam**: `GroundCheck.cs` as above.

### D.4 Blood healing

V1 heals by touching gore particles: `Bloodsplatter` (prefab = ParticleSystem + trigger SphereCollider) `OnTriggerEnter -> Collide` (`Bloodsplatter.cs:163`): `if (ready && canCollide && other.CompareTag("Player")) NewMovement.GetHealth(hpAmount, silent:false, fromExplosion)`; `hpAmount` set per hitter (3 shotgun/explosion, 1 nail, default from `BloodsplatterManager.GetGore`), `ready` becomes true only through `Bloodsplatter.GetReady()` (called by `Enemy.ProcessBloodEffects` unless `noheal`). Also `hpOnParticleCollision` path heals 3 via `CreateBloodstain`. To reproduce for proxies: `var g = BloodsplatterManager.Instance.GetGore(GoreType.Small/Head/Limb/Body, eid, fromExplosion); g.transform.position = hitPoint; g.GetComponent<Bloodsplatter>().GetReady();` and parent under a `GoreZone` (`eid.GetGoreZone().goreZone`). `GetHealth` caps at 100 (`:2090`) and is blocked while `dead`.

### D.5 Spawning real enemies (for reference)

* There is **no Addressables key per enemy**. Enemy prefabs are referenced by `SpawnableObject` ScriptableObjects inside a `SpawnableObjectsDatabase` (`SpawnableObjectsDatabase.cs`: arrays `enemies, objects, sandboxTools, sandboxObjects, specialSandbox, unlockables, debug`; `SpawnableObject.gameObject`, `enemyType`, `identifier`), serialized in the scene's `SpawnMenu.objects` (`SpawnMenu.cs:16`, private) and `SandboxSaver.objects` (`SandboxSaver.cs:20`). Runtime access: `Resources.FindObjectsOfTypeAll<SpawnableObject>()` or reflection on `MonoSingleton<SpawnMenu>.Instance` field `objects`. Prefabs live in `StreamingAssets/aa/StandaloneWindows64/gameprefabs_assets_all.bundle` (found: `Filth`, `Stray`, `Soldier`, `ZombieVertexlit Variant`, `ShotgunHusk Variant`; root layer 12, hitboxes layer 10, class `Zombie`; other bundles such as `other_assets_all.bundle` may hold more).
* Freeze a real enemy the way the sandbox does: `Sandbox/EnemySpawnableInstance.Pause(bool freeze=true)` (`:129`): `eid.enabled = false`, disable the enemy behaviours, `NavMeshAgent.enabled=false`, `Animator.enabled=false`, Rigidbody kinematic. Light-weight alternatives: `EnemyIdentifier.ignorePlayer = true`, cheats `BlindEnemies`/`EnemyIgnorePlayer`/`DisableEnemySpawns`/`InvincibleEnemies`.
* A real enemy costs: NavMeshAgent, Animator, SkinnedMeshRenderer(s), AudioSources, Rigidbodies per limb. Not worth it for a hitbox mirror.

### D.6 Recommended invisible proxy (hit by ALL weapons)

Build per host enemy a GameObject hierarchy parented under a `GoreZone` object:

```
BridgeEnemy_<id>   (layer 12 EnemyTrigger optional; tag "Enemy"; EnemyIdentifier [enemyType=Filth, enemyClass=Husk?, health set, dontUnlockBestiary=true, ignorePlayer=true],
                    BridgeEnemyProxy : MonoBehaviour, ITarget)   // no Rigidbody/NavMeshAgent/Animator/Renderer
 +- Body  (layer 10 or 11, tag "Body" , CapsuleCollider/BoxCollider non-trigger, EnemyIdentifierIdentifier)
 +- Head  (layer 10 or 11, tag "Head" , SphereCollider,  EnemyIdentifierIdentifier)  -> eid.weakPoint = Head.gameObject
 +- Limb* (layer 10, tag "Limb"/"EndLimb", EnemyIdentifierIdentifier)  (optional)
 +- StepTrigger (layer 12, trigger)  (optional, enables enemy-step jump: GroundCheck.canJump)
```
* **Layer 10 vs 11**: layer 10 `Limb` does not collide with the player (doesn't block movement) and `GroundCheck` ignores it as ground (`ColliderIsStillUsable` rejects 10). Layer 11 `BigCorpse` **blocks the player and counts as ground** (`GroundCheck.ColliderIsCheckable` accepts 11) and is the only enemy layer that collides with layer-15 (dashing) player. Use 10 for "ghost" hitboxes, 11 if host enemies should be solid.
* **Harmony prefix on `EnemyIdentifier.DeliverDamage`** for proxy instances (return false):
```csharp
[HarmonyPatch(typeof(EnemyIdentifier), "DeliverDamage")]
static bool Prefix(EnemyIdentifier __instance, GameObject target, Vector3 force, Vector3 hitPoint, float multiplier,
                   bool tryForExplode, float critMultiplier, GameObject sourceWeapon, bool ignoreTotalDamageTakenMultiplier, bool fromExplosion)
{
  var p = __instance.GetComponent<BridgeEnemyProxy>(); if (p == null) return true;
  float limb = target.CompareTag("Head") ? 1f : (target.CompareTag("Limb") || target.CompareTag("EndLimb")) ? 0.5f : 0f;
  float dmg = multiplier + multiplier * limb * critMultiplier;           // mirror Enemy.CalculateDamage
  string limbName = target.CompareTag("Head") ? "head" : limb > 0 ? "limb" : "body";
  ... send (p.hostId, dmg, __instance.hitter, hitPoint, force) to host; update eid.health locally for style/kill detection
  bool killed = __instance.health <= 0f && !__instance.dead;
  StyleCalculator-> MonoSingleton<StyleCalculator>.Instance.HitCalculator(__instance.hitter, "zombie", limbName, killed, __instance, sourceWeapon);
  spawn gore + GetReady() for heal;  if (killed) __instance.Death();
  __instance.hitterAttributes.Clear();
  return false;
}
```
  Hit-stop, camera shake, headshot combos, quickdraw and weapon freshness are produced on the **weapon side** (before/after `DeliverDamage`), so they work for any collider that passes the layer/tag/`EnemyIdentifierIdentifier` checks. Style/kill-charge/blood-heal are **not** produced unless the prefix replicates `HitCalculator`/gore as above. Note `Weapon freshness` is keyed on `sourceWeapon` passed through to `StyleHUD.AddPoints`.
* **Coins/ricochet**: hitscan ricochet aim assist works from layers 10/11 colliders (above). Coin targeting additionally needs the proxy to implement `ULTRAKILL.Enemy.ITarget` (`Id, Type = TargetType.ENEMY, EID, GameObject, Rigidbody, Transform, Position, HeadPosition, SetData(ref TargetData), UpdateCachedTransformData()`; see `Enemy.cs:3118` for `SetData`) and register: `MonoSingleton<PortalManagerV2>.Instance.TargetTracker.RegisterTarget(proxy, cancellationToken)` (`TargetTracker.cs:187`). Set `data.position/headPosition/rotation/velocity` from the proxy transform. **[UNCERTAIN]** Not tested; `TargetTracker` uses Burst jobs over target arrays, so keep `Position`/`HeadPosition` cached and valid. Alternative (**[inferred]**): clone the registration pattern of `Cannonball`/`Glass` via `CoinTracker.RegisterTarget(ITarget, CancellationToken)` (type COIN/GLASS only).
* Initialise `EnemyIdentifier` fields before `Start`: set `enemyType`/`enemyClass`, `dontUnlockBestiary = true`, `checkingSpawnStatus = false`, `health`, `ignorePlayer = true`. Leave `zombie/machine/statue` null (this is why the prefix is required). `EnemyIdentifier.Update` runs `UpdateTarget()`/`UpdateEnemyScanner()` which dereference `EnemyTarget`/`PlayerTracker`; fine in a sandbox scene.
* **Kill count**: `StatsManager.kills` is only incremented in `Enemy.CountDeath`; if you want it, increment manually. `dead = true` makes beams treat the hit as a corpse hit; remove the colliders (destroy the proxy) after death to avoid corpse-hit logic.
* **Explosion knockback / `PortalUtils.AddForcePortalAware`** target Rigidbodies of the limb; proxies have none, so no physics reaction (fine).
* Enemy hits on the player (host attacks) should go through `NewMovement.GetHurt` (A.5). `EnemyIdentifier.CheckHurtException` etc. are irrelevant for proxies.

---

## 6. E. Scenes

### E.1 `SceneHelper` (`SceneHelper.cs`)

`[ConfigureSingleton(NoAutoInstance | PersistAutoInstance | DestroyDuplicates)] class SceneHelper : MonoSingleton<SceneHelper>`; exposes `static string CurrentScene/LastScene/PendingScene`.

```csharp
public static void LoadScene(string sceneName, bool noBlocker = false);        // :
public static Coroutine LoadSceneAsync(string sceneName, bool noBlocker = false);
public static void RestartScene(); public static Coroutine RestartSceneAsync();
public static void LoadPreviousScene();   // "Main Menu" fallback
```
`LoadSceneCoroutine` (`:666`): sets `Time.timeScale = 0`, `CancelInvoke()` + `enabled=false` on **every MonoBehaviour not in the DontDestroyOnLoad scene**, then `Addressables.LoadSceneAsync(sanitizedName)`; after load it re-inits `AssistController/TimeController`. Our plugin's MonoBehaviours must live in DDOL (BepInEx manager object does). `SanitizeLevelPath` strips `Assets/Scenes/` and `.unity`. `SceneManager.sceneLoaded` is also the hook (`OnSceneLoaded` replaces the EventSystem and calls `GameStateManager.SceneReset`).

### E.2 Scene names (Addressable keys) [catalog.json]

`Main Menu`, `Intro`, `Tutorial`, `Endless` (Cyber Grind), `uk_construct` (sandbox), `CreditsMuseum2`, `EarlyAccessEnd`, `TundraAssets`, `Intermission1`, `Intermission2`, `Level 0-1..0-5, 0-E, 0-S`, `Level 1-1..1-4, 1-E, 1-S`, `Level 2-1..2-4, 2-S`, `Level 3-1, 3-2`, `Level 4-1..4-4, 4-S`, `Level 5-1..5-4, 5-S`, `Level 6-1, 6-2`, `Level 7-1..7-4, 7-S`, `Level 8-1..8-4`, `Level P-1, P-2`, and `Assets/Scenes/Level 8-3 Space Only.unity` (in `other_scenes_all.bundle`). Bundles: `campaign_scenes_*.bundle`, `specialscenes_scenes_*.bundle`, `other_scenes_all.bundle` (all under `ULTRAKILL_Data/StreamingAssets/aa/StandaloneWindows64`). The console commands `scene <name>` / `scenes` (`GameConsole.Commands/Scene.cs`, `Scenes.cs`) wrap `SceneHelper.LoadScene` / list `Addressables.LoadResourceLocationsAsync("Assets/Scenes")`; special scenes are blocked from the console only in non-debug builds, not from code.

### E.3 Best "empty shell": `uk_construct` (the sandbox, "Level Info" `StockMapInfo`) [asset]

* Root objects (selection): `FirstRoom Pit` (contains `Player`, `PlayerLoadoutTarget`), `StatsManager` (`levelNumber 0`), `Level Info` (`StockMapInfo{sandboxTools=1, hideStockHUD=0}`), **`Cheats Enabler`** (`CheatsEnabler.Start -> CheatsController.ActivateCheats()`, so cheat menu and console are on with no consent screen), `Navigation Manager` (`SandboxNavmesh`), `Sandbox Checkpoint` (`CheckPoint` + `DefaultSandboxCheckpoint`), `Spawner Arm (1)` (`SandboxArm`), `OutOfBounds`, `Player Fall Activator` (`PlayerActivator`), `Garry Shop/Sandbox Shop/Shop` (`ShopZone`), `EventSystem`, `OutdoorsLighting`, `IndoorsLighting`, large static geometry roots (Warehouse, Starting Room Garage, Outer Garage, Lake, Flatgrass...).
* No story triggers, no rank/level end (`levelNumber 0`, so `LevelStatsEnabler` deactivates itself, `StatsManager` ranks nothing), `SceneHelper.IsSceneRankless` per `EmbeddedSceneInfo`.
* Why not others: `Endless` brings Cyber Grind logic (`NewMovement.endlessMode`, `FinalCyberRank`, wave spawner, `DeathZone`), `Tutorial`/campaign levels have scripted doors/cutscenes/music; `Main Menu` has no player.
* Drawbacks: lots of geometry/lighting/AI navmesh to disable; Player is parented to a prefab at authoring time (unparented by `GroundCheck.OnEnable`).

### E.4 Starting the scene programmatically, skipping intro/cutscenes

* Flow: `Bootstrap.Start` (`Bootstrap.cs:6`) -> if `!GameBuildSettings.noTutorial && (!GetTutorial() || !GetIntro())` -> `"Tutorial"`; else `startScene != null ? startScene : "Intro"`. `GameBuildSettings.GetInstance()` reads `StreamingAssets/GameBuildSettings.json` (absent here) else `Default {startScene=null,noTutorial=false}`; there is a ready preset **`GameBuildSettings.SandboxOnly { startScene = "uk_construct", noTutorial = true }`** (`GameBuildSettings.cs:27`).
* `Intro` scene (video + `IntroViolenceScreen`, `InitGame`): `InitGame.Awake` applies `Screen.SetResolution` from LocalPrefs `resolutionWidth/Height/fullscreen`; `IntroViolenceScreen.Start` sets `Application.targetFrameRate = Screen.currentResolution.refreshRate` and `QualitySettings.vSyncCount` from pref `vSync` (`IntroViolenceScreen.cs:39-40`). Skipping `Intro` skips these, so **apply resolution/vSync/targetFrameRate yourself**. `IntroViolenceScreen.GetTargetScene()` returns `"Tutorial"` or `"Main Menu"` (private).
* Options (least invasive first): (1) Harmony **postfix `GameBuildSettings.GetInstance` -> `SandboxOnly`** (skips Intro/menu/tutorial; no game file touched); (2) after `Bootstrap`, call `SceneHelper.LoadScene("uk_construct", noBlocker: true)` from a DDOL behaviour once `MonoSingleton<SceneHelper>` exists; (3) postfix `IntroViolenceScreen.GetTargetScene` to return `"uk_construct"` and keep Intro init.
* The player starts **locked** in the "pit-falling" state: `PlayerActivatorRelay.Start` registers `GameState("pit-falling"){cameraInputLock = Lock, cursorLock = Lock}`; `NewMovement.activated == false`; weapons off. `PlayerActivator.Activate()` (`PlayerActivator.cs:27`, public) pops the state, sets `nm.activated = true; nm.cc.activated = true; nm.cc.enabled = true`, `GunControl.YesWeapon()`, `FistControl.YesFist()`, `ActivateObjects()` (HUD pieces via `PlayerActivatorRelay.Activate`). Easiest: `Object.FindObjectOfType<PlayerActivator>().Activate()` once the scene is ready, then reposition the player.
* "Cutscene skip": `CutsceneSkipText` exists in the player canvas (campaign cutscenes only); `Time.timeScale` is restored by `SceneHelper`.

### E.5 Hiding the level and sanitising the shell (practical list)

* `Player` sits under `FirstRoom Pit`; do **not** disable that root. Disable renderers and colliders of everything that is not Player/managers: iterate `SceneManager.GetActiveScene().GetRootGameObjects()`, skip names in {`FirstRoom Pit` (children handled separately), `StatsManager`, `Level Info`, `Cheats Enabler`, `EventSystem`, `Navigation Manager`, lighting if wanted}; set `Renderer.enabled=false` and `Collider.enabled=false` for layers 6/7/8/24 (and `OutOfBounds`, `DeathZone`, `ShopZone`, `Level Transition Pit`, `Player Fall Activator`).
* Easier for rendering: remove layers 6/7/8/24 from the cameras' culling masks. Static meshes may be merged at runtime (`StatsManager` has `MeshCombineManager`; `LucasMeshCombine` namespace), so culling masks are more reliable than toggling individual renderers.
* Skybox: Main Camera clearFlags Skybox; replace with SolidColor alpha 0 in the capture cameras.
* `MapInfoBase.Instance` (`MapInfoBase.cs`: `sandboxTools`, `hideStockHUD`, `continuousGibCollisions`, `removeGibsWithoutAbsorbers`, `gibRemoveTime`) - `hideStockHUD` would hide the stock HUD at start (`HudController.Start`, `PlayerActivatorRelay.Activate`); we want the HUD, so leave false.

### E.6 Unlocking all weapons (non-persistent)

Weapons are equipped from the save: `GunSetter.CheckWeapon` (`GunSetter.cs:191`) uses prefs `weapon.rev0 ...` and `GameProgressSaver.CheckGear(name)` (save slot in `<game>/Saves/Slot1`). `GameProgressSaver.AddGear(string)` (`:584`) would modify the user's save (avoid). The editor-only cheat `OverwriteUnlocks` does nothing in a release build (`Application.isEditor && Debug.isDebugBuild`). Use the **forced loadout** mechanism instead: `GunSetter.forcedLoadout` / `FistControl.forcedLoadout` (type `ForcedLoadout`: `VariantSetting revolver, altRevolver, shotgun, altShotgun, nailgun, altNailgun, railcannon, rocketLauncher; ArmVariantSetting arm`; each `VariantSetting{blueVariant, greenVariant, redVariant}` is a `VariantOption {IfEquipped, ForceOn, ForceOff}`):
```csharp
var fl = new ForcedLoadout { revolver = All(ForceOn), altRevolver = All(ForceOn), shotgun = ..., ... , arm = new ArmVariantSetting{blueVariant=ForceOn, greenVariant=ForceOn, redVariant=ForceOn} };
MonoSingleton<GunSetter>.Instance.forcedLoadout = fl;  MonoSingleton<GunSetter>.Instance.ResetWeapons();
MonoSingleton<FistControl>.Instance.forcedLoadout = fl; MonoSingleton<FistControl>.Instance.ResetFists();
```
(`PlayerLoadout.SetLoadout()` does exactly this, `PlayerLoadout.cs:30-36`.) The `FirstRoom Pit` has a `PlayerLoadoutTarget` that applies `CommitLoadout`. Weapons are prefab references (`AssetReference[] revolverPierce ...` on `GunSetter`) loaded with Addressables.

### E.7 Cheats and other helpers available in `uk_construct`

`CheatsManager.Instance.SetCheatActive(ICheat, bool, saveState)` (`CheatsManager.cs:148`), `GetCheatInstance<T>()`: ids `ultrakill.noclip`, `flight`, `invincibility`, `hide-ui`, `hide-weapons`, `infinite-wall-jumps`, `no-weapon-cooldown`, `blind-enemies`, `enemy-ignore-player`, `invincible-enemies`, `kill-all-enemies`, `disable-enemy-spawns`, `full-bright`, `teleport-menu`, `sandbox.*`. Most are only for debugging; the bridge can call the underlying statics (`Invincibility.Enabled`, `HideUI.Active`).

---

## 7. F. Pause, focus, input, frame rate

* **PlayerSettings** [asset: globalgamemanagers]: `runInBackground = False`, `visibleInBackground = False`, `resizableWindow = True`, `fullscreenMode = 2` (raw serialized value; **[UNCERTAIN]** mapping to Exclusive/Borderless/Maximized), `defaultIsNativeResolution`, `useFlipModelSwapchain = True`, `allowFullscreenSwitch = False`, `activeInputHandler = 2`, color space gamma (0), companyName `Hakita`.
* **No script handles focus**: grep for `OnApplicationFocus/OnApplicationPause/runInBackground/Application.isFocused` finds only `CustomMusicPlayer.cs:88` (`WaitUntil(() => Application.isFocused && ...)`). With `runInBackground = false` the Unity player loop **stops entirely** (no Update, FixedUpdate, rendering) when the window is unfocused. => `Application.runInBackground = true` (set in `Awake` of the plugin; takes effect immediately). Also consider not letting the ULTRAKILL window be minimised/occluded (**[UNCERTAIN]** whether D3D11 still presents/render cameras when minimised; offscreen RT cameras should still render but vsync + minimised swapchain can throttle; set `QualitySettings.vSyncCount = 0` and own `Application.targetFrameRate`).
* **Pause**: only via action `InputSource.Pause` (`OptionsManager.Update`, `OptionsManager.cs`): `OptionsManager.Pause()` (`:243`) sets `paused`, disables `nm`, registers `GameState("pause"){cursorLock=Unlock, cameraInputLock=Lock, playerInputLock=Lock}`, sets all audio pitch 0, and `LateUpdate` forces `Time.timeScale = 0`. `UnPause()` restores `Time.timeScale = TimeController.timeScale * timeScaleModifier`. Hit-stop and slow-mo are also `Time.timeScale` (`TimeController`), so host sync must tolerate `Time.timeScale == 0` and use unscaled time for transport.
* **GameState** (`GameStateManager.cs`, DDOL): stack of states with `playerInputLock/cameraInputLock/cursorLock/timerModifier/priority`; exposes `PlayerInputLocked`, `CameraLocked`, `CursorLocked`. `EvaluateState` sets `Cursor.lockState`. Register a bridge state to force-lock camera/input (`RegisterState(new GameState("bridge") { cameraInputLock = LockMode.Lock })`; `PopState(key)`).
* **Input**: gameplay reads the **new Input System 1.7.0** through `InputManager.InputSource` (`PlayerInput` -> `InputActions` asset built from JSON; user binds in `<game>/Preferences/Binds.json`; `InputManager.Awake` copies defaults and applies saved bindings; `OnDisable` saves them). Legacy `Input.*` is used only for incidental things (`R` restart in `StatsManager.Update`, scroll wheel, `Home/Tilde/BackQuote` cheat menu `CheatsController.cs:225`, console, intro skip).
* **Background input**: Input System default `InputSettings.backgroundBehavior = ResetAndDisableNonBackgroundDevices` and keyboard/mouse devices are not `canRunInBackground` (**[UNCERTAIN]**, settings asset not readable with the tools used): when the game window is unfocused, the real keyboard/mouse produce no input. Injection plan: create virtual devices and queue state events:
```csharp
var kb = InputSystem.AddDevice<Keyboard>("BridgeKeyboard");   // or a custom layout
var mouse = InputSystem.AddDevice<Mouse>("BridgeMouse");
InputSystem.QueueStateEvent(kb, new KeyboardState(Key.W, Key.Space));        // held keys
InputSystem.QueueStateEvent(mouse, new MouseState { delta = d, buttons = ... });
```
  Because actions are bound to `<Keyboard>/...`/`<Mouse>/...` generically, any keyboard device satisfies them (check `Binds.json`/`InputActions` for explicit device filters, none seen). Set `InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus` and give the layout `"canRunInBackground": true` (**verify**) so the virtual devices are not disabled/reset on focus loss. `InputManager.LastButtonDevice` switches on any button press: `NewMovement.Update` zeroes movement when `LastButtonDevice is Gamepad && gamepadFreezeCount > 0`.
  Alternative that avoids devices entirely: Harmony-patch `InputActionState.ReadValue<T>/IsPressed/WasPerformedThisFrame` (`InputActionState.cs`) or the readers in `PlayerInput`. Prefer devices: weapon scripts use callbacks (`action.started/canceled`) and `Time.frameCount` stamps.
  Look: bypass sensitivity by writing `CameraController.rotationX/rotationY` directly (B.1).
* **Frame rate**: pref `frameRateLimit` (LocalPrefs int, `SetFrameRateLimit`, `GraphicsSettings.cs:305`): 0 = unlimited(-1), 1 = 2x refresh rate, 2 = 30, 3 = 60, 4 = 120, 5 = 144, 6 = 240, 7 = 288; `vSync` (LocalPrefs bool) -> `QualitySettings.vSyncCount`. The game sets them in the Intro scene (`IntroViolenceScreen.Start`) and in graphics settings; override from the plugin (`QualitySettings.vSyncCount = 0; Application.targetFrameRate = N`) after the first scene load. Physics is fixed 125 Hz independent of frame rate. This machine: `frameRateLimit 1, vSync true, fullscreen true, resolution 1920x1080`.
* Prefs access: `PrefsManager` (`PrefsManager.cs`, `MonoSingleton`, persistent): `GetBool/Int/Float/String[Local]`, `Set...`, `onPrefChanged(string key, object value)`. **Two JSON files** in `<game>/Preferences/`: `Prefs.json` (synced) and `LocalPrefs.json` (machine-local: `resolutionWidth/Height`, `fullscreen`, `vSync`, `frameRateLimit`, `mouseSensitivity`, `bloodStain*`, `maxGore`, `colorPalette*`, `disabledComputeShaders`...). Setters mark dirty and flush every 3 s (`CommitMode DirtySlowTick`) and on quit, i.e. **they persist to the user's real prefs**. To change settings without persisting, write the public `prefMap/localPrefMap` dictionaries directly and invoke `PrefsManager.onPrefChanged`, or restore the old values on quit. Defaults table at `PrefsManager.cs:94` (e.g. `fieldOfView 105, mouseSensitivity 50, difficulty 2, hudType 1, pixelization 0, outlineThickness 1, dithering 0.2, vertexWarping 0, screenShake 1, cameraTilt true, weaponHoldPosition 0, bloodEnabled true`). Constants in `ConfigProperties.cs`.
* Audio: Main Camera carries the single `AudioListener`; sounds use `AudioSource.Play(tracked: true)` extension and mixers in `AudioMixerController` (`allSound`, `goreSound`, `musicSound`, `doorSound`, `unfreezeableSound`). To forward audio, tap the listener with `OnAudioFilterRead` on the Main Camera GameObject.

---

## 8. G. HUD elements to hide / use

| Element | Class / object | Notes |
|---|---|---|
| Level stats (time/kills/style/secrets) | `LevelStats`, `LevelStatsEnabler` (`Canvas/Level Stats Controller`), `PauseMenu/Level Stats` | In `uk_construct` `levelNumber == 0` so `LevelStatsEnabler.Start` deactivates itself (`LevelStatsEnabler.cs`). `StatsManager.timer` runs only when `StartTimer()` is called (and displays only in these panels). Held key `HUD.Stats` action toggles them otherwise. |
| Level name popup | `LevelNamePopup` (`Canvas/Level Name Popup`), `OnLevelStart.levelNameOnStart` | Disable GameObject. |
| Navmesh warning | `Canvas/Navmesh Warning` (sandbox) | Disable (we don't use NavMesh). |
| Cheat status overlay | `CheatsController` (`cheatsEnabledPanel`), cheat `ultrakill.debug.hide-cheats-status` (`HideCheatsStatus.HideStatus`) | Shows "CHEATS ENABLED". |
| Boss bars | `BossBarManager` under `Canvas/Boss Healths` | Only for enemies with `BossHealthBar`; proxies never create one unless `ForceBossBars`. |
| Messages/subtitles/tips | `HudMessageReceiver` (`Canvas/MessageHud`), `SubtitleController` | `HudMessageReceiver.Instance.SendHudMessage(...)`. |
| Hellmap, Weapon Wheel, Spawn menu, Pause/Options | `HellMap`, `WeaponWheel`, `SpawnMenu`, `PauseMenu`, `SettingsMenu` | Weapon wheel is a legit UI to capture; others closed by default. |
| Style meter | `StyleHUD` (`HUD Camera/HUD/StyleCanvas`; `StyleHUD.cs`): `rankIndex` (0..7, `rankIndex == 7` also disables hard-damage), `currentRank`, `AddPoints(int points, string pointID, GameObject sourceWeapon = null, EnemyIdentifier eid = null, int count = -1, string prefix = "", string postfix = "")`, `RemovePoints`, `ComboOver()`, `DescendRank()`, `ResetAllFreshness()`, `GetFreshness(GameObject sourceWeapon)`. `StyleCalculator.HitCalculator` feeds it. Visible toggles: prefs `styleMeter`, `styleInfo`, `HudController.SetStyleVisibleTemp()`. |
| HP bar | `HealthBar` (`HealthBar.cs`): `hpSliders`, `afterImageSliders`, `antiHpSlider`, `hpText`; reads `nmov.hp`/`antiHp` every `Update`. Lives under `GunCanvas/StatsPanel/Filler/Panel (2)/Filler` (hudType 1) and `AltHud/Filler/Health*` (hudType 2/3). `FlashImage` (`GreenHPFlash`, `WarningHPFlash`) flash on heal/hard damage. |
| Stamina | `StaminaMeter`; Speedometer `Speedometer` (pref `speedometer` int); railcannon meter `RailcannonMeter`; `PowerUpMeter`; weapon icon `WeaponHUD`/`WeaponIcon`; `HUDPos` positions world-space canvases. |
| Crosshair | `Crosshair` (`Canvas/Crosshair Filler/Crosshair`), pref `crossHair`, `crossHairColor`, `crossHairHud`, `CanvasController.crosshair`. |
| Hurt screen / blood | `NewMovement.hurtScreen` (`Canvas/HurtScreen`), global `_HurtScreenColor`, `ScreenBlood`, `DeathSequence` (`Canvas/DeathSequence`, `BlackScreen`), death effect keyword `DEAD`. |
| Master hide | cheat `ultrakill.hide-ui` => `HideUI.Active` (static) => `HudController.CheckSituation()` disables `GunCanvas`/alt HUDs, style meter, crosshair, power-up meter. Useful as a debug toggle only; we want the HUD captured. |

---

## 9. H. Other notes for the bridge

* **Singleton pattern** (`MonoSingleton.cs`): `MonoSingleton<T>.Instance` (find/auto-create unless `ConfigureSingleton(NoAutoInstance)`), `TryGetInstance(out T)`, `static event Action<T> InstanceChanged` (fires when a new instance becomes current; ideal "scene ready" hook for `NewMovement`, `CameraController`, `PostProcessV2_Handler`, `StatsManager`), `MakeCurrent()`. `[DefaultExecutionOrder(-200)]` on the base. Persistent (DDOL) singletons: `PrefsManager`, `SceneHelper`, `GameStateManager` (plain MonoBehaviour with static `Instance`), `AudioMixerController`... Scene singletons are re-created per scene load: re-attach our hooks on `InstanceChanged`/`SceneManager.sceneLoaded`.
* **Time**: `Time.timeScale` is mutated by pause, hit-stop (`TimeController.HitStop(float)`, `ParryFlash`), `AssistController.gameSpeed`. Don't rely on `Time.time` for transport timing.
* **Rigidbody extension methods** (`Gravity` namespace): `GetGravityDirection`, `GetGravityVector`, `SetCustomGravity`, `SetCustomGravityMode`, `SetGravityMode`. The game supports per-volume gravity (`GravityVolume`); keep default unless intentionally changed.
* **Portals**: ULTRAKILL 2026 has a portal system (`PortalManagerV2`, `PortalPhysicsV2.Raycast/SphereCast` wrappers around `Physics.*`). In `uk_construct` the manager exists but with no portals the wrappers behave like plain physics. `NewMovement` implements `IPortalTraveller`; teleporting by setting `transform.position` is OK (checkpoints do it) but call `PortalManagerV2.Instance.Reset()` equivalent only if portals are used.
* **Difficulty**: pref `difficulty` int 0..4 (0 Harmless, 1 Lenient, 2 Standard, 3 Violent, 4 Brutal; validator in `PrefsManager`); affects starting hp (200 on 0), stamina regen, hard-damage rules (`difficulty >= 2`), enemy stats. This machine: 2.
* **Cursor**: locked by `GameStateManager` "game" state (`StatsManager.Awake` registers it); window focus changes can unlock it.
* **Steam**: `SteamController` exists in each scene (Facepunch.Steamworks); `DiscordController`; saves in `<game>/Saves/Slot1`, prefs in `<game>/Preferences`. The sandbox writes no rank data (`levelNumber 0`).
* **Physics scene duplicates**: `SceneHelper.SetUpFootstepPhysicsScene` creates a second `LocalPhysicsMode.Physics3D` scene `"<scene> - Footsteps"` at each scene load; it can be ignored.
* **Particles**: `SettingsMenu` toggles (`SetSimpleExplosions` ignores layer pairs 23-9, 23-27). Gore is capped by `maxGore`, blood by `bloodStainMax`.
* **Mesh/colliders created at runtime**: set `Mesh.indexFormat = UInt32` for big chunks, keep `isReadable` true (needed for `AcquireReadOnlyMeshData` in wall-jump logic), `MeshCollider.convex=false` for static terrain, `cookingOptions` default.
* **Strings for gameplay logic** worth knowing: hitter names (`"revolver"`, `"shotgun"`, `"nail"`, `"railcannon"`, `"explosion"`, `"punch"`, `"heavypunch"`, `"ground slam"`, `"coin"`, `"fire"`, `"zapper"`, `"deathzone"`, `"enemy"`, `"cannonball"`, `"hammer"`, `"drill"`), parsed by `StyleCalculator`, `Enemy.GetHurt` and weapon-specific code.

---

## 10. Open questions / to verify in-game

1. Alpha channel semantics of ULTRAKILL's object shaders when rendered with a dedicated camera into ARGB32 (B.6.1) and whether soft particles / outline shaders require the MRT/global textures.
2. Whether virtual Input System devices keep working with the window unfocused (`canRunInBackground`, `backgroundBehavior`); InputSettings asset was not readable (B/F).
3. `TargetTracker.RegisterTarget` with a custom `ITarget` proxy for coin auto-targeting (D.6).
4. Meaning of `hudType = 0` and whether `fullscreenMode = 2` is exclusive or borderless on this build.
5. Rendering/vsync behaviour of a minimised or fully occluded ULTRAKILL window under D3D11.
6. Exact in-game "pixel/metre" calibration; the project has no official physical scale. Values in A.1/A.3 are exact; the metre mapping is a design choice.
7. `Mesh.AcquireReadOnlyMeshData` cost profile of the wall-jump path for the chunk sizes chosen for ER terrain.
