using System;
using System.Collections.Generic;
using UltraRing.Link;
using UnityEngine;

namespace UltraRing.Ultrakill.Terrain
{
    /// <summary>
    /// Builds invisible ULTRAKILL colliders from the host's terrain, sampled through the ray mailbox
    /// (docs/research/host-contract.md section 5.1).
    ///
    /// Sampling: a world-aligned grid of cell columns (host metres, <see cref="BridgeConfig.TerrainCell"/>). Each column
    /// is sampled in phases, one ray per phase and nothing in flight twice: a LOW downward ray from just above V1's ground
    /// reference, a HIGH downward ray when the low one missed (ledges and cliffs taller than the headroom: the low ray
    /// starts inside them), and an upward CEILING ray from the floor that was found. Rays are submitted in batches, nearest
    /// to V1's predicted position first. The vertical reference is the floor under V1's feet, not V1 itself, so jumping
    /// does not re-sample the world.
    ///
    /// Walls: within <see cref="WallRadius"/> of V1 (and along its look-ahead path) every cell with a floor also casts horizontal
    /// rays along its +x and +z edges, at knee, chest and head height, in both directions (see <see cref="TerrainWallGeometry"/>).
    /// Walls become BoxColliders, so tall walls, thin walls and pillars are solid even though no floor ray ever sees them.
    ///
    /// Colliders: 8 x 8 m chunks, rebuilt only when their samples changed (see <see cref="TerrainMeshBuilder"/>).
    /// </summary>
    internal sealed class TerrainManager
    {
        // ---- tuning (host metres unless noted) -------------------------------------------------
        private const int MaxBatchRays = 4096;
        private const int MaxWallRaysPerBatch = 2800; // the rest of a batch is kept for floors
        private const float LowHeadroom = 1.2f;     // low ray starts this far above the ground reference
        private const float LowReach = 30f;         // ... and ends this far below it
        private const float HighReach = 8f;         // high ray covers lowStart .. lowStart + this
        private const float SlopeK = 0.8f;          // floor rays start this much higher per metre from V1 (~39 degree slopes)
        private const float SlopeCap = 6f;          // ... up to this far above the ground reference
        private const int NeighbourSteps = 4;       // known floors this many cells towards V1 seed a cell's start height
        private const float FrontierDist = 3f;      // cells without a sampled neighbour are only cast this close to V1
        private const float PriorityRadius = 2f;    // the cells this close to V1 are always sampled first
        private const float SeedRefTol = 3f;        // a neighbour sampled from a ground reference further off is not trusted
        private const float WallRadius = 12f;       // horizontal wall rays are cast this close to V1 / its path
        private const float LookAheadSeconds = 1f;
        private const float LookAheadMax = 20f;
        private const float WallRefreshAge = 10f;
        private const float WallBaseTol = 0.1f;     // re-cast an edge when its base floor moved this much
        private const float WallHitEps = 0.02f;
        private const float RayEnd = 0.02f;         // wall rays end this far past the neighbour's centre
        private const float CeilStart = 0.3f;       // ceiling ray starts this far above the floor
        private const float CeilReach = 5f;         // ... and goes this far up
        private const float RefStaleNear = 1f;      // re-sample a cell when the ground reference moved this much
        private const float RefStaleFar = 2.5f;     // beyond NearZone
        private const float NearZone = 12f;
        private const float RefreshNearDist = 3f;   // cells this close to V1 are re-sampled every ...
        private const float RefreshNearAge = 1f;    // ... seconds
        private const float RefreshMidDist = 10f;
        private const float RefreshMidAge = 6f;
        private const float PredictSeconds = 0.4f;
        private const float PredictMax = 10f;
        private const float EvictMargin = 16f;
        private const float IdleScanInterval = 0.1f;
        private const float StallLogSeconds = 3f;
        private const int MaxBuildsPerFrame = 2;
        private const float MinRebuildInterval = 0.2f;
        private const float UrgentRebuildInterval = 0.05f; // chunks under V1's feet
        private const float UrgentRadius = 2f;
        private const float ChangeEps = 0.02f;      // floor/ceiling changes smaller than this are noise
        private const float BigChange = 0.05f;      // floor changes larger than this re-run the ceiling ray

        private const byte KindWall = 10;

        private struct Entry
        {
            public TerrainChunk C;
            public int Idx;
            public ushort Ver;
            public byte Kind;
            public byte Sub;    // wall rays: edge * 6 + slot
            public float A, B;  // floor rays: start / end height; wall rays: origin coordinate along the edge axis / direction sign
        }

        /// <summary>Debug view (F10): translucent floor (green), wall (red) and ceiling (blue) volumes on layer 0.</summary>
        public static bool DebugDraw;

        private readonly Transform _parent;
        private readonly Dictionary<long, TerrainChunk> _chunks = new Dictionary<long, TerrainChunk>();
        private readonly List<TerrainChunk> _scratchChunks = new List<TerrainChunk>();
        private readonly TerrainMeshBuilder _builder = new TerrainMeshBuilder();
        private readonly float[] _floorGrid = new float[TerrainMeshBuilder.Grid * TerrainMeshBuilder.Grid];
        private readonly float[] _ceilGrid = new float[TerrainChunk.Cells];
        private readonly float[] _ceilGrid17 = new float[TerrainMeshBuilder.Grid * TerrainMeshBuilder.Grid];
        private readonly TerrainWallGeometry _wallGeo = new TerrainWallGeometry();
        private readonly List<WallBox> _boxes = new List<WallBox>();
        private WallParams _wp;
        private readonly TerrainChunk[] _nb = new TerrainChunk[4];

        private readonly float[] _rays = new float[MaxBatchRays * 6];
        private readonly List<Vector3> _visV = new List<Vector3>();
        private readonly List<int> _visT = new List<int>();
        private readonly Entry[] _ents = new Entry[MaxBatchRays];

        private CoordMap _map;
        private float _cell = 0.5f;
        private float _radius = 24f;
        private int[] _offX = new int[0], _offZ = new int[0];
        private int _priorityCount;

        // V1 / look-ahead in host metres, refreshed every tick.
        private double _fx, _fz, _lax, _laz;
        private int _vcx, _vcz;
        private float _vCeil = float.NaN;
        private ushort _tag;
        private TerrainChunk _cache;
        private int _cacheCx = int.MinValue, _cacheCz = int.MinValue;
        private int _batchWallRays;

        // Ground reference.
        private bool _haveRef;
        private float _groundRef;

        // Batch in flight.
        private bool _inflight;
        private uint _seq;
        private int _inflightCount;
        private float _inflightSince;
        private float _batchRef;
        private bool _stallLogged;
        private uint _lastSeq;

        private float _nextScan;
        private float _nextEvict;

        // Stats.
        private int _batchesInWindow;
        private float _windowStart;
        private float _batchRate;
        private float _statusAt = -10f;
        private string _status = "no data yet";
        private bool _tagWarned;
        private long _totalBatches;
        private int _raysInWindow, _wallRaysInWindow;
        private float _rayRate, _wallRayRate;
        private bool _debugApplied;
        private static Material[] _dbgMats;
        private static bool _dbgMatsFailed;

