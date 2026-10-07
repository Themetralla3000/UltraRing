using System;
using System.IO;
using UltraRing.Ultrakill.Terrain;

/// <summary>Unity-free checks of the persistent terrain cache (TerrainCache.cs is compiled into this project).</summary>
public static class CacheTests
{
    private static int _failures;

    private static void Expect(bool cond, string name)
    {
        if (!cond) _failures++;
        Console.WriteLine($"{(cond ? "ok  " : "FAIL")} terrain cache: {name}");
    }

    private static CacheChunk Sample(int cx, int cz)
    {
        var c = new CacheChunk(cx, cz);
        for (int i = 0; i < CacheChunk.Cells; i += 3)
        {
            c.Flags[i] = 1;
            c.Floor[i] = i % 7 == 0 ? float.NaN : 10f + i * 0.01f;
            c.Ceil[i] = i % 5 == 0 ? float.NaN : 14.5f;
            c.Ref[i] = 10f;
            c.Time[i] = 1_700_000_000u + (uint)i;
        }
        for (int e = 0; e < 2 * CacheChunk.Cells; e += 4)
        {
            c.EFlags[e] = 1;
            c.EMask[e] = (byte)(e % 3);
            c.ETime[e] = 1_700_000_100u;
            if (c.EMask[e] == 0) continue;
            c.EBase[e] = 10.25f;
            for (int s = 0; s < 6; s++) c.EHit[e * 6 + s] = s % 2 == 0 ? TerrainCache.PackHit(0.123f * s) : (ushort)0xFFFF;
        }
        return c;
    }

    private static bool Same(CacheChunk a, CacheChunk b)
    {
        if (a.Cx != b.Cx || a.Cz != b.Cz) return false;
        for (int i = 0; i < CacheChunk.Cells; i++)
        {
            if (a.Flags[i] != b.Flags[i]) return false;
            if ((a.Flags[i] & 1) == 0) continue;
            if (!F(a.Floor[i], b.Floor[i]) || !F(a.Ceil[i], b.Ceil[i]) || !F(a.Ref[i], b.Ref[i]) || a.Time[i] != b.Time[i]) return false;
        }
        for (int e = 0; e < a.EFlags.Length; e++)
        {
            if (a.EFlags[e] != b.EFlags[e]) return false;
            if ((a.EFlags[e] & 1) == 0) continue;
            if (a.EMask[e] != b.EMask[e] || a.ETime[e] != b.ETime[e]) return false;
            if (a.EMask[e] == 0) continue;
            if (!F(a.EBase[e], b.EBase[e])) return false;
            for (int s = 0; s < 6; s++) if (a.EHit[e * 6 + s] != b.EHit[e * 6 + s]) return false;
        }
        return true;
    }

    private static bool F(float a, float b) => (float.IsNaN(a) && float.IsNaN(b)) || a == b;

