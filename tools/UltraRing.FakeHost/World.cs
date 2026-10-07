using System;
using System.Collections.Generic;
using System.Numerics;

namespace UltraRing.FakeHost;

public struct Box
{
    public Vector3 Min, Max;
    public uint Color;     // 0xAARRGGBB
    public string Tag;
}

public struct Seg
{
    public Vector3 A, B;
    public uint Color;
}

/// <summary>
/// The fake zone: analytic primitives in metres (left-handed, +Y up, +Z forward). Ground plane with a pit,
/// boxes (walls, a 2 m step, a 6 m pillar), a 20 m ramp up to a raised platform at y=4, and a door.
/// </summary>
public sealed class World
{
    public const float Half = 120f;
    public const float PitBottom = -6f;
    // pit (hole in the ground) in x/z
    public static readonly Vector2 HoleMin = new Vector2(8, 8);
    public static readonly Vector2 HoleMax = new Vector2(14, 14);
    // ramp: surface y = -0.2 * (z + 2) for z in [-22,-2], x in [-3,3]  (20 m long, rises 4 m toward -Z)
    public const float RampX = 3f, RampZ0 = -2f, RampZ1 = -22f, RampSlope = 0.2f;

    public readonly List<Box> Boxes = new List<Box>();
    public Box Door;
    public bool DoorOpen;
    public readonly List<Seg> StaticLines = new List<Seg>();

    public const uint ColWall = 0xFFB0B8C0, ColStep = 0xFFD0A050, ColPillar = 0xFFC070C0, ColPlat = 0xFF50C8C8,
        ColGrid = 0xFF485058, ColRamp = 0xFF60D060, ColPit = 0xFFE08030, ColDoor = 0xFF4080FF;

    public World()
    {
        // Walls.
        AddBox(new Vector3(-10, 0, 20), new Vector3(10, 5, 21), ColWall, "wall A");
        AddBox(new Vector3(-12, 0, -10), new Vector3(-11, 4, 20), ColWall, "wall B");
        // 2 m step.
        AddBox(new Vector3(2, 0, 4), new Vector3(6, 2, 6), ColStep, "step 2m");
        // 6 m pillar.
        AddBox(new Vector3(5, 0, -7), new Vector3(7, 6, -5), ColPillar, "pillar 6m");
        // Raised platform at the end of the ramp (top y = 4).
        AddBox(new Vector3(-6, 3.5f, -32), new Vector3(6, 4, -22), ColPlat, "platform y=4");
        // Pit walls (solid boxes around the hole, hole interior stays open); not drawn as boxes, the pit has its own lines.
        float x0 = HoleMin.X, x1 = HoleMax.X, z0 = HoleMin.Y, z1 = HoleMax.Y;
        PitWalls.Add(new Box { Min = new Vector3(x0 - 1, PitBottom, z0 - 1), Max = new Vector3(x0, 0, z1 + 1), Color = ColPit, Tag = "pit" });
        PitWalls.Add(new Box { Min = new Vector3(x1, PitBottom, z0 - 1), Max = new Vector3(x1 + 1, 0, z1 + 1), Color = ColPit, Tag = "pit" });
        PitWalls.Add(new Box { Min = new Vector3(x0, PitBottom, z0 - 1), Max = new Vector3(x1, 0, z0), Color = ColPit, Tag = "pit" });
        PitWalls.Add(new Box { Min = new Vector3(x0, PitBottom, z1), Max = new Vector3(x1, 0, z1 + 1), Color = ColPit, Tag = "pit" });
        // Door (closed = solid).
        Door = new Box { Min = new Vector3(-2, 0, 10), Max = new Vector3(2, 2.8f, 10.4f), Color = ColDoor, Tag = "door" };

        BuildStaticLines();
    }

    public readonly List<Box> PitWalls = new List<Box>();

    private void AddBox(Vector3 min, Vector3 max, uint color, string tag) =>
        Boxes.Add(new Box { Min = min, Max = max, Color = color, Tag = tag });

    public static bool InHole(float x, float z) =>
        x > HoleMin.X && x < HoleMax.X && z > HoleMin.Y && z < HoleMax.Y;

    // ---- rendering geometry ----

    private void BuildStaticLines()
    {
        // Ground grid every 4 m over +-48 m, split around the pit.
        for (float g = -48; g <= 48.01f; g += 4)
        {
            AddGridLine(true, g);
            AddGridLine(false, g);
        }
        // Pit rim at y=0 and bottom at y=-6 with verticals and a cross.
        Vector3 a = new Vector3(HoleMin.X, 0, HoleMin.Y), b = new Vector3(HoleMax.X, 0, HoleMin.Y),
            c = new Vector3(HoleMax.X, 0, HoleMax.Y), d = new Vector3(HoleMin.X, 0, HoleMax.Y);
        Vector3 dn = new Vector3(0, PitBottom, 0);
        Quad(a, b, c, d, ColPit);
        Quad(a + dn, b + dn, c + dn, d + dn, ColPit);
        Line(a, a + dn, ColPit); Line(b, b + dn, ColPit); Line(c, c + dn, ColPit); Line(d, d + dn, ColPit);
        Line(a + dn, c + dn, ColPit); Line(b + dn, d + dn, ColPit);
        // Ramp outline + cross lines.
        float yAt(float z) => -RampSlope * (z + 2);
        Vector3 r0l = new Vector3(-RampX, yAt(RampZ0), RampZ0), r0r = new Vector3(RampX, yAt(RampZ0), RampZ0);
        Vector3 r1l = new Vector3(-RampX, yAt(RampZ1), RampZ1), r1r = new Vector3(RampX, yAt(RampZ1), RampZ1);
        Quad(r0l, r0r, r1r, r1l, ColRamp);
        for (float z = RampZ0 - 2; z > RampZ1; z -= 2)
            Line(new Vector3(-RampX, yAt(z), z), new Vector3(RampX, yAt(z), z), ColRamp);
        Line(new Vector3(0, yAt(RampZ0), RampZ0), new Vector3(0, yAt(RampZ1), RampZ1), ColRamp);
        // Spawn marker.
        Line(new Vector3(-0.5f, 0.01f, 0), new Vector3(0.5f, 0.01f, 0), 0xFFFFFF60);
        Line(new Vector3(0, 0.01f, -0.5f), new Vector3(0, 0.01f, 0.5f), 0xFFFFFF60);
    }