        public TerrainManager(Transform parent) => _parent = parent;

        public string Status => _status;

        /// <summary>Forget every sample and destroy every collider (zone change, scene change, host restart).</summary>
        public void Reset()
        {
            foreach (var kv in _chunks) kv.Value.DestroyObjects();
            _chunks.Clear();
            // The in-flight batch (if any) is abandoned; its sequence stays busy in the mailbox, so the next
            // submission waits for RaysIdle (GuestLink.SubmitRays refuses until the host has answered).
            _inflight = false;
            _inflightCount = 0;
            Array.Clear(_ents, 0, _ents.Length);
            _cache = null; _cacheCx = int.MinValue;
            _map = null;
            _haveRef = false;
            _nextScan = 0f;
            _nextEvict = 0f;
            _status = "reset";
        }

        /// <summary>Once per frame while the host is alive: submit/collect ray batches and update colliders.</summary>
        public void Tick(GuestLink link, CoordMap map, Vector3 feetUk, Vector3 velocityUk)
        {
            if (link == null || map == null) return;
            float now = Time.unscaledTime;

            if (_map != null && !ReferenceEquals(_map, map)
                && (_map.Zone != map.Zone || _map.AnchorX != map.AnchorX || _map.AnchorY != map.AnchorY
                    || _map.AnchorZ != map.AnchorZ || _map.MetresPerUnit != map.MetresPerUnit))
                Reset();
            _map = map;

            ApplyConfig();

            // V1 in host metres (doubles: host coordinates can be large).
            double mpu = map.MetresPerUnit;
            double fx = (feetUk.x - CoordMap.Origin.x) * mpu + map.AnchorX;
            double fy = (feetUk.y - CoordMap.Origin.y) * mpu + map.AnchorY;
            double fz = (feetUk.z - CoordMap.Origin.z) * mpu + map.AnchorZ;
            int vcx = (int)Math.Floor(fx / _cell), vcz = (int)Math.Floor(fz / _cell);

            UpdateGroundRef(fy, vcx, vcz);

            _fx = fx; _fz = fz; _vcx = vcx; _vcz = vcz;
            _wp = WallParams.For(_cell, map.MetresPerUnit);
            {
                double lx = velocityUk.x * mpu * LookAheadSeconds, lz = velocityUk.z * mpu * LookAheadSeconds;
                double ll = Math.Sqrt(lx * lx + lz * lz);
                if (ll > LookAheadMax) { lx *= LookAheadMax / ll; lz *= LookAheadMax / ll; }
                _lax = lx; _laz = lz;
                var vc = GetChunk(vcx >> 4, vcz >> 4);
                _vCeil = vc == null ? float.NaN : vc.Ceil[((vcz & 15) << 4) | (vcx & 15)];
            }
            if (DebugDraw != _debugApplied) ApplyDebugState();

            // 1. Collect the batch in flight.
            if (_inflight)
            {
                if (link.RaysDone(_seq)) ApplyResults(link, now);
                else if (link.RaysIdle)
                {
                    // The mailbox says idle but never answered our sequence: it was reset (host restart). Drop it.
                    AbandonBatch();
                    _inflight = false;
                }
                else if (!_stallLogged && now - _inflightSince > StallLogSeconds)
                {
                    _stallLogged = true;
                    Plugin.Log.LogWarning($"Terrain batch #{_seq} unanswered for {StallLogSeconds:F0} s; still waiting (host busy or not alive).");
                }
            }

            // 2. Submit the next one.
            if (!_inflight && now >= _nextScan && link.RaysIdle)
            {
                double vx = velocityUk.x * mpu, vz = velocityUk.z * mpu;
                double px = vx * PredictSeconds, pz = vz * PredictSeconds;
                double pl = Math.Sqrt(px * px + pz * pz);
                double maxOff = Math.Min(PredictMax, _radius * 0.45f);
                if (pl > maxOff) { px *= maxOff / pl; pz *= maxOff / pl; }
                int pcx = (int)Math.Floor((fx + px) / _cell), pcz = (int)Math.Floor((fz + pz) / _cell);

                int count = BuildBatch(pcx, pcz, vcx, vcz, now);
                if (count > 0)
                {
                    uint seq = link.SubmitRays(_rays, count);
                    if (seq != 0)
                    {
                        _inflight = true;
                        _seq = seq;
                        _inflightCount = count;
                        _inflightSince = now;
                        _stallLogged = false;
                        _batchRef = _groundRef;
                        _nextScan = 0f;
                    }
                }
                else _nextScan = now + IdleScanInterval;
            }

            // 3. Evict far chunks.
            if (now >= _nextEvict)
            {
                _nextEvict = now + 0.5f;
                Evict(fx, fz);
            }

            // 4. Rebuild a couple of dirty chunks, nearest first.
            RebuildDirty(fx, fz, now);

            if (now - _statusAt > 0.5f) RefreshStatus(now);
        }

        /// <summary>True once sampled floor exists under <paramref name="feetUk"/> within <paramref name="maxDropUk"/>.</summary>
        public bool HasGroundBelow(Vector3 feetUk, float maxDropUk)
        {
            var map = _map;
            if (map == null) return false;
            double mpu = map.MetresPerUnit;
            double hx = (feetUk.x - CoordMap.Origin.x) * mpu + map.AnchorX;
            double hy = (feetUk.y - CoordMap.Origin.y) * mpu + map.AnchorY;
            double hz = (feetUk.z - CoordMap.Origin.z) * mpu + map.AnchorZ;
            // The dual quad (samples qi..qi+1, qj..qj+1) that contains the point; it is owned by one chunk.
            int qi = (int)Math.Floor(hx / _cell - 0.5), qj = (int)Math.Floor(hz / _cell - 0.5);
            var owner = GetChunk(qi >> 4, qj >> 4);
            if (owner == null || !owner.BuiltOnce || owner.Dirty || owner.Gos[TerrainChunk.Floor_] == null) return false;
            double maxDrop = maxDropUk * mpu;
            int present = 0;
            bool within = false;
            for (int k = 0; k < 4; k++)
            {
                if (!TryGetFloor(qi + (k & 1), qj + (k >> 1), out float h)) continue;
                present++;
                double drop = hy - h;
                if (drop >= -0.5 && drop <= maxDrop) within = true;
            }
            return present >= 3 && within;
        }

