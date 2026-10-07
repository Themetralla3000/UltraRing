using System;
using System.IO;
using System.Numerics;
using System.Threading;
using UltraRing.Link;

/// <summary>
/// In-process round trips: HostLink and GuestLink map the same bridge.shm (temp ERMC_DIR) and are driven
/// against each other. Also covers the host-side frames.shm reader.
/// </summary>
public static unsafe class RoundTripTests
{
    private static int _failures;

    private static void Expect(bool cond, string name)
    {
        if (!cond) _failures++;
        Console.WriteLine($"{(cond ? "ok  " : "FAIL")} {name}");
    }

    public static int Run()
    {
        _failures = 0;
        string dir = Path.Combine(Path.GetTempPath(), "ultraring-rt-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        string oldDir = Environment.GetEnvironmentVariable("ERMC_DIR");
        Environment.SetEnvironmentVariable("ERMC_DIR", dir);
        try
        {
            InitZeroing(dir);
            RoundTrip(dir);
            Frames(dir);
            GuestFramesWriter(dir);
        }
        catch (Exception e)
        {
            _failures++;
            Console.WriteLine("FAIL exception: " + e);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ERMC_DIR", oldDir);
            try { Directory.Delete(dir, true); } catch { }
        }
        return _failures;
    }

    private static void InitZeroing(string dir)
    {
        // Stale file with a wrong magic: [0,0x100000) must be zeroed, regions above must survive.
        string path = Path.Combine(dir, "zero", "bridge.shm");
        using (var raw = MappedFile.Open(path, Protocol.ShmSize))
        {
            *(uint*)(raw.Base + 0) = 0xDEADBEEF;
            *(uint*)(raw.Base + 0x800) = 0x11111111;
            *(uint*)(raw.Base + Protocol.OffEntities + 0x20) = 0x22222222;
            *(uint*)(raw.Base + Protocol.OffRays + 8) = 0x33333333;
        }
        using (var host = new HostLink())
        {
            Expect(host.Open(path), "host opens stale file");
            using (var raw = MappedFile.Open(path, Protocol.ShmSize))
            {
                Expect(*(uint*)(raw.Base + 0) == Protocol.Magic, "init: magic set");
                Expect(*(uint*)(raw.Base + 4) == Protocol.Version, "init: version=1");
                Expect(*(uint*)(raw.Base + 8) == Protocol.ShmSize, "init: size=8MiB");
                Expect(*(uint*)(raw.Base + 0x800) == 0, "init: control block zeroed");
                Expect(*(uint*)(raw.Base + Protocol.OffEntities + 0x20) == 0x22222222, "init: entity region NOT zeroed");
                Expect(*(uint*)(raw.Base + Protocol.OffRays + 8) == 0x33333333, "init: ray region NOT zeroed");
                var h = (ErmcHeader*)raw.Base;
                Expect(h->hostPid == (uint)Environment.ProcessId, "init: hostPid");
                Expect(h->hostStartMs != 0, "init: hostStartMs");
                Expect(h->coreStatus == 1, "init: coreStatus=1");
            }
        }
        using (var raw = MappedFile.Open(path, Protocol.ShmSize))
            Expect(((ErmcHeader*)raw.Base)->coreStatus == 0, "dispose: coreStatus=0");
    }

    private static void RoundTrip(string dir)
    {
        using var host = new HostLink();
        using var guest = new GuestLink();
        Expect(host.Open(), "host opens bridge.shm");
        Expect(!guest.Poll(), "guest first poll: not alive (baseline)");
        Expect(guest.Mapped, "guest mapped");

        // Heartbeat liveness: the first sample never counts, a change afterwards does.
        host.BumpHeartbeat();
        Expect(guest.Poll(), "guest alive after heartbeat changed");
        Expect(guest.HostFrame == 1, "guest sees host frame 1");

        // State seqlock.
        var st = new ErmcGameState();
        st.flags = Protocol.StatePlayerValid | Protocol.StateWindowValid;
        st.frame = host.BumpHeartbeat();
        st.camPos[0] = 1; st.camPos[1] = 2; st.camPos[2] = 3;
        st.playerPos[0] = 10; st.playerPos[1] = 20; st.playerPos[2] = 30;
        st.playerQuat[3] = 1;
        st.winX = 100; st.winY = 50; st.winW = 1280; st.winH = 720; st.bbW = 1280; st.bbH = 720;
        st.stageId = 0x3C000000;
        st.unitsPerMeter = 1f;
        host.WriteState(ref st);
        Expect(st.seq != 0 && (st.seq & 1) == 0, "state seq even and nonzero");
        Expect(guest.Poll(), "guest poll after state");
        Expect(guest.Snapshot(out var gs) && gs.stageId == 0x3C000000 && gs.winW == 1280 && gs.frame == 2, "state snapshot fields");
        Expect(gs.playerPos[1] == 20f && gs.camPos[2] == 3f && gs.seq == st.seq, "state vectors + seq");

        // Control write -> host snapshot.
        Expect(!host.UpdateControl(), "control: inactive before first publish");
        var c = new ErmcControl();
        c.flags = Protocol.CtrlOverrideCamera | Protocol.CtrlMoveHunter | Protocol.CtrlComposite;
        c.mcFrame = 77;
        c.camPos[0] = 5; c.camPos[1] = 6; c.camPos[2] = 7;
        c.camTarget[2] = 1; c.camUp[1] = 1;
        c.fovYDeg = 70;
        c.hunterPos[0] = 1; c.hunterPos[1] = 2; c.hunterPos[2] = 3;
        c.hunterYawDeg = 180;
        c.poseLag = 1;
        guest.WriteControl(ref c);
        Expect(c.seq != 0 && (c.seq & 1) == 0, "control seq even and nonzero");
        Expect(host.UpdateControl(), "control: active after publish");
        ErmcControl hc = host.Control;
        Expect(hc.mcFrame == 77 && hc.flags == c.flags && hc.camPos[2] == 7f && hc.hunterYawDeg == 180f && hc.seq == c.seq, "control fields");
        // Staleness: seq unchanged for > 1000 ms => inactive, but last snapshot is kept.
        Thread.Sleep((int)HostLink.ControlTimeoutMs + 150);
        Expect(!host.UpdateControl(), "control: inactive after 1000 ms without seq change");
        Expect(host.HasControl && host.Control.mcFrame == 77, "control: last snapshot retained");
        Expect(host.CompositorControl(out var cc) && (cc.flags & Protocol.CtrlComposite) != 0, "compositor view ignores staleness");
        c.mcFrame = 78;
        guest.WriteControl(ref c);
        Expect(host.UpdateControl() && host.Control.mcFrame == 78, "control: active again after rewrite");

        // Guest heartbeat liveness on the host side.
        host.UpdateGuestLiveness();
        Expect(!host.GuestAlive, "guest not alive on first sample");
        guest.BumpGuestHeartbeat();
        host.UpdateGuestLiveness();
        Expect(host.GuestAlive, "guest alive after mcHeartbeat moved");
        Expect(host.GuestPid == (uint)Environment.ProcessId, "guest pid published");

        // Rays: 40 rays, budget 0 => 16 per call, completes on the third call.
        var rays = new float[40 * 6];
        for (int i = 0; i < 40; i++)
        {
            bool down = (i % 2) == 0;
            rays[i * 6 + 0] = i; rays[i * 6 + 1] = down ? 10 : 1; rays[i * 6 + 2] = 0;
            rays[i * 6 + 3] = down ? i : i + 4; rays[i * 6 + 4] = down ? -10 : 1; rays[i * 6 + 5] = 0;
        }
        uint seq = guest.SubmitRays(rays, 40);
        Expect(seq != 0, "SubmitRays accepted");
        Expect(guest.SubmitRays(rays, 40) == 0, "second SubmitRays refused while in flight");
        Expect(host.RayBatchPending, "host sees pending batch");
        HostLink.RayCastFunc cast = (Vector3 s, Vector3 e, out Vector3 p) =>
        {
            // Floor at y=0 for downward rays; a wall at x=100 never reached by horizontal rays => miss.
            if (e.Y < s.Y) { p = new Vector3(s.X, 0, s.Z); return true; }
            p = default; return false;
        };
        int n1 = host.ServiceRays(cast, 0);
        Expect(n1 == 16 && !guest.RaysDone(seq), "rays: first call serves 16 and is not finished");
        Expect(host.ServiceRays(cast, 0) == 16, "rays: second call serves 16");
        Expect(host.ServiceRays(cast, 0) == 8 && guest.RaysDone(seq), "rays: third call finishes the batch");
        Expect(((ErmcRayHeader*)(guestRays(guest)))->processed == 0, "rays: processed reset to 0");
        var hits = guest.RayHits;
        Expect(hits[0].hit == 1 && hits[0].pos[1] == 0f && hits[0].normal[1] == 1f && hits[0].attr == 0, "ray hit: downward => normal (0,1,0)");
        Expect(hits[1].hit == 0 && hits[1].pos[0] == 0f && hits[1].normal[1] == 0f, "ray miss is all zero");
        Expect(guest.RaysIdle && host.RayBatchesServed == 1 && host.RaysServed == 40, "rays: idle again, stats");
        // Horizontal hit => normal = -dir.
        var one = new float[] { 0, 1, 0, 4, 1, 0 };
        uint seq2 = guest.SubmitRays(one, 1);
        host.ServiceRays((Vector3 s, Vector3 e, out Vector3 p) => { p = new Vector3(2, 1, 0); return true; }, 2.0);
        Expect(guest.RaysDone(seq2) && guest.RayHits[0].hit == 1 && guest.RayHits[0].normal[0] == -1f && guest.RayHits[0].pos[0] == 2f, "ray hit: horizontal => normal -dir");

        // Entities.
        var ents = new ErmcEntity[3];
        HostLink.FillEntity(ref ents[0], 0x1001, Protocol.EntSmallMonster, 4300, new Vector3(1, 0, 2), 0.4f, 1.8f, 221, 221, false, "c4300");
        HostLink.FillEntity(ref ents[1], 0x1002, Protocol.EntLargeMonster, 2120, new Vector3(5, 0, 5), 1.2f, 4.5f, 0, 6000, true, "c2120");
        HostLink.FillEntity(ref ents[2], 0x1003, Protocol.EntOther, 1100, new Vector3(-3, 1, 0), 0.4f, 1.8f, 100, 100, false, "c1100");
        host.PublishEntities(ents, st.frame);
        var list = new System.Collections.Generic.List<ErmcEntity>();
        Expect(guest.ReadEntities(list) && list.Count == 3, "entities: read 3");
        ErmcEntity e0 = list[0];
        Expect(e0.id == 0x1001 && e0.boxCenter[1] == 0.9f && e0.boxHalf[0] == 0.4f && e0.flags == Protocol.EntityWorldBox, "entity 0 box/flags");
        Expect(list[1].flags == (Protocol.EntityWorldBox | Protocol.EntityDead) && list[1].maxHp == 6000f, "entity 1 dead flag");
        Expect(Name(e0) == "c4300" && list[2].kind == Protocol.EntOther, "entity name/kind");
        host.ClearEntities(st.frame);
        Expect(guest.ReadEntities(list) && list.Count == 0, "entities: cleared");

        // Damage ring.
        Expect(guest.PushDamage(0x1001, 1.5f, 1, 2, 3, Protocol.DamageCritical), "damage push 1");
        Expect(guest.PushDamage(0x1002, 2.5f, 4, 5, 6, 0), "damage push 2");
        Expect(host.PendingDamage == 2, "damage pending = 2");
        var dbuf = new ErmcDamage[8];
        int dn = host.DrainDamage(dbuf);
        Expect(dn == 2 && dbuf[0].id == 0x1001 && dbuf[0].amount == 1.5f && dbuf[1].hitPos[2] == 6f && dbuf[0].flags == Protocol.DamageCritical, "damage drained in order");
        Expect(host.DrainDamage(dbuf) == 0, "damage drained once");
        int pushed = 0;
        while (guest.PushDamage(9, 1f, 0, 0, 0, 0)) pushed++;
        Expect(pushed == 256, "damage ring holds exactly 256");
        Expect(host.DrainDamage(dbuf.AsSpan(0, 8)) == 8 && host.PendingDamage == 248, "damage drains in chunks");
        var big = new ErmcDamage[512];
        Expect(host.DrainDamage(big) == 248, "damage drains remainder");
        // Drop-oldest: pretend the guest is 300 ahead (poke write directly).
        string shm = Path.Combine(dir, "bridge.shm");
        using (var raw = MappedFile.Open(shm, Protocol.ShmSize))
        {
            var q = (ErmcDamageQueueHeader*)(raw.Base + Protocol.OffDamage);
            var ring = (ErmcDamage*)(raw.Base + Protocol.OffDamage + 0x10);
            uint w = q->write;
            for (uint k = 0; k < 300; k++) { ring[(w + k) % 256].id = w + k; }
            q->write = w + 300;
            uint before = host.DamageDropped;
            int got = host.DrainDamage(big);
            Expect(got == 256 && host.DamageDropped - before == 44, "damage: >256 behind drops the oldest 44");
            Expect(big[0].id == w + 44 && big[255].id == w + 299, "damage: kept the newest 256");
        }
        Expect(guest.PushDamage(5, 1f, 0, 0, 0, 0), "damage ring usable after overflow");
        host.DrainDamage(big);

        // Hunter events.
        host.ReportHunterHit(50f, new Vector3(1, 2, 3), 1200f, 123, Protocol.EntSmallMonster);
        host.ReportHunterHit(25f, new Vector3(4, 5, 6), 1200f, 124, Protocol.EntLargeMonster);
        Expect(guest.ReadHunterEvents(out var ev) && ev.hitCount == 2 && ev.totalDamage == 75f && ev.lastDamage == 25f, "hunter: counters");
        Expect(ev.lastHitFrom[1] == 5f && ev.hunterMaxHp == 1200f && ev.lastHitFrame == 124 && ev.lastHitKind == Protocol.EntLargeMonster && (ev.seq & 1) == 0, "hunter: fields");

        // Life / death / switch / focus / action / prompt counters.
        uint life0 = guest.HostLife;
        host.BumpHostLife();
        Expect(guest.HostLife == life0 + 1, "hostLife++");
        host.BumpHostDeaths();
        Expect(guest.HostDeaths == 1, "hostDeaths++");
        Expect(!host.PollGuestDeath(), "mcDeaths: first poll latches");
        guest.BumpGuestDeaths();
        Expect(host.PollGuestDeath() && !host.PollGuestDeath() && host.GuestDeaths == 1, "mcDeaths: change detected once");
        host.BumpSwitchRequest();
        Expect(guest.SwitchRequests == 1, "F8: mcSwitchReq++");
        Expect(!host.PollFocusRequest(), "focus: first poll latches");
        guest.RequestHostFocus();
        Expect(host.PollFocusRequest() && !host.PollFocusRequest(), "focus: hostFocusReq change detected once");
        Expect(!host.PollActionRequest(out _), "action: first poll latches");
        uint req = guest.RequestAction();
        Expect(host.PollActionRequest(out uint got2) && got2 == req, "action: request seen");
        host.AckAction(req, 1);
        Expect(guest.ActionAck == req && guest.ActionResult == 1, "action: ack + result");
        host.SetPrompt("Open");
        Expect(guest.HostPrompt == "Open", "prompt text");
        host.SetPrompt("");
        Expect(guest.HostPrompt == "", "prompt cleared");
        host.SetPrompt("Abrir la puerta éé");
        Expect(guest.HostPrompt == "Abrir la puerta éé", "prompt utf-8");

        // Environment / collision writes still work on the guest side (smoke).
        guest.WriteCollision(1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        guest.WriteEnvironment(0, 0, 0, 0, 0);
    }

    private static IntPtr guestRays(GuestLink g)
    {
        // RayHits points at the hit array; the header sits RaysOffHits bytes before it.
        return (IntPtr)((byte*)g.RayHits - Protocol.RaysOffHits);
    }

    private static string Name(ErmcEntity e)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 48 && e.name[i] != 0; i++) sb.Append((char)e.name[i]);
        return sb.ToString();
    }

