using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UltraRing.Link;
using UnityEngine;
using UnityEngine.Rendering;

namespace UltraRing.Ultrakill.Render
{
    /// <summary>
    /// Captures V1's world effects, viewmodel and HUD into frames.shm for the host compositor
    /// (docs/research/host-contract.md section 9, memory path).
    ///
    /// Per host frame (at most one capture per hostHeartbeat change, at most <see cref="MaxInFlight"/> pending):
    /// <see cref="CaptureRig"/> renders three transparent-background RGBA targets, three AsyncGPUReadback requests
    /// copy them into native staging buffers, and once all three have arrived the frame is written into a
    /// frames.shm slot (<see cref="GuestFrames"/>) and ONLY THEN the control block for that pose is published
    /// (COMPOSITE set, mcFrame == poseId), so the host never sees a pose whose pixels are missing.
    /// If nothing has to be captured this frame (host frame unchanged / budget exhausted) Submit still returns true and
    /// leaves the previously published control in place (Tick refreshes it so the host's 1 s liveness check passes).
    /// Submit returns false only if capture is unavailable (rig not buildable yet, or disabled after repeated errors);
    /// the session then publishes the control without COMPOSITE.
    /// </summary>
    internal sealed unsafe class FrameCapture
    {
        // ---- tunables (static so they are easy to flip while testing; wire to BridgeConfig if wanted) ----

        /// <summary>Capture size = host window size * Scale (0.1..1), capped to 3840x2160. The host recreates its textures
        /// (expensive) whenever the size changes, so the size only follows the host window.</summary>
        public static float Scale = 1f;

        /// <summary>
        /// Reverse row order while copying into the slot. The host wants bottom-up rows (memory row 0 = bottom scanline).
        /// Unity's RenderTexture readback is bottom row first like Texture2D, so false is expected; flip if the overlay
        /// shows up side down in the host.
        /// </summary>
        public static bool FlipRows = false;

        /// <summary>
        /// CPU alpha repair per layer (see AlphaFix); ULTRAKILL's shaders may write unreliable alpha. World is mostly
        /// emissive/soft effects (MaxRgb keeps alpha that is already right); the hand layer is opaque arms and guns (Opaque).
        /// Use None for a layer once its alpha is verified correct in game (cheapest).
        /// </summary>
        public static AlphaFix WorldAlpha = AlphaFix.MaxRgb;
        public static AlphaFix HandAlpha = AlphaFix.Opaque;
        public static AlphaFix GuiAlpha = AlphaFix.MaxRgb;

        /// <summary>Frames that may be waiting for their readbacks.</summary>
        public const int MaxInFlight = 2;

        private const int MaxW = Protocol.FrameMaxW, MaxH = Protocol.FrameMaxH;
        private const long ResizeStableMs = 600;       // host window size must be stable this long before we follow it
        private const long PendingTimeoutMs = 2000;     // a readback that takes longer counts as an error
        private const long KeepAliveMs = 300;          // re-publish the last control at least this often (host needs < 1 s)
        private const int MaxConsecutiveErrors = 8;
        private const int PoolSize = MaxInFlight + 2;

        private sealed class Job
        {
            public readonly NativeArray<byte>[] Buf = new NativeArray<byte>[3];   // world, hand, gui (BGRA)
            public readonly Action<AsyncGPUReadbackRequest>[] Callbacks = new Action<AsyncGPUReadbackRequest>[3];
            public int Remaining;
            public bool Error, Cancelled, InUse;
            public int W, H;
            public ErmcControl Ctrl;
            public float Near, Far, Fov;
            public long IssuedMs;
        }

        private GuestFrames _frames;
        private CaptureRig _rig;
        private Job[] _pool = new Job[PoolSize];
        private readonly Queue<Job> _queue = new Queue<Job>();
        private bool _async;
        private Texture2D _syncTex;

        private bool _disabled;
        private string _disabledWhy;
        private int _errors;

        private ulong _lastHostFrame;
        private bool _haveHostFrame;
        private ErmcControl _lastControl;
        private bool _haveLast;
        private long _lastControlMs, _lastSubmitMs;