        /// <summary>Resample an area (a door opened, a prop broke).</summary>
        public void Invalidate(Vector3 aroundUk, float radiusUk)
        {
            var map = _map;
            if (map == null) return;
            double mpu = map.MetresPerUnit;
            double hx = (aroundUk.x - CoordMap.Origin.x) * mpu + map.AnchorX;
            double hz = (aroundUk.z - CoordMap.Origin.z) * mpu + map.AnchorZ;
            double r = radiusUk * mpu;
            double r2 = r * r;
            double chunkLen = TerrainChunk.Size * (double)_cell;
            foreach (var kv in _chunks)
            {
                var c = kv.Value;
                double bx0 = c.Cx * chunkLen, bz0 = c.Cz * chunkLen;
                double nx = Math.Max(bx0, Math.Min(hx, bx0 + chunkLen)) - hx;
                double nz = Math.Max(bz0, Math.Min(hz, bz0 + chunkLen)) - hz;
                if (nx * nx + nz * nz > r2) continue;
                for (int lz = 0; lz < TerrainChunk.Size; lz++)
                {
                    double cz = (c.Cz * TerrainChunk.Size + lz + 0.5) * _cell - hz;
                    for (int lx = 0; lx < TerrainChunk.Size; lx++)
                    {
                        double cx = (c.Cx * TerrainChunk.Size + lx + 0.5) * _cell - hx;
                        if (cx * cx + cz * cz > r2) continue;
                        int idx = lz * TerrainChunk.Size + lx;
                        if (c.State[idx] == TerrainChunk.Done) c.DoneCount--;
                        c.State[idx] = TerrainChunk.NeedLow;
                        unchecked { c.Ver[idx]++; }
                        ResetEdges(c, idx);
                        // The edges of the cells to the -x / -z probe into this one.
                        int gx = c.Cx * TerrainChunk.Size + lx, gz = c.Cz * TerrainChunk.Size + lz;
                        var cl = GetChunk((gx - 1) >> 4, gz >> 4);
                        if (cl != null) ResetEdge(cl, 0, (gz & 15) * TerrainChunk.Size + ((gx - 1) & 15));
                        var cb = GetChunk(gx >> 4, (gz - 1) >> 4);
                        if (cb != null) ResetEdge(cb, 1, (((gz - 1) & 15) * TerrainChunk.Size) + (gx & 15));
                    }
                }
            }
            _nextScan = 0f;
        }

        private static void ResetEdges(TerrainChunk c, int idx)
        {
            ResetEdge(c, 0, idx);
            ResetEdge(c, 1, idx);
        }

        private static void ResetEdge(TerrainChunk c, int e, int idx)
        {
            int ei = e * TerrainChunk.Cells + idx;
            c.EState[ei] = TerrainChunk.WNeed;
            c.EPend[ei] = 0;
        }

        /// <summary>The in-flight batch will never be answered: its wall edges must be cast again.</summary>
        private void AbandonBatch()
        {
            for (int i = 0; i < _inflightCount; i++)
            {
                ref Entry e = ref _ents[i];
                if (e.C != null && e.Kind == KindWall) ResetEdge(e.C, e.Sub / 6, e.Idx);
                e.C = null;
            }
        }

        // ---------------------------------------------------------------------------------------
        // configuration, ground reference
        // ---------------------------------------------------------------------------------------

        private void ApplyConfig()
        {
            float cell = Mathf.Max(0.25f, BridgeConfig.TerrainCell.Value);
            float radius = Mathf.Clamp(BridgeConfig.TerrainRadius.Value, 4f, 64f);
            if (_offX.Length != 0 && cell == _cell && radius == _radius) return;
            if (cell != _cell && _chunks.Count > 0)
            {
                var m = _map;
                Reset();
                _map = m;
            }
            _cell = cell;
            _radius = radius;
            BuildOffsets();
        }

        /// <summary>All cell offsets inside the sampling disc, nearest first.</summary>
        private void BuildOffsets()
        {
            float rc = _radius / _cell;
            int r = (int)Math.Ceiling(rc);
            float rc2 = rc * rc;
            int n = 0;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                    if (dx * dx + dz * dz <= rc2) n++;
            var keys = new int[n];
            var packed = new int[n];
            int k = 0;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                    if (dx * dx + dz * dz <= rc2)
                    {
                        keys[k] = dx * dx + dz * dz;
                        packed[k] = ((dz + r) << 16) | (dx + r);
                        k++;
                    }
            Array.Sort(keys, packed);
            float pr = PriorityRadius / _cell;
            int pr2 = (int)Math.Floor(pr * pr);
            _priorityCount = 0;
            while (_priorityCount < n && keys[_priorityCount] <= pr2) _priorityCount++;
            _offX = new int[n];
            _offZ = new int[n];
            for (int i = 0; i < n; i++)
            {
                _offX[i] = (packed[i] & 0xFFFF) - r;
                _offZ[i] = (packed[i] >> 16) - r;
            }
        }

        /// <summary>
        /// The reference follows the known floor under V1's feet while V1 is on or near it, and holds while V1 is
        /// airborne, so a jump (or a dash over a pit) never makes the world re-sample.
        /// </summary>
        /// <summary>V1 was teleported (recall): take the new feet height as the reference on the next tick.</summary>
        public void ReseedGroundReference() => _haveRef = false;

        private void UpdateGroundRef(double feetY, int vcx, int vcz)
        {
            if (!_haveRef)
            {
                _groundRef = (float)feetY;
                _haveRef = true;
                return;
            }
            if (TryGetFloor(vcx, vcz, out float fl) && Math.Abs(feetY - fl) <= 1.0)
                _groundRef = fl;
            else if (feetY < _groundRef - 12.0 || feetY > _groundRef + LowHeadroom + HighReach * 0.5f)
                _groundRef = (float)feetY; // far below or above anything sampled: look around V1 again
        }

        // ---------------------------------------------------------------------------------------
        // chunk access
        // ---------------------------------------------------------------------------------------

        private TerrainChunk GetChunk(int cx, int cz)
        {
            _chunks.TryGetValue(TerrainChunk.Key(cx, cz), out var c);
            return c;
        }

        private TerrainChunk CreateChunk(int cx, int cz)
        {
            var c = new TerrainChunk(cx, cz) { Dirty = false };
            _chunks[c.Key()] = c;
            return c;
        }

        private bool TryGetFloor(int ix, int iz, out float y)
        {
            var c = GetChunk(ix >> 4, iz >> 4);
            if (c == null) { y = float.NaN; return false; }
            y = c.Floor[((iz & 15) << 4) | (ix & 15)];
            return !float.IsNaN(y);
        }

        // ---------------------------------------------------------------------------------------
        // batches
        // ---------------------------------------------------------------------------------------

        private int BuildBatch(int pcx, int pcz, int vcx, int vcz, float now)
        {
            int count = 0;
            _batchWallRays = 0;
            _cache = null; _cacheCx = int.MinValue; _cacheCz = int.MinValue; // chunks may have been evicted since
            unchecked { _tag++; }
            if (_tag == 0) _tag = 1;

            // 1. The ground under and around V1 always comes first.
            for (int k = 0; k < _priorityCount && count < MaxBatchRays; k++)
                ProcessCell(vcx + _offX[k], vcz + _offZ[k], true, now, ref count);

            // 2. Everything else, nearest to V1's predicted position first.
            for (int k = 0; k < _offX.Length && count < MaxBatchRays; k++)
                ProcessCell(pcx + _offX[k], pcz + _offZ[k], false, now, ref count);
            return count;
        }

