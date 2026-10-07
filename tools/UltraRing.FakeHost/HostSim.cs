using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using UltraRing.Link;

namespace UltraRing.FakeHost;

public enum LifeState { Settling, Alive, Dead }

public struct WinInfo
{
    public int X, Y, W, H;
    public bool Valid, Focused;
}

public sealed class Enemy
{
    public ulong Id;
    public uint Kind;          // 1 large, 2 small, 3 other (non-hostile)
    public uint EmId;
    public string Name;
    public Vector3 Center;
    public float Radius, Angle, Speed;   // walking circle (m, rad, m/s)
    public float CapR, CapH;
    public float Hp, MaxHp;
    public bool Dead;
    public long RespawnAtMs;
    public long NextMeleeMs;
    public Vector3 Pos;
    public bool Hostile => Kind != Protocol.EntOther;
}

/// <summary>
/// The "Elden Ring" side of the bridge: life state machine, stand-in, synthetic terrain mailbox, entities,
/// damage, hunter events, action prompt. Everything protocol-level goes through <see cref="HostLink"/>.
/// </summary>
public sealed class HostSim
{
    public const uint ZoneId = 0x3C000000;
    public const long SettleMs = 1500;
    public const long DeadMs = 3000;
    public const long EnemyRespawnMs = 3000;
    public const float HunterMaxHp = 1200f;

    public readonly HostLink Link;
    public readonly World World = new World();
    public readonly Enemy[] Enemies;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public LifeState Life = LifeState.Settling;
    public Vector3 Spawn = Vector3.Zero;
    public Vector3 Pos;
    public float YawDeg = 180f;      // facing = (-sin y, -cos y); 180 faces +Z
    public bool StoodIn;
    public bool MeleeEnabled = true;
    public bool CompositedRecently;  // set by the form: a composite was drawn within the last 500 ms

    // Camera of the frame (what state publishes and what the window renders).
    public Vector3 CamPos, CamTarget, CamUp = Vector3.UnitY;
    public float FovY = 60f;
    public bool CamOverrideApplied;
    public bool HideHunter;

    // Last control (for overlays / pose history).
    public bool CtrlActive;
    public ErmcControl Ctrl;
    public ulong AppliedPoseId;      // control.mcFrame when a camera was applied this tick

    // Statistics.
    public uint DamageEvents;
    public uint HunterHits;
    public string LastDamage = "";
    public string LastAction = "";
    public ulong Frame;
    public int WinW, WinH;

    public event Action<string> Log;

    private long _lifeSinceMs, _lastStoodInMs = -100000, _lastAliveOrDeadMs;
    private bool _everAlive;
    private int _belowTicks;
    private readonly ErmcDamage[] _dmgBuf = new ErmcDamage[64];
    private readonly ErmcEntity[] _entBuf;
    private readonly ulong[] _pubIds;
    private readonly bool[] _pubHostileAlive;
    private int _pubCount;
    private bool _firstTick = true;

    public long NowMs => _clock.ElapsedMilliseconds;

    public HostSim(HostLink link)
    {
        Link = link;
        Enemies = new[]
        {
            Make(0, 2, 4300, "c4300", new Vector3(6, 0, 12), 3f, 0.8f, 0.4f, 1.8f, 221),
            Make(1, 2, 3000, "c3000", new Vector3(-6, 0, 10), 2.5f, 0.6f, 0.5f, 2.0f, 600),
            Make(2, 1, 2120, "c2120", new Vector3(14, 0, -12), 5f, 0.9f, 1.2f, 4.5f, 6000),
            Make(3, 3, 1100, "c1100", new Vector3(-3, 0, -8), 2f, 0.5f, 0.4f, 1.8f, 100),
            Make(4, 2, 4310, "c4310", new Vector3(-2, 0, 16), 2f, 1.0f, 0.4f, 1.8f, 221),
        };
        _entBuf = new ErmcEntity[Enemies.Length];
        _pubIds = new ulong[Enemies.Length];
        _pubHostileAlive = new bool[Enemies.Length];
        _lifeSinceMs = 0;
        Pos = Spawn;
        ResetCamera();
    }