        private int _wantW, _wantH;
        private long _wantSince;

        // statistics
        private int _fpsCount;
        private long _fpsSince;
        private float _fps;
        private ulong _lastFrameId;
        private float _readbackMs, _writeMs;
        private int _skipped;

        public string Status
        {
            get
            {
                if (_disabled) return "disabled: " + _disabledWhy;
                if (_rig == null) return "idle" + (CaptureRig.LastBuildProblem != null ? " (" + CaptureRig.LastBuildProblem + ")" : "");
                return $"{_rig.Width}x{_rig.Height}, {_fps:F0} fps captured, {_queue.Count} in flight, last frame id {_lastFrameId}, " +
                       $"readback {_readbackMs:F1} ms, slot write {_writeMs:F1} ms{(_async ? "" : " (sync)")}";
            }
        }

        /// <summary>
        /// Capture this frame for <paramref name="control"/>'s pose. Returns true if the capture takes ownership of
        /// publishing <paramref name="control"/> (with COMPOSITE set) once the pixels are in a slot; false if the
        /// caller must write the control block itself (no compositing this frame).
        /// </summary>
        public bool Submit(GuestLink link, ErmcControl control, CoordMap map)
        {
            if (_disabled) return false;
            try
            {
                if (!EnsureFrames()) return false;
                long now = link.NowMs;
                if (!EnsureRig(link, now)) return false;
                _lastSubmitMs = now;

                ulong hostFrame = link.HostFrame;
                if (_haveHostFrame && hostFrame == _lastHostFrame) return true;   // one capture per host frame
                if (_queue.Count >= MaxInFlight) { _skipped++; return true; }
                Job job = FreeJob();
                if (job == null) { _skipped++; return true; }
                _lastHostFrame = hostFrame;
                _haveHostFrame = true;

                float mpu = map != null ? map.MetresPerUnit : 1f;
                job.Ctrl = control;
                job.Near = _rig.Main.nearClipPlane * mpu;
                job.Far = _rig.Main.farClipPlane * mpu;
                job.Fov = _rig.Main.fieldOfView;
                job.W = _rig.Width;
                job.H = _rig.Height;
                job.IssuedMs = now;
                job.Error = false;
                job.Cancelled = false;
                job.InUse = true;
                job.Remaining = 3;

                _rig.Render();
                RenderTexture[] rts = { _rig.WorldRT, _rig.HandRT, _rig.GuiRT };
                if (_async)
                {
                    for (int i = 0; i < 3; i++)
                        AsyncGPUReadback.RequestIntoNativeArray(ref job.Buf[i], rts[i], 0, TextureFormat.BGRA32, job.Callbacks[i]);
                }
                else
                {
                    for (int i = 0; i < 3; i++) ReadSync(rts[i], job.Buf[i], job.W, job.H);
                    job.Remaining = 0;
                }
                _queue.Enqueue(job);
                return true;
            }
            catch (Exception e)
            {
                Disable("exception in Submit: " + e);
                return false;
            }
        }

        /// <summary>Every frame: collect finished readbacks and publish them.</summary>
        public void Tick(GuestLink link)
        {
            if (_disabled) return;
            long now = link.NowMs;
            try
            {
                while (_queue.Count > 0)
                {
                    Job j = _queue.Peek();
                    if (j.Remaining == 0)
                    {
                        _queue.Dequeue();
                        if (j.Error) { j.InUse = false; OnError("readback failed"); }
                        else
                        {
                            _readbackMs = Mathf.Lerp(_readbackMs, now - j.IssuedMs, 0.1f);
                            Land(link, j, now);
                            j.InUse = false;
                        }
                    }
                    else if (now - j.IssuedMs > PendingTimeoutMs)
                    {
                        _queue.Dequeue();
                        j.Cancelled = true;     // stays in use until its callbacks fire
                        OnError("readback timed out");
                    }
                    else break;
                }

                // The host drops the guest's control if its seq stalls for 1 s; frames can stall (host paused, slow GPU).
                if (_haveLast && now - _lastControlMs >= KeepAliveMs && now - _lastSubmitMs < 500)
                {
                    ErmcControl c = _lastControl;
                    link.WriteControl(ref c);
                    _lastControl = c;
                    _lastControlMs = now;
                }

                if (now - _fpsSince >= 1000)
                {
                    _fps = _fpsCount * 1000f / Math.Max(1, now - _fpsSince);
                    _fpsCount = 0;
                    _fpsSince = now;
                }
            }
            catch (Exception e)
            {
                Disable("exception in Tick: " + e);
            }
        }

