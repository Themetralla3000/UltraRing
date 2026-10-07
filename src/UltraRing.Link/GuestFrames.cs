using System;
using System.Threading;

namespace UltraRing.Link
{
    /// <summary>What to do with the alpha channel of a BGRA layer while it is copied into a slot.</summary>
    public enum AlphaFix
    {
        /// <summary>Copy untouched.</summary>
        None = 0,
        /// <summary>Any pixel with a non-zero byte becomes fully opaque (alpha = 255). For opaque geometry rendered
        /// by shaders that write garbage alpha; breaks additive/soft effects (they become opaque).</summary>
        Opaque = 1,
        /// <summary>alpha = max(alpha, r, g, b). Repairs colour that Unity premultiplied with SrcAlpha blending
        /// (rgb = c*a, alpha = a*a): the true alpha is never below the largest channel.</summary>
        MaxRgb = 2,
    }

    /// <summary>
    /// Guest-side writer of frames.shm (the counterpart of Minecraft Ring's FramePassthrough, spec 1.2 / 9): creates the
    /// full-size file, invalidates and initialises the header, then publishes frames through the per-slot seqlock.
    /// Memory path only: it never touches the host-owned range 0x40..0xDF and never sets the GPU slot flag.
    /// Not thread safe; call from one thread.
    /// </summary>
    public sealed unsafe class GuestFrames : IDisposable
    {
        public const int LayerWorld = 0, LayerDepth = 1, LayerGui = 2, LayerHand = 3;

        private readonly MappedFile _file;
        private readonly byte* _b;
        private ulong _counter;
        private int _nextSlot;

        private readonly uint[] _beginSeq = new uint[Protocol.FrameSlots];
        private readonly int[] _w = new int[Protocol.FrameSlots];
        private readonly int[] _h = new int[Protocol.FrameSlots];
        private readonly bool[] _open = new bool[Protocol.FrameSlots];
        // The depth layer of a slot only ever holds one constant; remember what is already there (size + value bits).
        private readonly long[] _depthSize = new long[Protocol.FrameSlots];
        private readonly uint[] _depthBits = new uint[Protocol.FrameSlots];

        public string Path { get; }

        /// <summary>The last frame id handed out by <see cref="Publish"/> (or the seed, before the first publish).</summary>
        public ulong LastFrameId => _counter;

        /// <summary>latestFrameId as stored in the file (+0x10).</summary>
        public ulong LatestFrameId => Volatile.Read(ref *(ulong*)(_b + 0x10));

        private GuestFrames(MappedFile file, string path)
        {
            _file = file;
            _b = file.Base;
            Path = path;
        }

        /// <summary>
        /// Creates/extends frames.shm to the full size and initialises it like FramePassthrough.java:163-195:
        /// release-store magic = 0, counter = max(time*1024, latestFrameId), zero the 0x100-byte header of each slot,
        /// version = 3, finally release-store magic. Throws if the file cannot be created or mapped.
        /// </summary>
        public static GuestFrames Open(string path = null)
        {
            path = path ?? BridgePaths.File("frames.shm");
            MappedFile f = MappedFile.Open(path, Protocol.FramesFileSize);
            var g = new GuestFrames(f, path);
            g.Invalidate();
            return g;
        }

        private void Invalidate()
        {
            ErmcFramesHeader* h = (ErmcFramesHeader*)_b;
            Volatile.Write(ref h->magic, 0u);
            Thread.MemoryBarrier();
            ulong seed = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1024UL;
            ulong latest = Volatile.Read(ref h->latestFrameId);
            _counter = Math.Max(seed, latest);
            for (int s = 0; s < Protocol.FrameSlots; s++)
            {
                ulong* p = (ulong*)SlotBase(s);
                for (int i = 0; i < Protocol.FrameHdr / 8; i++) p[i] = 0;
                _depthSize[s] = 0;
            }
            h->version = Protocol.FramesVersion;
            h->latestSlot = 0;
            h->reserved = 0;
            Thread.MemoryBarrier();
            Volatile.Write(ref h->magic, Protocol.FramesMagic);
        }

        private byte* SlotBase(int slot) => _b + 0x1000 + (long)slot * Protocol.FrameSlotSize;

        /// <summary>
        /// Starts writing a frame of the given size into the next slot (round robin, so the two most recently
        /// published slots survive) and marks it in progress (seq odd). Returns the slot index, or -1 on a bad size.
        /// Every BeginSlot must be followed by <see cref="Publish"/> or <see cref="Abort"/>.
        /// </summary>
        public int BeginSlot(int width, int height)
        {
            if (width < 1 || width > Protocol.FrameMaxW || height < 1 || height > Protocol.FrameMaxH) return -1;
            int slot = _nextSlot;
            _nextSlot = (_nextSlot + 1) % Protocol.FrameSlots;
            ErmcFrameHeader* h = (ErmcFrameHeader*)SlotBase(slot);
            uint s = Volatile.Read(ref h->seq);
            if ((s & 1) != 0) s++;
            _beginSeq[slot] = s;
            Volatile.Write(ref h->seq, s + 1);
            Thread.MemoryBarrier();
            _w[slot] = width;
            _h[slot] = height;
            _open[slot] = true;
            return slot;
        }

        /// <summary>Bytes of one layer of the slot opened by BeginSlot (width*height*4, no row padding).</summary>
        public long LayerBytes(int slot) => (long)_w[slot] * _h[slot] * 4;

        /// <summary>Pointer to a layer's pixels inside the opened slot.</summary>
        public byte* LayerPtr(int slot, int layer) => SlotBase(slot) + Protocol.FrameHdr + layer * LayerBytes(slot);

