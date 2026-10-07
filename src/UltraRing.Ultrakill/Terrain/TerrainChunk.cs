using System.Collections.Generic;
using UnityEngine;

namespace UltraRing.Ultrakill.Terrain
{
    /// <summary>
    /// One 16 x 16 block of sampling cells (8 x 8 m at the default 0.5 m cell). Holds the raw samples in host metres
    /// and the Unity objects (root, up to three mesh colliders, one box-collider wall object, debug visuals) built from them.
    /// </summary>
    internal sealed class TerrainChunk
    {
        public const int Size = 16;
        public const int Cells = Size * Size;

        // Per-cell sampling phases.
        public const byte NeedLow = 0;   // never sampled or stale: cast the low floor ray
        public const byte NeedHigh = 1;  // low ray missed: cast the high floor ray (ledges above the headroom)
        public const byte NeedCeil = 2;  // floor known: cast the ceiling ray
        public const byte Done = 3;

        // Per-edge wall phases.
        public const byte WNeed = 0;     // rays must be (re)cast
        public const byte WPending = 1;  // rays in flight
        public const byte WDone = 2;

        // Collider slots. Floor_/Wall_/Ceil_ are mesh colliders; RayWall_ holds box colliders.
        public const int Floor_ = 0, Wall_ = 1, Ceil_ = 2, RayWall_ = 3;
        public const int Slots = 4;

        public readonly int Cx, Cz;
        public bool Alive = true;

        /// <summary>Floor height per cell in host metres, NaN where there is no floor (unsampled, void).</summary>
        public readonly float[] Floor = new float[Cells];
        /// <summary>Ceiling height per cell in host metres, NaN where none was found above the floor.</summary>
        public readonly float[] Ceil = new float[Cells];
        /// <summary>Vertical reference (host m) the cell's rays were cast from.</summary>
        public readonly float[] Ref = new float[Cells];
        /// <summary>Time (unscaled seconds) of the cell's last completed sample.</summary>
        public readonly float[] Stamp = new float[Cells];
        public readonly byte[] State = new byte[Cells];
        /// <summary>Bumped when a cell is invalidated so answers to older in-flight rays are ignored.</summary>
        public readonly ushort[] Ver = new ushort[Cells];
        /// <summary>Batch tag of the last ray this cell contributed to (stops a cell being queued twice in a batch).</summary>
        public readonly ushort[] Queued = new ushort[Cells];
        public int DoneCount;

        // ---- persistent cache bookkeeping ----
        /// <summary>The cell came from the cache and has not been re-sampled in this session yet.</summary>
        public readonly bool[] Cached = new bool[Cells];
        public int CachedLeft;
        /// <summary>The cache holds a record for the cell / edge.</summary>
        public readonly bool[] InCache = new bool[Cells];
        public readonly bool[] EInCache = new bool[2 * Cells];
        /// <summary>The cell / edge has data the cache record does not (yet).</summary>
        public readonly bool[] NeedSave = new bool[Cells];
        public readonly bool[] ENeedSave = new bool[2 * Cells];
        /// <summary>Some NeedSave / ENeedSave is set.</summary>
        public bool CacheDirty;

        // ---- horizontal wall rays: two edges per cell (0 = towards +x, 1 = towards +z), index edge * Cells + cell ----
        public readonly byte[] EState = new byte[2 * Cells];
        /// <summary>Which directions were cast: bit0 from this cell towards the neighbour, bit1 from the neighbour back.</summary>
        public readonly byte[] EMask = new byte[2 * Cells];
        public readonly byte[] EPend = new byte[2 * Cells];
        /// <summary>The floor height the rays' heights were relative to (NaN when nothing was cast).</summary>
        public readonly float[] EBase = new float[2 * Cells];
        public readonly float[] EStamp = new float[2 * Cells];
        /// <summary>6 hit distances per edge (metres from each ray's origin along the edge, NaN = miss / not cast):
        /// forward knee, chest, head, then reverse knee, chest, head.</summary>
        public readonly float[] EHit = new float[2 * Cells * 6];

        /// <summary>Floors or ceilings changed: the whole chunk (meshes and boxes) is rebuilt.</summary>
        public bool Dirty;
        /// <summary>Only wall ray results changed: just the boxes are rebuilt (floors stay valid for <c>HasGroundBelow</c>).</summary>
        public bool WallsDirty;
        public bool BuiltOnce;
        public float LastBuild;

        public GameObject Root;
        public readonly GameObject[] Gos = new GameObject[Slots];
        public readonly MeshCollider[] Cols = new MeshCollider[3];
        public readonly Mesh[] Meshes = new Mesh[Slots];
        public readonly List<BoxCollider> Boxes = new List<BoxCollider>();
        /// <summary>The wall boxes in ULTRAKILL space (what the BoxColliders currently describe).</summary>
        public readonly List<Bounds> BoxBounds = new List<Bounds>();
        /// <summary>Debug visual child per slot (null while the debug view is off).</summary>
        public readonly GameObject[] Vis = new GameObject[Slots];

        public TerrainChunk(int cx, int cz)
        {
            Cx = cx;
            Cz = cz;
            for (int i = 0; i < Cells; i++)
            {
                Floor[i] = float.NaN;
                Ceil[i] = float.NaN;
            }
            for (int i = 0; i < EBase.Length; i++) EBase[i] = float.NaN;
            for (int i = 0; i < EHit.Length; i++) EHit[i] = float.NaN;
        }

        public static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;

        public long Key() => Key(Cx, Cz);

        public void DestroyObjects()
        {
            Alive = false;
            for (int i = 0; i < Slots; i++)
            {
                if (Meshes[i] != null) Object.Destroy(Meshes[i]);
                Meshes[i] = null;
                if (i < 3) Cols[i] = null;
                Gos[i] = null;
                Vis[i] = null;
            }
            Boxes.Clear();
            BoxBounds.Clear();
            if (Root != null) Object.Destroy(Root);
            Root = null;
        }
    }
}
