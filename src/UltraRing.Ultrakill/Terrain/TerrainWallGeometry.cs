using System;
using System.Collections.Generic;

namespace UltraRing.Ultrakill.Terrain
{
    /// <summary>An axis-aligned wall volume in host metres.</summary>
    internal struct WallBox
    {
        public double X0, X1, Z0, Z1;
        public float Y0, Y1;
    }

    /// <summary>
    /// Heights and thicknesses of the horizontal wall probes, all in host metres. The probe heights scale with V1's real
    /// height (3.5 units, 1.75 m at 0.5 m/unit): knee 0.2, chest 0.57 and head 0.97 of it.
    /// </summary>
    internal struct WallParams
    {
        /// <summary>Ray heights above the edge's base floor (the higher of the two cell floors).</summary>
        public float KneeY, ChestY, HeadY;
        /// <summary>Box extents above the base floor for the hit combinations.</summary>
        public float KneeTop, ChestTop, BeamBottom, FullTop;
        /// <summary>Boxes reach this far below the lowest floor so no slit remains.</summary>
        public float Under;
        /// <summary>Step-up height of V1 (2.1 units).</summary>
        public float Climb;
        /// <summary>Minimum box thickness, and the thickness assumed when only one side of an obstacle was seen.</summary>
        public float MinThick, OneSideThick;
        public float Cell;

        public static WallParams For(float cell, float metresPerUnit)
        {
            float s = 2f * metresPerUnit; // V1 is 3.5 units: 1.75 m at 0.5 m/unit gives s = 1
            return new WallParams
            {
                Cell = cell,
                KneeY = 0.35f * s,
                ChestY = 1.0f * s,
                HeadY = 1.7f * s,
                KneeTop = 0.5f * s,
                ChestTop = 1.35f * s,
                BeamBottom = 1.45f * s,
                FullTop = 2.5f * s,
                Under = 0.1f,
                Climb = 2.1f * metresPerUnit,
                MinThick = 0.12f,
                OneSideThick = 0.2f,
            };
        }
    }

    /// <summary>
    /// Turns horizontal ray hits into wall boxes (Unity-free so it can be tested on its own).
    ///
    /// Every cell owns two edges: towards +x and towards +z. Each edge is probed at three heights above its base floor,
    /// from the cell centre to the neighbour's centre (forward) and back (reverse; rays that start inside solid geometry miss,
    /// so both ends are needed). A hit says there is an obstacle between the two centres at that height. The hits of one
    /// edge are combined into one box: its along-axis extent comes from the actual hit distances (sub-cell precision), its
    /// vertical extent from which heights were blocked (all three: full wall, knee only: low obstacle, head only: beam).
    /// Neighbouring edges with the same profile are merged into long boxes.
    /// </summary>
    internal sealed class TerrainWallGeometry
    {
        public const int Size = 16;
        public const int Grid = Size + 1;
        private const int Cells = Size * Size;

        private const float MergeTol = 0.1f;
        private const double Overlap = 0.02;

        public const int KneeBit = 1, ChestBit = 2, HeadBit = 4;

        private struct Ew
        {
            public bool Has;
            public float Lo, Hi, Y0, Y1;
        }

        private readonly Ew[] _ew = new Ew[2 * Cells];

        /// <summary>Bit set of the bands hit among three consecutive entries (NaN = miss).</summary>
        public static int BandMask(float[] hits, int o)
        {
            int m = 0;
            if (!float.IsNaN(hits[o])) m |= KneeBit;
            if (!float.IsNaN(hits[o + 1])) m |= ChestBit;
            if (!float.IsNaN(hits[o + 2])) m |= HeadBit;
            return m;
        }