    private static Enemy Make(int i, uint kind, uint em, string name, Vector3 center, float radius, float speed, float r, float h, float maxHp)
    {
        var e = new Enemy
        {
            Id = 0x0000_1000_0000_0000UL + (ulong)i + 1, Kind = kind, EmId = em, Name = name, Center = center, Radius = radius,
            Angle = i * 1.3f, Speed = speed, CapR = r, CapH = h, Hp = maxHp, MaxHp = maxHp,
        };
        e.Pos = e.Center + new Vector3(MathF.Cos(e.Angle), 0, MathF.Sin(e.Angle)) * radius;
        return e;
    }

    private void Say(string s) => Log?.Invoke(s);

    // ---- commands (UI) ----

    public void SetSpawn(Vector3 p)
    {
        Spawn = p;
        Say($"spawn set to ({p.X:0.##}, {p.Y:0.##}, {p.Z:0.##})");
    }

    /// <summary>Moves the Tarnished back to the spawn through the SETTLING path (ends with hostLife++).</summary>
    public void Respawn()
    {
        Life = LifeState.Settling;
        _lifeSinceMs = NowMs;
        Pos = Spawn;
        Say("respawn: SETTLING at spawn");
    }

    public void ResetEnemies()
    {
        foreach (var e in Enemies) { e.Hp = e.MaxHp; e.Dead = false; }
        Say("enemies reset to full hp");
    }

    /// <summary>Simulates the Tarnished taking <paramref name="hp"/> damage (HP loss event for the guest).</summary>
    public void HitMe(float hp)
    {
        if (Life != LifeState.Alive) { Say("hit me ignored: not ALIVE"); return; }
        Vector3 from = Pos;
        uint kind = Protocol.EntLargeMonster;
        float best = float.MaxValue;
        for (int i = 0; i < _pubCount; i++)
        {
            Enemy e = Enemies[i];
            if (!_pubHostileAlive[i]) continue;
            float d = Vector3.DistanceSquared(e.Pos, Pos);
            if (d < best) { best = d; from = e.Pos; kind = e.Kind; }
        }
        Link.ReportHunterHit(hp, from, HunterMaxHp, Frame, kind);
        HunterHits++;
        Say($"hunter hit: -{hp:0} HP of {HunterMaxHp:0} from ({from.X:0.0},{from.Y:0.0},{from.Z:0.0}) kind={kind}");
    }

    /// <summary>"Kill plane": the stood-in Tarnished died in the host without the guest asking.</summary>
    public void KillPlane()
    {
        if (Life != LifeState.Alive) return;
        Link.BumpHostDeaths();
        Say("kill plane: hostDeaths++");
        StartDeath("kill plane");
    }

    private void StartDeath(string why)
    {
        Life = LifeState.Dead;
        _lifeSinceMs = NowMs;
        _lastAliveOrDeadMs = _lifeSinceMs;
        _belowTicks = 0;
        Say($"YOU DIED ({why}); DEAD for {DeadMs} ms, then SETTLING at spawn");
    }

    // ---- per-frame ----

    public void ResetCamera()
    {
        FollowCamera();
    }

    private void FollowCamera()
    {
        float y = YawDeg * MathF.PI / 180f;
        var f = new Vector3(-MathF.Sin(y), 0, -MathF.Cos(y));
        CamPos = Pos - f * 4f + new Vector3(0, 2.4f, 0);
        CamTarget = Pos + new Vector3(0, 1.2f, 0) + f * 2f;
        CamUp = Vector3.UnitY;
        FovY = 60f;
    }

