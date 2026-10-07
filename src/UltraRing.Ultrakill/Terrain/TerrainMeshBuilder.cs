using System;
using System.Collections.Generic;
using UnityEngine;

namespace UltraRing.Ultrakill.Terrain
{
    /// <summary>
    /// Turns one chunk's samples into collider geometry (floor, wall and ceiling triangle lists in ULTRAKILL units).
    ///
    /// Floors are a dual grid: the cell-centre samples are the vertices and every four neighbouring samples make a quad.
    /// A quad whose edges all rise less than <c>step</c> is a smooth slope (two triangles, or one when a corner has no floor).
    /// A quad with a steeper edge becomes four flat "quarters", one per corner at that corner's own height, and
    /// vertical walls are put on the mid-lines where neighbouring quarters differ in height, so V1 can wall-jump a
    /// cliff or step up a ledge but never walks up a near-vertical face.
    /// Ceilings are one flat downward-facing tile per cell.
    /// All inputs are host metres; <see cref="CoordMap"/> converts every vertex.
    /// </summary>
    internal sealed class TerrainMeshBuilder
    {
        public const int Grid = TerrainChunk.Size + 1;

        /// <summary>Walls reach this far below the lower floor so no slit remains.</summary>
        private const float WallUnder = 0.10f;
        /// <summary>Walls are lengthened by this at both ends (metres) to overlap their neighbours.</summary>
        private const double WallEnd = 0.02;
        /// <summary>Height differences below this between steep-quad quarters are not worth a wall.</summary>
        private const float MinWall = 0.05f;
        /// <summary>Ceiling tiles are enlarged by this (metres) on every side.</summary>
        private const double CeilGrow = 0.01;
        private const float MinCross2 = 1e-10f;

        public readonly List<Vector3> FloorV = new List<Vector3>(4096);
        public readonly List<int> FloorT = new List<int>(4096);
        public readonly List<Vector3> WallV = new List<Vector3>(1024);
        public readonly List<int> WallT = new List<int>(1024);
        public readonly List<Vector3> CeilV = new List<Vector3>(1024);
        public readonly List<int> CeilT = new List<int>(1024);

        private CoordMap _map;

        /// <summary>
        /// <paramref name="floors"/> is a 17 x 17 grid (row = z, column = x) of cell floors starting at cell
        /// (<paramref name="x0"/>, <paramref name="z0"/>); the last row and column belong to the neighbouring chunks.
        /// <paramref name="ceils"/> is the chunk's own 16 x 16 ceilings.
        /// </summary>
        public void Build(CoordMap map, int x0, int z0, float cell, float step, float[] floors, float[] ceils)
        {
            _map = map;
            FloorV.Clear(); FloorT.Clear();
            WallV.Clear(); WallT.Clear();
            CeilV.Clear(); CeilT.Clear();
            BuildFloorsAndWalls(x0, z0, cell, step, floors);
            BuildCeilings(x0, z0, cell, floors, ceils);
        }

        private void BuildFloorsAndWalls(int x0, int z0, float cell, float step, float[] g)
        {
            Vector3 up = Vector3.up;
            double half = cell * 0.5;
            for (int j = 0; j < TerrainChunk.Size; j++)
            {
                double za = (z0 + j + 0.5) * cell, zb = za + cell, zm = za + half;
                for (int i = 0; i < TerrainChunk.Size; i++)
                {
                    float hA = g[j * Grid + i];             // (i,   j)
                    float hB = g[j * Grid + i + 1];         // (i+1, j)
                    float hC = g[(j + 1) * Grid + i + 1];   // (i+1, j+1)
                    float hD = g[(j + 1) * Grid + i];       // (i,   j+1)
                    bool a = !float.IsNaN(hA), b = !float.IsNaN(hB), c = !float.IsNaN(hC), d = !float.IsNaN(hD);
                    int n = (a ? 1 : 0) + (b ? 1 : 0) + (c ? 1 : 0) + (d ? 1 : 0);
                    if (n < 3) continue;

                    double xa = (x0 + i + 0.5) * cell, xb = xa + cell, xm = xa + half;

                    bool steep = (a && b && Math.Abs(hA - hB) > step)
                                 || (b && c && Math.Abs(hB - hC) > step)
                                 || (c && d && Math.Abs(hC - hD) > step)
                                 || (d && a && Math.Abs(hD - hA) > step);
                    if (!steep && n == 3)
                    {
                        float lo = float.MaxValue, hi = float.MinValue;
                        if (a) { lo = Math.Min(lo, hA); hi = Math.Max(hi, hA); }
                        if (b) { lo = Math.Min(lo, hB); hi = Math.Max(hi, hB); }
                        if (c) { lo = Math.Min(lo, hC); hi = Math.Max(hi, hC); }
                        if (d) { lo = Math.Min(lo, hD); hi = Math.Max(hi, hD); }
                        steep = hi - lo > step * 1.5f; // the triangle's hypotenuse
                    }

                    if (!steep)
                    {
                        if (n == 4)
                        {
                            Vector3 vA = Uk(xa, hA, za), vB = Uk(xb, hB, za), vC = Uk(xb, hC, zb), vD = Uk(xa, hD, zb);
                            if (Math.Abs(hA - hC) <= Math.Abs(hB - hD))
                            {
                                Tri(FloorV, FloorT, vA, vB, vC, up);
                                Tri(FloorV, FloorT, vA, vC, vD, up);
                            }
                            else
                            {
                                Tri(FloorV, FloorT, vA, vB, vD, up);
                                Tri(FloorV, FloorT, vB, vC, vD, up);
                            }
                        }
                        else if (!a) Tri(FloorV, FloorT, Uk(xb, hB, za), Uk(xb, hC, zb), Uk(xa, hD, zb), up);
                        else if (!b) Tri(FloorV, FloorT, Uk(xa, hA, za), Uk(xb, hC, zb), Uk(xa, hD, zb), up);
                        else if (!c) Tri(FloorV, FloorT, Uk(xa, hA, za), Uk(xb, hB, za), Uk(xa, hD, zb), up);
                        else Tri(FloorV, FloorT, Uk(xa, hA, za), Uk(xb, hB, za), Uk(xb, hC, zb), up);
                        continue;
                    }

                    // Steep: one flat quarter per corner...
                    if (a) Flat(xa, xm, za, zm, hA);
                    if (b) Flat(xm, xb, za, zm, hB);
                    if (c) Flat(xm, xb, zm, zb, hC);
                    if (d) Flat(xa, xm, zm, zb, hD);
                    // ...and a wall wherever two neighbouring quarters differ.
                    if (a && b) WallX(xm, za, zm, hA, hB);      // A is on the -x side
                    if (d && c) WallX(xm, zm, zb, hD, hC);
                    if (a && d) WallZ(zm, xa, xm, hA, hD);      // A is on the -z side
                    if (b && c) WallZ(zm, xm, xb, hB, hC);
                }
            }
        }

