using System.Collections.Generic;
using UnityEngine;

namespace UltraRing.Ultrakill.Render
{
    /// <summary>
    /// The cameras and render textures that split V1's image into the host's three layers, each on a transparent
    /// background (docs/research/ultrakill-internals.md B.6):
    ///   world  - Main Camera's view without the (hidden anyway) level: projectiles, explosions, gibs, particles.
    ///   hand   - the viewmodel: HUD Camera's layer 13 (weapons and arms).
    ///   gui    - the world-space HUD (GunCanvas/StyleCanvas/FinishCanvas, moved to layer 3 while bridging, drawn from
    ///            HUD Camera's point of view) and then the screen-space overlay canvas (Player/Canvas, switched to
    ///            ScreenSpaceCamera on our orthographic UI camera) on top.
    /// Every camera is disabled and only draws when <see cref="Render"/> calls Camera.Render(), so the pass happens at a
    /// deterministic point (after CameraController moved the camera). ULTRAKILL's own cameras keep running untouched.
    /// </summary>
    internal sealed class CaptureRig
    {
        /// <summary>Unnamed built-in layer used to separate the world-space HUD canvases from the viewmodel (13) and UI (5).</summary>
        public const int LayerHudWorld = 3;
        public const int LayerUi = 5;
        public const int LayerAlwaysOnTop = 13;

        /// <summary>Layers removed from the world pass: player (2), water (4), level geometry (6,7,8,17,24,25) and the
        /// invisible enemy hitboxes / triggers (10,11,12,16,20).</summary>
        public const int ExcludedWorldLayers = (1 << 2) | (1 << 4) | (1 << 6) | (1 << 7) | (1 << 8) | (1 << 10) | (1 << 11)
                                               | (1 << 12) | (1 << 16) | (1 << 17) | (1 << 20) | (1 << 24) | (1 << 25);

        /// <summary>
        /// Also stop ULTRAKILL's own Main/HUD cameras from drawing anything (cullingMask 0) to save GPU time. Its window is
        /// nearly invisible while bridged. Off by default: PostProcessV2_Handler and its command buffers expect the
        /// cameras to render. Read when a rig is built.
        /// </summary>
        public static bool HideMainRender = false;

        private const int HudRescanFrames = 30;

        public readonly Camera Main, Hud;
        public readonly int Width, Height;
        public Camera WorldCam { get; private set; }
        public Camera ViewCam { get; private set; }
        public Camera UiCam { get; private set; }
        public RenderTexture WorldRT { get; private set; }
        public RenderTexture HandRT { get; private set; }
        public RenderTexture GuiRT { get; private set; }

        private GameObject _root;
        private readonly int _worldMask;
        private readonly int _mainMaskOrig, _hudMaskOrig;
        private readonly bool _maskHidden;

        private Canvas _overlay;
        private RenderMode _overlayMode;
        private Camera _overlayCam;
        private float _overlayPlane;
        private bool _overlaySaved;

        private readonly Dictionary<Transform, int> _movedLayers = new Dictionary<Transform, int>();
        private HudController _lastHud;
        private int _frame;

        /// <summary>Why the rig could not be built (null while it can).</summary>
        public static string LastBuildProblem { get; private set; }

        public static CaptureRig TryCreate(int width, int height)
        {
            CameraController cc = V1.Camera;
            if (cc == null || cc.cam == null) { LastBuildProblem = "no CameraController/Main Camera"; return null; }
            Camera hud = cc.hudCamera;
            if (hud == null)
            {
                Transform t = cc.cam.transform.Find("HUD Camera");
                if (t != null) hud = t.GetComponent<Camera>();
            }
            if (hud == null) { LastBuildProblem = "no HUD Camera"; return null; }
            LastBuildProblem = null;
            return new CaptureRig(cc.cam, hud, width, height);
        }

        private CaptureRig(Camera main, Camera hud, int width, int height)
        {
            Main = main;
            Hud = hud;
            Width = width;
            Height = height;
            _mainMaskOrig = main.cullingMask;
            _hudMaskOrig = hud.cullingMask;
            _worldMask = _mainMaskOrig & ~ExcludedWorldLayers;

            _root = new GameObject("UltraRing Capture Rig");
            _root.AddComponent<BridgeMarker>();

            RenderTextureFormat fmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.BGRA32)
                ? RenderTextureFormat.BGRA32
                : RenderTextureFormat.ARGB32;
            WorldRT = MakeRT("UltraRing world", fmt);
            HandRT = MakeRT("UltraRing hand", fmt);
            GuiRT = MakeRT("UltraRing gui", fmt);

            WorldCam = MakeCam("UltraRing world camera", main.transform);
            ViewCam = MakeCam("UltraRing viewmodel camera", hud.transform.parent != null ? hud.transform.parent : _root.transform);
            UiCam = MakeCam("UltraRing UI camera", _root.transform);

            UiCam.orthographic = true;
            UiCam.orthographicSize = 5f;
            UiCam.nearClipPlane = 0.1f;
            UiCam.farClipPlane = 100f;
            UiCam.cullingMask = 1 << LayerUi;
            UiCam.clearFlags = CameraClearFlags.Depth;   // draws over what the HUD-world pass left in GuiRT
            UiCam.targetTexture = GuiRT;

            if (HideMainRender)
            {
                main.cullingMask = 0;
                hud.cullingMask = 0;
                _maskHidden = true;
            }
        }