        /// <summary>Which directions an edge between floors <paramref name="fA"/> (this cell) and <paramref name="fB"/> (neighbour) is probed from.</summary>
        public static int PlanMask(float fA, float fB, float climb, out float baseY)
        {
            bool a = !float.IsNaN(fA), b = !float.IsNaN(fB);
            if (!a && !b) { baseY = float.NaN; return 0; }
            if (a && b)
            {
                baseY = Math.Max(fA, fB);
                // A cliff-sized difference is a floor wall already; only the higher cell probes outwards.
                if (Math.Abs(fA - fB) > climb) return fA > fB ? 1 : 2;
                return 3;
            }
            baseY = a ? fA : fB;
            return a ? 1 : 2;
        }

        /// <summary>
        /// One edge's hits to a box: along-axis extent <paramref name="lo"/>..<paramref name="hi"/> (metres from this cell's centre
        /// towards the neighbour) and absolute heights <paramref name="y0"/>..<paramref name="y1"/>. Returns false when nothing blocks.
        /// </summary>
        public static bool ComputeEdge(in WallParams p, float fA, float fB, float ceilMin, float[] hits, int o,
            out float lo, out float hi, out float y0, out float y1)
        {
            lo = hi = y0 = y1 = 0f;
            bool aOk = !float.IsNaN(fA), bOk = !float.IsNaN(fB);
            if (!aOk && !bOk) return false;

            int mf = BandMask(hits, o), mr = BandMask(hits, o + 3);
            // A knee-only hit towards higher ground within V1's step-up height is a stair riser or ramp, not a wall.
            if (mf == KneeBit && aOk && bOk && fB - fA > 0.05f && fB - fA <= p.Climb) mf = 0;
            if (mr == KneeBit && aOk && bOk && fA - fB > 0.05f && fA - fB <= p.Climb) mr = 0;
            int mask = mf | mr;
            if (mask == 0) return false;

            float c = p.Cell;
            float pf = float.MaxValue, qr = float.MinValue;
            if (mf != 0)
                for (int b = 0; b < 3; b++)
                    if (!float.IsNaN(hits[o + b])) pf = Math.Min(pf, hits[o + b]);
            if (mr != 0)
                for (int b = 0; b < 3; b++)
                    if (!float.IsNaN(hits[o + 3 + b])) qr = Math.Max(qr, c - hits[o + 3 + b]);

            if (mf != 0 && mr != 0)
            {
                lo = pf;
                hi = qr;
                if (hi - lo > c) { hi = lo + p.OneSideThick; } // contradictory faces: trust the near one
                if (hi - lo < p.MinThick)
                {
                    float mid = (lo + hi) * 0.5f;
                    lo = mid - p.MinThick * 0.5f;
                    hi = mid + p.MinThick * 0.5f;
                }
            }
            else if (mf != 0)
            {
                lo = pf;
                hi = Math.Min(pf + p.OneSideThick, c + 0.1f);
                if (hi - lo < p.MinThick) lo = hi - p.MinThick;
            }
            else
            {
                hi = qr;
                lo = Math.Max(qr - p.OneSideThick, -0.1f);
                if (hi - lo < p.MinThick) hi = lo + p.MinThick;
            }

            float baseY = aOk && bOk ? Math.Max(fA, fB) : (aOk ? fA : fB);
            float lowest = aOk && bOk ? Math.Min(fA, fB) : baseY;
            if ((mask & KneeBit) != 0) y0 = lowest - p.Under;
            else if ((mask & ChestBit) != 0) y0 = baseY + p.KneeTop;
            else y0 = baseY + p.BeamBottom;
            if ((mask & HeadBit) != 0) y1 = baseY + p.FullTop;
            else if ((mask & ChestBit) != 0) y1 = baseY + p.ChestTop;
            else y1 = baseY + p.KneeTop;
            if (!float.IsNaN(ceilMin) && ceilMin < y1 && ceilMin > y0 + 0.2f) y1 = ceilMin + 0.02f;
            return y1 - y0 >= 0.15f;
        }

