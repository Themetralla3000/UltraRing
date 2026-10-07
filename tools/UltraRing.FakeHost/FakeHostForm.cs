using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using UltraRing.Link;

namespace UltraRing.FakeHost;

/// <summary>
/// The fake host window: the "Elden Ring" client area. Publishes its rect, runs the ~60 Hz tick, draws a software
/// wireframe from the control camera, and composites the guest's latest frames.shm frame over it.
/// </summary>
public sealed unsafe class FakeHostForm : Form
{
    private readonly HostLink _link;
    private readonly HostSim _sim;
    private readonly HostFrames _frames = new HostFrames();
    private readonly SoftRenderer _renderer = new SoftRenderer();
    private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly HashSet<Keys> _keys = new HashSet<Keys>();
    private readonly double _exitAfterSec;
    private PanelForm _panel;

    private Bitmap _bmp;
    private long _lastTickMs;
    private bool _f8Down;

    // guest frame buffers (persisted: only re-copied when a fresher frame is picked)
    private byte[] _gWorld = new byte[0], _gGui = new byte[0], _gHand = new byte[0];
    private int _gW, _gH;
    private bool _gHasHand, _haveGuestFrame;
    private ulong _gFrameId, _gPoseId;
    private long _lastCompositeMs = long.MinValue;

    // stats
    private long _statMs;
    private int _uploads, _rayBatchBase;
    private ulong _raysBase;
    private double _guestFps, _batchesPerSec, _raysPerSec;
    private long _lastTextMs;
    private string[] _overlay = new string[0];
    private readonly Font _font = new Font("Consolas", 9f);
    private ulong _lastGuestHb;
    private long _guestHbMoveMs;

    public FakeHostForm(HostLink link, double exitAfterSec, Vector3 spawn)
    {
        _link = link;
        _sim = new HostSim(link);
        _sim.SetSpawn(spawn);
        _sim.Pos = spawn;
        _exitAfterSec = exitAfterSec;
        _sim.Log += Logger.Line;

        Text = "Fake Elden Ring (UltraRing.FakeHost)";
        ClientSize = new Size(1280, 720);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(40, 40);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        KeyPreview = true;
        BackColor = Color.Black;

        _timer.Interval = 15;
        _timer.Tick += (s, e) => Tick();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _panel = new PanelForm(_sim) { StartPosition = FormStartPosition.Manual };
        _panel.Location = new Point(Right + 8, Top);
        _panel.Show(this);
        _lastTickMs = _clock.ElapsedMilliseconds;
        _timer.Start();
        Logger.Line($"fake host window up; bridge dir = {BridgePaths.Dir}");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _frames.Dispose();
        _link.Dispose();    // coreStatus = 0
        Logger.Line("clean shutdown: coreStatus=0");
        base.OnFormClosed(e);
    }