        private RenderTexture MakeRT(string name, RenderTextureFormat fmt)
        {
            var rt = new RenderTexture(Width, Height, 24, fmt, RenderTextureReadWrite.Default)
            {
                name = name,
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
            };
            rt.Create();
            return rt;
        }

        private Camera MakeCam(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;                    // only draws when we call Render()
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.depth = -100f;
            cam.renderingPath = RenderingPath.Forward;
            return cam;
        }

        /// <summary>The Main and HUD cameras are still the live ones.</summary>
        public bool Valid
        {
            get
            {
                if (_root == null || Main == null || Hud == null || WorldRT == null) return false;
                CameraController cc = V1.Camera;
                return cc != null && cc.cam == Main;
            }
        }

        /// <summary>Renders world, hand and gui into their render textures from the cameras' current poses.</summary>
        public void Render()
        {
            // ---- world ----
            Transform mt = Main.transform;
            WorldCam.transform.SetPositionAndRotation(mt.position, mt.rotation);
            WorldCam.fieldOfView = Main.fieldOfView;
            WorldCam.nearClipPlane = Main.nearClipPlane;
            WorldCam.farClipPlane = Main.farClipPlane;
            WorldCam.cullingMask = _worldMask;
            WorldCam.targetTexture = WorldRT;
            WorldCam.Render();

            // ---- the HUD-side cameras share HUD Camera's pose, scale (ultrawide fix) and FOV ----
            EnsureHudLayers();
            EnsureOverlay();
            Canvas.ForceUpdateCanvases();   // canvases normally rebuild after LateUpdate, i.e. after this pass

            Transform ht = Hud.transform;
            Transform vt = ViewCam.transform;
            vt.localPosition = ht.localPosition;
            vt.localRotation = ht.localRotation;
            vt.localScale = ht.localScale;
            ViewCam.fieldOfView = Hud.fieldOfView;
            ViewCam.nearClipPlane = Hud.nearClipPlane;
            ViewCam.farClipPlane = Hud.farClipPlane;

            ViewCam.cullingMask = 1 << LayerAlwaysOnTop;
            ViewCam.targetTexture = HandRT;
            ViewCam.Render();

            ViewCam.cullingMask = 1 << LayerHudWorld;
            ViewCam.targetTexture = GuiRT;
            ViewCam.Render();

            UiCam.Render();
        }

        // ---- world-space HUD canvases: layer 13 -> 3 ---------------------------------------------------

        private void EnsureHudLayers()
        {
            HudController hc = HudController.Instance;
            bool rescan = hc != _lastHud || _frame++ % HudRescanFrames == 0;
            if (!rescan || hc == null) return;
            _lastHud = hc;
            foreach (Canvas c in hc.GetComponentsInChildren<Canvas>(true))
            {
                if (c.isRootCanvas) MoveTree(c.transform);
            }
        }

        private void MoveTree(Transform t)
        {
            GameObject go = t.gameObject;
            if (go.layer != LayerHudWorld)
            {
                if (!_movedLayers.ContainsKey(t)) _movedLayers[t] = go.layer;
                go.layer = LayerHudWorld;
            }
            for (int i = 0; i < t.childCount; i++) MoveTree(t.GetChild(i));
        }

        // ---- screen-space overlay canvas ---------------------------------------------------------------

        private void EnsureOverlay()
        {
            if (_overlay == null)
            {
                if (!MonoSingleton<CanvasController>.TryGetInstance(out CanvasController cc) || cc == null) return;
                _overlay = cc.GetComponent<Canvas>();
                if (_overlay == null) return;
            }
            if (!_overlaySaved)
            {
                _overlayMode = _overlay.renderMode;
                _overlayCam = _overlay.worldCamera;
                _overlayPlane = _overlay.planeDistance;
                _overlaySaved = true;
            }
            if (_overlay.renderMode != RenderMode.ScreenSpaceCamera || _overlay.worldCamera != UiCam)
            {
                _overlay.renderMode = RenderMode.ScreenSpaceCamera;
                _overlay.worldCamera = UiCam;
                _overlay.planeDistance = 10f;
            }
        }

        // ---- teardown ----------------------------------------------------------------------------------

        public void Destroy()
        {
            if (_overlay != null && _overlaySaved)
            {
                _overlay.worldCamera = _overlayCam;
                _overlay.renderMode = _overlayMode;
                _overlay.planeDistance = _overlayPlane;
            }
            _overlay = null;
            foreach (KeyValuePair<Transform, int> kv in _movedLayers)
            {
                if (kv.Key != null) kv.Key.gameObject.layer = kv.Value;
            }
            _movedLayers.Clear();
            if (_maskHidden)
            {
                if (Main != null) Main.cullingMask = _mainMaskOrig;
                if (Hud != null) Hud.cullingMask = _hudMaskOrig;
            }
            if (WorldCam != null) WorldCam.targetTexture = null;
            if (ViewCam != null) ViewCam.targetTexture = null;
            if (UiCam != null) UiCam.targetTexture = null;
            if (_root != null) Object.Destroy(_root);   // takes the UI camera with it
            if (WorldCam != null) Object.Destroy(WorldCam.gameObject);
            if (ViewCam != null) Object.Destroy(ViewCam.gameObject);
            _root = null;
            Release(WorldRT); Release(HandRT); Release(GuiRT);
            WorldRT = HandRT = GuiRT = null;
        }

        private static void Release(RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Object.Destroy(rt);
        }
    }
}