    private static void Frames(string dir)
    {
        string path = Path.Combine(dir, "frames.shm");
        using var frames = new HostFrames();
        Expect(!frames.TryOpen(path, true), "frames: missing file is not created");
        Expect(!File.Exists(path), "frames: host did not create frames.shm");

        // Guest side (what FramePassthrough does): create full size, header, then magic last.
        using var raw = MappedFile.Open(path, Protocol.FramesFileSize);
        byte* b = raw.Base;
        ((ErmcFramesHeader*)b)->version = Protocol.FramesVersion;
        Expect(!frames.TryOpen(path, true), "frames: magic 0 rejected");
        const int w = 4, h = 2, layer = w * h * 4;
        // slot poses: 0 -> 5, 1 -> 11, 2 -> 9
        ulong[] poses = { 5, 11, 9 };
        for (int s = 0; s < 3; s++)
        {
            byte* slot = b + 0x1000 + (long)s * Protocol.FrameSlotSize;
            var fh = (ErmcFrameHeader*)slot;
            fh->seq = 2;
            fh->width = w; fh->height = h;
            fh->flags = Protocol.FrameWorld | Protocol.FrameGui | (s == 1 ? Protocol.FrameHand : 0);
            fh->frameId = 100 + poses[s];
            fh->poseId = poses[s];
            fh->mcNear = 0.1f; fh->mcFar = 1000f;
            byte* d = slot + Protocol.FrameHdr;
            for (int i = 0; i < layer; i++) { d[i] = (byte)(s + 1); d[2 * layer + i] = (byte)(0x40 + s); d[3 * layer + i] = (byte)(0x80 + s); }
            for (int i = 0; i < w * h; i++) ((float*)(d + layer))[i] = 0.5f + s;
        }
        Volatile.Write(ref ((ErmcFramesHeader*)b)->magic, Protocol.FramesMagic);
        Expect(frames.TryOpen(path, true) && frames.IsOpen, "frames: opens after magic is published");

        Expect(frames.PoseForPresent(0) == 0, "pose history empty => 0");
        frames.NoteAppliedPose(10); frames.NoteAppliedPose(11); frames.NoteAppliedPose(12);
        Expect(frames.PoseForPresent(0) == 12 && frames.PoseForPresent(1) == 11 && frames.PoseForPresent(2) == 10, "pose_for_present lags");
        Expect(frames.PoseForPresent(5) == 10, "pose_for_present clamps lag to pos-1");
        for (ulong p = 13; p <= 30; p++) frames.NoteAppliedPose(p);
        Expect(frames.PoseForPresent(0) == 30 && frames.PoseForPresent(6) == 24 && frames.PoseForPresent(50) == 24, "pose_for_present lag clamps to 6");

        Expect(frames.PickSlot(11) == 1, "pick_slot: exact");
        Expect(frames.PickSlot(10) == 2, "pick_slot: greatest older (9)");
        Expect(frames.PickSlot(100) == 1, "pick_slot: newest older (11)");
        Expect(frames.PickSlot(4) == -1, "pick_slot: none older => -1");
        ((ErmcFrameHeader*)(b + 0x1000 + Protocol.FrameSlotSize))->seq = 3;   // slot 1 mid-write
        Expect(frames.PickSlot(11) == 2, "pick_slot: skips odd seq");
        ((ErmcFrameHeader*)(b + 0x1000 + Protocol.FrameSlotSize))->seq = 4;
        Expect(frames.PickExact == 1 && frames.PickOlder == 3 && frames.PickMissing == 1, "pick_slot stats");

        Expect(frames.ReadSlotHeader(1, out var hdr) && hdr.width == w && hdr.poseId == 11, "slot header read");
        var world = new byte[layer]; var depth = new byte[layer]; var gui = new byte[layer]; var hand = new byte[layer];
        Expect(frames.CopySlot(1, out hdr, world, depth, gui, hand), "copy_slot ok");
        Expect(world[0] == 2 && world[layer - 1] == 2 && gui[3] == 0x41 && hand[7] == 0x81, "copy_slot layers (world/gui/hand)");
        Expect(BitConverter.ToSingle(depth, 0) == 1.5f, "copy_slot depth layer");
        hand[0] = 0;
        Expect(frames.CopySlot(0, out hdr, world, default, gui, hand) && hand[0] == 0, "copy_slot: no hand flag => hand untouched");
        Expect(!frames.CopySlot(1, out _, new byte[layer - 1], depth, gui, hand), "copy_slot: short buffer rejected");
        ((ErmcFrameHeader*)(b + 0x1000))->seq = 5;
        Expect(!frames.CopySlot(0, out _, world, depth, gui, hand), "copy_slot: odd seq rejected");
        Expect(frames.LatestFrameId == 0, "latestFrameId read");
        *(ulong*)(b + 0x10) = 777;
        Expect(frames.LatestFrameId == 777, "latestFrameId read after guest write");
    }

