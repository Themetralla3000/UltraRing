using System;
using System.Runtime.InteropServices;

namespace UltraRing.Ultrakill.Platform
{
    /// <summary>
    /// ITaskbarList2::MarkFullscreenWindow through raw vtable calls, so Explorer hides the taskbar for our borderless
    /// overlay (which leaves one pixel uncovered and is therefore not detected as fullscreen on its own).
    /// </summary>
    internal static class TaskbarFullscreen
    {
        private static readonly Guid ClsidTaskbarList = new Guid("56FDF344-FD6D-11D0-958A-006097C9A090");
        private static readonly Guid IidTaskbarList2 = new Guid("602D4995-B13A-429B-A66E-1935E44F4317");
        private const int RpcEChangedMode = unchecked((int)0x80010106);
        private const int ENotImpl = unchecked((int)0x80004001);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int HrInitFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MarkFn(IntPtr self, IntPtr hwnd, int fullscreen);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);

        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr obj);

        /// <summary>Returns null on success, otherwise a description of the failure.</summary>
        public static string Mark(IntPtr hwnd, bool fullscreen)
        {
            int init = CoInitializeEx(IntPtr.Zero, 0);
            IntPtr obj = IntPtr.Zero;
            try
            {
                if (init < 0 && init != RpcEChangedMode) return "CoInitializeEx 0x" + init.ToString("X8");
                Guid clsid = ClsidTaskbarList, iid = IidTaskbarList2;
                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* INPROC_SERVER */, ref iid, out obj);
                if (hr < 0 || obj == IntPtr.Zero) return "CoCreateInstance 0x" + hr.ToString("X8");
                IntPtr vtbl = Marshal.ReadIntPtr(obj);
                var hrInit = Marshal.GetDelegateForFunctionPointer<HrInitFn>(Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size));
                var mark = Marshal.GetDelegateForFunctionPointer<MarkFn>(Marshal.ReadIntPtr(vtbl, 8 * IntPtr.Size));
                int ready = hrInit(obj);
                if (ready < 0 && ready != ENotImpl) return "HrInit 0x" + ready.ToString("X8");
                int r = mark(obj, hwnd, fullscreen ? 1 : 0);
                return r < 0 ? "MarkFullscreenWindow 0x" + r.ToString("X8") : null;
            }
            catch (Exception e)
            {
                return e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                if (obj != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr vtbl = Marshal.ReadIntPtr(obj);
                        Marshal.GetDelegateForFunctionPointer<ReleaseFn>(Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size))(obj);
                    }
                    catch (Exception) { }
                }
                if (init >= 0) CoUninitialize();
            }
        }
    }
}
