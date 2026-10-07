using System;
using System.Text;

namespace UltraRing.Ultrakill.Platform
{
    /// <summary>Locates ULTRAKILL's own top-level window and the host game's main window. Main thread only.</summary>
    internal static class WindowFinder
    {
        public const string UnityClass = "UnityWndClass";

        private static readonly NativeMethods.EnumWindowsProc Callback = OnWindow;
        private static readonly StringBuilder ClassBuf = new StringBuilder(64);
        private static bool _wantUnity;
        private static uint _wantPid;
        private static IntPtr _best;
        private static long _bestArea;

        /// <summary>The Unity player window of this process (class "UnityWndClass"), largest first.</summary>
        public static IntPtr FindUnityWindow()
        {
            _wantUnity = true;
            _wantPid = NativeMethods.GetCurrentProcessId();
            _best = IntPtr.Zero;
            _bestArea = -1;
            // Unity creates its window on the main (script) thread; fall back to a process-wide scan.
            NativeMethods.EnumThreadWindows(NativeMethods.GetCurrentThreadId(), Callback, IntPtr.Zero);
            if (_best == IntPtr.Zero) NativeMethods.EnumWindows(Callback, IntPtr.Zero);
            return _best;
        }

        /// <summary>The host's main window: visible, unowned, largest client area among the host process's windows.</summary>
        public static IntPtr FindHostWindow(int hostPid)
        {
            if (hostPid <= 0) return IntPtr.Zero;
            _wantUnity = false;
            _wantPid = (uint)hostPid;
            _best = IntPtr.Zero;
            _bestArea = -1;
            NativeMethods.EnumWindows(Callback, IntPtr.Zero);
            return _best;
        }

        public static bool IsHostWindowStillValid(IntPtr hwnd, int hostPid)
        {
            return hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd) && NativeMethods.ProcessIdOf(hwnd) == (uint)hostPid;
        }

        private static bool OnWindow(IntPtr hwnd, IntPtr lParam)
        {
            try
            {
                if (NativeMethods.ProcessIdOf(hwnd) != _wantPid) return true;
                long area;
                if (_wantUnity)
                {
                    ClassBuf.Clear();
                    if (NativeMethods.GetClassNameW(hwnd, ClassBuf, ClassBuf.Capacity) <= 0) return true;
                    if (ClassBuf.ToString() != UnityClass) return true;
                    area = NativeMethods.GetWindowRect(hwnd, out var r) ? (long)r.Width * r.Height : 0;
                    if (NativeMethods.IsWindowVisible(hwnd)) area += 1L << 40; // prefer visible windows
                }
                else
                {
                    if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                    if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true;
                    if (!NativeMethods.GetClientRect(hwnd, out var c)) return true;
                    area = (long)c.Width * c.Height;
                    if (area <= 0) return true;
                }
                if (area > _bestArea)
                {
                    _bestArea = area;
                    _best = hwnd;
                }
            }
            catch (Exception)
            {
                // keep enumerating
            }
            return true;
        }
    }
}