        /// <summary>Drop pending captures (control released).</summary>
        /// <summary>Cancel and give ULTRAKILL its HUD, canvas and layers back (host gone); Submit rebuilds the rig.</summary>
        public void ReleaseRig()
        {
            Cancel();
            DestroyRig();
            _haveHostFrame = false;
        }

        public void Cancel()
        {
            foreach (Job j in _queue)
            {
                j.Cancelled = true;
                if (j.Remaining == 0) j.InUse = false;
            }
            _queue.Clear();
            _haveLast = false;
        }

        public void Teardown()
        {
            Cancel();
            DestroyRig();
            _haveHostFrame = false;
        }

        // ---- setup -------------------------------------------------------------------------------------

        private bool EnsureFrames()
        {
            if (_frames != null) return true;
            try
            {
                _frames = GuestFrames.Open();
                Plugin.Log.LogInfo("frames.shm ready at " + _frames.Path);
                return true;
            }
            catch (Exception e)
            {
                Disable("cannot create frames.shm: " + e.Message);
                return false;
            }
        }

        private bool EnsureRig(GuestLink link, long now)
        {
            if (_rig != null && !_rig.Valid) DestroyRig();      // scene change / cameras recreated

            ComputeSize(link, out int w, out int h);
            if (_rig != null && (_rig.Width != w || _rig.Height != h))
            {
                if (w != _wantW || h != _wantH) { _wantW = w; _wantH = h; _wantSince = now; }
                if (now - _wantSince >= ResizeStableMs) DestroyRig();   // else keep capturing at the old size
            }
            else
            {
                _wantW = w; _wantH = h; _wantSince = now;
            }
            if (_rig != null) return true;

            _rig = CaptureRig.TryCreate(w, h);
            if (_rig == null) return false;
            _async = SystemInfo.supportsAsyncGPUReadback;
            for (int i = 0; i < _pool.Length; i++) _pool[i] = NewJob(w, h);
            _fpsSince = now;
            _fpsCount = 0;
            _haveHostFrame = false;
            Plugin.Log.LogInfo($"Capture rig built: {w}x{h}, readback {(_async ? "async" : "synchronous (async unsupported)")}, format {_rig.WorldRT.format}.");
            return true;
        }