        private void Flat(double x1, double x2, double z1, double z2, float h)
        {
            Vector3 p1 = Uk(x1, h, z1), p2 = Uk(x2, h, z1), p3 = Uk(x2, h, z2), p4 = Uk(x1, h, z2);
            Tri(FloorV, FloorT, p1, p2, p3, Vector3.up);
            Tri(FloorV, FloorT, p1, p3, p4, Vector3.up);
        }

        /// <summary>Wall in the plane x = <paramref name="x"/>; <paramref name="hNeg"/> is the floor on the -x side.</summary>
        private void WallX(double x, double z1, double z2, float hNeg, float hPos)
        {
            float lo = Math.Min(hNeg, hPos), hi = Math.Max(hNeg, hPos);
            if (hi - lo <= MinWall) return;
            float nx = hNeg < hPos ? -1f : 1f; // faces the lower side
            Wall(x, z1 - WallEnd, x, z2 + WallEnd, lo, hi, nx, 0f);
        }

        /// <summary>Wall in the plane z = <paramref name="z"/>; <paramref name="hNeg"/> is the floor on the -z side.</summary>
        private void WallZ(double z, double x1, double x2, float hNeg, float hPos)
        {
            float lo = Math.Min(hNeg, hPos), hi = Math.Max(hNeg, hPos);
            if (hi - lo <= MinWall) return;
            float nz = hNeg < hPos ? -1f : 1f;
            Wall(x1 - WallEnd, z, x2 + WallEnd, z, lo, hi, 0f, nz);
        }

        private void Wall(double x1, double z1, double x2, double z2, float lo, float hi, float nx, float nz)
        {
            Vector3 a = Uk(x1, lo - WallUnder, z1), b = Uk(x2, lo - WallUnder, z2);
            Vector3 c = Uk(x2, hi, z2), d = Uk(x1, hi, z1);
            var n = new Vector3(nx, 0f, nz);
            Tri(WallV, WallT, a, b, c, n);
            Tri(WallV, WallT, a, c, d, n);
        }

        private void BuildCeilings(int x0, int z0, float cell, float[] floors, float[] ceils)
        {
            double half = cell * 0.5 + CeilGrow;
            for (int j = 0; j < TerrainChunk.Size; j++)
            {
                for (int i = 0; i < TerrainChunk.Size; i++)
                {
                    float h = ceils[j * TerrainChunk.Size + i];
                    if (float.IsNaN(h) || float.IsNaN(floors[j * Grid + i])) continue;
                    double xc = (x0 + i + 0.5) * cell, zc = (z0 + j + 0.5) * cell;
                    Vector3 p1 = Uk(xc - half, h, zc - half), p2 = Uk(xc + half, h, zc - half);
                    Vector3 p3 = Uk(xc + half, h, zc + half), p4 = Uk(xc - half, h, zc + half);
                    Tri(CeilV, CeilT, p1, p2, p3, Vector3.down);
                    Tri(CeilV, CeilT, p1, p3, p4, Vector3.down);
                }
            }
        }

        private Vector3 Uk(double hostX, float hostY, double hostZ) => _map.ToUk(hostX, hostY, hostZ);

        /// <summary>Adds a triangle wound so that its front face looks along <paramref name="facing"/>; skips degenerate ones.</summary>
        private static void Tri(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 facing)
        {
            Vector3 cr = Vector3.Cross(b - a, c - a);
            if (cr.sqrMagnitude < MinCross2) return;
            int i = v.Count;
            v.Add(a);
            if (Vector3.Dot(cr, facing) >= 0f) { v.Add(b); v.Add(c); }
            else { v.Add(c); v.Add(b); }
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
        }
    }
}