        /// <summary>
        /// Copies a tightly packed BGRA8 image (width*height*4 bytes, the size given to BeginSlot) into a colour layer
        /// (LayerWorld / LayerGui / LayerHand), optionally reversing the row order (flip) and fixing alpha on the way.
        /// The host expects bottom-up rows (row 0 = bottom scanline), premultiplied alpha, background alpha 0.
        /// </summary>
        public bool WriteLayer(int slot, int layer, byte* src, long srcBytes, bool flipRows, AlphaFix fix)
        {
            if (slot < 0 || slot >= Protocol.FrameSlots || !_open[slot]) return false;
            if (layer != LayerWorld && layer != LayerGui && layer != LayerHand) return false;
            long layerBytes = LayerBytes(slot);
            if (src == null || srcBytes < layerBytes) return false;
            int w = _w[slot], hgt = _h[slot];
            long rowBytes = (long)w * 4;
            byte* dst = LayerPtr(slot, layer);
            for (int y = 0; y < hgt; y++)
            {
                byte* srow = src + (flipRows ? (long)(hgt - 1 - y) : y) * rowBytes;
                byte* drow = dst + (long)y * rowBytes;
                switch (fix)
                {
                    case AlphaFix.Opaque:
                    {
                        uint* sp = (uint*)srow;
                        uint* dp = (uint*)drow;
                        for (int x = 0; x < w; x++)
                        {
                            uint p = sp[x];
                            dp[x] = p != 0 ? p | 0xFF000000u : 0u;
                        }
                        break;
                    }
                    case AlphaFix.MaxRgb:
                    {
                        // Bytes are B,G,R,A; read as little-endian uint: 0xAARRGGBB.
                        uint* sp = (uint*)srow;
                        uint* dp = (uint*)drow;
                        for (int x = 0; x < w; x++)
                        {
                            uint p = sp[x];
                            uint a = p >> 24;
                            uint r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
                            uint m = r > g ? r : g;
                            if (b > m) m = b;
                            if (m > a) a = m;
                            dp[x] = (p & 0x00FFFFFFu) | (a << 24);
                        }
                        break;
                    }
                    default:
                        Buffer.MemoryCopy(srow, drow, rowBytes, rowBytes);
                        break;
                }
            }
            return true;
        }

        /// <summary>
        /// Fills the depth layer with a constant (1.0 = nothing drawn there / unoccluded). The fill is skipped when the
        /// slot already holds that constant at this size, so after the first frame it costs nothing.
        /// </summary>
        public void FillDepth(int slot, float value = 1f)
        {
            if (slot < 0 || slot >= Protocol.FrameSlots || !_open[slot]) return;
            uint bits = *(uint*)&value;
            long size = ((long)_w[slot] << 32) | (uint)_h[slot];
            if (_depthSize[slot] == size && _depthBits[slot] == bits) return;
            float* d = (float*)LayerPtr(slot, LayerDepth);
            long n = (long)_w[slot] * _h[slot];
            for (long i = 0; i < n; i++) d[i] = value;
            _depthSize[slot] = size;
            _depthBits[slot] = bits;
        }

        /// <summary>
        /// Finishes the slot opened by BeginSlot: fills the header, publishes the even seq, then latestSlot and (release)
        /// latestFrameId. Returns the new frame id (strictly increasing, above everything published before). The caller
        /// must write the control block with mcFrame == poseId only after this returns.
        /// </summary>
        public ulong Publish(int slot, ulong poseId, uint flags, float mcNear, float mcFar, float fovYDeg, float aspect)
        {
            if (slot < 0 || slot >= Protocol.FrameSlots || !_open[slot]) return 0;
            ErmcFramesHeader* fh = (ErmcFramesHeader*)_b;
            ulong latest = Volatile.Read(ref fh->latestFrameId);
            ulong id = _counter + 1;
            if (id <= latest) id = latest + 1;
            _counter = id;

            ErmcFrameHeader* h = (ErmcFrameHeader*)SlotBase(slot);
            h->width = (uint)_w[slot];
            h->height = (uint)_h[slot];
            h->flags = flags & ~Protocol.FrameGpu;
            h->frameId = id;
            h->poseId = poseId;
            h->mcNear = mcNear;
            h->mcFar = mcFar;
            h->fovYDeg = fovYDeg;
            h->aspect = aspect;
            h->gpuIndex = 0;
            h->gpuGeneration = 0;
            Thread.MemoryBarrier();
            uint e = _beginSeq[slot] + 2;
            if (e == 0) e = 2;
            Volatile.Write(ref h->seq, e);
            _open[slot] = false;

            fh->latestSlot = (uint)slot;
            Thread.MemoryBarrier();
            Volatile.Write(ref fh->latestFrameId, id);
            return id;
        }

        /// <summary>Gives up on an opened slot: it becomes invalid (width/height 0, even seq) so the host skips it.</summary>
        public void Abort(int slot)
        {
            if (slot < 0 || slot >= Protocol.FrameSlots || !_open[slot]) return;
            ErmcFrameHeader* h = (ErmcFrameHeader*)SlotBase(slot);
            h->width = 0;
            h->height = 0;
            Thread.MemoryBarrier();
            uint e = _beginSeq[slot] + 2;
            if (e == 0) e = 2;
            Volatile.Write(ref h->seq, e);
            _open[slot] = false;
            _depthSize[slot] = 0;
        }

        public void Dispose() => _file.Dispose();
    }
}