        private TerrainChunk GetCached(int cx, int cz)
        {
            if (cx != _cacheCx || cz != _cacheCz)
            {
                _cache = GetChunk(cx, cz);
                _cacheCx = cx;
                _cacheCz = cz;
            }
            return _cache;
        }

        /// <summary>Queues the next floor/ceiling ray of one cell, or (floors done) its wall rays.</summary>
        private void ProcessCell(int ix, int iz, bool priority, float now, ref int count)
        {
            var c = GetCached(ix >> 4, iz >> 4);
            int idx = ((iz & 15) << 4) | (ix & 15);
            int kind;
            if (c == null) kind = TerrainChunk.NeedLow;
            else
            {
                if (c.Queued[idx] == _tag) return;
                byte st = c.State[idx];
                if (st == TerrainChunk.Done)
                {
                    if (priority) return;
                    if (NeedsRefresh(c, idx, ix - _vcx, iz - _vcz, now))
                    {
                        c.State[idx] = TerrainChunk.NeedLow;
                        c.DoneCount--;
                        kind = TerrainChunk.NeedLow;
                    }
                    else
                    {
                        WallWork(c, idx, ix, iz, now, ref count);
                        return;
                    }
                }
                else kind = st;
            }

            float cell = _cell;
            float x = (float)((ix + 0.5) * cell), z = (float)((iz + 0.5) * cell);
            float y0, y1;
            if (kind == TerrainChunk.NeedCeil)
            {
                float fl = c.Floor[idx];
                if (float.IsNaN(fl)) { c.State[idx] = TerrainChunk.Done; c.DoneCount++; return; }
                y0 = fl + CeilStart;
                y1 = y0 + CeilReach;
            }
            else
            {
                double ddx = (ix + 0.5) * cell - _fx, ddz = (iz + 0.5) * cell - _fz;
                float top = FloorTop(ix, iz, (float)Math.Sqrt(ddx * ddx + ddz * ddz), priority);
                if (float.IsNaN(top)) return; // not reachable yet: wait until neighbours towards V1 are sampled
                if (kind == TerrainChunk.NeedLow)
                {
                    y0 = top;
                    y1 = _groundRef - LowReach;
                    if (y1 > top - 4f) y1 = top - 4f;
                }
                else
                {
                    y0 = top + HighReach;
                    y1 = top;
                }
            }
            if (c == null) c = CreateChunk(ix >> 4, iz >> 4);
            _cache = c; _cacheCx = c.Cx; _cacheCz = c.Cz;

            int o = count * 6;
            _rays[o] = x; _rays[o + 1] = y0; _rays[o + 2] = z;
            _rays[o + 3] = x; _rays[o + 4] = y1; _rays[o + 5] = z;
            ref Entry e = ref _ents[count];
            e.C = c;
            e.Idx = idx;
            e.Ver = c.Ver[idx];
            e.Kind = (byte)kind;
            e.A = y0;
            e.B = y1;
            c.Queued[idx] = _tag;
            count++;
        }

        /// <summary>
        /// Height the low floor ray of a cell starts at. Near V1 that is just above the ground reference; further out it
        /// rises with distance (<see cref="SlopeK"/>) so ramps and stairs climbing away from V1 are not started inside; and when
        /// a sampled cell lies up to <see cref="NeighbourSteps"/> cells towards V1 its floor seeds the start (so the start follows
        /// the real slope, and stays under low ceilings). NaN: no sampled cell nearby and too far from V1 to guess.
        /// </summary>
        private float FloorTop(int ix, int iz, float dist, bool priority)
        {
            float groundRef = _groundRef;
            float bySlope = groundRef + Mathf.Min(LowHeadroom + SlopeK * dist, SlopeCap);
            if (!float.IsNaN(_vCeil) && dist < 4f) bySlope = Mathf.Max(groundRef + 0.4f, Mathf.Min(bySlope, _vCeil - 0.1f));
            if (priority || dist < 0.75f) return bySlope;

            double ddx = _fx - (ix + 0.5) * _cell, ddz = _fz - (iz + 0.5) * _cell;
            double dl = Math.Sqrt(ddx * ddx + ddz * ddz);
            bool anySampled = false;
            if (dl > 1e-6)
            {
                double ux = ddx / dl, uz = ddz / dl;
                int lastX = ix, lastZ = iz;
                for (int k = 1; k <= NeighbourSteps; k++)
                {
                    int nx = ix + (int)Math.Round(ux * k), nz = iz + (int)Math.Round(uz * k);
                    if (nx == lastX && nz == lastZ) continue;
                    lastX = nx; lastZ = nz;
                    var nc = GetChunk(nx >> 4, nz >> 4);
                    if (nc == null) continue;
                    int ni = ((nz & 15) << 4) | (nx & 15);
                    byte st = nc.State[ni];
                    if (st != TerrainChunk.Done && st != TerrainChunk.NeedCeil) continue;
                    if (Math.Abs(nc.Ref[ni] - groundRef) > SeedRefTol) continue;
                    anySampled = true;
                    float fn = nc.Floor[ni];
                    if (float.IsNaN(fn)) continue;
                    float dn = Mathf.Max(0f, dist - k * _cell);
                    if (fn > groundRef + SlopeK * dn + 2.5f) continue; // steeper than any slope reachable from V1: stale or a roof
                    float top = fn + LowHeadroom + SlopeK * k * _cell;
                    float cn = nc.Ceil[ni];
                    if (!float.IsNaN(cn)) top = Mathf.Max(fn + 0.4f, Mathf.Min(top, cn - 0.1f));
                    return top;
                }
            }
            if (anySampled || dist <= FrontierDist) return bySlope;
            return float.NaN;
        }

        private bool NeedsRefresh(TerrainChunk c, int idx, int dxCells, int dzCells, float now)
        {
            float dx = dxCells * _cell, dz = dzCells * _cell;
            float d2 = dx * dx + dz * dz;
            float age = now - c.Stamp[idx];
            if (d2 < RefreshNearDist * RefreshNearDist && age > RefreshNearAge) return true;
            if (d2 < RefreshMidDist * RefreshMidDist && age > RefreshMidAge) return true;
            float thr = d2 < NearZone * NearZone ? RefStaleNear : RefStaleFar;
            return Mathf.Abs(_groundRef - c.Ref[idx]) > thr;
        }

        // ---------------------------------------------------------------------------------------
        // horizontal wall rays
        // ---------------------------------------------------------------------------------------

        /// <summary>True when the cell lies within <see cref="WallRadius"/> of V1's path over the next second.</summary>
        private bool InWallZone(double cx, double cz)
        {
            double px = cx - _fx, pz = cz - _fz;
            double l2 = _lax * _lax + _laz * _laz;
            double t = l2 > 1e-6 ? (px * _lax + pz * _laz) / l2 : 0.0;
            if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
            double dx = px - _lax * t, dz = pz - _laz * t;
            return dx * dx + dz * dz <= (double)WallRadius * WallRadius;
        }