        /// <summary>
        /// Boxes for one chunk. <paramref name="floors"/> and <paramref name="ceils"/> are 17 x 17 grids (row = z) starting at cell
        /// (<paramref name="x0"/>, <paramref name="z0"/>); <paramref name="emask"/> and <paramref name="ehit"/> are the chunk's edge data.
        /// </summary>
        public void Build(int x0, int z0, in WallParams p, float[] floors, float[] ceils, byte[] emask, float[] ehit, List<WallBox> output)
        {
            output.Clear();
            for (int e = 0; e < 2; e++)
            {
                for (int j = 0; j < Size; j++)
                {
                    for (int i = 0; i < Size; i++)
                    {
                        int ei = e * Cells + j * Size + i;
                        Ew w = default;
                        if (emask[ei] != 0)
                        {
                            int gA = j * Grid + i;
                            int gB = e == 0 ? gA + 1 : gA + Grid;
                            float cA = ceils[gA], cB = ceils[gB];
                            float cm = float.IsNaN(cA) ? cB : (float.IsNaN(cB) ? cA : Math.Min(cA, cB));
                            if (ComputeEdge(p, floors[gA], floors[gB], cm, ehit, ei * 6, out float lo, out float hi, out float y0, out float y1))
                                w = new Ew { Has = true, Lo = lo, Hi = hi, Y0 = y0, Y1 = y1 };
                        }
                        _ew[ei] = w;
                    }
                }
            }

            double cell = p.Cell;
            // X edges: the box lies across x, runs of cells along z.
            for (int i = 0; i < Size; i++)
                MergeRuns(0, i, x0, z0, cell, output);
            // Z edges: the box lies across z, runs of cells along x.
            for (int j = 0; j < Size; j++)
                MergeRuns(1, j, x0, z0, cell, output);
        }

        private void MergeRuns(int e, int line, int x0, int z0, double cell, List<WallBox> output)
        {
            int start = -1;
            Ew prev = default, acc = default;
            for (int k = 0; k <= Size; k++)
            {
                Ew w = default;
                if (k < Size)
                {
                    int ei = e == 0 ? e * Cells + k * Size + line : e * Cells + line * Size + k;
                    w = _ew[ei];
                }
                bool extend = start >= 0 && w.Has && Math.Abs(w.Lo - prev.Lo) <= MergeTol && Math.Abs(w.Hi - prev.Hi) <= MergeTol
                              && Math.Abs(w.Y0 - prev.Y0) <= MergeTol && Math.Abs(w.Y1 - prev.Y1) <= MergeTol;
                if (extend)
                {
                    acc.Lo = Math.Min(acc.Lo, w.Lo);
                    acc.Hi = Math.Max(acc.Hi, w.Hi);
                    acc.Y0 = Math.Min(acc.Y0, w.Y0);
                    acc.Y1 = Math.Max(acc.Y1, w.Y1);
                    prev = w;
                    continue;
                }
                if (start >= 0) Emit(e, line, start, k - 1, acc, x0, z0, cell, output);
                if (w.Has) { start = k; acc = w; prev = w; }
                else start = -1;
            }
        }

        private static void Emit(int e, int line, int k0, int k1, Ew a, int x0, int z0, double cell, List<WallBox> output)
        {
            var b = new WallBox { Y0 = a.Y0, Y1 = a.Y1 };
            if (e == 0)
            {
                double xa = (x0 + line + 0.5) * cell;
                b.X0 = xa + a.Lo; b.X1 = xa + a.Hi;
                b.Z0 = (z0 + k0) * cell - Overlap; b.Z1 = (z0 + k1 + 1) * cell + Overlap;
            }
            else
            {
                double za = (z0 + line + 0.5) * cell;
                b.Z0 = za + a.Lo; b.Z1 = za + a.Hi;
                b.X0 = (x0 + k0) * cell - Overlap; b.X1 = (x0 + k1 + 1) * cell + Overlap;
            }
            output.Add(b);
        }
    }
}
