using System;
using System.Text;
using UltraRing.Link;
using UnityEngine;
using static UltraRing.Ultrakill.Platform.NativeMethods;

namespace UltraRing.Ultrakill.Platform
{
    /// <summary>How the input window stays out of the player's sight while owning keyboard and mouse.</summary>
    internal enum OverlayWindowMode
    {
        /// <summary>Full-size window, WS_EX_LAYERED with constant alpha 1/255 (what Minecraft Ring does with GLFW).</summary>
        Layered,
        /// <summary>Full-size window (full render resolution) clipped with SetWindowRgn to a single pixel. No layering.</summary>
        Region,
        /// <summary>The window itself is 1x1 at the host client's bottom-right corner (Unity renders at 1x1).</summary>
        Tiny,
    }

    /// <summary>
    /// Keeps ULTRAKILL's window borderless, nearly transparent and glued above the host window so it receives
    /// keyboard and mouse while the player sees the host game (which composites ULTRAKILL's frames).
    /// Follows Minecraft Ring's WindowsOverlay/Overlay: the window is owned by the host window (GWLP_HWNDPARENT), so it
    /// stays above it without being topmost; constant opacity 1/255; focus only after showing / after F8 back.
    /// Main thread only; every failure is logged once and never thrown.
    /// </summary>
    internal sealed class WindowOverlay
    {
        /// <summary>Leave this many pixels at the bottom uncovered so DWM keeps compositing (no independent flip / opaque fullscreen).</summary>
        private const int ReserveBottomPixel = 1;
        private const long ScanMs = 1000, VerifyMs = 250, HostLostGraceMs = 1500, SettleMs = 600;
        private const long FocusRetryMs = 350;
        private const int MaxFocusAttempts = 6;
        private const long RelevantStyleMask = BorderStyles | WS_CHILD;

        // ---- windows
        private IntPtr _hwnd, _host;
        private int _hostPid;
        private long _nextUnityScan, _nextHostScan, _nextVerify;
        private bool _hwndLostLogged;

        // ---- original state (restored when the host goes away or on shutdown)
        private long _origStyle, _origEx;
        private IntPtr _origOwner;
        private RECT _origRect;
        private bool _origLayered;
        private byte _origAlpha = 255;

        // ---- overlay state
        private bool _applied;
        private bool _hidden;
        private bool _hostWasAvailable;
        private long _lastAvailableMs, _lastNowMs;
        private OverlayWindowMode _mode;
        private bool _modeInit;
        private RECT _wantRect;            // what we last asked the window to be
        private bool _haveWantRect;
        private long _rectChangedMs;
        private int _regionW, _regionH;
        private int _alpha = -1;
        private bool _layeredOk = true;
        private bool _fullscreenMarked;
        private long _nextTaskbarMs;
        private bool _taskbarFailLogged;
        private int _mismatches;
        private int _reapplyCount;
        private long _reapplyWindowStart, _backoffUntil;
        private bool _backoffLogged;

        // ---- resolution
        private int _resTargetW, _resTargetH, _resTries;
        private long _nextResMs;
        private bool _resGaveUp;

        // ---- focus
        private bool _focusPending;
        private bool _focusNeedsDriving;
        private int _focusAttempts;
        private long _nextFocusMs;
        private bool _wasDrawNothing = true;
        private bool _everDrove;
        private bool _lastFocused;
        private string _focusNote = "-";

        // ---- cursor
        private int _cursorFixes;

        // ---- diagnostics
        private bool _failed;
        private int _errors;
        private string _note = "idle";

        /// <summary>True while the overlay styles / owner are applied to ULTRAKILL's window.</summary>
        public bool OverlayActive => _applied;

        public string Status => BuildStatus();

        // ======================================================================================
        // Public API (called by BridgeSession)
        // ======================================================================================

        public void Tick(GuestLink link, in ErmcGameState state, bool hostAvailable, bool hostMode, bool drawNothing)
        {
            if (_failed) return;
            try
            {
                TickCore(link, in state, hostAvailable, hostMode, drawNothing);
            }
            catch (Exception e)
            {
                _errors++;
                LogOnce("tick:" + e.GetType().Name, $"Window overlay error ({_errors}): {e}");
                if (_errors >= 25)
                {
                    _failed = true;
                    Plugin.Log.LogError("Window overlay disabled after repeated errors.");
                    SafeRestore("too many errors");
                }
            }
        }

