using System;
using System.Collections.Generic;
using System.Text;
using UltraRing.Link;
using UltraRing.Ultrakill.Combat;
using UltraRing.Ultrakill.Interaction;
using UltraRing.Ultrakill.Platform;
using UltraRing.Ultrakill.Render;
using UltraRing.Ultrakill.Terrain;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UltraRing.Ultrakill
{
    /// <summary>
    /// The guest side of the bridge, once per frame after ULTRAKILL's camera has moved:
    /// host liveness, zone anchoring, recalls (V1 moved to where the host put its stand-in), life and damage
    /// both ways, and the control block that makes the host camera and stand-in follow V1.
    /// Follows the start-driving sequence of Minecraft Ring's guest (docs/research/host-contract.md section 4.2).
    /// </summary>
    [DefaultExecutionOrder(30000)]
    internal sealed class BridgeSession : MonoBehaviour
    {
        public static BridgeSession Instance { get; private set; }

        /// <summary>Do not drive until this long after a recall (Minecraft Ring: recallSettled(400)).</summary>
        private const long RecallSettleMs = 400;
        /// <summary>Hold V1 in place after a recall until host terrain is under it, at most this long.</summary>
        private const long GroundWaitMs = 4000;

        public GuestLink Link { get; private set; }
        public CoordMap Map { get; private set; }
        public bool InBridgeScene { get; private set; }
        public bool HostMode { get; private set; }
        public bool Driving { get; private set; }

        private TerrainManager _terrain;
        private EnemyProxyManager _enemies;
        private FrameCapture _capture;
        private WindowOverlay _overlay;
        private HostInteraction _interaction;

        private ErmcGameState _state;
        private bool _alive;
        private ulong _poseId;

        private bool _recallPending;
        private long _recallAtMs = long.MinValue;
        private Vector3 _recallPivot;
        private bool _holdingForGround;
        private GameObject _tempFloor;

        private bool _countersInit;
        private uint _lastHostLife, _lastHostDeaths, _lastSwitchReq;
        private bool _hunterInit;
        private uint _lastHits;
        private float _lastTotalDamage;
        private bool _wasDead;
        private bool _deathFromHost;
        private bool _controlReleased = true;
        private bool _clearedStaleControl;
        private long _hostGoneSinceMs = long.MinValue;
        private bool _everRecalled;
        private Vector3 _prePin;
        private bool _prePinSet;
        private bool _showDebug;
        private float _pendingDamage;
        private long _mapCreatedMs = long.MinValue;
        private readonly HashSet<string> _logged = new HashSet<string>();
        /// <summary>Re-anchor the map when V1 is this far (units) from the Unity origin (float precision).</summary>
        private const float RebaseUnits = 5000f;
        /// <summary>Hostile-entity table may still hold the old zone for a moment after the zone changed.</summary>
        private const long ZoneSettleMs = 300;

        public void Init(GuestLink link)
        {
            Instance = this;
            Link = link;
            var root = new GameObject("UltraRing Bridge Objects");
            root.AddComponent<BridgeMarker>();
            DontDestroyOnLoad(root);
            _terrain = new TerrainManager(root.transform);
            _enemies = new EnemyProxyManager(root.transform);
            _capture = new FrameCapture();
            _overlay = new WindowOverlay();
            _interaction = new HostInteraction();
            _showDebug = BridgeConfig.DebugOverlay.Value;
            SceneManager.sceneLoaded += OnSceneLoaded;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        private void OnProcessExit(object sender, EventArgs e)
        {
            try { ReleaseControl(); } catch { }
        }

        /// <summary>Entry for the scene that was loading when the session was created.</summary>
        public void HandleSceneLoaded(Scene scene, LoadSceneMode mode) => OnSceneLoaded(scene, mode);

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Plugin.Log.LogInfo($"Scene loaded: '{SceneHelper.CurrentScene}' (unity name '{scene.name}', {mode})");
            if (mode != LoadSceneMode.Single) return; // e.g. SceneHelper's additive "<scene> - Footsteps" physics scene
            // Addressable scenes get hashed names; SceneHelper knows the real one (set before the load starts).
            bool bridgeScene = SceneHelper.CurrentScene == LevelShell.SceneName || scene.name == LevelShell.SceneName;
            if (bridgeScene == InBridgeScene && !bridgeScene) return;
            InBridgeScene = bridgeScene;
            ReleaseControl();
            _terrain.Reset();
            _enemies.Clear();
            _capture.Teardown();
            Map = null;
            _pendingDamage = 0f;
            _recallPending = true;
            _everRecalled = false;
            _prePinSet = false;
            if (bridgeScene)
            {
                Plugin.Log.LogInfo("Bridge scene loaded; preparing the shell.");
                StartCoroutine(LevelShell.Prepare(this));
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9)) _showDebug = !_showDebug;
            if (Input.GetKeyDown(KeyCode.F10)) Terrain.TerrainManager.DebugDraw = !Terrain.TerrainManager.DebugDraw;
            if (!InBridgeScene || !_alive) return;
            if (Input.GetKeyDown(BridgeConfig.SwitchKey.Value) && !HostMode) EnterHostMode();
        }

        private void LateUpdate()
        {
            try { LateUpdateCore(); }
            catch (Exception e)
            {
                // Whatever failed, the host must not keep COMPOSITE / MOVE_HUNTER from the last good frame.
                LogOnce("frame", "Bridge frame failed; releasing control: " + e);
                try { ReleaseControl(); _capture.Cancel(); } catch { }
            }
        }

        private void LogOnce(string key, string message)
        {
            if (_logged.Add(key)) Plugin.Log.LogError(message);
        }

        /// <summary>Runs one subsystem; a failure is logged once and does not stop the rest of the frame.</summary>
        private void Guard(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { LogOnce(what, what + " failed (further errors suppressed): " + e); }
        }

        private void LateUpdateCore()
        {
            _alive = Link.Poll();
            Link.BumpGuestHeartbeat();
            if (!_alive)
            {
                // A restarted host may restart its counters from a lower value: baseline again on re-attach.
                _countersInit = false;
                _hunterInit = false;
            }
            bool haveState = _alive && Link.Snapshot(out _state);
            _overlay.Tick(Link, _state, _alive && haveState && InBridgeScene, HostMode, !Driving);
            if (_alive && haveState && !_clearedStaleControl)
            {
                // A previous guest may have died with COMPOSITE set; the host compositor has no timeout. Done on the
                // first successful poll, whatever scene is loaded.
                _clearedStaleControl = true;
                _controlReleased = false;
                ReleaseControl();
            }
            if (!InBridgeScene) return;

            var nm = V1.Movement;
            if (!_alive || !haveState)
            {
                if (Driving) Plugin.Log.LogInfo("Host lost; releasing control.");
                ReleaseControl();
                if (_hostGoneSinceMs == long.MinValue) _hostGoneSinceMs = Link.NowMs;
                else if (Link.NowMs - _hostGoneSinceMs > 2000)
                {
                    _capture.ReleaseRig();
                    if (HostMode) ExitHostMode();
                }
                Guard("pin", () => PinUntilFirstRecall(nm)); // no host: do not free-fall through the removed floors
                return;
            }
            _hostGoneSinceMs = long.MinValue;

            Guard("counters", ReadCounters);
            Guard("anchor", UpdateAnchor);
            Guard("host damage", () => { ApplyHostDamage(); DrainHostDamage(); });
            TrackOwnDeath();

            nm = V1.Movement;
            bool hostAlive = Has(Protocol.StatePlayerValid) && !Has(Protocol.StateHostBusy) && !Has(Protocol.StatePlayerDead);

            if (_recallPending && hostAlive && Map != null && nm != null && (nm.activated || nm.dead))
            {
                try { DoRecall(); }
                catch (Exception e)
                {
                    // Do not retry (and throw) every frame; the next host life change recalls again.
                    _recallPending = false;
                    _recallAtMs = Link.NowMs;
                    _everRecalled = true;
                    LogOnce("recall", "Recall failed: " + e);
                }
            }
            Guard("pin", () => PinUntilFirstRecall(nm));
            Guard("rebase", () => RebaseIfFar(nm));

            if (Map != null && hostAlive && nm != null && !_recallPending)
            {
                Guard("terrain", () => _terrain.Tick(Link, Map, V1.Feet(nm), nm.rb.velocity));
                if (Link.NowMs - _mapCreatedMs >= ZoneSettleMs) Guard("enemies", () => _enemies.Tick(Link, Map));
                Guard("ground hold", () => HoldForGround(nm));
            }

            bool ready = hostAlive && Map != null && Map.Zone == _state.stageId && !_recallPending
                         && Link.NowMs - _recallAtMs >= RecallSettleMs && !_holdingForGround && !HostMode && V1.Ready;
            if (ready) Drive(nm);
            else ReleaseControl();
            Guard("interaction", () => _interaction.Tick(Link, Driving, _terrain, Map, nm != null ? V1.Feet(nm) : Vector3.zero));
            Guard("capture", () => _capture.Tick(Link));
        }

        private bool Has(uint flag) => (_state.flags & flag) != 0;

        // ---- counters: life, deaths, F8 from the host --------------------------------------

        private void ReadCounters()
        {
            uint life = Link.HostLife, deaths = Link.HostDeaths, sw = Link.SwitchRequests;
            if (!_countersInit)
            {
                // Minecraft Ring treats the first hostLife read as a change (recall on attach); deaths and F8
                // only count from now on.
                _countersInit = true;
                _lastHostLife = life;
                _lastHostDeaths = deaths;
                _lastSwitchReq = sw;
                _recallPending = true;
                return;
            }
            if (life != _lastHostLife)
            {
                _lastHostLife = life;
                _recallPending = true;
                Plugin.Log.LogInfo("Host player usable again at a new place; recalling V1.");
            }
            if (deaths != _lastHostDeaths)
            {
                bool increased = deaths > _lastHostDeaths; // a lower value is a restarted counter, not a death
                _lastHostDeaths = deaths;
                var nm = V1.Movement;
                if (increased && nm != null && !nm.dead)
                {
                    Plugin.Log.LogInfo("The stand-in died in the host game; V1 dies too.");
                    nm.GetHurt(99999, false, 0f, ignoreInvincibility: true);
                    _deathFromHost = nm.dead; // GetHurt is a no-op after the level ended or with the invincibility cheat
                }
            }
            if (sw != _lastSwitchReq)
            {
                _lastSwitchReq = sw;
                if (HostMode) ExitHostMode();
            }
        }

        // ---- zone anchoring and recall -----------------------------------------------------

        private void UpdateAnchor()
        {
            uint zone = _state.stageId;
            if (zone == 0 || zone == 0xFFFFFFFFu || !Has(Protocol.StatePlayerValid)) return;
            if (Map != null && Map.Zone == zone) return;
            unsafe
            {
                fixed (float* p = _state.playerPos)
                    Map = new CoordMap(zone, p[0], p[1], p[2], BridgeConfig.MetresPerUnit.Value);
            }
            _mapCreatedMs = Link.NowMs;
            _pendingDamage = 0f;
            Plugin.Log.LogInfo($"Zone {zone:X8}: anchored at host ({Map.AnchorX:F1}, {Map.AnchorY:F1}, {Map.AnchorZ:F1}).");
            ReleaseControl();
            _terrain.Reset();
            _enemies.Clear();
            _recallPending = true;
        }

        /// <summary>
        /// Single-precision physics and meshes jitter far from the origin (open-world zones are km across): move the
        /// anchor to V1 and recall. Terrain is host-metre based, so only the colliders are rebuilt.
        /// </summary>
        private void RebaseIfFar(NewMovement nm)
        {
            if (Map == null || nm == null || _recallPending || HostMode) return;
            if (nm.transform.position.sqrMagnitude < RebaseUnits * RebaseUnits) return;
            Vector3 host = Map.ToHost(V1.Feet(nm));
            Map = new CoordMap(Map.Zone, host.x, host.y, host.z, Map.MetresPerUnit);
            Plugin.Log.LogInfo($"Zone {Map.Zone:X8}: re-anchored at host ({host.x:F1}, {host.y:F1}, {host.z:F1}), V1 was far from the origin.");
            _mapCreatedMs = Link.NowMs;
            ReleaseControl();
            _terrain.Reset();
            _enemies.Clear();
            _recallPending = true;
        }

        private unsafe void DoRecall()
        {
            Vector3 feet;
            float yaw;
            fixed (float* p = _state.playerPos) feet = Map.ToUk(p[0], p[1], p[2]);
            fixed (float* q = _state.playerQuat) yaw = CoordMap.UnityYawFromHostQuat(q[1], q[3]);
            V1.Teleport(feet, yaw, revive: true);
            _recallPivot = V1.Movement.transform.position;
            _recallPending = false;
            _recallAtMs = Link.NowMs;
            _holdingForGround = true;
            _everRecalled = true;
            _terrain.ReseedGroundReference();
            PlaceTempFloor(feet);
            Plugin.Log.LogInfo($"Recall: V1 moved to {feet} (yaw {yaw:F0}).");
        }

        /// <summary>The shell removed every floor: until the host first places V1, keep it from free-falling.</summary>
        private void PinUntilFirstRecall(NewMovement nm)
        {
            if (_everRecalled || nm == null || !nm.activated) return;
            if (!_prePinSet)
            {
                _prePin = nm.transform.position;
                _prePinSet = true;
            }
            nm.rb.velocity = Vector3.zero;
            nm.rb.position = _prePin;
            nm.transform.position = _prePin;
        }

        /// <summary>
        /// After a recall V1 hovers until host terrain under it has been sampled (the first ray batches take a few
        /// frames), standing on a small temporary floor like Minecraft Ring's guest does.
        /// </summary>
        private void HoldForGround(NewMovement nm)
        {
            if (!_holdingForGround) return;
            bool ground = _terrain.HasGroundBelow(V1.Feet(nm), Map.ToUkLength(2f));
            if (ground || Link.NowMs - _recallAtMs > GroundWaitMs)
            {
                _holdingForGround = false;
                if (_tempFloor != null) Destroy(_tempFloor, 1f);
                _tempFloor = null;
                if (!ground) Plugin.Log.LogWarning("No host terrain under V1 after the recall; releasing anyway.");
                return;
            }
            nm.rb.velocity = Vector3.zero;
            nm.rb.position = _recallPivot;
            nm.transform.position = _recallPivot;
        }

        private void PlaceTempFloor(Vector3 feet)
        {
            if (_tempFloor != null) Destroy(_tempFloor);
            _tempFloor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _tempFloor.name = "UltraRing temporary floor";
            _tempFloor.layer = 8; // Environment
            _tempFloor.tag = "Floor";
            _tempFloor.GetComponent<Renderer>().enabled = false;
            _tempFloor.transform.position = feet + Vector3.down * 0.5f;
            _tempFloor.transform.localScale = new Vector3(4f, 1f, 4f);
            _tempFloor.AddComponent<BridgeMarker>();
        }

        // ---- damage and deaths -------------------------------------------------------------

        private void ApplyHostDamage()
        {
            if (!Link.ReadHunterEvents(out ErmcHunterEvents ev)) return;
            if (!_hunterInit || ev.hitCount < _lastHits)
            {
                _hunterInit = true;
                _lastHits = ev.hitCount;
                _lastTotalDamage = ev.totalDamage;
                return;
            }
            if (ev.hitCount == _lastHits) return;
            float hostDamage = ev.totalDamage - _lastTotalDamage;
            _lastHits = ev.hitCount;
            _lastTotalDamage = ev.totalDamage;
            var nm = V1.Movement;
            if (nm == null || nm.dead || !Driving || hostDamage <= 0f) return;
            if (Combat.ParrySystem.TryParry(ev, hostDamage)) return; // punched just in time: parried, no damage
            float share = hostDamage / Mathf.Max(ev.hunterMaxHp, 1f);
            _pendingDamage = Mathf.Min(_pendingDamage + share * 100f * BridgeConfig.HostDamageScale.Value, 100f);
        }

        /// <summary>
        /// Applies accumulated host damage. Hits inside V1's hurt invincibility are kept and land when it ends instead of
        /// being lost; a dash (layer 15 without hurt invincibility) still dodges them like in ULTRAKILL.
        /// </summary>
        private void DrainHostDamage()
        {
            var nm = V1.Movement;
            if (_pendingDamage <= 0f) return;
            if (nm == null || nm.dead || !Driving) { _pendingDamage = 0f; return; }
            if (nm.hurtInvincibility > 0f) return; // wait for the i-frames to run out
            if (nm.gameObject.layer == 15) { _pendingDamage = 0f; return; } // dashing: dodged
            int damage = Mathf.FloorToInt(_pendingDamage);
            if (damage < 1) return; // chip damage accumulates
            _pendingDamage -= damage;
            nm.GetHurt(damage, true);
        }

        private void TrackOwnDeath()
        {
            var nm = V1.Movement;
            bool dead = nm != null && nm.dead;
            if (dead && !_wasDead)
            {
                if (_deathFromHost) Plugin.Log.LogInfo("V1 died with the stand-in.");
                else if (Driving || !_controlReleased)
                {
                    Plugin.Log.LogInfo("V1 died; the stand-in dies in the host game too.");
                    Link.BumpGuestDeaths();
                }
                _deathFromHost = false;
            }
            _wasDead = dead;
        }

        // ---- control -----------------------------------------------------------------------

        private void Drive(NewMovement nm)
        {
            var cam = nm.cc.cam;
            Transform ct = cam.transform;
            Vector3 eye = Map.ToHost(ct.position);
            Vector3 target = Map.ToHost(ct.position + ct.forward);
            Vector3 up = ct.up;
            Vector3 feet = Map.ToHost(V1.Feet(nm));

            var c = new ErmcControl
            {
                flags = Protocol.CtrlOverrideCamera | Protocol.CtrlMoveHunter | Protocol.CtrlHideHunter,
                mcFrame = ++_poseId,
                fovYDeg = cam.fieldOfView,
                poseLag = 1,
                hunterYawDeg = CoordMap.HostYawFromUnity(nm.cc.rotationY),
            };
            if (V1.Grounded(nm)) c.flags |= Protocol.CtrlGrounded;
            unsafe
            {
                c.camPos[0] = eye.x; c.camPos[1] = eye.y; c.camPos[2] = eye.z;
                c.camTarget[0] = target.x; c.camTarget[1] = target.y; c.camTarget[2] = target.z;
                c.camUp[0] = up.x; c.camUp[1] = up.y; c.camUp[2] = up.z;
                c.hunterPos[0] = feet.x; c.hunterPos[1] = feet.y; c.hunterPos[2] = feet.z;
            }

            if (!Driving) Plugin.Log.LogInfo("Driving the host camera and stand-in.");
            Driving = true;
            _controlReleased = false;
            if (BridgeConfig.Composite.Value && _capture.Submit(Link, c, Map)) return; // published with its frame
            Link.WriteControl(ref c);
        }

        /// <summary>One control block with no flags: the host gives its camera and character back and stops compositing.</summary>
        public void ReleaseControl()
        {
            Driving = false;
            if (_controlReleased || Link == null) return;
            var c = new ErmcControl { mcFrame = ++_poseId };
            Link.WriteControl(ref c);
            _capture.Cancel();
            _controlReleased = true;
        }

        private void EnterHostMode()
        {
            HostMode = true;
            ReleaseControl();
            Link.RequestHostFocus();
            _capture.ReleaseRig();
            _overlay.EnterHostMode();
            Plugin.Log.LogInfo("Control -> host game (F8 there to come back).");
        }

        private void ExitHostMode()
        {
            HostMode = false;
            _recallPending = true;
            _overlay.ExitHostMode();
            Plugin.Log.LogInfo("Control -> ULTRAKILL.");
        }

        private void OnApplicationQuit() => Shutdown();

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            Shutdown();
        }

        private void Shutdown()
        {
            try
            {
                ReleaseControl();
                _capture?.Teardown();
            }
            finally
            {
                _overlay?.Restore();
            }
        }

        // ---- diagnostics ------------------------------------------------------------------

        private void OnGUI()
        {
            if (!_showDebug) return;
            var sb = new StringBuilder();
            sb.AppendLine($"UltraRing {Plugin.Version}  host {(_alive ? "alive" : "not running")}  frame {Link.HostFrame}");
            sb.AppendLine($"state flags {_state.flags:X}  zone {_state.stageId:X8}  driving {Driving}  hostMode {HostMode}");
            sb.AppendLine($"recall pending {_recallPending}  holding {_holdingForGround}  map {(Map == null ? "-" : Map.Zone.ToString("X8"))}");
            sb.AppendLine($"terrain: {_terrain.Status}");
            sb.AppendLine($"enemies: {_enemies.Status}");
            sb.AppendLine($"capture: {_capture.Status}");
            sb.AppendLine($"window: {_overlay.Status}");
            sb.AppendLine($"interact: {_interaction.Status}");
            GUI.Label(new Rect(10, 10, 900, 200), sb.ToString());
        }
    }
}