    public unsafe void Tick(double dt, WinInfo win, float moveFwd, float turn)
    {
        long now = NowMs;
        WinW = win.W; WinH = win.H;
        Link.UpdateGuestLiveness();
        bool ctrlActive = Link.UpdateControl();
        CtrlActive = ctrlActive;
        ErmcControl c = Link.Control;
        Ctrl = c;
        uint cf = ctrlActive ? c.flags : 0;

        if (_firstTick)
        {
            _firstTick = false;
            Say($"host started: spawn=({Spawn.X:0.##},{Spawn.Y:0.##},{Spawn.Z:0.##}) zone=0x{ZoneId:X8}; SETTLING for {SettleMs} ms");
        }

        // Life state machine.
        if (Life == LifeState.Settling && now - _lifeSinceMs >= SettleMs)
        {
            Life = LifeState.Alive;
            _lifeSinceMs = now;
            _everAlive = true;
            Pos = Spawn;
            Link.BumpHostLife();
            Say($"ALIVE: hostLife={Link.HostLife} (guest must recall to ({Spawn.X:0.##},{Spawn.Y:0.##},{Spawn.Z:0.##}))");
        }
        else if (Life == LifeState.Dead && now - _lifeSinceMs >= DeadMs)
        {
            Life = LifeState.Settling;
            _lifeSinceMs = now;
            Pos = Spawn;
            Say("respawn: SETTLING at spawn");
        }
        if (Life == LifeState.Alive || Life == LifeState.Dead) _lastAliveOrDeadMs = now;

        // Stand-in / free movement.
        StoodIn = Life == LifeState.Alive && (cf & Protocol.CtrlMoveHunter) != 0;
        if (StoodIn)
        {
            _lastStoodInMs = now;
            Pos = new Vector3(c.hunterPos[0], c.hunterPos[1], c.hunterPos[2]);
            YawDeg = c.hunterYawDeg;
        }
        else if (Life == LifeState.Alive && (moveFwd != 0 || turn != 0))
        {
            YawDeg += turn * 120f * (float)dt;
            float y = YawDeg * MathF.PI / 180f;
            Pos += new Vector3(-MathF.Sin(y), 0, -MathF.Cos(y)) * (moveFwd * 4f * (float)dt);
            Pos.Y = World.GroundBelow(Pos.X, Pos.Y, Pos.Z, Pos.Y);
        }
        HideHunter = StoodIn && (cf & Protocol.CtrlHideHunter) != 0;

        // The guest says its player died: kill the Tarnished (only if recently stood in, ALIVE, not dead).
        bool guestDied = Link.PollGuestDeath();
        if (guestDied)
        {
            if (Life == LifeState.Alive && now - _lastStoodInMs <= 2000)
                StartDeath("guest mcDeaths++");
            else
                Say($"mcDeaths changed ({Link.GuestDeaths}) but ignored (life={Life}, stood in {now - _lastStoodInMs} ms ago)");
        }

        // Fell below the map: NoDead holds HP=1 for 3 ticks, then the host lets it die and counts hostDeaths.
        if (Life == LifeState.Alive && Pos.Y < -50f)
        {
            if (++_belowTicks >= 3)
            {
                Link.BumpHostDeaths();
                Say("fell below the map: hostDeaths++");
                StartDeath("below map");
            }
        }
        else _belowTicks = 0;

        bool alive = Life == LifeState.Alive;

        // Camera.
        FollowCamera();
        CamOverrideApplied = false;
        AppliedPoseId = 0;
        if (alive && (cf & Protocol.CtrlOverrideCamera) != 0)
        {
            CamPos = new Vector3(c.camPos[0], c.camPos[1], c.camPos[2]);
            CamTarget = new Vector3(c.camTarget[0], c.camTarget[1], c.camTarget[2]);
            CamUp = new Vector3(c.camUp[0], c.camUp[1], c.camUp[2]);
            if (c.fovYDeg > 5f && c.fovYDeg < 170f) FovY = c.fovYDeg;
            CamOverrideApplied = true;
            AppliedPoseId = c.mcFrame;
        }

        // Services that only run while ALIVE.
        if (alive)
        {
            Link.ServiceRays(World.Raycast, HostLink.DefaultRayBudgetMs);
            ServiceDamage(now);
            UpdateEnemies(dt, now);
            if (MeleeEnabled) Melee(now);
            PublishEntities();
            ServiceAction(now);
        }
        else
        {
            _pubCount = 0;
            Link.ClearEntities(Frame);
            Link.SetPrompt("");
            Link.PollActionRequest(out _);   // keep the baseline moving; requests while not ALIVE are dropped
        }

        PublishState(win, alive, now);
    }