        private void WallWork(TerrainChunk c, int idx, int ix, int iz, float now, ref int count)
        {
            if (_batchWallRays >= MaxWallRaysPerBatch) return;
            double cxm = (ix + 0.5) * _cell, czm = (iz + 0.5) * _cell;
            if (!InWallZone(cxm, czm)) return;
            for (int e = 0; e < 2; e++)
            {
                int ei = e * TerrainChunk.Cells + idx;
                byte ws = c.EState[ei];
                if (ws == TerrainChunk.WPending) continue;

                // The neighbour this edge points at must be sampled too.
                int bx = e == 0 ? ix + 1 : ix, bz = e == 0 ? iz : iz + 1;
                TerrainChunk bc = (bx >> 4) == c.Cx && (bz >> 4) == c.Cz ? c : GetChunk(bx >> 4, bz >> 4);
                if (bc == null) continue;
                int bidx = ((bz & 15) << 4) | (bx & 15);
                if (bc.State[bidx] != TerrainChunk.Done) continue;

                float fA = c.Floor[idx], fB = bc.Floor[bidx];
                int mask = TerrainWallGeometry.PlanMask(fA, fB, _wp.Climb, out float baseY);
                if (ws == TerrainChunk.WDone
                    && mask == c.EMask[ei]
                    && (float.IsNaN(baseY) == float.IsNaN(c.EBase[ei]))
                    && (float.IsNaN(baseY) || Math.Abs(baseY - c.EBase[ei]) <= WallBaseTol)
                    && now - c.EStamp[ei] < WallRefreshAge * (0.75f + ((ix * 7 + iz * 13) & 7) * 0.0625f))
                    continue;

                int hb = ei * 6;
                if (mask == 0)
                {
                    for (int s = 0; s < 6; s++) SetHit(c, hb + s, float.NaN);
                    c.EMask[ei] = 0; c.EBase[ei] = float.NaN; c.EState[ei] = TerrainChunk.WDone; c.EStamp[ei] = now;
                    c.WallsDirty = true;
                    continue;
                }

                int need = ((mask & 1) != 0 ? 3 : 0) + ((mask & 2) != 0 ? 3 : 0);
                if (count + need > MaxBatchRays || _batchWallRays + need > MaxWallRaysPerBatch) continue;

                double xa = cxm, za = czm;
                float cell = _cell;
                int pend = 0;
                for (int dir = 0; dir < 2; dir++)
                {
                    float ceilO = dir == 0 ? c.Ceil[idx] : bc.Ceil[bidx];
                    for (int band = 0; band < 3; band++)
                    {
                        int slot = dir * 3 + band;
                        float y = baseY + (band == 0 ? _wp.KneeY : band == 1 ? _wp.ChestY : _wp.HeadY);
                        if ((mask & (1 << dir)) == 0 || (!float.IsNaN(ceilO) && y >= ceilO - 0.05f))
                        {
                            SetHit(c, hb + slot, float.NaN);
                            continue;
                        }
                        // Origin and target centres along the edge.
                        double o0 = dir == 0 ? 0.0 : cell, o1 = dir == 0 ? cell + RayEnd : -RayEnd;
                        int o = count * 6;
                        if (e == 0)
                        {
                            _rays[o] = (float)(xa + o0); _rays[o + 1] = y; _rays[o + 2] = (float)za;
                            _rays[o + 3] = (float)(xa + o1); _rays[o + 4] = y; _rays[o + 5] = (float)za;
                        }
                        else
                        {
                            _rays[o] = (float)xa; _rays[o + 1] = y; _rays[o + 2] = (float)(za + o0);
                            _rays[o + 3] = (float)xa; _rays[o + 4] = y; _rays[o + 5] = (float)(za + o1);
                        }
                        ref Entry en = ref _ents[count];
                        en.C = c;
                        en.Idx = idx;
                        en.Kind = KindWall;
                        en.Sub = (byte)(e * 6 + slot);
                        en.A = (float)((e == 0 ? xa : za) + o0);
                        en.B = dir == 0 ? 1f : -1f;
                        count++;
                        pend++;
                    }
                }
                _batchWallRays += pend;
                c.EMask[ei] = (byte)mask;
                c.EBase[ei] = baseY;
                c.EPend[ei] = (byte)pend;
                c.EStamp[ei] = now;
                c.EState[ei] = pend == 0 ? TerrainChunk.WDone : TerrainChunk.WPending;
                if (pend == 0) c.WallsDirty = true;
            }
        }

        private static void SetHit(TerrainChunk c, int hi, float v)
        {
            float old = c.EHit[hi];
            bool oldNaN = float.IsNaN(old), newNaN = float.IsNaN(v);
            if (oldNaN && newNaN) return;
            if (oldNaN != newNaN || Math.Abs(old - v) > WallHitEps)
            {
                c.EHit[hi] = v;
                c.WallsDirty = true; // wall boxes are chunk-local: edges belong to the chunk of their first cell
            }
        }

        private unsafe void ApplyResults(GuestLink link, float now)
        {
            ErmcRayHit* hits = link.RayHits;
            float bref = _batchRef;
            int wallRays = 0;
            for (int i = 0; i < _inflightCount; i++)
            {
                ref Entry e = ref _ents[i];
                var c = e.C;
                int idx = e.Idx;
                if (c == null) continue;
                bool hit = hits[i].hit != 0;
                float y = hits[i].pos[1];
                if (hit && (float.IsNaN(y) || float.IsInfinity(y))) hit = false;

                if (e.Kind == KindWall)
                {
                    wallRays++;
                    int edge = e.Sub / 6, slot = e.Sub % 6;
                    int ei = edge * TerrainChunk.Cells + idx;
                    e.C = null;
                    if (!c.Alive || c.EState[ei] != TerrainChunk.WPending) continue;
                    float t = float.NaN;
                    if (hit)
                    {
                        float along = hits[i].pos[edge == 0 ? 0 : 2];
                        t = (along - e.A) * e.B;
                        if (float.IsNaN(t) || t < -0.05f || t > _cell + RayEnd + 0.05f) t = float.NaN;
                        else t = Mathf.Clamp(t, 0f, _cell);
                    }
                    SetHit(c, ei * 6 + slot, t);
                    if (c.EPend[ei] > 0 && --c.EPend[ei] == 0)
                    {
                        c.EState[ei] = TerrainChunk.WDone;
                        c.EStamp[ei] = now;
                    }
                    continue;
                }

                if (!c.Alive || c.Ver[idx] != e.Ver) { e.C = null; continue; }

                switch (e.Kind)
                {
                    case TerrainChunk.NeedLow:
                        c.Ref[idx] = bref;
                        if (hit && y <= e.A + 0.05f && y >= e.B - 0.05f)
                            AfterFloor(c, idx, y, now);
                        else
                            c.State[idx] = TerrainChunk.NeedHigh;
                        break;

                    case TerrainChunk.NeedHigh:
                        c.Ref[idx] = bref;
                        if (hit && y >= e.B - 0.05f && y <= e.A + 0.05f)
                            AfterFloor(c, idx, y, now);
                        else
                        {
                            SetFloor(c, idx, float.NaN);
                            SetCeil(c, idx, float.NaN);
                            Finish(c, idx, now);
                        }
                        break;

                    default:
                    {
                        float fl = c.Floor[idx];
                        float ceil = float.NaN;
                        if (hit && !float.IsNaN(fl) && y > fl + CeilStart + 0.02f && y <= fl + CeilStart + CeilReach + 0.05f)
                            ceil = y;
                        SetCeil(c, idx, ceil);
                        Finish(c, idx, now);
                        break;
                    }
                }
                e.C = null;
            }
            _raysInWindow += _inflightCount;
            _wallRaysInWindow += wallRays;
            _inflight = false;
            _totalBatches++;
            _lastSeq = _seq;
            _batchesInWindow++;
            _nextScan = 0f;
        }