    private static void GuestFramesWriter(string dir)
    {
        string path = Path.Combine(dir, "gf", "frames.shm");
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        // A stale file from an earlier guest: wrong magic, big latestFrameId, host-owned bytes 0x40..0xDF with a pattern.
        using (var stale = MappedFile.Open(path, Protocol.FramesFileSize))
        {
            *(uint*)stale.Base = 0xDEADBEEF;
            *(ulong*)(stale.Base + 0x10) = ulong.MaxValue / 2;
            for (int i = 0x40; i < 0xE0; i++) stale.Base[i] = (byte)(i ^ 0x5A);
            *(uint*)(stale.Base + 0x1000 + 4) = 77;   // slot 0 width
        }
        using var host = new HostFrames();
        Expect(!host.TryOpen(path, true), "guest frames: stale magic rejected before the guest initialises");
        using var gf = GuestFrames.Open(path);
        Expect(new FileInfo(path).Length >= Protocol.FramesFileSize, "guest frames: file has the full size");
        Expect(host.TryOpen(path, true), "guest frames: host opens after GuestFrames.Open");
        using (var peek = MappedFile.Open(path, Protocol.FramesFileSize))
        {
            byte* hb = peek.Base;
            Expect(*(uint*)hb == Protocol.FramesMagic && *(uint*)(hb + 4) == Protocol.FramesVersion, "guest frames: magic + version 3");
            bool hostBytesKept = true;
            for (int i = 0x40; i < 0xE0; i++) if (hb[i] != (byte)(i ^ 0x5A)) hostBytesKept = false;
            Expect(hostBytesKept, "guest frames: never touches host-owned 0x40..0xDF");
            Expect(*(uint*)(hb + 0x1000 + 4) == 0, "guest frames: slot headers zeroed");
        }
        Expect(gf.LastFrameId >= ulong.MaxValue / 2, "guest frames: counter seeded above stale latestFrameId");
        Expect(host.PickSlot(1) == -1, "guest frames: nothing published => no slot");

        const int w = 5, h = 3, layer = w * h * 4;
        var src = new byte[layer];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                src[o] = (byte)(10 * y + x); src[o + 1] = (byte)(100 + y); src[o + 2] = (byte)(200 + x); src[o + 3] = 255;
            }
        var wrk = new byte[layer]; var depth = new byte[layer]; var gui = new byte[layer]; var hand = new byte[layer];