    private void AddGridLine(bool alongX, float g)
    {
        const float lo = -48, hi = 48;
        float h0 = alongX ? HoleMin.Y : HoleMin.X, h1 = alongX ? HoleMax.Y : HoleMax.X;
        float c0 = alongX ? HoleMin.X : HoleMin.Y, c1 = alongX ? HoleMax.X : HoleMax.Y;
        if (g > h0 && g < h1)
        {
            GridSeg(alongX, g, lo, c0);
            GridSeg(alongX, g, c1, hi);
        }
        else GridSeg(alongX, g, lo, hi);
    }

    private void GridSeg(bool alongX, float fixedCoord, float from, float to)
    {
        Vector3 a = alongX ? new Vector3(from, 0, fixedCoord) : new Vector3(fixedCoord, 0, from);
        Vector3 b = alongX ? new Vector3(to, 0, fixedCoord) : new Vector3(fixedCoord, 0, to);
        Line(a, b, ColGrid);
    }

    private void Line(Vector3 a, Vector3 b, uint col) => StaticLines.Add(new Seg { A = a, B = b, Color = col });

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, uint col)
    {
        Line(a, b, col); Line(b, c, col); Line(c, d, col); Line(d, a, col);
    }

    // ---- ray casting (metres, segment start->end) ----

    /// <summary>Nearest hit of the segment, like CastRay: two-sided, misses geometry the ray starts inside of.</summary>
    public bool Raycast(Vector3 s, Vector3 e, out Vector3 hit)
    {
        Vector3 d = e - s;
        float best = 2f;   // t in [0,1]

        // Ground plane y=0 with the pit hole.
        if (MathF.Abs(d.Y) > 1e-9f)
        {
            float t = -s.Y / d.Y;
            if (t >= 0 && t <= 1 && t < best)
            {
                float px = s.X + t * d.X, pz = s.Z + t * d.Z;
                if (MathF.Abs(px) <= Half && MathF.Abs(pz) <= Half && !InHole(px, pz)) best = t;
            }
            // Pit bottom.
            t = (PitBottom - s.Y) / d.Y;
            if (t >= 0 && t <= 1 && t < best)
            {
                float px = s.X + t * d.X, pz = s.Z + t * d.Z;
                if (InHole(px, pz)) best = t;
            }
        }

        // Ramp: f(t) = y(t) + slope*(z(t)+2) = 0
        float den = d.Y + RampSlope * d.Z;
        if (MathF.Abs(den) > 1e-9f)
        {
            float t = -(s.Y + RampSlope * (s.Z + 2)) / den;
            if (t >= 0 && t <= 1 && t < best)
            {
                float px = s.X + t * d.X, pz = s.Z + t * d.Z;
                if (MathF.Abs(px) <= RampX && pz <= RampZ0 && pz >= RampZ1) best = t;
            }
        }

        for (int i = 0; i < Boxes.Count; i++)
        {
            Box b = Boxes[i];
            if (RayBox(s, d, b.Min, b.Max, out float t) && t < best) best = t;
        }
        for (int i = 0; i < PitWalls.Count; i++)
        {
            Box b = PitWalls[i];
            if (RayBox(s, d, b.Min, b.Max, out float t) && t < best) best = t;
        }
        if (!DoorOpen && RayBox(s, d, Door.Min, Door.Max, out float td) && td < best) best = td;

        if (best > 1f) { hit = default; return false; }
        hit = s + d * best;
        return true;
    }

    private static bool RayBox(Vector3 s, Vector3 d, Vector3 mn, Vector3 mx, out float tHit)
    {
        tHit = 0;
        float tEnter = float.NegativeInfinity, tExit = float.PositiveInfinity;
        for (int a = 0; a < 3; a++)
        {
            float so = a == 0 ? s.X : a == 1 ? s.Y : s.Z;
            float dd = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            float lo = a == 0 ? mn.X : a == 1 ? mn.Y : mn.Z;
            float hi = a == 0 ? mx.X : a == 1 ? mx.Y : mx.Z;
            if (MathF.Abs(dd) < 1e-9f)
            {
                if (so < lo || so > hi) return false;
            }
            else
            {
                float t0 = (lo - so) / dd, t1 = (hi - so) / dd;
                if (t0 > t1) { float tmp = t0; t0 = t1; t1 = tmp; }
                if (t0 > tEnter) tEnter = t0;
                if (t1 < tExit) tExit = t1;
                if (tEnter > tExit) return false;
            }
        }
        if (tEnter < 0 || tEnter > 1) return false;   // starts inside (CastRay misses) or too far
        tHit = tEnter;
        return true;
    }

    /// <summary>Height of the floor below (x,y,z) looking down from y+3, or fallback if nothing is hit.</summary>
    public float GroundBelow(float x, float y, float z, float fallback)
    {
        return Raycast(new Vector3(x, y + 3, z), new Vector3(x, y - 60, z), out Vector3 h) ? h.Y : fallback;
    }

    public float DistanceToDoor(Vector3 p)
    {
        Vector3 c = Vector3.Clamp(p, Door.Min, Door.Max);
        return (p - c).Length();
    }
}