        /// <summary>F8 pressed in ULTRAKILL: hide our window so the host game gets the keyboard and mouse.</summary>
        public void EnterHostMode()
        {
            try
            {
                _hidden = true;
                _focusPending = false;
                if (_applied && IsWindowOk())
                {
                    if (_fullscreenMarked) MarkFullscreen(false);
                    // Hiding an owned window can immediately focus the owner; the session already bumped hostFocusReq.
                    ShowWindow(_hwnd, SW_HIDE);
                    _note = "host mode: window hidden";
                }
            }
            catch (Exception e) { LogOnce("enter", "EnterHostMode failed: " + e.Message); }
        }

        /// <summary>The host's F8 (mcSwitchReq): show our window above the host again and take the focus.</summary>
        public void ExitHostMode()
        {
            try
            {
                _hidden = false;
                if (_applied && IsWindowOk())
                {
                    PrepareHost();
                    ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
                    _haveWantRect = false;           // force geometry to be re-applied on the next Tick
                    _alpha = -1;
                    _note = "host mode over: window shown";
                }
                RequestFocus(needsDriving: false);
                TryFocus(_lastNowMs, drawNothing: true, force: true);
            }
            catch (Exception e) { LogOnce("exit", "ExitHostMode failed: " + e.Message); }
        }

        /// <summary>Undo every window change (shutdown).</summary>
        public void Restore() => SafeRestore("shutdown");

        // ======================================================================================
        // Per-frame logic
        // ======================================================================================

        private void TickCore(GuestLink link, in ErmcGameState state, bool hostAvailable, bool hostMode, bool drawNothing)
        {
            if (BridgeConfig.InputOverlay == null || !BridgeConfig.InputOverlay.Value)
            {
                if (_applied) RestoreNormal("InputOverlay disabled");
                _note = "disabled";
                return;
            }

            long now = link.NowMs;
            _lastNowMs = now;
            if (!EnsureUnityWindow(now)) return;
            EnsureMode();

            int hostPid = link.HostProcessId;
            bool hostPresent = EnsureHostWindow(now, hostPid);
            if (hostAvailable) _lastAvailableMs = now;

            // Apply only once the host is really up; once applied, keep going while its window exists
            // (loading screens stop the heartbeat for seconds).
            bool want = hostPresent && (hostAvailable || _applied);
            if (!want)
            {
                if (_applied) RestoreNormal(hostPresent ? "host unavailable" : "host window gone");
                _hostWasAvailable = false;
                if (!_applied) _note = hostPid == 0 ? "no host" : hostPresent ? "waiting for host heartbeat" : "no host window";
                return;
            }
            if (!_hostWasAvailable) { _hostWasAvailable = true; }

            if (!_applied)
            {
                if (hostMode) return; // do not take over the window while the host has control
                if (!ApplyOverlay(now)) return;
                RequestFocus(needsDriving: !_everDrove);
            }

            // Host mode: stay hidden (BridgeSession also calls EnterHostMode; this covers a missed call).
            if (hostMode)
            {
                if (!_hidden) EnterHostMode();
                _wasDrawNothing = true;
                return;
            }
            if (_hidden)
            {
                ExitHostMode();
                return;
            }

            // Driving started (again): take the focus back if it was lost to the host (a click passed through while we drew nothing).
            if (_wasDrawNothing && !drawNothing)
            {
                _everDrove = true;
                IntPtr fg = GetForegroundWindow();
                if (fg == _hwnd) _focusPending = false;
                else if (fg == IntPtr.Zero || ProcessIdOf(fg) == (uint)_hostPid) RequestFocus(needsDriving: true);
                else _focusPending = false; // the user is in another application: never steal the focus from it
            }
            _wasDrawNothing = drawNothing;

            UpdateAlpha(drawNothing);
            UpdateGeometry(now, in state, hostAvailable);
            if (now >= _nextVerify)
            {
                _nextVerify = now + VerifyMs;
                Verify(now, drawNothing);
            }
            UpdateResolution(now);
            TryFocus(now, drawNothing, force: false);
        }

        // ---------------------------------------------------------------- window discovery