    private unsafe void PublishState(WinInfo win, bool alive, long now)
    {
        var st = new ErmcGameState();
        Frame = Link.BumpHeartbeat();
        st.frame = Frame;
        st.unitsPerMeter = 1f;
        uint flags = 0;
        if (win.Valid)
        {
            flags |= Protocol.StateWindowValid;
            st.winX = win.X; st.winY = win.Y; st.winW = win.W; st.winH = win.H;
            st.bbW = (uint)win.W; st.bbH = (uint)win.H;
        }
        if (win.Focused) flags |= Protocol.StateWindowFocused;
        if (Life == LifeState.Dead) flags |= Protocol.StatePlayerDead | Protocol.StateHostBusy;
        else if (Life != LifeState.Alive && _everAlive && now - _lastAliveOrDeadMs < 60000) flags |= Protocol.StateHostBusy;
        if (alive)
        {
            flags |= Protocol.StateCameraValid | Protocol.StatePlayerValid;
            if (CamOverrideApplied) flags |= Protocol.StateCamOverridden;
            if (CompositedRecently) flags |= Protocol.StateCompositing;
            st.camPos[0] = CamPos.X; st.camPos[1] = CamPos.Y; st.camPos[2] = CamPos.Z;
            st.camTarget[0] = CamTarget.X; st.camTarget[1] = CamTarget.Y; st.camTarget[2] = CamTarget.Z;
            st.camUp[0] = CamUp.X; st.camUp[1] = CamUp.Y; st.camUp[2] = CamUp.Z;
            st.fovYDeg = FovY;
            st.nearZ = 0.05f;
            st.farZ = 2000f;
            st.aspect = win.H > 0 ? (float)win.W / win.H : 16f / 9f;
            st.playerPos[0] = Pos.X; st.playerPos[1] = Pos.Y; st.playerPos[2] = Pos.Z;
            // Host formula (player_facing.h): theta = yaw in radians, q = (0, sin(theta/2), 0, cos(theta/2)).
            float theta = YawDeg * MathF.PI / 180f;
            st.playerQuat[0] = 0; st.playerQuat[1] = MathF.Sin(theta * 0.5f); st.playerQuat[2] = 0; st.playerQuat[3] = MathF.Cos(theta * 0.5f);
            st.stageId = ZoneId;
        }
        st.flags = flags;
        Link.WriteState(ref st);
    }

    // ---- entities / damage ----

    private void UpdateEnemies(double dt, long now)
    {
        foreach (var e in Enemies)
        {
            if (e.Dead)
            {
                if (now >= e.RespawnAtMs)
                {
                    e.Dead = false;
                    e.Hp = e.MaxHp;
                    Say($"{e.Name} respawned with {e.Hp:0} hp");
                }
                continue;
            }
            e.Angle += e.Speed / e.Radius * (float)dt;
            e.Pos = e.Center + new Vector3(MathF.Cos(e.Angle), 0, MathF.Sin(e.Angle)) * e.Radius;
        }
    }

