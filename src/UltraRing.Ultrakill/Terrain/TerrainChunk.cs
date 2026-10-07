using UnityEngine;

namespace UltraRing.Ultrakill.Terrain
{
    /// <summary>
    /// One 16 x 16 block of sampling cells (8 x 8 m at the default 0.5 m cell). Holds the raw samples in host metres
    /// and the Unity objects (root + up to three mesh colliders) built from them.
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

        // Collider slots.
        public const int Floor_ = 0, Wall_ = 1, Ceil_ = 2;

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
        public int DoneCount;

        public bool Dirty;
        public bool BuiltOnce;
        public float LastBuild;

        public GameObject Root;
        public readonly GameObject[] Gos = new GameObject[3];
        public readonly MeshCollider[] Cols = new MeshCollider[3];
        public readonly Mesh[] Meshes = new Mesh[3];

        public TerrainChunk(int cx, int cz)
        {
            Cx = cx;
            Cz = cz;
            for (int i = 0; i < Cells; i++)
            {
                Floor[i] = float.NaN;
                Ceil[i] = float.NaN;
            }
        }

        public static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;

        public long Key() => Key(Cx, Cz);

        public void DestroyObjects()
        {
            Alive = false;
            for (int i = 0; i < 3; i++)
            {
                if (Meshes[i] != null) Object.Destroy(Meshes[i]);
                Meshes[i] = null;
                Cols[i] = null;
                Gos[i] = null;
            }
            if (Root != null) Object.Destroy(Root);
            Root = null;
        }
    }
}