        private bool EnsureUnityWindow(long now)
        {
            if (_hwnd != IntPtr.Zero && IsWindow(_hwnd)) return true;
            if (_hwnd != IntPtr.Zero)
            {
                // Destroyed under us (for example the owner went away). Nothing left to manage.
                if (!_hwndLostLogged) Plugin.Log.LogWarning("ULTRAKILL's window was destroyed.");
                _hwndLostLogged = true;
                _hwnd = IntPtr.Zero;
                _applied = false;
                _fullscreenMarked = false;
                _haveWantRect = false;
            }
            if (now < _nextUnityScan) return false;
            _nextUnityScan = now + ScanMs;
            _hwnd = WindowFinder.FindUnityWindow();
            if (_hwnd == IntPtr.Zero)
            {
                _note = "no ULTRAKILL window yet";
                return false;
            }
            _hwndLostLogged = false;
            Plugin.Log.LogInfo($"ULTRAKILL window: 0x{_hwnd.ToInt64():X}");
            return true;
        }

        private bool EnsureHostWindow(long now, int hostPid)
        {
            if (hostPid != _hostPid)
            {
                _hostPid = hostPid;
                _host = IntPtr.Zero;
                _nextHostScan = 0;
            }
            if (_host != IntPtr.Zero && !WindowFinder.IsHostWindowStillValid(_host, hostPid))
            {
                _host = IntPtr.Zero;
                _nextHostScan = 0;
            }
            if (_host == IntPtr.Zero && hostPid > 0 && now >= _nextHostScan)
            {
                _nextHostScan = now + ScanMs;
                _host = WindowFinder.FindHostWindow(hostPid);
                if (_host != IntPtr.Zero) Plugin.Log.LogInfo($"Host window: 0x{_host.ToInt64():X} (pid {hostPid})");
            }
            return _host != IntPtr.Zero;
        }

        private void EnsureMode()
        {
            if (_modeInit) return;
            _modeInit = true;
            string name = Environment.GetEnvironmentVariable("ULTRARING_WINDOW_MODE");
            if (string.IsNullOrEmpty(name) && BridgeConfig.WindowMode != null) name = BridgeConfig.WindowMode.Value;
            if (!Enum.TryParse(name ?? "", true, out _mode)) _mode = OverlayWindowMode.Layered;
            Plugin.Log.LogInfo($"Window overlay mode: {_mode}");
        }

        // ---------------------------------------------------------------- applying / restoring

        /// <summary>Make the window borderless, owned by the host, layered (or clipped). Returns false if it could not be done yet.</summary>
        private bool ApplyOverlay(long now)
        {
            if (!IsWindowOk()) return false;
            if (IsIconic(_hwnd)) { _note = "ULTRAKILL window minimised; waiting"; return false; }

            // Snapshot for Restore (only when going from normal to overlay).
            _origStyle = GetStyle(_hwnd);
            _origEx = GetExStyle(_hwnd);
            _origOwner = GetOwner(_hwnd);
            GetWindowRect(_hwnd, out _origRect);
            _origLayered = (_origEx & WS_EX_LAYERED) != 0;
            _origAlpha = 255;
            if (_origLayered && GetLayeredWindowAttributes(_hwnd, out _, out byte a, out uint f) && (f & LWA_ALPHA) != 0) _origAlpha = a;

            PrepareHost();
            if (!ApplyStylesAndOwner()) return false;
            _applied = true;
            _haveWantRect = false;
            _alpha = -1;
            _resTries = 0;
            _resGaveUp = false;
            _mismatches = 0;
            _note = "overlay applied";
            Plugin.Log.LogInfo($"Overlay ON: {_mode}, owner host 0x{_host.ToInt64():X}; original {_origRect}, style 0x{_origStyle:X}, ex 0x{_origEx:X}.");
            return true;
        }