        /// <summary>A floor was found: store it; the ceiling is re-cast only if the floor is new or moved.</summary>
        private void AfterFloor(TerrainChunk c, int idx, float y, float now)
        {
            bool big = SetFloor(c, idx, y);
            if (big) c.State[idx] = TerrainChunk.NeedCeil;
            else Finish(c, idx, now);
        }

        private static void Finish(TerrainChunk c, int idx, float now)
        {
            if (c.State[idx] != TerrainChunk.Done) c.DoneCount++;
            c.State[idx] = TerrainChunk.Done;
            c.Stamp[idx] = now;
        }

        /// <summary>Stores a floor height. Returns true when the floor appeared, vanished or moved by more than <see cref="BigChange"/>.</summary>
        private bool SetFloor(TerrainChunk c, int idx, float v)
        {
            float old = c.Floor[idx];
            bool oldNaN = float.IsNaN(old), newNaN = float.IsNaN(v);
            if (oldNaN && newNaN) return false;
            if (oldNaN != newNaN || Math.Abs(old - v) > ChangeEps)
            {
                c.Floor[idx] = v;
                MarkDirty(c, idx);
                return oldNaN != newNaN || Math.Abs(old - v) > BigChange;
            }
            return false;
        }

        private void SetCeil(TerrainChunk c, int idx, float v)
        {
            float old = c.Ceil[idx];
            bool oldNaN = float.IsNaN(old), newNaN = float.IsNaN(v);
            if (oldNaN && newNaN) return;
            if (oldNaN != newNaN || Math.Abs(old - v) > ChangeEps)
            {
                c.Ceil[idx] = v;
                c.Dirty = true; // ceilings are chunk-local: the neighbours' quads do not depend on them
            }
        }

        /// <summary>A floor changed: this chunk's mesh, and the neighbours whose edge quads use this sample, must be rebuilt.</summary>
        private void MarkDirty(TerrainChunk c, int idx)
        {
            c.Dirty = true;
            int lx = idx & 15, lz = idx >> 4;
            if (lx == 0) MarkChunkDirty(c.Cx - 1, c.Cz);
            if (lz == 0) MarkChunkDirty(c.Cx, c.Cz - 1);
            if (lx == 0 && lz == 0) MarkChunkDirty(c.Cx - 1, c.Cz - 1);
        }

        private void MarkChunkDirty(int cx, int cz)
        {
            var n = GetChunk(cx, cz);
            if (n != null) n.Dirty = true;
        }

        // ---------------------------------------------------------------------------------------
        // eviction, meshes
        // ---------------------------------------------------------------------------------------

        private void Evict(double fx, double fz)
        {
            double len = TerrainChunk.Size * (double)_cell;
            double lim = _radius + EvictMargin;
            double lim2 = lim * lim;
            _scratchChunks.Clear();
            foreach (var kv in _chunks)
            {
                var c = kv.Value;
                double bx = c.Cx * len, bz = c.Cz * len;
                double nx = Math.Max(bx, Math.Min(fx, bx + len)) - fx;
                double nz = Math.Max(bz, Math.Min(fz, bz + len)) - fz;
                if (nx * nx + nz * nz > lim2) _scratchChunks.Add(c);
            }
            for (int i = 0; i < _scratchChunks.Count; i++)
            {
                var c = _scratchChunks[i];
                _chunks.Remove(c.Key());
                c.DestroyObjects();
            }
            _scratchChunks.Clear();
        }

        private void RebuildDirty(double fx, double fz, float now)
        {
            double len = TerrainChunk.Size * (double)_cell;
            double urgent2 = (double)UrgentRadius * UrgentRadius;
            for (int n = 0; n < MaxBuildsPerFrame; n++)
            {
                TerrainChunk best = null;
                double bestD = double.MaxValue;
                foreach (var kv in _chunks)
                {
                    var c = kv.Value;
                    if (!c.Dirty && !c.WallsDirty) continue;
                    // Distance to the chunk's rectangle: the chunk V1 stands in always sorts first.
                    double bx = c.Cx * len, bz = c.Cz * len;
                    double dx = Math.Max(bx, Math.Min(fx, bx + len)) - fx;
                    double dz = Math.Max(bz, Math.Min(fz, bz + len)) - fz;
                    double d = dx * dx + dz * dz;
                    float minInterval = d <= urgent2 ? UrgentRebuildInterval : MinRebuildInterval;
                    if (c.BuiltOnce && now - c.LastBuild < minInterval) continue;
                    if (d < bestD) { bestD = d; best = c; }
                }
                if (best == null) return;
                BuildChunk(best, now);
            }
        }

        private void BuildChunk(TerrainChunk c, float now)
        {
            var map = _map;
            if (map == null) return;

            // Gather the 17 x 17 floor and ceiling grids (the last row/column come from the +x, +z neighbours).
            _nb[0] = c;
            _nb[1] = GetChunk(c.Cx + 1, c.Cz);
            _nb[2] = GetChunk(c.Cx, c.Cz + 1);
            _nb[3] = GetChunk(c.Cx + 1, c.Cz + 1);
            const int G = TerrainMeshBuilder.Grid;
            for (int j = 0; j < G; j++)
            {
                int rowN = (j >> 4) << 1;
                int lz = j & 15;
                for (int i = 0; i < G; i++)
                {
                    var nc = _nb[rowN + (i >> 4)];
                    _floorGrid[j * G + i] = nc == null ? float.NaN : nc.Floor[(lz << 4) | (i & 15)];
                    _ceilGrid17[j * G + i] = nc == null ? float.NaN : nc.Ceil[(lz << 4) | (i & 15)];
                }
            }
            Array.Copy(c.Ceil, _ceilGrid, TerrainChunk.Cells);
            for (int i = 0; i < 4; i++) _nb[i] = null;

            // Wall boxes first: they use the measured floors; the mesh builder then fills single-sample gaps in the grid in place.
            int x0 = c.Cx * TerrainChunk.Size, z0 = c.Cz * TerrainChunk.Size;
            _wallGeo.Build(x0, z0, _wp, _floorGrid, _ceilGrid17, c.EMask, c.EHit, _boxes);

            if (!c.Dirty && c.BuiltOnce)
            {
                // Only wall rays changed: the (expensive to cook) mesh colliders stay as they are.
                ApplyBoxes(c, map);
                c.WallsDirty = false;
                c.LastBuild = now;
                return;
            }

            _builder.Build(map, x0, z0, _cell,
                Mathf.Max(0.05f, BridgeConfig.TerrainStepHeight.Value), _floorGrid, _ceilGrid);

            ApplyMesh(c, TerrainChunk.Floor_, _builder.FloorV, _builder.FloorT, "Floor", "Floor");
            ApplyMesh(c, TerrainChunk.Wall_, _builder.WallV, _builder.WallT, "Walls", "Wall");
            ApplyMesh(c, TerrainChunk.Ceil_, _builder.CeilV, _builder.CeilT, "Ceiling", null);
            ApplyBoxes(c, map);

            c.Dirty = false;
            c.WallsDirty = false;
            c.BuiltOnce = true;
            c.LastBuild = now;
        }