        private Job NewJob(int w, int h)
        {
            var j = new Job { W = w, H = h };
            int bytes = w * h * 4;
            for (int i = 0; i < 3; i++)
            {
                j.Buf[i] = new NativeArray<byte>(bytes, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                j.Callbacks[i] = req =>
                {
                    if (req.hasError) j.Error = true;
                    j.Remaining--;
                    if (j.Remaining <= 0 && j.Cancelled) j.InUse = false;
                };
            }
            return j;
        }

        private Job FreeJob()
        {
            foreach (Job j in _pool)
                if (j != null && !j.InUse) return j;
            return null;
        }

        private void ComputeSize(GuestLink link, out int w, out int h)
        {
            int sw = Screen.width, sh = Screen.height;
            if (link.Snapshot(out ErmcGameState st) && (st.flags & Protocol.StateWindowValid) != 0 && st.winW > 0 && st.winH > 0)
            {
                sw = st.winW;
                sh = st.winH;
            }
            double s = Math.Max(0.1, Math.Min(1.0, Scale));
            double fw = Math.Max(2, sw) * s, fh = Math.Max(2, sh) * s;
            double cap = Math.Min(1.0, Math.Min(MaxW / fw, MaxH / fh));
            w = Math.Max(16, (int)Math.Round(fw * cap));
            h = Math.Max(16, (int)Math.Round(fh * cap));
            if (w > MaxW) w = MaxW;
            if (h > MaxH) h = MaxH;
        }

        private void DestroyRig()
        {
            if (_rig == null && _pool[0] == null) return;
            // Staging buffers must outlive their GPU copies.
            foreach (Job j in _pool) if (j != null) j.Cancelled = true;
            _queue.Clear();
            try { AsyncGPUReadback.WaitAllRequests(); } catch { }
            foreach (Job j in _pool)
            {
                if (j == null) continue;
                for (int i = 0; i < 3; i++) if (j.Buf[i].IsCreated) j.Buf[i].Dispose();
                j.InUse = false;
            }
            for (int i = 0; i < _pool.Length; i++) _pool[i] = null;
            if (_syncTex != null) { UnityEngine.Object.Destroy(_syncTex); _syncTex = null; }
            if (_rig != null) { _rig.Destroy(); _rig = null; }
            _haveLast = false;
        }

        // ---- readback / publish --------------------------------------------------------------------------

        private void ReadSync(RenderTexture rt, NativeArray<byte> dst, int w, int h)
        {
            if (_syncTex == null || _syncTex.width != w || _syncTex.height != h)
            {
                if (_syncTex != null) UnityEngine.Object.Destroy(_syncTex);
                _syncTex = new Texture2D(w, h, TextureFormat.BGRA32, false);
            }
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            _syncTex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            RenderTexture.active = prev;
            NativeArray<byte> raw = _syncTex.GetRawTextureData<byte>();
            Buffer.MemoryCopy(NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(raw), NativeArrayUnsafeUtility.GetUnsafePtr(dst),
                dst.Length, Math.Min(raw.Length, dst.Length));
        }

        private void Land(GuestLink link, Job j, long now)
        {
            var sw = Stopwatch.StartNew();
            int slot = _frames.BeginSlot(j.W, j.H);
            if (slot < 0) { OnError("bad capture size " + j.W + "x" + j.H); return; }
            long bytes = (long)j.W * j.H * 4;
            bool ok =
                _frames.WriteLayer(slot, GuestFrames.LayerWorld, (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(j.Buf[0]), bytes, FlipRows, WorldAlpha) &&
                _frames.WriteLayer(slot, GuestFrames.LayerHand, (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(j.Buf[1]), bytes, FlipRows, HandAlpha) &&
                _frames.WriteLayer(slot, GuestFrames.LayerGui, (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(j.Buf[2]), bytes, FlipRows, GuiAlpha);
            if (!ok) { _frames.Abort(slot); OnError("layer copy failed"); return; }
            _frames.FillDepth(slot, 1f);   // v0.1: no depth, everything drawn unoccluded and unlit
            float aspect = (float)j.W / j.H;
            _lastFrameId = _frames.Publish(slot, j.Ctrl.mcFrame, Protocol.FrameWorld | Protocol.FrameGui | Protocol.FrameHand,
                j.Near, j.Far, j.Fov, aspect);
            _writeMs = Mathf.Lerp(_writeMs, (float)sw.Elapsed.TotalMilliseconds, 0.1f);

            // The pixels are in a slot: only now may the host see this pose.
            ErmcControl c = j.Ctrl;
            c.flags |= Protocol.CtrlComposite;
            link.WriteControl(ref c);
            _lastControl = c;
            _haveLast = true;
            _lastControlMs = now;
            _errors = 0;
            _fpsCount++;
        }

        private void OnError(string why)
        {
            _errors++;
            if (_errors == 1) Plugin.Log.LogWarning("Frame capture: " + why);
            if (_errors >= MaxConsecutiveErrors) Disable(why + " (" + _errors + " times in a row)");
        }

        private void Disable(string why)
        {
            if (_disabled) return;
            _disabled = true;
            _disabledWhy = why;
            Plugin.Log.LogError("Frame capture disabled for this session: " + why);
            try { Teardown(); } catch { }
        }
    }
}