        private bool ApplyStylesAndOwner()
        {
            long style = (GetStyle(_hwnd) & ~RelevantStyleMask) | WS_POPUP | WS_VISIBLE;
            long ex = (GetExStyle(_hwnd) & ~(WS_EX_TOOLWINDOW | WS_EX_TOPMOST)) | WS_EX_APPWINDOW; // own taskbar button
            bool layered = _mode == OverlayWindowMode.Layered && _layeredOk;
            if (layered) ex |= WS_EX_LAYERED;
            else if (!_origLayered) ex &= ~WS_EX_LAYERED;

            if (GetOwner(_hwnd) != _host)
            {
                if (!SetLong(_hwnd, GWLP_HWNDPARENT, _host.ToInt64(), out int err))
                {
                    LogOnce("owner", $"Could not set the host window as owner (Win32 error {err}); the overlay would not stay above the host.");
                    _note = "owner failed";
                    return false;
                }
            }
            SetLong(_hwnd, GWL_STYLE, style, out _);
            SetLong(_hwnd, GWL_EXSTYLE, ex, out _);
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

            // Win11: no rounded corners / accent border / show-hide animation on our window.
            SetDwm(33 /* WINDOW_CORNER_PREFERENCE */, 1 /* DONOTROUND */);
            SetDwm(34 /* BORDER_COLOR */, unchecked((int)0xFFFFFFFE) /* NONE */);
            SetDwm(3 /* TRANSITIONS_FORCEDISABLED */, 1);

            _alpha = -1; // re-apply opacity
            return true;
        }

        private void SetDwm(int attr, int value)
        {
            try { DwmSetWindowAttribute(_hwnd, attr, ref value, sizeof(int)); }
            catch (Exception) { }
        }