        private void EnsureRoot(TerrainChunk c)
        {
            if (c.Root != null) return;
            c.Root = new GameObject($"UltraRing terrain {c.Cx},{c.Cz}");
            c.Root.AddComponent<BridgeMarker>();
            if (_parent != null) c.Root.transform.SetParent(_parent, false);
        }

        /// <summary>A collider object: layer 8 (Environment), optionally tagged.</summary>
        private GameObject NewColliderObject(TerrainChunk c, string name, string tag)
        {
            EnsureRoot(c);
            var go = new GameObject(name);
            go.layer = 8; // Environment
            if (tag != null)
            {
                try { go.tag = tag; }
                catch (UnityException)
                {
                    if (!_tagWarned) { _tagWarned = true; Plugin.Log.LogWarning($"Tag '{tag}' is not defined; terrain colliders left untagged."); }
                }
            }
            go.transform.SetParent(c.Root.transform, false);
            return go;
        }

        private void ApplyMesh(TerrainChunk c, int slot, List<Vector3> verts, List<int> tris, string name, string tag)
        {
            if (tris.Count == 0)
            {
                if (c.Cols[slot] != null)
                {
                    c.Cols[slot].sharedMesh = null;
                    c.Gos[slot].SetActive(false);
                }
                return;
            }

            if (c.Gos[slot] == null)
            {
                var go = NewColliderObject(c, name, tag);
                var col = go.AddComponent<MeshCollider>();
                col.convex = false;
                col.isTrigger = false;
                var mesh = new Mesh { name = $"UltraRing terrain {name} {c.Cx},{c.Cz}" };
                mesh.MarkDynamic();
                c.Gos[slot] = go;
                c.Cols[slot] = col;
                c.Meshes[slot] = mesh;
            }

            var m = c.Meshes[slot];
            var collider = c.Cols[slot];
            c.Gos[slot].SetActive(true);
            collider.sharedMesh = null;
            m.Clear();
            m.indexFormat = verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            m.SetVertices(verts);
            m.SetTriangles(tris, 0, true);
            m.UploadMeshData(false); // keep it readable: ULTRAKILL's WallCheck walks the triangles
            collider.sharedMesh = m;  // cooks the collider
            if (DebugDraw) EnsureVisual(c, slot);
        }

        /// <summary>Wall boxes (from the horizontal rays) as BoxColliders on one layer-8 "Wall" object, reusing existing colliders.</summary>
        private void ApplyBoxes(TerrainChunk c, CoordMap map)
        {
            const int slot = TerrainChunk.RayWall_;
            int n = _boxes.Count;
            if (n == 0)
            {
                if (c.Gos[slot] != null && c.BoxBounds.Count > 0)
                {
                    for (int i = 0; i < c.Boxes.Count; i++) if (c.Boxes[i] != null) UnityEngine.Object.Destroy(c.Boxes[i]);
                    c.Boxes.Clear();
                    c.BoxBounds.Clear();
                    c.Gos[slot].SetActive(false);
                    if (c.Meshes[slot] != null) c.Meshes[slot].Clear();
                }
                return;
            }

            if (c.Gos[slot] == null) c.Gos[slot] = NewColliderObject(c, "Ray walls", "Wall");
            var go = c.Gos[slot];
            go.SetActive(true);

            bool changed = n != c.BoxBounds.Count;
            while (c.Boxes.Count > n)
            {
                int last = c.Boxes.Count - 1;
                if (c.Boxes[last] != null) UnityEngine.Object.Destroy(c.Boxes[last]);
                c.Boxes.RemoveAt(last);
                c.BoxBounds.RemoveAt(last);
            }
            for (int i = 0; i < n; i++)
            {
                WallBox b = _boxes[i];
                Vector3 mn = map.ToUk(b.X0, b.Y0, b.Z0), mx = map.ToUk(b.X1, b.Y1, b.Z1);
                var bounds = new Bounds((mn + mx) * 0.5f, mx - mn);
                if (i >= c.Boxes.Count)
                {
                    var col = go.AddComponent<BoxCollider>();
                    col.isTrigger = false;
                    col.center = bounds.center;
                    col.size = bounds.size;
                    c.Boxes.Add(col);
                    c.BoxBounds.Add(bounds);
                    changed = true;
                    continue;
                }
                Bounds old = c.BoxBounds[i];
                if ((old.center - bounds.center).sqrMagnitude > 1e-6f || (old.size - bounds.size).sqrMagnitude > 1e-6f)
                {
                    c.Boxes[i].center = bounds.center;
                    c.Boxes[i].size = bounds.size;
                    c.BoxBounds[i] = bounds;
                    changed = true;
                }
            }
            if (DebugDraw && (changed || c.Vis[slot] == null)) EnsureVisual(c, slot);
        }

        // ---------------------------------------------------------------------------------------
        // debug view
        // ---------------------------------------------------------------------------------------

        private void ApplyDebugState()
        {
            _debugApplied = DebugDraw;
            foreach (var kv in _chunks)
            {
                var c = kv.Value;
                for (int slot = 0; slot < TerrainChunk.Slots; slot++)
                {
                    if (DebugDraw) EnsureVisual(c, slot);
                    else DestroyVisual(c, slot);
                }
            }
            Plugin.Log.LogInfo($"Terrain debug view {(DebugDraw ? "ON (green floors, red walls, blue ceilings)" : "OFF")}.");
        }

        private static void DestroyVisual(TerrainChunk c, int slot)
        {
            if (c.Vis[slot] != null) UnityEngine.Object.Destroy(c.Vis[slot]);
            c.Vis[slot] = null;
        }