        ulong lastId = gf.LastFrameId;
        var ids = new ulong[4];
        var slots = new int[4];
        fixed (byte* sp = src)
        {
            for (int n = 0; n < 4; n++)
            {
                int slot = gf.BeginSlot(w, h);
                slots[n] = slot;
                Expect(slot >= 0 && gf.WriteLayer(slot, GuestFrames.LayerWorld, sp, layer, false, AlphaFix.None), "guest frames: write world " + n);
                if (n == 1) Expect(host.PickSlot(1000) != slot, "guest frames: slot being written (odd seq) is skipped");
                gf.WriteLayer(slot, GuestFrames.LayerGui, sp, layer, true, AlphaFix.None);
                if (n != 2) gf.WriteLayer(slot, GuestFrames.LayerHand, sp, layer, false, AlphaFix.None);
                gf.FillDepth(slot, 1f);
                uint flags = Protocol.FrameWorld | Protocol.FrameGui | (n != 2 ? Protocol.FrameHand : 0);
                ids[n] = gf.Publish(slot, (ulong)(10 + n), flags, 0.05f, 2000f, 90f, 16f / 9f);
                Expect(ids[n] > lastId, "guest frames: frame ids strictly increase " + n);
                lastId = ids[n];
            }
        }
        Expect(slots[0] != slots[1] && slots[1] != slots[2] && slots[2] != slots[0] && slots[3] == slots[0], "guest frames: slots rotate round robin");
        Expect(host.LatestFrameId == ids[3] && ids[3] == gf.LatestFrameId, "guest frames: latestFrameId released");
        using (var peek = MappedFile.Open(path, Protocol.FramesFileSize))
            Expect(*(uint*)(peek.Base + 8) == (uint)slots[3], "guest frames: latestSlot written");

