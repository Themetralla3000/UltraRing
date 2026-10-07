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
    /// Colliders: 8 x 8 m chunks, rebuilt only when their samples changed (see <see cref="TerrainMeshBuilder"/>).
    /// </summary>
    internal sealed class TerrainManager
    {
        // ---- tuning (host metres unless noted) -------------------------------------------------
        private const int MaxBatchRays = 2048;
        private const float LowHeadroom = 1.2f;     // low ray starts this far above the ground reference
        private const float LowReach = 30f;         // ... and ends this far below it
        private const float HighReach = 12f;        // high ray covers headroom .. headroom + this
        private const float CeilStart = 0.3f;       // ceiling ray starts this far above the floor
        private const float CeilReach = 5f;         // ... and goes this far up
        private const float RefStaleNear = 2.5f;    // re-sample a cell when the ground reference moved this much
        private const float RefStaleFar = 6f;       // beyond NearZone
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
        private const float ChangeEps = 0.02f;      // floor/ceiling changes smaller than this are noise
        private const float BigChange = 0.05f;      // floor changes larger than this re-run the ceiling ray

        private struct Entry
        {
            public TerrainChunk C;
            public int Idx;
            public ushort Ver;
            public byte Kind;
        }

        private readonly Transform _parent;
        private readonly Dictionary<long, TerrainChunk> _chunks = new Dictionary<long, TerrainChunk>();
        private readonly List<TerrainChunk> _scratchChunks = new List<TerrainChunk>();
        private readonly TerrainMeshBuilder _builder = new TerrainMeshBuilder();
        private readonly float[] _floorGrid = new float[TerrainMeshBuilder.Grid * TerrainMeshBuilder.Grid];
        private readonly float[] _ceilGrid = new float[TerrainChunk.Cells];
        private readonly TerrainChunk[] _nb = new TerrainChunk[4];

        private readonly float[] _rays = new float[MaxBatchRays * 6];
        private readonly Entry[] _ents = new Entry[MaxBatchRays];

        private CoordMap _map;
        private float _cell = 0.5f;
        private float _radius = 24f;
        private int[] _offX = new int[0], _offZ = new int[0];

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

            // 1. Collect the batch in flight.
            if (_inflight)
            {
                if (link.RaysDone(_seq)) ApplyResults(link, now);
                else if (link.RaysIdle)
                {
                    // The mailbox says idle but never answered our sequence: it was reset (host restart). Drop it.
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
                    }
                }
            }
            _nextScan = 0f;
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
            TerrainChunk cache = null;
            int cacheCx = int.MinValue, cacheCz = int.MinValue;
            float groundRef = _groundRef;
            float cell = _cell;
            for (int k = 0; k < _offX.Length && count < MaxBatchRays; k++)
            {
                int ix = pcx + _offX[k], iz = pcz + _offZ[k];
                int cx = ix >> 4, cz = iz >> 4;
                if (cx != cacheCx || cz != cacheCz)
                {
                    cache = GetChunk(cx, cz);
                    cacheCx = cx;
                    cacheCz = cz;
                }
                int idx = ((iz & 15) << 4) | (ix & 15);
                int kind;
                if (cache == null) kind = TerrainChunk.NeedLow;
                else
                {
                    byte st = cache.State[idx];
                    if (st == TerrainChunk.Done)
                    {
                        if (!NeedsRefresh(cache, idx, ix - vcx, iz - vcz, now)) continue;
                        cache.State[idx] = TerrainChunk.NeedLow;
                        cache.DoneCount--;
                        kind = TerrainChunk.NeedLow;
                    }
                    else kind = st;
                }
                if (cache == null) cache = CreateChunk(cx, cz);

                float x = (float)((ix + 0.5) * cell), z = (float)((iz + 0.5) * cell);
                float y0, y1;
                switch (kind)
                {
                    case TerrainChunk.NeedLow:
                        y0 = groundRef + LowHeadroom;
                        y1 = groundRef - LowReach;
                        break;
                    case TerrainChunk.NeedHigh:
                        y0 = groundRef + LowHeadroom + HighReach;
                        y1 = groundRef + LowHeadroom;
                        break;
                    default:
                        float fl = cache.Floor[idx];
                        if (float.IsNaN(fl)) { cache.State[idx] = TerrainChunk.Done; cache.DoneCount++; continue; }
                        y0 = fl + CeilStart;
                        y1 = y0 + CeilReach;
                        break;
                }
                int o = count * 6;
                _rays[o] = x; _rays[o + 1] = y0; _rays[o + 2] = z;
                _rays[o + 3] = x; _rays[o + 4] = y1; _rays[o + 5] = z;
                _ents[count].C = cache;
                _ents[count].Idx = idx;
                _ents[count].Ver = cache.Ver[idx];
                _ents[count].Kind = (byte)kind;
                count++;
            }
            return count;
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

        private unsafe void ApplyResults(GuestLink link, float now)
        {
            ErmcRayHit* hits = link.RayHits;
            float bref = _batchRef;
            for (int i = 0; i < _inflightCount; i++)
            {
                ref Entry e = ref _ents[i];
                var c = e.C;
                int idx = e.Idx;
                if (c == null) continue;
                if (!c.Alive || c.Ver[idx] != e.Ver) { e.C = null; continue; }
                bool hit = hits[i].hit != 0;
                float y = hits[i].pos[1];
                if (hit && (float.IsNaN(y) || float.IsInfinity(y))) hit = false;

                switch (e.Kind)
                {
                    case TerrainChunk.NeedLow:
                        c.Ref[idx] = bref;
                        if (hit && y <= bref + LowHeadroom + 0.05f && y >= bref - LowReach - 0.05f)
                            AfterFloor(c, idx, y, now);
                        else
                            c.State[idx] = TerrainChunk.NeedHigh;
                        break;

                    case TerrainChunk.NeedHigh:
                        c.Ref[idx] = bref;
                        if (hit && y >= bref + LowHeadroom - 0.05f && y <= bref + LowHeadroom + HighReach + 0.05f)
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
            for (int n = 0; n < MaxBuildsPerFrame; n++)
            {
                TerrainChunk best = null;
                double bestD = double.MaxValue;
                foreach (var kv in _chunks)
                {
                    var c = kv.Value;
                    if (!c.Dirty) continue;
                    if (c.BuiltOnce && now - c.LastBuild < MinRebuildInterval) continue;
                    double dx = (c.Cx + 0.5) * len - fx, dz = (c.Cz + 0.5) * len - fz;
                    double d = dx * dx + dz * dz;
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

            // Gather the 17 x 17 floor grid (the last row/column come from the +x, +z neighbours).
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
                }
            }
            Array.Copy(c.Ceil, _ceilGrid, TerrainChunk.Cells);
            for (int i = 0; i < 4; i++) _nb[i] = null;

            _builder.Build(map, c.Cx * TerrainChunk.Size, c.Cz * TerrainChunk.Size, _cell,
                Mathf.Max(0.05f, BridgeConfig.TerrainStepHeight.Value), _floorGrid, _ceilGrid);

            ApplyMesh(c, TerrainChunk.Floor_, _builder.FloorV, _builder.FloorT, "Floor", "Floor");
            ApplyMesh(c, TerrainChunk.Wall_, _builder.WallV, _builder.WallT, "Walls", "Wall");
            ApplyMesh(c, TerrainChunk.Ceil_, _builder.CeilV, _builder.CeilT, "Ceiling", null);

            c.Dirty = false;
            c.BuiltOnce = true;
            c.LastBuild = now;
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

            if (c.Root == null)
            {
                c.Root = new GameObject($"UltraRing terrain {c.Cx},{c.Cz}");
                c.Root.AddComponent<BridgeMarker>();
                if (_parent != null) c.Root.transform.SetParent(_parent, false);
            }
            if (c.Gos[slot] == null)
            {
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
        }

        // ---------------------------------------------------------------------------------------
        // status
        // ---------------------------------------------------------------------------------------

        private void RefreshStatus(float now)
        {
            _statusAt = now;
            if (now - _windowStart >= 2f)
            {
                _batchRate = _batchesInWindow / Mathf.Max(0.001f, now - _windowStart);
                _batchesInWindow = 0;
                _windowStart = now;
            }
            int done = 0, built = 0, dirty = 0;
            foreach (var kv in _chunks)
            {
                done += kv.Value.DoneCount;
                if (kv.Value.BuiltOnce) built++;
                if (kv.Value.Dirty) dirty++;
            }
            string batch = _inflight
                ? $"batch #{_seq} in flight {_inflightCount} rays ({now - _inflightSince:F1}s)"
                : $"last batch #{_lastSeq} done, idle";
            _status = $"cells {done} sampled, {_chunks.Count} chunks ({built} built, {dirty} dirty), {batch}, "
                      + $"{_batchRate:F1} batches/s, ground ref {(_haveRef ? _groundRef.ToString("F1") : "-")} m";
        }
    }
}