        /// <summary>A translucent renderer on a collider-free child of the slot's object, on layer 0 so the bridge's world camera draws it.</summary>
        private void EnsureVisual(TerrainChunk c, int slot)
        {
            var host = c.Gos[slot];
            if (host == null) return;
            var mats = GetDebugMaterials();
            if (mats == null) return;

            Mesh mesh;
            Material mat;
            if (slot == TerrainChunk.RayWall_)
            {
                if (c.Meshes[slot] == null) c.Meshes[slot] = new Mesh { name = $"UltraRing terrain debug boxes {c.Cx},{c.Cz}" };
                mesh = c.Meshes[slot];
                BuildBoxMesh(c.BoxBounds, mesh);
                mat = mats[1];
            }
            else
            {
                mesh = c.Meshes[slot];
                if (mesh == null) return;
                mat = mats[slot == TerrainChunk.Floor_ ? 0 : slot == TerrainChunk.Wall_ ? 1 : 2];
            }

            GameObject vis = c.Vis[slot];
            if (vis == null)
            {
                vis = new GameObject("Debug visual");
                vis.layer = 0; // Default: in the world camera's culling mask (layer 8 itself is excluded)
                vis.transform.SetParent(host.transform, false);
                var mf = vis.AddComponent<MeshFilter>();
                var mr = vis.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mf.sharedMesh = mesh;
                mr.sharedMaterial = mat;
                c.Vis[slot] = vis;
            }
            else
            {
                vis.GetComponent<MeshFilter>().sharedMesh = mesh;
                vis.GetComponent<MeshRenderer>().sharedMaterial = mat;
            }
        }

        private void BuildBoxMesh(List<Bounds> boxes, Mesh mesh)
        {
            _visV.Clear();
            _visT.Clear();
            for (int i = 0; i < boxes.Count; i++)
            {
                Vector3 mn = boxes[i].min, mx = boxes[i].max;
                int b = _visV.Count;
                _visV.Add(new Vector3(mn.x, mn.y, mn.z)); _visV.Add(new Vector3(mx.x, mn.y, mn.z));
                _visV.Add(new Vector3(mx.x, mn.y, mx.z)); _visV.Add(new Vector3(mn.x, mn.y, mx.z));
                _visV.Add(new Vector3(mn.x, mx.y, mn.z)); _visV.Add(new Vector3(mx.x, mx.y, mn.z));
                _visV.Add(new Vector3(mx.x, mx.y, mx.z)); _visV.Add(new Vector3(mn.x, mx.y, mx.z));
                // Both windings (the materials may cull): 6 quads, each twice.
                int[] q = { 0, 1, 2, 3, 4, 7, 6, 5, 0, 4, 5, 1, 1, 5, 6, 2, 2, 6, 7, 3, 3, 7, 4, 0 };
                for (int f = 0; f < 6; f++)
                {
                    int a = b + q[f * 4], bb = b + q[f * 4 + 1], cc = b + q[f * 4 + 2], d = b + q[f * 4 + 3];
                    _visT.Add(a); _visT.Add(bb); _visT.Add(cc); _visT.Add(a); _visT.Add(cc); _visT.Add(d);
                    _visT.Add(a); _visT.Add(cc); _visT.Add(bb); _visT.Add(a); _visT.Add(d); _visT.Add(cc);
                }
            }
            mesh.Clear();
            mesh.SetVertices(_visV);
            mesh.SetTriangles(_visT, 0, true);
        }

        /// <summary>Floor green, wall red, ceiling blue, 35 % alpha. Tries several shaders the player build is likely to contain.</summary>
        private static Material[] GetDebugMaterials()
        {
            if (_dbgMats != null) return _dbgMats;
            if (_dbgMatsFailed) return null;
            string[] candidates = { "Sprites/Default", "UI/Default", "Unlit/Transparent", "Legacy Shaders/Transparent/Diffuse", "Hidden/Internal-Colored" };
            Shader sh = null;
            foreach (var name in candidates)
            {
                Shader s;
                try { s = Shader.Find(name); }
                catch (Exception) { s = null; }
                if (s != null && s.isSupported)
                {
                    Plugin.Log.LogInfo($"Terrain debug view: using shader '{name}'.");
                    sh = s;
                    break;
                }
                Plugin.Log.LogInfo($"Terrain debug view: shader '{name}' {(s == null ? "not found" : "not supported")}.");
            }
            if (sh == null)
            {
                _dbgMatsFailed = true;
                Plugin.Log.LogWarning("Terrain debug view: no usable shader; nothing will be drawn.");
                return null;
            }
            var cols = new[] { new Color(0.1f, 0.9f, 0.2f, 0.35f), new Color(0.95f, 0.1f, 0.1f, 0.35f), new Color(0.15f, 0.35f, 1f, 0.35f) };
            var mats = new Material[3];
            for (int i = 0; i < 3; i++)
            {
                var m = new Material(sh) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 3000 };
                if (sh.name == "Unlit/Transparent")
                {
                    var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                    t.SetPixel(0, 0, cols[i]);
                    t.Apply();
                    m.mainTexture = t;
                }
                else if (m.HasProperty("_Color")) m.SetColor("_Color", cols[i]);
                if (m.HasProperty("_SrcBlend")) m.SetInt("_SrcBlend", 5);  // SrcAlpha
                if (m.HasProperty("_DstBlend")) m.SetInt("_DstBlend", 10); // OneMinusSrcAlpha
                if (m.HasProperty("_Cull")) m.SetInt("_Cull", 0);
                if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 0);
                mats[i] = m;
            }
            _dbgMats = mats;
            return mats;
        }

        // ---------------------------------------------------------------------------------------
        // status
        // ---------------------------------------------------------------------------------------

        private void RefreshStatus(float now)
        {
            _statusAt = now;
            if (now - _windowStart >= 2f)
            {
                float dt = Mathf.Max(0.001f, now - _windowStart);
                _batchRate = _batchesInWindow / dt;
                _rayRate = _raysInWindow / dt;
                _wallRayRate = _wallRaysInWindow / dt;
                _batchesInWindow = 0;
                _raysInWindow = 0;
                _wallRaysInWindow = 0;
                _windowStart = now;
            }
            int done = 0, built = 0, dirty = 0, boxes = 0, edges = 0;
            foreach (var kv in _chunks)
            {
                var c = kv.Value;
                done += c.DoneCount;
                if (c.BuiltOnce) built++;
                if (c.Dirty || c.WallsDirty) dirty++;
                boxes += c.Boxes.Count;
                for (int i = 0; i < c.EState.Length; i++) if (c.EState[i] == TerrainChunk.WDone && c.EMask[i] != 0) edges++;
            }
            string batch = _inflight
                ? $"batch #{_seq} in flight {_inflightCount} rays ({now - _inflightSince:F1}s)"
                : $"last batch #{_lastSeq} done, idle";
            _status = $"cells {done} sampled, {_chunks.Count} chunks ({built} built, {dirty} dirty), {batch}, "
                      + $"{_batchRate:F1} batches/s, {_rayRate:F0} rays/s ({_wallRayRate:F0} wall), walls {edges} edges -> {boxes} boxes, "
                      + $"ground ref {(_haveRef ? _groundRef.ToString("F1") : "-")} m{(DebugDraw ? ", DEBUG VIEW" : "")}";
        }
    }
}
