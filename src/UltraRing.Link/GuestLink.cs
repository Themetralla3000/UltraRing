using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace UltraRing.Link
{
    /// <summary>
    /// The guest's (ULTRAKILL's) view of bridge.shm, the counterpart of Minecraft Ring's
    /// BridgeShm/ErLink. x64 is TSO, so plain stores plus a full barrier around seqlock
    /// boundaries are enough; Volatile keeps the JIT from caching or reordering the counters.
    /// </summary>
    public sealed unsafe class GuestLink : IDisposable
    {
        /// <summary>The host counts as alive if its heartbeat moved within this window (Minecraft Ring: 2000 ms).</summary>
        public const long AliveTimeoutMs = 2000;

        private MappedFile _file;
        private byte* _b;
        private long _lastOpenAttemptMs = long.MinValue;
        private ulong _lastHeartbeat;
        private bool _haveHeartbeat;
        private long _lastHeartbeatChangeMs;
        private ErmcGameState _latest;
        private bool _hasState;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public string LastError { get; private set; }
        public bool Mapped => _b != null;
        public long NowMs => _clock.ElapsedMilliseconds;

        private ErmcHeader* H => (ErmcHeader*)(_b + Protocol.OffHeader);

        /// <summary>Maps the file if needed and refreshes the cached host state. True while the host is running.</summary>
        public bool Poll()
        {
            long now = NowMs;
            if (_b == null)
            {
                if (_lastOpenAttemptMs != long.MinValue && now - _lastOpenAttemptMs < 1000) return false;
                _lastOpenAttemptMs = now;
                try
                {
                    _file = MappedFile.Open(BridgePaths.File("bridge.shm"), Protocol.ShmSize);
                    _b = _file.Base;
                    InitHeader();
                    LastError = null;
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                    return false;
                }
            }
            ulong hb = Volatile.Read(ref H->hostHeartbeat);
            if (!_haveHeartbeat)
            {
                // A stale counter from a previous session must not count as live.
                _lastHeartbeat = hb;
                _haveHeartbeat = true;
                _lastHeartbeatChangeMs = 0;
            }
            else if (hb != _lastHeartbeat)
            {
                _lastHeartbeat = hb;
                _lastHeartbeatChangeMs = now;
            }
            bool alive = Alive;
            if (alive && ReadState(out ErmcGameState s))
            {
                _latest = s;
                _hasState = true;
            }
            return alive;
        }

        private void InitHeader()
        {
            ErmcHeader* h = H;
            if (h->magic != Protocol.Magic || h->version != Protocol.Version)
            {
                for (long i = 0; i < Protocol.OffRays; i += 8) *(ulong*)(_b + i) = 0;
                h->version = Protocol.Version;
                h->size = Protocol.ShmSize;
                Thread.MemoryBarrier();
                Volatile.Write(ref h->magic, Protocol.Magic);
            }
            h->mcPid = (uint)Process.GetCurrentProcess().Id;
            h->mcStartMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public bool Alive => _b != null && _lastHeartbeatChangeMs > 0 && NowMs - _lastHeartbeatChangeMs < AliveTimeoutMs;
        public ulong HostFrame => _lastHeartbeat;
        public int HostProcessId => _b != null ? (int)Volatile.Read(ref H->hostPid) : 0;

        public bool Snapshot(out ErmcGameState s)
        {
            s = _latest;
            return _hasState;
        }

        public bool ReadState(out ErmcGameState s)
        {
            var p = (ErmcGameState*)(_b + Protocol.OffState);
            for (int tries = 0; tries < 2000; tries++)
            {
                uint s1 = Volatile.Read(ref p->seq);
                if ((s1 & 1) != 0) { Thread.SpinWait(1); continue; }
                s = *p;
                Thread.MemoryBarrier();
                if (Volatile.Read(ref p->seq) == s1) return s1 != 0;
            }
            s = default;
            return false;
        }

        public void WriteControl(ref ErmcControl c)
        {
            if (_b == null) return;
            var p = (ErmcControl*)(_b + Protocol.OffControl);
            uint s = Volatile.Read(ref p->seq);
            if ((s & 1) != 0) s++;
            Volatile.Write(ref p->seq, s + 1);
            Thread.MemoryBarrier();
            c.seq = s + 1;
            *p = c;
            Thread.MemoryBarrier();
            Volatile.Write(ref p->seq, s + 2);
            c.seq = s + 2;
        }

        /// <summary>Simulation feet for the host's terrain contact sampler (see ErmcCollisionControl).</summary>
        public void WriteCollision(uint flags, uint zone, float fx, float fy, float fz, float px, float py, float pz,
            float vx, float vy, float vz)
        {
            if (_b == null) return;
            var p = (ErmcCollisionControl*)(_b + Protocol.OffCollisionControl);
            uint s = Volatile.Read(ref p->seq) & ~1u;
            Volatile.Write(ref p->seq, s + 1);
            Thread.MemoryBarrier();
            p->flags = flags == 0 ? 0 : flags | 2;   // bit1: full previous feet present
            p->zone = zone;
            p->feet[0] = fx; p->feet[1] = fy; p->feet[2] = fz;
            p->previousFeetX = px; p->previousFeetY = py; p->previousFeetZ = pz;
            p->velocity[0] = vx; p->velocity[1] = vy; p->velocity[2] = vz;
            Thread.MemoryBarrier();
            Volatile.Write(ref p->seq, s + 2);
        }

        public void WriteEnvironment(uint flags, uint timeRevision, uint dayTicks, uint weatherRevision, uint weather)
        {
            if (_b == null) return;
            var p = (ErmcEnvironment*)(_b + Protocol.OffEnvironment);
            uint s = Volatile.Read(ref p->seq) & ~1u;
            Volatile.Write(ref p->seq, s + 1);
            Thread.MemoryBarrier();
            p->flags = flags; p->timeRevision = timeRevision; p->dayTicks = dayTicks;
            p->weatherRevision = weatherRevision; p->weather = weather;
            Thread.MemoryBarrier();
            Volatile.Write(ref p->seq, s + 2);
        }

        // ---- header counters ---------------------------------------------------------------

        public void BumpGuestHeartbeat()
        {
            if (_b != null) Volatile.Write(ref H->mcHeartbeat, H->mcHeartbeat + 1);
        }

        public uint SwitchRequests => _b != null ? Volatile.Read(ref H->mcSwitchReq) : 0;
        public uint HostLife => _b != null ? Volatile.Read(ref H->hostLife) : 0;
        public uint HostDeaths => _b != null ? Volatile.Read(ref H->hostDeaths) : 0;
        public uint ActionAck => _b != null ? Volatile.Read(ref H->hostActionAck) : 0;
        public int ActionResult => _b != null ? Volatile.Read(ref H->hostActionResult) : 0;

        public void RequestHostFocus()
        {
            if (_b != null) Volatile.Write(ref H->hostFocusReq, H->hostFocusReq + 1);
        }

        public void BumpGuestDeaths()
        {
            if (_b != null) Volatile.Write(ref H->mcDeaths, H->mcDeaths + 1);
        }

        public uint RequestAction()
        {
            if (_b == null) return 0;
            uint v = H->mcActionReq + 1;
            Volatile.Write(ref H->mcActionReq, v);
            return v;
        }

        public string HostPrompt
        {
            get
            {
                if (_b == null) return "";
                byte* s = H->hostPrompt;
                int n = 0;
                while (n < 63 && s[n] != 0) n++;
                return n == 0 ? "" : Encoding.UTF8.GetString(s, n);
            }
        }

        // ---- ray queries -------------------------------------------------------------------

        private ErmcRayHeader* Rays => (ErmcRayHeader*)(_b + Protocol.OffRays);

        public bool RaysIdle => _b != null && Volatile.Read(ref Rays->reqSeq) == Volatile.Read(ref Rays->respSeq);

        /// <summary>
        /// Submits rays (start.xyz, end.xyz per ray, host units). Returns the request sequence, or 0 while a
        /// batch is still in flight or the host is not attached.
        /// </summary>
        public uint SubmitRays(float[] rays, int count, uint flags = 0, uint[] filter = null)
        {
            if (_b == null || !Alive || !RaysIdle) return 0;
            ErmcRayHeader* r = Rays;
            if (filter != null) { r->filterA = filter[0]; r->filterB = filter[1]; r->filterC = filter[2]; }
            count = Math.Min(count, Protocol.MaxRays);
            float* dst = (float*)(_b + Protocol.OffRays + Protocol.RaysOffRays);
            for (int i = 0; i < count * 6; i++) dst[i] = rays[i];
            r->count = (uint)count;
            r->flags = flags;
            uint seq = r->reqSeq + 1;
            if (seq == 0) seq = 1;
            Thread.MemoryBarrier();
            Volatile.Write(ref r->reqSeq, seq);
            return seq;
        }

        public bool RaysDone(uint seq) => _b != null && Volatile.Read(ref Rays->respSeq) == seq;

        public ErmcRayHit* RayHits => (ErmcRayHit*)(_b + Protocol.OffRays + Protocol.RaysOffHits);

        // ---- hunter events, entities, damage, contacts ------------------------------------

        public bool ReadHunterEvents(out ErmcHunterEvents ev)
        {
            ev = default;
            if (_b == null) return false;
            var p = (ErmcHunterEvents*)(_b + Protocol.OffHunter);
            for (int tries = 0; tries < 2000; tries++)
            {
                uint s1 = Volatile.Read(ref p->seq);
                if ((s1 & 1) != 0) { Thread.SpinWait(1); continue; }
                ev = *p;
                Thread.MemoryBarrier();
                if (Volatile.Read(ref p->seq) == s1) return true;
            }
            return false;
        }

        /// <summary>Seqlock read of the host's hittable entities into <paramref name="out"/>.</summary>
        public bool ReadEntities(List<ErmcEntity> output)
        {
            if (_b == null || !Alive) return false;
            var hdr = (ErmcEntityTableHeader*)(_b + Protocol.OffEntities);
            var ents = (ErmcEntity*)(_b + Protocol.OffEntities + 0x10);
            for (int tries = 0; tries < 2000; tries++)
            {
                uint s1 = Volatile.Read(ref hdr->seq);
                if ((s1 & 1) != 0) { Thread.SpinWait(1); continue; }
                int count = (int)Math.Min(hdr->count, (uint)Protocol.MaxEntities);
                output.Clear();
                for (int i = 0; i < count; i++) output.Add(ents[i]);
                Thread.MemoryBarrier();
                if (Volatile.Read(ref hdr->seq) == s1) return s1 != 0;
            }
            return false;
        }

        /// <summary>Queues a hit for the host to apply. False if the ring is full.</summary>
        public bool PushDamage(ulong id, float amount, float x, float y, float z, uint flags)
        {
            if (_b == null) return false;
            var q = (ErmcDamageQueueHeader*)(_b + Protocol.OffDamage);
            uint write = q->write;
            uint read = Volatile.Read(ref q->read);
            if (write - read >= Protocol.DamageRing) return false;
            var e = (ErmcDamage*)(_b + Protocol.OffDamage + 0x10) + (write % Protocol.DamageRing);
            e->id = id;
            e->amount = amount;
            e->hitPos[0] = x; e->hitPos[1] = y; e->hitPos[2] = z;
            e->flags = flags;
            e->reserved = 0;
            Thread.MemoryBarrier();
            Volatile.Write(ref q->write, write + 1);
            return true;
        }

        /// <summary>
        /// Reads the host's latest complete terrain contact table. Returns the count, -2 if unchanged since
        /// <paramref name="previousSeq"/>, or -1 if nothing consistent is available.
        /// </summary>
        public int ReadContacts(ErmcTerrainContact[] output, out ErmcTerrainContactsHeader header, uint previousSeq)
        {
            header = default;
            if (_b == null || !Alive) return -1;
            var h = (ErmcTerrainContactsHeader*)(_b + Protocol.OffContacts);
            for (int tries = 0; tries < 8; tries++)
            {
                uint seq = Volatile.Read(ref h->seq);
                if (seq == previousSeq && seq != 0) return -2;
                if (seq == 0 || (seq & 1) != 0) continue;
                header = *h;
                if ((header.valid & 1) == 0) return -1;
                int headerSize = (header.valid & 2) != 0 ? 40 : 32;
                int count = (int)header.count;
                if (count < 0 || count > Protocol.MaxContacts || count > output.Length) return -1;
                var src = (ErmcTerrainContact*)((byte*)h + headerSize);
                for (int i = 0; i < count; i++) output[i] = src[i];
                Thread.MemoryBarrier();
                if (Volatile.Read(ref h->seq) == seq) return count;
            }
            return -1;
        }

        public void Dispose()
        {
            _file?.Dispose();
            _file = null;
            _b = null;
        }
    }
}
