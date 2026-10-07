using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace UltraRing.Link
{
    /// <summary>
    /// A file-backed shared mapping opened through kernel32 directly. Mono's MemoryMappedFile is
    /// avoided on purpose: the host DLL maps the same file with CreateFileMapping/MapViewOfFile
    /// and both views must be the same physical pages.
    /// </summary>
    public sealed unsafe class MappedFile : IDisposable
    {
        public byte* Base { get; private set; }
        public long Size { get; }
        public string Path { get; }

        private IntPtr _file;
        private IntPtr _mapping;

        private MappedFile(string path, long size)
        {
            Path = path;
            Size = size;
        }

        /// <summary>Opens or creates <paramref name="path"/> and maps <paramref name="size"/> bytes (the file grows if shorter).</summary>
        public static MappedFile Open(string path, long size)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            var m = new MappedFile(path, size);
            m._file = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero,
                OpenAlways, FileAttributeNormal, IntPtr.Zero);
            if (m._file == InvalidHandle)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile " + path);
            m._mapping = CreateFileMappingW(m._file, IntPtr.Zero, PageReadWrite, (uint)(size >> 32), (uint)size, null);
            if (m._mapping == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                m.Dispose();
                throw new Win32Exception(err, "CreateFileMapping " + path);
            }
            m.Base = (byte*)MapViewOfFile(m._mapping, FileMapAllAccess, 0, 0, (UIntPtr)(ulong)size);
            if (m.Base == null)
            {
                int err = Marshal.GetLastWin32Error();
                m.Dispose();
                throw new Win32Exception(err, "MapViewOfFile " + path);
            }
            return m;
        }

        /// <summary>
        /// Opens an EXISTING file only (like the host's frames.shm open: it never creates the file) and maps
        /// <paramref name="size"/> bytes. Returns null with <paramref name="error"/> set if the file is missing or
        /// shorter than <paramref name="size"/>.
        /// </summary>
        public static MappedFile OpenExisting(string path, long size, out string error)
        {
            error = null;
            var m = new MappedFile(path, size);
            m._file = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero,
                OpenExistingDisp, FileAttributeNormal, IntPtr.Zero);
            if (m._file == InvalidHandle)
            {
                error = "CreateFile(OPEN_EXISTING) failed: " + Marshal.GetLastWin32Error();
                m.Dispose();
                return null;
            }
            if (!GetFileSizeEx(m._file, out long fileSize) || fileSize < size)
            {
                error = "file too small: " + fileSize + " < " + size;
                m.Dispose();
                return null;
            }
            m._mapping = CreateFileMappingW(m._file, IntPtr.Zero, PageReadWrite, (uint)(size >> 32), (uint)size, null);
            if (m._mapping == IntPtr.Zero)
            {
                error = "CreateFileMapping failed: " + Marshal.GetLastWin32Error();
                m.Dispose();
                return null;
            }
            m.Base = (byte*)MapViewOfFile(m._mapping, FileMapAllAccess, 0, 0, (UIntPtr)(ulong)size);
            if (m.Base == null)
            {
                error = "MapViewOfFile failed: " + Marshal.GetLastWin32Error();
                m.Dispose();
                return null;
            }
            return m;
        }

        public void Dispose()
        {
            if (Base != null) UnmapViewOfFile((IntPtr)Base);
            Base = null;
            if (_mapping != IntPtr.Zero) CloseHandle(_mapping);
            _mapping = IntPtr.Zero;
            if (_file != IntPtr.Zero && _file != InvalidHandle) CloseHandle(_file);
            _file = IntPtr.Zero;
        }

        private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
        private const uint FileShareRead = 1, FileShareWrite = 2;
        private const uint OpenExistingDisp = 3, OpenAlways = 4, FileAttributeNormal = 0x80;
        private const uint PageReadWrite = 0x04, FileMapAllAccess = 0xF001F;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition,
            uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr security, uint protect, uint sizeHigh,
            uint sizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UnmapViewOfFile(IntPtr view);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileSizeEx(IntPtr file, out long size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    public static class BridgePaths
    {
        /// <summary>Same rule as the host DLL: ERMC_DIR, set by the launcher to the project's runtime directory.</summary>
        public static string Dir
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("ERMC_DIR");
                if (string.IsNullOrEmpty(dir)) dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ermc");
                return dir;
            }
        }

        public static string File(string name) => System.IO.Path.Combine(Dir, name);
    }
}