        private void RestoreNormal(string reason)
        {
            if (!_applied) return;
            try
            {
                if (IsWindowOk())
                {
                    if (_fullscreenMarked) MarkFullscreen(false);
                    SetWindowRgn(_hwnd, IntPtr.Zero, true);
                    _regionW = _regionH = 0;
                    if (_origLayered) SetLayeredWindowAttributes(_hwnd, 0, _origAlpha, LWA_ALPHA);
                    SetLong(_hwnd, GWLP_HWNDPARENT, _origOwner.ToInt64(), out _);
                    SetLong(_hwnd, GWL_STYLE, _origStyle, out _);
                    SetLong(_hwnd, GWL_EXSTYLE, _origEx, out _);
                    if (_origRect.Width > 0 && _origRect.Height > 0)
                    {
                        SetWindowPos(_hwnd, IntPtr.Zero, _origRect.Left, _origRect.Top, _origRect.Width, _origRect.Height,
                            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                    }
                    else
                    {
                        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                    }
                    if (_hidden || !IsWindowVisible(_hwnd)) ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
                }
                Plugin.Log.LogInfo($"Overlay OFF ({reason}); normal window restored.");
            }
            catch (Exception e)
            {
                LogOnce("restore", "Restoring the window failed: " + e.Message);
            }
            _applied = false;
            _hidden = false;
            _haveWantRect = false;
            _fullscreenMarked = false;
            _focusPending = false;
            _alpha = -1;
            _wasDrawNothing = true;
            _note = "normal window (" + reason + ")";
        }

        private void SafeRestore(string reason)
        {
            try { RestoreNormal(reason); }
            catch (Exception) { }
        }

        // ---------------------------------------------------------------- opacity

        private void UpdateAlpha(bool drawNothing)
        {
            if (_mode != OverlayWindowMode.Layered || !_layeredOk) return;
            // 1/255 while ULTRAKILL is shown; fully transparent (clicks fall through to the host) while it draws nothing.
            int want = drawNothing ? 0 : 1;
            if (want == _alpha) return;
            if (!SetLayeredWindowAttributes(_hwnd, 0, (byte)want, LWA_ALPHA))
            {
                OnLayeredFailed("SetLayeredWindowAttributes failed, Win32 error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                return;
            }
            if (!GetLayeredWindowAttributes(_hwnd, out _, out byte got, out uint flags) || (flags & LWA_ALPHA) == 0 || got != want)
            {
                OnLayeredFailed($"GetLayeredWindowAttributes disagrees (alpha {got}, flags {flags}, wanted {want})");
                return;
            }
            SetClickThrough(drawNothing);
            if (_alpha < 0) Plugin.Log.LogInfo($"Layered window: constant alpha {want}/255 accepted by Windows.");
            _alpha = want;
        }

        /// <summary>
        /// Alpha 0 alone does not reliably pass mouse input through a layered window; WS_EX_TRANSPARENT does.
        /// On while ULTRAKILL draws nothing (host busy, V1 not driving), off while V1 is played.
        /// </summary>
        private void SetClickThrough(bool on)
        {
            long ex = GetExStyle(_hwnd);
            long want = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
            if (want != ex) SetLong(_hwnd, GWL_EXSTYLE, want, out _);
        }

        private void OnLayeredFailed(string why)
        {
            Plugin.Log.LogWarning($"Layered mode unusable: {why}. Falling back to Region mode (full-size window clipped to 1 pixel).");
            _layeredOk = false;
            _mode = OverlayWindowMode.Region;
            _haveWantRect = false;
            _alpha = -1;
            if (IsWindowOk() && !_origLayered)
            {
                SetLong(_hwnd, GWL_EXSTYLE, GetExStyle(_hwnd) & ~WS_EX_LAYERED, out _);
                SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
        }

        // ---------------------------------------------------------------- geometry

        /// <summary>Host client area (screen coordinates) from the shared state, else asked from the window directly.</summary>
        private bool HostClientRect(in ErmcGameState state, bool hostAvailable, out RECT r)
        {
            r = default;
            if (hostAvailable && (state.flags & Protocol.StateWindowValid) != 0 && state.winW >= 64 && state.winH >= 64)
            {
                r = new RECT { Left = state.winX, Top = state.winY, Right = state.winX + state.winW, Bottom = state.winY + state.winH };
                return true;
            }
            if (_host != IntPtr.Zero && !IsIconic(_host) && GetClientScreenRect(_host, out r) && r.Width >= 64 && r.Height >= 64) return true;
            return false;
        }

        private void UpdateGeometry(long now, in ErmcGameState state, bool hostAvailable)
        {
            if (!HostClientRect(in state, hostAvailable, out RECT c)) return;
            if (c.Left <= -30000 || c.Top <= -30000) return; // minimised host

            RECT want;
            if (_mode == OverlayWindowMode.Tiny)
                want = new RECT { Left = c.Right - 1, Top = c.Bottom - 1, Right = c.Right, Bottom = c.Bottom };
            else
                want = new RECT { Left = c.Left, Top = c.Top, Right = c.Right, Bottom = Math.Max(c.Top + 1, c.Bottom - ReserveBottomPixel) };

            if (_haveWantRect && Same(want, _wantRect) && !_hidden) return;
            ApplyRect(want, now);
        }

        private void ApplyRect(RECT want, long now)
        {
            if (!IsWindowOk()) return;
            if (!SetWindowPos(_hwnd, IntPtr.Zero, want.Left, want.Top, want.Width, want.Height, SWP_NOZORDER | SWP_NOACTIVATE))
                LogOnce("setpos", "SetWindowPos failed, Win32 error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            if (!_haveWantRect || !Same(want, _wantRect)) _rectChangedMs = now;
            _wantRect = want;
            _haveWantRect = true;
            UpdateRegion(want);
            UpdateTaskbarMark(now, force: true);
            LogOnce("rect:" + want, $"Overlay glued to host client area {want} (mode {_mode}).");
        }

        private void UpdateRegion(RECT want)
        {
            if (_mode != OverlayWindowMode.Region) return;
            int w = want.Width, h = want.Height;
            if (w == _regionW && h == _regionH) return;
            IntPtr rgn = CreateRectRgn(Math.Max(0, w - 1), Math.Max(0, h - 1), w, h);
            if (rgn == IntPtr.Zero) return;
            // The system owns the region after a successful call.
            if (SetWindowRgn(_hwnd, rgn, true) == 0) DeleteObject(rgn);
            _regionW = w;
            _regionH = h;
        }

        private static bool Same(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

        // ---------------------------------------------------------------- verification / cursor / taskbar

        private void Verify(long now, bool drawNothing)
        {
            if (!IsWindowOk() || !_haveWantRect) return;
            bool bad = false;
            long style = GetStyle(_hwnd), ex = GetExStyle(_hwnd);
            if ((style & BorderStyles) != 0) bad = true;
            if (GetOwner(_hwnd) != _host) bad = true;
            if (_mode == OverlayWindowMode.Layered && _layeredOk && (ex & WS_EX_LAYERED) == 0) bad = true;
            if (GetWindowRect(_hwnd, out RECT r) && !Same(r, _wantRect) && now - _rectChangedMs > SettleMs) bad = true;

            if (bad)
            {
                if (++_mismatches >= 2 && now >= _backoffUntil)
                {
                    _mismatches = 0;
                    if (now - _reapplyWindowStart > 10000) { _reapplyWindowStart = now; _reapplyCount = 0; }
                    if (++_reapplyCount > 10)
                    {
                        _backoffUntil = now + 5000;
                        _reapplyCount = 0;
                        if (!_backoffLogged)
                        {
                            Plugin.Log.LogWarning("Something keeps changing ULTRAKILL's window style/rect; backing off for 5 s.");
                            _backoffLogged = true;
                        }
                    }
                    else
                    {
                        LogOnce("reapply" + (_reapplyCount < 3 ? _reapplyCount.ToString() : ""), $"Window drifted (style 0x{style:X}, rect {r}); re-applying overlay.");
                        ApplyStylesAndOwner();
                        _haveWantRect = false;
                    }
                }
            }
            else
            {
                _mismatches = 0;
            }

            bool focused = GetForegroundWindow() == _hwnd;
            _lastFocused = focused;
            if (focused && !drawNothing && !_hidden) KeepCursorInside();
            UpdateTaskbarMark(now, force: false);
        }

        /// <summary>
        /// Unity clips and re-centres the cursor itself when it is locked and focused. If the window moved after the lock,
        /// the cursor can end up outside it: put it back in the middle (4 Hz at most). Counted in Status.
        /// </summary>
        private void KeepCursorInside()
        {
            if (Cursor.lockState == CursorLockMode.None) return;
            if (!GetCursorPos(out POINT p) || !GetWindowRect(_hwnd, out RECT r)) return;
            if (p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom) return;
            SetCursorPos(r.Left + r.Width / 2, r.Top + r.Height / 2);
            _cursorFixes++;
            if (_cursorFixes == 1) Plugin.Log.LogInfo("Cursor was outside the locked ULTRAKILL window; moved it back inside.");
        }

        private void UpdateTaskbarMark(long now, bool force)
        {
            if (_hidden || _mode == OverlayWindowMode.Tiny) return;
            if (now < _nextTaskbarMs && !force) return;
            bool covers = CoversMonitor();
            if (covers == _fullscreenMarked) return;
            _nextTaskbarMs = now + 1000;
            MarkFullscreen(covers);
        }

        private void MarkFullscreen(bool on)
        {
            string err = TaskbarFullscreen.Mark(_hwnd, on);
            if (err == null) { _fullscreenMarked = on; return; }
            if (!_taskbarFailLogged) Plugin.Log.LogWarning("Could not tell Explorer the overlay is fullscreen: " + err);
            _taskbarFailLogged = true;
        }

        private bool CoversMonitor()
        {
            try
            {
                var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO)) };
                IntPtr mon = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
                if (mon == IntPtr.Zero || !GetMonitorInfoW(mon, ref mi)) return false;
                if (!GetWindowRect(_hwnd, out RECT r)) return false;
                RECT s = mi.rcMonitor;
                return r.Left <= s.Left && r.Top <= s.Top && r.Right >= s.Right && r.Bottom >= s.Bottom - ReserveBottomPixel;
            }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------- resolution

        /// <summary>
        /// Unity follows an externally resized window by itself; this only intervenes if the render size still differs
        /// after a settle time (frame capture uses ULTRAKILL's resolution, which must match the host aspect).
        /// </summary>
        private void UpdateResolution(long now)
        {
            if (_mode == OverlayWindowMode.Tiny || !_haveWantRect || _resGaveUp) return;
            if (now < _nextResMs || now - _rectChangedMs < SettleMs) return;
            int w = _wantRect.Width, h = _wantRect.Height;
            if (w != _resTargetW || h != _resTargetH) { _resTargetW = w; _resTargetH = h; _resTries = 0; }
            if (Screen.width == w && Screen.height == h) return;
            if (_resTries >= 3)
            {
                _resGaveUp = true;
                Plugin.Log.LogWarning($"Unity render size {Screen.width}x{Screen.height} would not match the window {w}x{h}; giving up.");
                return;
            }
            _resTries++;
            _nextResMs = now + 1500;
            Plugin.Log.LogInfo($"Render size {Screen.width}x{Screen.height} -> {w}x{h} (windowed).");
            Screen.SetResolution(w, h, FullScreenMode.Windowed);
            _rectChangedMs = now; // let Unity finish before verifying the rect again
        }

        // ---------------------------------------------------------------- focus

        private void RequestFocus(bool needsDriving)
        {
            _focusPending = true;
            _focusNeedsDriving = needsDriving;
            _focusAttempts = 0;
            _nextFocusMs = 0;
        }

        /// <summary>
        /// Bring our window to the foreground, a bounded number of times per request. The host is the foreground
        /// process at that moment (F8 was pressed in it), so SetForegroundWindow alone may be refused: attach to the
        /// foreground thread's input queue first, ALT-tap as a last resort. Never retried once it worked.
        /// </summary>
        private void TryFocus(long now, bool drawNothing, bool force)
        {
            if (!_focusPending || _hidden || !_applied) return;
            if (!IsWindowOk()) return;
            if (GetForegroundWindow() == _hwnd)
            {
                _focusPending = false;
                _focusNote = "focused";
                return;
            }
            if (_focusNeedsDriving && drawNothing) return;
            if (!force && now < _nextFocusMs) return;
            if (!IsWindowVisible(_hwnd)) ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            _nextFocusMs = now + FocusRetryMs;
            _focusAttempts++;
            bool ok = ForceForeground(_hwnd, useAltTap: _focusAttempts >= 3);
            if (ok)
            {
                _focusPending = false;
                _focusNote = $"focused after {_focusAttempts} attempt(s)";
                Plugin.Log.LogInfo("ULTRAKILL window has the focus.");
            }
            else if (_focusAttempts >= MaxFocusAttempts)
            {
                _focusPending = false;
                _focusNote = "focus refused by Windows";
                Plugin.Log.LogWarning("Windows refused to give ULTRAKILL's window the focus; click it once.");
            }
        }

        private static bool ForceForeground(IntPtr hwnd, bool useAltTap)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == hwnd) return true;
            uint me = GetCurrentThreadId();
            uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                SetFocus(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(me, fgThread, false);
            }
            if (GetForegroundWindow() == hwnd) return true;
            if (useAltTap)
            {
                // A synthetic Alt press unlocks SetForegroundWindow for the calling process.
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                SetForegroundWindow(hwnd);
            }
            return GetForegroundWindow() == hwnd;
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>A minimised host would hide its owned window: restore it without activating (once per activation).</summary>
        private void PrepareHost()
        {
            if (_host != IntPtr.Zero && IsWindow(_host) && IsIconic(_host)) ShowWindow(_host, SW_SHOWNOACTIVATE);
        }

        private bool IsWindowOk() => _hwnd != IntPtr.Zero && IsWindow(_hwnd);

        private readonly System.Collections.Generic.HashSet<string> _logged = new System.Collections.Generic.HashSet<string>();

        private void LogOnce(string key, string message)
        {
            if (_logged.Count > 200 || !_logged.Add(key)) return;
            Plugin.Log.LogInfo(message);
        }

        private string BuildStatus()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(_failed ? "FAILED" : _applied ? (_hidden ? "hidden (host mode)" : "overlay") : "normal");
                sb.Append(' ').Append(_mode);
                if (_applied && IsWindowOk() && GetWindowRect(_hwnd, out RECT r))
                {
                    sb.Append(" rect ").Append(r);
                    sb.Append(" alpha ").Append(_mode == OverlayWindowMode.Layered ? _alpha.ToString() : "-");
                    sb.Append(" owner ").Append(GetOwner(_hwnd) == _host && _host != IntPtr.Zero ? "host" : "other");
                    sb.Append(" fg ").Append(GetForegroundWindow() == _hwnd ? "ours" : "other");
                    sb.Append(" render ").Append(Screen.width).Append('x').Append(Screen.height);
                }
                sb.Append(" focus ").Append(_focusPending ? "pending#" + _focusAttempts : _focusNote);
                if (_cursorFixes > 0) sb.Append(" cursorFixes ").Append(_cursorFixes);
                sb.Append(" [").Append(_note).Append(']');
                return sb.ToString();
            }
            catch (Exception e)
            {
                return "status error: " + e.Message;
            }
        }
    }
}