    // ---- input ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.F8)
        {
            if (!_f8Down)
            {
                _f8Down = true;
                _link.BumpSwitchRequest();
                Logger.Line($"F8: mcSwitchReq={_link.SwitchRequests}");
            }
            e.Handled = true;
            return;
        }
        if (_keys.Add(e.KeyCode))
        {
            if (e.KeyCode == Keys.H) _sim.HitMe(100f);
            else if (e.KeyCode == Keys.K) _sim.KillPlane();
            else if (e.KeyCode == Keys.R) _sim.Respawn();
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.F8) _f8Down = false;
        _keys.Remove(e.KeyCode);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _keys.Clear();
        _f8Down = false;
    }

    private WinInfo GetWin()
    {
        var w = new WinInfo();
        Size cs = ClientSize;
        if (WindowState == FormWindowState.Minimized || cs.Width < 16 || cs.Height < 16) return w;
        Point p = PointToScreen(Point.Empty);
        w.X = p.X; w.Y = p.Y; w.W = cs.Width; w.H = cs.Height;
        w.Valid = true;
        w.Focused = ContainsFocus || ActiveForm == this;
        return w;
    }

    // ---- tick ----

    private void Tick()
    {
        long now = _clock.ElapsedMilliseconds;
        double dt = Math.Min(0.1, (now - _lastTickMs) / 1000.0);
        _lastTickMs = now;
        if (_exitAfterSec > 0 && now > _exitAfterSec * 1000) { Close(); return; }

        // hostFocusReq: bring this window to the front.
        if (_link.PollFocusRequest())
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            Logger.Line("hostFocusReq: window activated");
        }

        float fwd = (_keys.Contains(Keys.W) ? 1 : 0) - (_keys.Contains(Keys.S) ? 1 : 0);
        float turn = (_keys.Contains(Keys.A) ? 1 : 0) - (_keys.Contains(Keys.D) ? 1 : 0);
        WinInfo win = GetWin();
        _sim.CompositedRecently = _lastCompositeMs != long.MinValue && now - _lastCompositeMs < 500;
        _sim.Tick(dt, win, fwd, turn);
        if (_sim.CamOverrideApplied) _frames.NoteAppliedPose(_sim.AppliedPoseId);

        if (win.Valid)
        {
            Render(win, now);
            Invalidate();
        }
        UpdateStats(now);
    }

    private void Render(WinInfo win, long now)
    {
        if (_bmp == null || _bmp.Width != win.W || _bmp.Height != win.H)
        {
            _bmp?.Dispose();
            _bmp = new Bitmap(win.W, win.H, PixelFormat.Format32bppPArgb);
        }
        BitmapData bd = _bmp.LockBits(new Rectangle(0, 0, win.W, win.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var cam = Camera.Make(_sim.CamPos, _sim.CamTarget, _sim.CamUp, _sim.FovY, (float)win.W / win.H);
            _renderer.Begin((uint*)bd.Scan0, bd.Stride / 4, win.W, win.H, cam);
            _renderer.Clear(0xFF1A2A3C, 0xFF0C1016);
            DrawScene();
            CompositeGuest(now);
        }
        finally
        {
            _bmp.UnlockBits(bd);
        }
    }

    private void DrawScene()
    {
        World w = _sim.World;
        foreach (Seg s in w.StaticLines) _renderer.Line3(s.A, s.B, s.Color);
        foreach (Box b in w.Boxes) _renderer.Box(b.Min, b.Max, b.Color);
        _renderer.Box(w.Door.Min, w.Door.Max, w.DoorOpen ? 0xFF305080u : World.ColDoor);
        foreach (Enemy e in _sim.Enemies)
        {
            uint col = e.Dead ? 0xFF707070u : e.Hostile ? (e.Kind == Protocol.EntLargeMonster ? 0xFFFF3030u : 0xFFFF7050u) : 0xFFFFE040u;
            _renderer.Box(new Vector3(e.Pos.X - e.CapR, e.Pos.Y, e.Pos.Z - e.CapR), new Vector3(e.Pos.X + e.CapR, e.Pos.Y + e.CapH, e.Pos.Z + e.CapR), col);
        }
        if (!_sim.HideHunter && _sim.Life != LifeState.Dead)
        {
            Vector3 p = _sim.Pos;
            _renderer.Box(new Vector3(p.X - 0.4f, p.Y, p.Z - 0.4f), new Vector3(p.X + 0.4f, p.Y + 1.8f, p.Z + 0.4f), 0xFFFF40FF);
            float y = _sim.YawDeg * MathF.PI / 180f;
            Vector3 f = new Vector3(-MathF.Sin(y), 0, -MathF.Cos(y));
            _renderer.Line3(p + new Vector3(0, 1.4f, 0), p + new Vector3(0, 1.4f, 0) + f * 1.2f, 0xFFFFFFFF);
        }
    }

    // ---- compositing (compositor.cpp, memory path) ----

    private void CompositeGuest(long now)
    {
        _frames.TryOpen();   // throttled to once per 2 s
        if (!_link.CompositorControl(out ErmcControl c) || (c.flags & Protocol.CtrlComposite) == 0 || !_frames.IsOpen) return;

        ulong poseId = _frames.PoseForPresent(c.poseLag);
        int slot = _frames.PickSlot(poseId);
        if (slot >= 0 && _frames.ReadSlotHeader(slot, out ErmcFrameHeader h) && (h.flags & Protocol.FrameGpu) == 0)
        {
            bool fresh = h.frameId > _frames.LastUploaded;
            if (fresh)
            {
                int layer = (int)(h.width * h.height * 4);
                if (_gWorld.Length < layer) { _gWorld = new byte[layer]; _gGui = new byte[layer]; _gHand = new byte[layer]; }
                if (_frames.CopySlot(slot, out h, _gWorld, default, _gGui, _gHand))
                {
                    _gW = (int)h.width; _gH = (int)h.height;
                    _gHasHand = (h.flags & Protocol.FrameHand) != 0;
                    _gFrameId = h.frameId; _gPoseId = h.poseId;
                    _frames.LastUploaded = h.frameId;
                    _haveGuestFrame = true;
                    _uploads++;
                }
            }
        }
        if (_haveGuestFrame)
        {
            _renderer.CompositeGuest(_gWorld, _gGui, _gHand, _gHasHand, _gW, _gH);
            _lastCompositeMs = now;
        }
    }

    // ---- overlay ----

    private void UpdateStats(long now)
    {
        if (now - _statMs >= 1000)
        {
            double sec = (now - _statMs) / 1000.0;
            _statMs = now;
            _guestFps = _uploads / sec;
            _uploads = 0;
            _batchesPerSec = (_link.RayBatchesServed - _rayBatchBase) / sec;
            _raysPerSec = (_link.RaysServed - _raysBase) / sec;
            _rayBatchBase = (int)_link.RayBatchesServed;
            _raysBase = _link.RaysServed;
        }
        if (now - _lastTextMs < 250) return;
        _lastTextMs = now;

        ulong hb = _link.GuestHeartbeat;
        if (hb != _lastGuestHb) { _lastGuestHb = hb; _guestHbMoveMs = now; }
        bool moving = now - _guestHbMoveMs < 1500 && _guestHbMoveMs != 0;

        ErmcControl c = _sim.Ctrl;
        var lines = new List<string>(24);
        lines.Add($"HOST  life={_sim.Life}  frame={_sim.Frame}  hostLife={_link.HostLife}  hostDeaths={_link.HostDeaths}  F8 count={_link.SwitchRequests}");
        lines.Add($"      pos=({_sim.Pos.X:0.00},{_sim.Pos.Y:0.00},{_sim.Pos.Z:0.00}) yaw={_sim.YawDeg:0.0}  stoodIn={_sim.StoodIn}  hidden={_sim.HideHunter}  door={(_sim.World.DoorOpen ? "open" : "closed")}");
        lines.Add($"GUEST alive={_link.GuestAlive} mcHeartbeat={hb} ({(moving ? "moving" : "STALLED")}) pid={_link.GuestPid} deaths={_link.GuestDeaths}");
        lines.Add($"CTRL  active={_sim.CtrlActive} flags={FlagText(c.flags)} seq={c.seq} pose={c.mcFrame} lag={c.poseLag}");
        lines.Add($"      hunterPos=({c.hunterPos[0]:0.00},{c.hunterPos[1]:0.00},{c.hunterPos[2]:0.00}) yawDeg={c.hunterYawDeg:0.0} fov={c.fovYDeg:0.0}");
        lines.Add($"CAM   {(_sim.CamOverrideApplied ? "OVERRIDDEN" : "host")} pos=({_sim.CamPos.X:0.0},{_sim.CamPos.Y:0.0},{_sim.CamPos.Z:0.0}) fov={_sim.FovY:0.0}  win={_sim.WinW}x{_sim.WinH}");
        lines.Add($"FRAMES mapped={_frames.IsOpen} guest fps={_guestFps:0.0} size={_gW}x{_gH} frameId={_gFrameId} poseId={_gPoseId} pick exact/older/miss={_frames.PickExact}/{_frames.PickOlder}/{_frames.PickMissing}");
        if (!_frames.IsOpen && _frames.LastError != null) lines.Add($"       frames.shm: {_frames.LastError}");
        lines.Add($"RAYS  batches/s={_batchesPerSec:0.0} rays/s={_raysPerSec:0} total batches={_link.RayBatchesServed} pending={_link.RayBatchPending}");
        lines.Add($"DMG   events={_sim.DamageEvents} pendingRing={_link.PendingDamage} dropped={_link.DamageDropped}  hunter hits={_sim.HunterHits}");
        lines.Add($"      last: {_sim.LastDamage}");
        if (_sim.LastAction.Length > 0) lines.Add($"ACT   {_sim.LastAction}  prompt-near-door={_sim.NearDoor}");
        foreach (Enemy e in _sim.Enemies)
            lines.Add($"  {e.Name} k{e.Kind} {(e.Dead ? "DEAD " : "     ")}hp {e.Hp,5:0}/{e.MaxHp:0}");
        _overlay = lines.ToArray();
    }

    private static string FlagText(uint f)
    {
        var sb = new StringBuilder();
        void A(uint bit, string n) { if ((f & bit) != 0) { if (sb.Length > 0) sb.Append('|'); sb.Append(n); } }
        A(Protocol.CtrlOverrideCamera, "CAM"); A(Protocol.CtrlMoveHunter, "MOVE"); A(Protocol.CtrlHideHunter, "HIDE");
        A(Protocol.CtrlComposite, "COMP"); A(Protocol.CtrlNoDepthTest, "NODEPTH"); A(Protocol.CtrlGrounded, "GROUND"); A(Protocol.CtrlFlying, "FLY");
        return sb.Length == 0 ? "0" : sb.ToString();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        if (_bmp != null) g.DrawImageUnscaled(_bmp, 0, 0);
        else g.Clear(Color.Black);
        string[] lines = _overlay;
        if (lines.Length == 0) return;
        float lh = _font.GetHeight(g) + 1;
        float maxW = 0;
        foreach (string s in lines) maxW = Math.Max(maxW, g.MeasureString(s, _font).Width);
        using (var br = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            g.FillRectangle(br, 4, 4, maxW + 10, lh * lines.Length + 8);
        float y = 8;
        foreach (string s in lines)
        {
            g.DrawString(s, _font, Brushes.White, 8, y);
            y += lh;
        }
        if (_sim.Life != LifeState.Alive)
        {
            string t = _sim.Life == LifeState.Dead ? "YOU DIED" : "LOADING / SETTLING";
            using var big = new Font("Arial", 28f, FontStyle.Bold);
            SizeF sz = g.MeasureString(t, big);
            g.DrawString(t, big, _sim.Life == LifeState.Dead ? Brushes.Crimson : Brushes.Goldenrod, (ClientSize.Width - sz.Width) / 2, (ClientSize.Height - sz.Height) / 2);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
}