    public static int Run()
    {
        _failures = 0;
        TerrainCache.Log = m => Console.WriteLine("  log: " + m);
        // Serialization round trip (chunks at negative coordinates too: region -1).
        var c1 = Sample(-3, 5);
        var c2 = Sample(-8, 7);
        byte[] data = TerrainCache.Serialize(-1, 0, 0.5f, new[] { c1, c2 });
        var back = TerrainCache.Deserialize(data, 0.5f, -1, 0, out string err);
        Expect(back != null && back.Length == 2 && Same(c1, back[0]) && Same(c2, back[1]), "round trip (" + data.Length + " bytes)");
        Expect(TerrainCache.Deserialize(data, 0.25f, -1, 0, out _) == null, "other cell size ignored");
        Expect(TerrainCache.Deserialize(data, 0.5f, 0, 0, out _) == null, "other region ignored");

        var bad = (byte[])data.Clone();
        bad[bad.Length / 2] ^= 0x5A;
        Expect(TerrainCache.Deserialize(bad, 0.5f, -1, 0, out _) == null, "corrupt payload ignored");
        var oldVer = (byte[])data.Clone();
        oldVer[4] = 99;
        Expect(TerrainCache.Deserialize(oldVer, 0.5f, -1, 0, out _) == null, "other version ignored");
        Expect(TerrainCache.Deserialize(new byte[] { 1, 2, 3 }, 0.5f, -1, 0, out _) == null, "garbage ignored");
        Expect(TerrainCache.Deserialize(data.AsSpan(0, data.Length - 10).ToArray(), 0.5f, -1, 0, out _) == null, "truncated ignored");
        Expect(Math.Abs(TerrainCache.UnpackHit(TerrainCache.PackHit(0.4567f)) - 0.4567f) < 0.001f && float.IsNaN(TerrainCache.UnpackHit(TerrainCache.PackHit(float.NaN))), "hit quantisation");

        // Overlay merges cell-wise.
        var m = c1.Clone();
        var d = new CacheChunk(-3, 5);
        d.Flags[1] = 1; d.Floor[1] = 99f; d.Ceil[1] = float.NaN; d.Ref[1] = 98f; d.Time[1] = 5;
        m.Overlay(d);
        Expect(m.Floor[1] == 99f && m.Flags[0] == 1 && F(m.Floor[3], c1.Floor[3]), "overlay");

        // Files: save, reopen, load in the background, tolerate a broken file.
        string dir = Path.Combine(Path.GetTempPath(), "ultraring-cache-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            string zone = Path.Combine(dir, "0D000000");
            var tc = new TerrainCache(zone, 0x0D000000, 0.5f);
            tc.Apply(Sample(-3, 5));
            tc.Apply(Sample(9, 10));
            tc.Update(0, 0, 130, 190); // regions without files are ready at once
            Expect(tc.Get(-3, 5) != null && tc.Get(9, 10) != null, "apply into empty regions");
            Expect(tc.FlushAndWait(5000) && TerrainCache.SavedRegions == 2, "wrote 2 region files");
            Expect(File.Exists(Path.Combine(zone, "-1_0.bin")) && File.Exists(Path.Combine(zone, "1_1.bin")), "file names");

            var tc2 = new TerrainCache(zone, 0x0D000000, 0.5f);
            Expect(tc2.ZoneFiles == 2, "zone files counted");
            Expect(tc2.Get(-3, 5) == null, "not loaded before Update");
            for (int i = 0; i < 200 && (tc2.Get(-3, 5) == null || tc2.Get(9, 10) == null); i++)
            {
                tc2.Update(0, 0, 130, 190);
                System.Threading.Thread.Sleep(10);
            }
            Expect(tc2.Get(-3, 5) != null && Same(Sample(-3, 5), tc2.Get(-3, 5)) && tc2.Get(9, 10) != null, "reloaded from disk");

            // Update during load merges into the file's data.
            var tc3 = new TerrainCache(zone, 0x0D000000, 0.5f);
            var delta = new CacheChunk(-3, 5);
            delta.Flags[1] = 1; delta.Floor[1] = 42f; delta.Ref[1] = 40f; delta.Time[1] = 7;
            tc3.Apply(delta); // region still loading: queued
            for (int i = 0; i < 200 && tc3.Get(-3, 5) == null; i++) { tc3.Update(0, 0, 130, 190); System.Threading.Thread.Sleep(10); }
            var got = tc3.Get(-3, 5);
            Expect(got != null && got.Floor[1] == 42f && got.Flags[0] == 1, "delta applied while loading is merged");

            // A corrupt file is ignored, not fatal.
            File.WriteAllBytes(Path.Combine(zone, "-1_0.bin"), new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 });
            var tc4 = new TerrainCache(zone, 0x0D000000, 0.5f);
            for (int i = 0; i < 100 && tc4.RegionsLoading > 0 || i == 0; i++) { tc4.Update(0, 0, 130, 190); System.Threading.Thread.Sleep(10); }
            Expect(tc4.Get(-3, 5) == null && tc4.Get(9, 10) != null && TerrainCache.LoadErrors >= 1, "corrupt region ignored");

            // Far clean regions are dropped from memory.
            tc4.Update(5000, 5000, 130, 190);
            Expect(tc4.RegionsInMemory == 0, "far regions unloaded");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
        return _failures;
    }
}
