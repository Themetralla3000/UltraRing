using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace UltraRing.Ultrakill.Terrain
{
    // Unity-free on purpose (compiled into the unit tests too): no UnityEngine, no BepInEx.

    /// <summary>
    /// The persisted form of one 16 x 16 cell block (same grid as <see cref="TerrainChunk"/>): floor / ceiling heights,
    /// the reference they were sampled from, a wall-clock timestamp, and the wall-edge probe results. Records are
    /// immutable once published to a <see cref="TerrainCache"/> (changes replace the record), so the IO worker can read them.
    /// </summary>
    internal sealed class CacheChunk
    {
        public const int Size = 16;
        public const int Cells = Size * Size;

        public readonly int Cx, Cz;
        /// <summary>bit0: the cell has data (a completed sample; Floor may still be NaN = void).</summary>
        public readonly byte[] Flags = new byte[Cells];
        public readonly float[] Floor = new float[Cells];
        public readonly float[] Ceil = new float[Cells];
        public readonly float[] Ref = new float[Cells];
        /// <summary>Unix seconds of the cell's last completed sample.</summary>
        public readonly uint[] Time = new uint[Cells];

        /// <summary>bit0: the edge (index edge * Cells + cell) has data.</summary>
        public readonly byte[] EFlags = new byte[2 * Cells];
        public readonly byte[] EMask = new byte[2 * Cells];
        public readonly float[] EBase = new float[2 * Cells];
        public readonly uint[] ETime = new uint[2 * Cells];
        /// <summary>6 hit distances per edge in millimetres, 0xFFFF = miss.</summary>
        public readonly ushort[] EHit = new ushort[2 * Cells * 6];

        public CacheChunk(int cx, int cz)
        {
            Cx = cx;
            Cz = cz;
            for (int i = 0; i < Cells; i++) { Floor[i] = float.NaN; Ceil[i] = float.NaN; Ref[i] = float.NaN; }
            for (int i = 0; i < EBase.Length; i++) EBase[i] = float.NaN;
            for (int i = 0; i < EHit.Length; i++) EHit[i] = 0xFFFF;
        }

        public int CellCount
        {
            get { int n = 0; for (int i = 0; i < Cells; i++) if ((Flags[i] & 1) != 0) n++; return n; }
        }

        public CacheChunk Clone()
        {
            var c = new CacheChunk(Cx, Cz);
            Array.Copy(Flags, c.Flags, Cells);
            Array.Copy(Floor, c.Floor, Cells);
            Array.Copy(Ceil, c.Ceil, Cells);
            Array.Copy(Ref, c.Ref, Cells);
            Array.Copy(Time, c.Time, Cells);
            Array.Copy(EFlags, c.EFlags, EFlags.Length);
            Array.Copy(EMask, c.EMask, EMask.Length);
            Array.Copy(EBase, c.EBase, EBase.Length);
            Array.Copy(ETime, c.ETime, ETime.Length);
            Array.Copy(EHit, c.EHit, EHit.Length);
            return c;
        }

        /// <summary>Copies every cell / edge that <paramref name="d"/> has data for over this record (use on a fresh clone).</summary>
        public void Overlay(CacheChunk d)
        {
            for (int i = 0; i < Cells; i++)
            {
                if ((d.Flags[i] & 1) == 0) continue;
                Flags[i] = 1;
                Floor[i] = d.Floor[i];
                Ceil[i] = d.Ceil[i];
                Ref[i] = d.Ref[i];
                Time[i] = d.Time[i];
            }
            for (int e = 0; e < EFlags.Length; e++)
            {
                if ((d.EFlags[e] & 1) == 0) continue;
                EFlags[e] = 1;
                EMask[e] = d.EMask[e];
                EBase[e] = d.EBase[e];
                ETime[e] = d.ETime[e];
                Array.Copy(d.EHit, e * 6, EHit, e * 6, 6);
            }
        }
    }

    /// <summary>One 8 x 8 chunk (64 m at the default 0.5 m cell) file's worth of <see cref="CacheChunk"/> records.</summary>
    internal sealed class CacheRegion
    {
        public readonly int Rx, Rz;
        public readonly Dictionary<long, CacheChunk> Chunks = new Dictionary<long, CacheChunk>();
        /// <summary>Deltas applied before the file finished loading; merged (newest wins) when it does.</summary>
        public readonly Dictionary<long, CacheChunk> Pending = new Dictionary<long, CacheChunk>();
        public bool Loaded;
        public bool Dirty;
        public int SavesPending; // touched by the worker (Interlocked)

        public CacheRegion(int rx, int rz) { Rx = rx; Rz = rz; }
    }

    /// <summary>
    /// Persistent terrain samples of one zone (stage id), in host-metre grid coordinates, stored as region files
    /// <c>&lt;dir&gt;/&lt;rx&gt;_&lt;rz&gt;.bin</c> (8 x 8 chunks each). Main thread API; file IO runs on one background worker with
    /// immutable snapshots. Only regions near the player stay in memory.
    /// </summary>
    internal sealed class TerrainCache
    {
        public const int RegionChunks = 8;
        private const ushort Version = 1;
        private const int MaxLoadsInFlight = 2;

        /// <summary>Set by the host application to route messages into its log.</summary>
        public static Action<string> Log;

        // Process-wide stats (the worker updates them).
        private static long _lastSaveTicks, _savedRegions, _savedBytes, _saveErrors, _loadErrors;
        public static DateTime LastSaveUtc { get { long t = Interlocked.Read(ref _lastSaveTicks); return t == 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc); } }
        public static long SavedRegions => Interlocked.Read(ref _savedRegions);
        public static long SavedBytes => Interlocked.Read(ref _savedBytes);
        public static long SaveErrors => Interlocked.Read(ref _saveErrors);
        public static long LoadErrors => Interlocked.Read(ref _loadErrors);

        private struct LoadResult
        {
            public long Key;
            public CacheChunk[] Chunks; // null: no usable file
        }

        public readonly uint Zone;
        public readonly float Cell;
        private readonly string _dir;
        private readonly Dictionary<long, CacheRegion> _regions = new Dictionary<long, CacheRegion>();
        private readonly HashSet<long> _files = new HashSet<long>();
        private readonly ConcurrentQueue<LoadResult> _results = new ConcurrentQueue<LoadResult>();
        private readonly List<KeyValuePair<double, long>> _want = new List<KeyValuePair<double, long>>();
        private readonly List<long> _drop = new List<long>();
        private int _loadsInFlight;

        public TerrainCache(string zoneDir, uint zone, float cell)
        {
            _dir = zoneDir;
            Zone = zone;
            Cell = cell;
            try
            {
                if (Directory.Exists(zoneDir))
                    foreach (var f in Directory.GetFiles(zoneDir, "*.bin"))
                    {
                        string n = Path.GetFileNameWithoutExtension(f);
                        int us = n.IndexOf('_', 1);
                        if (us > 0 && int.TryParse(n.Substring(0, us), out int rx) && int.TryParse(n.Substring(us + 1), out int rz))
                            _files.Add(Key(rx, rz));
                    }
            }
            catch (Exception e) { Say("terrain cache: cannot list " + zoneDir + ": " + e.Message); }
        }

        public static long Key(int a, int b) => ((long)a << 32) | (uint)b;

        private static void Say(string m) { try { Log?.Invoke(m); } catch (Exception) { } }

        /// <summary>Region files known for this zone (on disk, plus the ones written this session).</summary>
        public int ZoneFiles => _files.Count;
        public int RegionsInMemory => _regions.Count;
        public int RegionsLoading { get { int n = 0; foreach (var r in _regions.Values) if (!r.Loaded) n++; return n; } }
        public bool HasPendingSaves
        {
            get { foreach (var r in _regions.Values) if (r.Dirty || Volatile.Read(ref r.SavesPending) > 0) return true; return false; }
        }

        private string PathOf(int rx, int rz) => Path.Combine(_dir, rx + "_" + rz + ".bin");

        /// <summary>The cached record of a chunk, or null when absent or its region is not (yet) loaded.</summary>
        public CacheChunk Get(int cx, int cz)
        {
            if (!_regions.TryGetValue(Key(cx >> 3, cz >> 3), out var r) || !r.Loaded) return null;
            r.Chunks.TryGetValue(Key(cx, cz), out var c);
            return c;
        }

        private CacheRegion Ensure(int rx, int rz)
        {
            long k = Key(rx, rz);
            if (_regions.TryGetValue(k, out var r)) return r;
            r = new CacheRegion(rx, rz);
            _regions[k] = r;
            if (!_files.Contains(k)) { r.Loaded = true; return r; }
            Interlocked.Increment(ref _loadsInFlight);
            string path = PathOf(rx, rz);
            float cell = Cell;
            TerrainCacheWorker.Enqueue(() =>
            {
                CacheChunk[] chunks = null;
                try
                {
                    if (File.Exists(path))
                    {
                        chunks = Deserialize(File.ReadAllBytes(path), cell, rx, rz, out string err);
                        if (chunks == null)
                        {
                            Interlocked.Increment(ref _loadErrors);
                            Say("terrain cache: ignoring " + path + " (" + err + ")");
                        }
                    }
                }
                catch (Exception e)
                {
                    chunks = null;
                    Interlocked.Increment(ref _loadErrors);
                    Say("terrain cache: cannot read " + path + ": " + e.Message);
                }
                _results.Enqueue(new LoadResult { Key = k, Chunks = chunks });
            });
            return r;
        }

        /// <summary>Merges new samples (a record holding only the cells / edges to write) into the cache.</summary>
        public void Apply(CacheChunk delta)
        {
            var r = Ensure(delta.Cx >> 3, delta.Cz >> 3);
            long ck = Key(delta.Cx, delta.Cz);
            if (!r.Loaded)
            {
                if (r.Pending.TryGetValue(ck, out var old)) { var m = old.Clone(); m.Overlay(delta); delta = m; }
                r.Pending[ck] = delta;
                return;
            }
            MergeIn(r, ck, delta);
        }

        private static void MergeIn(CacheRegion r, long ck, CacheChunk delta)
        {
            if (r.Chunks.TryGetValue(ck, out var old)) { var m = old.Clone(); m.Overlay(delta); delta = m; }
            r.Chunks[ck] = delta;
            r.Dirty = true;
        }

        /// <summary>Collects finished loads, requests the regions around (x, z) (host metres) nearest first, drops far clean ones.</summary>
        public void Update(double x, double z, double loadRadius, double unloadRadius)
        {
            while (_results.TryDequeue(out var res))
            {
                Interlocked.Decrement(ref _loadsInFlight);
                if (!_regions.TryGetValue(res.Key, out var r) || r.Loaded) continue;
                if (res.Chunks != null)
                    foreach (var c in res.Chunks) r.Chunks[Key(c.Cx, c.Cz)] = c;
                else _files.Remove(res.Key); // unusable file: it will be replaced by the next save
                r.Loaded = true;
                foreach (var kv in r.Pending) MergeIn(r, kv.Key, kv.Value);
                r.Pending.Clear();
            }

            double len = RegionChunks * CacheChunk.Size * (double)Cell;
            int x0 = (int)Math.Floor((x - loadRadius) / len), x1 = (int)Math.Floor((x + loadRadius) / len);
            int z0 = (int)Math.Floor((z - loadRadius) / len), z1 = (int)Math.Floor((z + loadRadius) / len);
            _want.Clear();
            for (int rz = z0; rz <= z1; rz++)
                for (int rx = x0; rx <= x1; rx++)
                {
                    long k = Key(rx, rz);
                    if (_regions.ContainsKey(k) || !_files.Contains(k)) continue; // (regions without a file need no load)
                    double d = DistToRegion(rx, rz, x, z, len);
                    if (d <= loadRadius) _want.Add(new KeyValuePair<double, long>(d, k));
                }
            if (_want.Count > 1) _want.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < _want.Count; i++)
            {
                if (Volatile.Read(ref _loadsInFlight) >= MaxLoadsInFlight) break;
                long k = _want[i].Value;
                Ensure((int)(k >> 32), (int)(uint)k);
            }

            _drop.Clear();
            foreach (var kv in _regions)
            {
                var r = kv.Value;
                if (!r.Loaded || r.Dirty || r.Pending.Count > 0 || Volatile.Read(ref r.SavesPending) > 0) continue;
                if (DistToRegion(r.Rx, r.Rz, x, z, len) > unloadRadius) _drop.Add(kv.Key);
            }
            for (int i = 0; i < _drop.Count; i++) _regions.Remove(_drop[i]);
        }

        private static double DistToRegion(int rx, int rz, double x, double z, double len)
        {
            double bx = rx * len, bz = rz * len;
            double dx = Math.Max(bx, Math.Min(x, bx + len)) - x;
            double dz = Math.Max(bz, Math.Min(z, bz + len)) - z;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Queues a write of every dirty region (snapshotted now; the worker serialises). Returns how many.</summary>
        public int Save()
        {
            int n = 0;
            foreach (var r in _regions.Values)
            {
                if (!r.Dirty || !r.Loaded) continue;
                r.Dirty = false;
                var chunks = new CacheChunk[r.Chunks.Count];
                r.Chunks.Values.CopyTo(chunks, 0);
                if (chunks.Length == 0) continue;
                Interlocked.Increment(ref r.SavesPending);
                _files.Add(Key(r.Rx, r.Rz));
                string path = PathOf(r.Rx, r.Rz);
                int rx = r.Rx, rz = r.Rz;
                float cell = Cell;
                var region = r;
                TerrainCacheWorker.Enqueue(() =>
                {
                    try
                    {
                        byte[] data = Serialize(rx, rz, cell, chunks);
                        WriteAtomic(path, data);
                        Interlocked.Exchange(ref _lastSaveTicks, DateTime.UtcNow.Ticks);
                        Interlocked.Increment(ref _savedRegions);
                        Interlocked.Add(ref _savedBytes, data.Length);
                    }
                    catch (Exception e)
                    {
                        Interlocked.Increment(ref _saveErrors);
                        Say("terrain cache: cannot write " + path + ": " + e.Message);
                    }
                    finally { Interlocked.Decrement(ref region.SavesPending); }
                });
                n++;
            }
            return n;
        }

        /// <summary>Save() and wait (at most <paramref name="ms"/>) for the worker to finish everything queued.</summary>
        public bool FlushAndWait(int ms)
        {
            Save();
            return TerrainCacheWorker.WaitIdle(ms);
        }

        private static void WriteAtomic(string path, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception)
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }

        // ---------------------------------------------------------------------------------------
        // file format
        //   "URTC" | u16 version | u8 chunkSize | u8 regionChunks | f32 cell | i32 rx | i32 rz | i32 payloadLen | u32 fnv1a(payload)
        //   then a raw deflate stream of the payload:
        //     i32 chunkCount, per chunk: i32 cx, i32 cz,
        //       u16 cellCount, per cell:  u16 idx | u8 bits(1 floor, 2 ceil) | u32 time | f32 ref | [f32 floor] | [f32 ceil]
        //       u16 edgeCount, per edge:  u16 idx | u8 mask | u32 time | (mask != 0: f32 base | 6 x u16 hitMm)
        // ---------------------------------------------------------------------------------------

        public static ushort PackHit(float t)
        {
            if (float.IsNaN(t)) return 0xFFFF;
            int v = (int)Math.Round(t * 1000.0);
            if (v < 0) v = 0;
            if (v > 65534) v = 65534;
            return (ushort)v;
        }

        public static float UnpackHit(ushort u) => u == 0xFFFF ? float.NaN : u * 0.001f;

        private static uint Fnv(byte[] b, int len)
        {
            uint h = 2166136261u;
            for (int i = 0; i < len; i++) { h ^= b[i]; h *= 16777619u; }
            return h;
        }

        public static byte[] Serialize(int rx, int rz, float cell, IList<CacheChunk> chunks)
        {
            byte[] payload;
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true))
                {
                    w.Write(chunks.Count);
                    for (int ci = 0; ci < chunks.Count; ci++)
                    {
                        var c = chunks[ci];
                        w.Write(c.Cx);
                        w.Write(c.Cz);
                        w.Write((ushort)c.CellCount);
                        for (int i = 0; i < CacheChunk.Cells; i++)
                        {
                            if ((c.Flags[i] & 1) == 0) continue;
                            bool hf = !float.IsNaN(c.Floor[i]), hc = !float.IsNaN(c.Ceil[i]);
                            w.Write((ushort)i);
                            w.Write((byte)((hf ? 1 : 0) | (hc ? 2 : 0)));
                            w.Write(c.Time[i]);
                            w.Write(c.Ref[i]);
                            if (hf) w.Write(c.Floor[i]);
                            if (hc) w.Write(c.Ceil[i]);
                        }
                        int ne = 0;
                        for (int e = 0; e < c.EFlags.Length; e++) if ((c.EFlags[e] & 1) != 0) ne++;
                        w.Write((ushort)ne);
                        for (int e = 0; e < c.EFlags.Length; e++)
                        {
                            if ((c.EFlags[e] & 1) == 0) continue;
                            w.Write((ushort)e);
                            w.Write(c.EMask[e]);
                            w.Write(c.ETime[e]);
                            if (c.EMask[e] == 0) continue;
                            w.Write(c.EBase[e]);
                            for (int s = 0; s < 6; s++) w.Write(c.EHit[e * 6 + s]);
                        }
                    }
                }
                payload = ms.ToArray();
            }

            using (var outMs = new MemoryStream(payload.Length / 2 + 64))
            {
                using (var w = new BinaryWriter(outMs, System.Text.Encoding.UTF8, true))
                {
                    w.Write((byte)'U'); w.Write((byte)'R'); w.Write((byte)'T'); w.Write((byte)'C');
                    w.Write(Version);
                    w.Write((byte)CacheChunk.Size);
                    w.Write((byte)RegionChunks);
                    w.Write(cell);
                    w.Write(rx);
                    w.Write(rz);
                    w.Write(payload.Length);
                    w.Write(Fnv(payload, payload.Length));
                }
                using (var ds = new DeflateStream(outMs, CompressionLevel.Fastest, true))
                    ds.Write(payload, 0, payload.Length);
                return outMs.ToArray();
            }
        }

        private const int HeaderLen = 4 + 2 + 1 + 1 + 4 + 4 + 4 + 4 + 4;

        /// <summary>Parses a region file. Returns null (and a reason) for anything unexpected: corrupt, old version, other cell size or region.</summary>
        public static CacheChunk[] Deserialize(byte[] data, float cell, int rx, int rz, out string error)
        {
            error = null;
            try
            {
                if (data == null || data.Length < HeaderLen) { error = "too short"; return null; }
                using (var hr = new BinaryReader(new MemoryStream(data, 0, HeaderLen)))
                {
                    if (hr.ReadByte() != 'U' || hr.ReadByte() != 'R' || hr.ReadByte() != 'T' || hr.ReadByte() != 'C') { error = "bad magic"; return null; }
                    ushort ver = hr.ReadUInt16();
                    if (ver != Version) { error = "version " + ver; return null; }
                    if (hr.ReadByte() != CacheChunk.Size || hr.ReadByte() != RegionChunks) { error = "layout"; return null; }
                    float fcell = hr.ReadSingle();
                    if (Math.Abs(fcell - cell) > 1e-6f) { error = "cell size " + fcell; return null; }
                    if (hr.ReadInt32() != rx || hr.ReadInt32() != rz) { error = "region mismatch"; return null; }
                    int plen = hr.ReadInt32();
                    uint hash = hr.ReadUInt32();
                    if (plen < 4 || plen > 64 * 1024 * 1024) { error = "payload size"; return null; }

                    var payload = new byte[plen];
                    using (var ds = new DeflateStream(new MemoryStream(data, HeaderLen, data.Length - HeaderLen), CompressionMode.Decompress))
                    {
                        int got = 0;
                        while (got < plen)
                        {
                            int n = ds.Read(payload, got, plen - got);
                            if (n <= 0) break;
                            got += n;
                        }
                        if (got != plen) { error = "truncated"; return null; }
                    }
                    if (Fnv(payload, plen) != hash) { error = "checksum"; return null; }
                    return ReadPayload(payload, rx, rz, out error);
                }
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        private static CacheChunk[] ReadPayload(byte[] payload, int rx, int rz, out string error)
        {
            error = null;
            using (var r = new BinaryReader(new MemoryStream(payload)))
            {
                int count = r.ReadInt32();
                if (count < 0 || count > RegionChunks * RegionChunks) { error = "chunk count"; return null; }
                var list = new CacheChunk[count];
                for (int ci = 0; ci < count; ci++)
                {
                    int cx = r.ReadInt32(), cz = r.ReadInt32();
                    if ((cx >> 3) != rx || (cz >> 3) != rz) { error = "chunk outside region"; return null; }
                    var c = new CacheChunk(cx, cz);
                    int nc = r.ReadUInt16();
                    for (int k = 0; k < nc; k++)
                    {
                        int i = r.ReadUInt16();
                        if (i >= CacheChunk.Cells) { error = "cell index"; return null; }
                        byte bits = r.ReadByte();
                        c.Flags[i] = 1;
                        c.Time[i] = r.ReadUInt32();
                        c.Ref[i] = r.ReadSingle();
                        if ((bits & 1) != 0) c.Floor[i] = r.ReadSingle();
                        if ((bits & 2) != 0) c.Ceil[i] = r.ReadSingle();
                    }
                    int ne = r.ReadUInt16();
                    for (int k = 0; k < ne; k++)
                    {
                        int e = r.ReadUInt16();
                        if (e >= c.EFlags.Length) { error = "edge index"; return null; }
                        c.EFlags[e] = 1;
                        c.EMask[e] = r.ReadByte();
                        c.ETime[e] = r.ReadUInt32();
                        if (c.EMask[e] == 0) continue;
                        c.EBase[e] = r.ReadSingle();
                        for (int s = 0; s < 6; s++) c.EHit[e * 6 + s] = r.ReadUInt16();
                    }
                    list[ci] = c;
                }
                return list;
            }
        }
    }

    /// <summary>The single background thread that does the cache's file IO (jobs run in submission order).</summary>
    internal static class TerrainCacheWorker
    {
        private static readonly object Gate = new object();
        private static readonly Queue<Action> Jobs = new Queue<Action>();
        private static Thread _thread;
        private static int _pending;

        public static void Enqueue(Action job)
        {
            lock (Gate)
            {
                Jobs.Enqueue(job);
                _pending++;
                if (_thread == null)
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = "UltraRing terrain cache", Priority = ThreadPriority.BelowNormal };
                    _thread.Start();
                }
                Monitor.Pulse(Gate);
            }
        }

        private static void Run()
        {
            while (true)
            {
                Action job;
                lock (Gate)
                {
                    while (Jobs.Count == 0) Monitor.Wait(Gate);
                    job = Jobs.Dequeue();
                }
                try { job(); }
                catch (Exception) { /* jobs report their own errors */ }
                finally { lock (Gate) _pending--; }
            }
        }

        public static bool WaitIdle(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                lock (Gate) if (_pending == 0) return true;
                Thread.Sleep(5);
            }
            lock (Gate) return _pending == 0;
        }
    }
}