        // Poses 11, 12, 13 survive (pose 10 was overwritten by pose 13).
        Expect(host.PickSlot(13) == slots[3] && host.PickSlot(12) == slots[2] && host.PickSlot(11) == slots[1], "guest frames: host pick_slot exact");
        Expect(host.PickSlot(10) == -1, "guest frames: overwritten pose is gone");
        Expect(host.PickSlot(99) == slots[3], "guest frames: newest older pose");
        Expect(host.ReadSlotHeader(slots[3], out var hdr) && hdr.width == w && hdr.height == h && hdr.poseId == 13 && hdr.frameId == ids[3]
               && hdr.mcNear == 0.05f && hdr.mcFar == 2000f && hdr.fovYDeg == 90f && Math.Abs(hdr.aspect - 16f / 9f) < 1e-6
               && hdr.flags == (Protocol.FrameWorld | Protocol.FrameGui | Protocol.FrameHand) && hdr.gpuIndex == 0 && hdr.gpuGeneration == 0
               && hdr.seq != 0 && (hdr.seq & 1) == 0, "guest frames: slot header fields");
        Expect(host.CopySlot(slots[3], out hdr, wrk, depth, gui, hand), "guest frames: host copies the slot");
        Expect(wrk.AsSpan().SequenceEqual(src), "guest frames: world layer bytes round trip (no flip)");
        Expect(hand.AsSpan().SequenceEqual(src), "guest frames: hand layer bytes");
        bool flipped = true;
        for (int y = 0; y < h; y++)
            for (int i = 0; i < w * 4; i++)
                if (gui[y * w * 4 + i] != src[(h - 1 - y) * w * 4 + i]) flipped = false;
        Expect(flipped, "guest frames: vertical flip reverses rows");
        bool depthOne = true;
        for (int i = 0; i < w * h; i++) if (BitConverter.ToSingle(depth, i * 4) != 1.0f) depthOne = false;
        Expect(depthOne, "guest frames: depth layer is 1.0f");
        Expect(host.ReadSlotHeader(slots[2], out hdr) && (hdr.flags & Protocol.FrameHand) == 0, "guest frames: hand flag omitted when not written");