    private void PublishEntities()
    {
        for (int i = 0; i < Enemies.Length; i++)
        {
            Enemy e = Enemies[i];
            HostLink.FillEntity(ref _entBuf[i], e.Id, e.Kind, e.EmId, e.Pos, e.CapR, e.CapH, e.Dead ? 0 : e.Hp, e.MaxHp, e.Dead, e.Name);
            _pubIds[i] = e.Id;
            _pubHostileAlive[i] = e.Hostile && !e.Dead;
        }
        _pubCount = Enemies.Length;
        Link.PublishEntities(_entBuf, Frame);
    }

    /// <summary>erHp = ceil(amount * maxHp / clamp(20*sqrt(maxHp/100), 10, 300)), minimum 1 (game.cpp:1434-1439).</summary>
    public static int ErHp(float amount, float maxHp)
    {
        float mcHealth = Math.Clamp(20f * MathF.Sqrt(maxHp / 100f), 10f, 300f);
        int hp = (int)MathF.Ceiling(amount * maxHp / mcHealth);
        return Math.Max(1, hp);
    }

    private unsafe void ServiceDamage(long now)
    {
        int n = Link.DrainDamage(_dmgBuf);
        for (int i = 0; i < n; i++)
        {
            ErmcDamage d = _dmgBuf[i];
            DamageEvents++;
            if (d.id == 0)
            {
                LastDamage = $"world strike @({d.hitPos[0]:0.0},{d.hitPos[1]:0.0},{d.hitPos[2]:0.0}) flags={d.flags}";
                Say("[dmg] " + LastDamage);
                continue;
            }
            if (!(d.amount > 0f && d.amount < 10000f))
            {
                Say($"[dmg] id=0x{d.id:X} dropped: amount {d.amount} out of range");
                continue;
            }
            int idx = -1;
            for (int k = 0; k < _pubCount; k++) if (_pubIds[k] == d.id) { idx = k; break; }
            if (idx < 0) { Say($"[dmg] id=0x{d.id:X} dropped: not published last tick"); continue; }
            if (!_pubHostileAlive[idx]) { Say($"[dmg] id=0x{d.id:X} dropped: not hostile/alive"); continue; }
            Enemy e = Enemies[idx];
            int hp = ErHp(d.amount, e.MaxHp);
            float before = e.Hp;
            e.Hp = MathF.Max(0, e.Hp - hp);
            LastDamage = $"{e.Name} amount={d.amount:0.###} -> -{hp} hp ({before:0}->{e.Hp:0}/{e.MaxHp:0}) flags={d.flags}";
            Say("[hit] " + LastDamage);
            if (e.Hp <= 0)
            {
                e.Dead = true;
                e.RespawnAtMs = now + EnemyRespawnMs;
                Say($"[kill] {e.Name} died, respawns in {EnemyRespawnMs} ms");
            }
        }
    }

    private void Melee(long now)
    {
        foreach (var e in Enemies)
        {
            if (!e.Hostile || e.Dead || now < e.NextMeleeMs) continue;
            Vector3 d = e.Pos - Pos;
            if (d.Length() > 2f) continue;
            e.NextMeleeMs = now + 1000;
            HitMe(e.Kind == Protocol.EntLargeMonster ? 150f : 60f);
        }
    }

    // ---- action / prompt ----

    private void ServiceAction(long now)
    {
        bool near = World.DistanceToDoor(Pos) <= 2f;
        if (Link.PollActionRequest(out uint req))
        {
            int result = near ? 1 : 0;
            if (near)
            {
                World.DoorOpen = !World.DoorOpen;
                LastAction = "door " + (World.DoorOpen ? "opened" : "closed");
            }
            else LastAction = "action: nothing here";
            Say($"[action] req={req} result={result} ({LastAction})");
            Link.AckAction(req, result);
        }
        Link.SetPrompt(StoodIn && near ? (World.DoorOpen ? "Close" : "Open") : "");
    }

    public bool NearDoor => World.DistanceToDoor(Pos) <= 2f;
}