        // Alpha repair modes (pixels are B,G,R,A).
        var px = new byte[] { 0, 0, 0, 0,   9, 0, 0, 0,   0, 20, 0, 0,   30, 10, 40, 5,   1, 2, 3, 200 };
        const int pw = 5;
        fixed (byte* pp = px)
        {
            int slot = gf.BeginSlot(pw, 1);
            gf.WriteLayer(slot, GuestFrames.LayerWorld, pp, px.Length, false, AlphaFix.Opaque);
            gf.WriteLayer(slot, GuestFrames.LayerGui, pp, px.Length, false, AlphaFix.MaxRgb);
            gf.Publish(slot, 50, Protocol.FrameWorld | Protocol.FrameGui, 0.1f, 100f, 90f, 1f);
            var w2 = new byte[pw * 4]; var g2 = new byte[pw * 4];
            Expect(host.CopySlot(slot, out _, w2, default, g2, default), "guest frames: alpha test slot copies");
            Expect(w2[3] == 0 && w2[7] == 255 && w2[11] == 255 && w2[15] == 255 && w2[19] == 255 && w2[4] == 9 && w2[9] == 20, "guest frames: AlphaFix.Opaque");
            Expect(g2[3] == 0 && g2[7] == 9 && g2[11] == 20 && g2[15] == 40 && g2[19] == 200 && g2[12] == 30, "guest frames: AlphaFix.MaxRgb");
        }

        // Abort leaves an invalid slot, bad sizes and short sources are refused.
        int a = gf.BeginSlot(w, h);
        gf.Abort(a);
        using (var peek = MappedFile.Open(path, Protocol.FramesFileSize))
        {
            var ah = (ErmcFrameHeader*)(peek.Base + 0x1000 + (long)a * Protocol.FrameSlotSize);
            Expect(ah->width == 0 && (ah->seq & 1) == 0, "guest frames: aborted slot is invalid");
        }
        Expect(gf.BeginSlot(0, 10) == -1 && gf.BeginSlot(Protocol.FrameMaxW + 1, 10) == -1 && gf.BeginSlot(10, Protocol.FrameMaxH + 1) == -1, "guest frames: bad sizes refused");
        int b = gf.BeginSlot(w, h);
        fixed (byte* sp = src) Expect(!gf.WriteLayer(b, GuestFrames.LayerWorld, sp, layer - 1, false, AlphaFix.None), "guest frames: short source refused");
        gf.Abort(b);
    }
}
