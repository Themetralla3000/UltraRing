using System;
using System.Collections.Generic;
using UltraRing.Link;
using UnityEngine;

namespace UltraRing.Ultrakill.Combat
{
    /// <summary>
    /// One invisible, hittable ULTRAKILL proxy per hostile host entity; ULTRAKILL damage goes to the host
    /// (docs/research/ultrakill-internals.md D.6, host-contract.md 6.1/6.2).
    /// </summary>
    internal sealed class EnemyProxyManager
    {
        /// <summary>How long a locally killed proxy lingers so the killing blow's gore and style can finish.</summary>
        private const float DeathLinger = 0.3f;
        /// <summary>If the host still reports a locally killed enemy alive after this long, the proxy is rebuilt.</summary>
        private const float ReviveAfter = 0.6f;

        private readonly Transform _parent;
        private readonly List<ErmcEntity> _ents = new List<ErmcEntity>(256);
        private readonly Dictionary<ulong, EnemyProxy> _byId = new Dictionary<ulong, EnemyProxy>();
        private readonly List<EnemyProxy> _list = new List<EnemyProxy>();

        private GoreZone _goreZone;
        private BloodsplatterManager _goreZoneBsm;

        private int _tick;
        private int _entityCount;
        private readonly Dictionary<ulong, float> _createRetryAt = new Dictionary<ulong, float>();
        /// <summary>Proxies are only made this close (host metres) to V1, and dropped beyond the larger radius.</summary>
        private const float SpawnRadiusM = 80f, DropRadiusM = 100f;
        private const float CreateRetryS = 5f;
        private int _hitsSent;
        private float _lastAmount;
        private string _lastName = "";
        private bool _loggedCreateFailure;

        /// <summary>The live manager and the mapping of its last tick (used by <see cref="ParrySystem"/>).</summary>
        internal static EnemyProxyManager Active;
        internal CoordMap LastMap;

        public EnemyProxyManager(Transform parent) => _parent = parent;

        /// <summary>
        /// Nearest living proxy whose box surface lies within <paramref name="maxUk"/> ULTRAKILL units of
        /// <paramref name="ukPos"/>; null when none. No allocation.
        /// </summary>
        public EnemyProxy Nearest(Vector3 ukPos, float maxUk)
        {
            EnemyProxy best = null;
            float bestSq = maxUk * maxUk;
            for (int i = 0; i < _list.Count; i++)
            {
                var p = _list[i];
                if (p == null || !p.IsAlive || p.HostDead) continue;
                // Measure to the box surface: the host reports an attacker's feet, the centre can be metres above them.
                Vector3 near = p.RootCol != null && p.RootCol.enabled ? p.RootCol.ClosestPoint(ukPos) : p.CenterWorld;
                float sq = (near - ukPos).sqrMagnitude;
                if (sq <= bestSq) { bestSq = sq; best = p; }
            }
            return best;
        }

        public string Status =>
            $"{_entityCount} entities, {_list.Count} proxies, {_hitsSent} hits sent" +
            (_hitsSent > 0 ? $", last {_lastAmount:F1} -> {_lastName}" : "");

        public void Tick(GuestLink link, CoordMap map)
        {
            if (link == null || map == null) return;
            Active = this;
            LastMap = map;
            float now = Time.unscaledTime;
            float hpPerUk = Mathf.Max(BridgeConfig.HostHpPerUkHp.Value, 0.01f);
            _tick++;

            if (link.ReadEntities(_ents))
            {
                _entityCount = _ents.Count;
                var nm = V1.Movement;
                bool haveV1 = nm != null;
                Vector3 v1 = haveV1 ? V1.Feet(nm) : Vector3.zero;
                for (int i = 0; i < _ents.Count; i++)
                {
                    try { ApplyEntity(link, _ents[i], map, now, hpPerUk, haveV1, v1); }
                    catch (Exception e) { LogCreateFailure(e); }
                }
                RemoveStale(link, map, now);
            }

            for (int i = 0; i < _list.Count; i++) Flush(_list[i], link, map);
        }

        public void Clear()
        {
            for (int i = _list.Count - 1; i >= 0; i--) Destroy(_list[i]);
            _list.Clear();
            _byId.Clear();
            _entityCount = 0;
            _createRetryAt.Clear();
            if (Active == this) Active = null;
        }

        // ---- per entity ---------------------------------------------------------------------

        private unsafe void ApplyEntity(GuestLink link, ErmcEntity e, CoordMap map, float now, float hpPerUk, bool haveV1, Vector3 v1)
        {
            if (e.kind != Protocol.EntLargeMonster && e.kind != Protocol.EntSmallMonster) return;
            float maxHp = e.maxHp;
            if (!(maxHp > 0f) || float.IsInfinity(maxHp) || float.IsNaN(e.hp)) return;
            bool dead = (e.flags & Protocol.EntityDead) != 0 || e.hp <= 0f;

            _byId.TryGetValue(e.id, out EnemyProxy p);
            if (p != null && (p.Eid == null || p == null))
            {
                // destroyed behind our back
                _byId.Remove(e.id);
                _list.Remove(p);
                p = null;
            }
            if (p != null && p.LocalDead && !dead && now - p.DeadSince > ReviveAfter)
            {
                // killed here but the host enemy lives on (invincible, rounding...): rebuild it
                Flush(p, link, map);
                Destroy(p);
                _byId.Remove(e.id);
                _list.Remove(p);
                p = null;
            }
            if (haveV1 && !dead)
            {
                // No distance cut for what is already alive until DropRadiusM (hysteresis); a proxy that is not
                // stamped this tick is flushed and removed by RemoveStale.
                Vector3 c = map.ToUk(e.boxCenter[0], e.boxCenter[1], e.boxCenter[2]);
                float limit = map.ToUkLength(p != null ? DropRadiusM : SpawnRadiusM);
                if ((c - v1).sqrMagnitude > limit * limit) return;
            }
            if (p == null)
            {
                if (dead) return;
                if (_createRetryAt.TryGetValue(e.id, out float retryAt) && now < retryAt) return;
                p = Create(e, map);
                if (p == null)
                {
                    if (_createRetryAt.Count > 1024) _createRetryAt.Clear();
                    _createRetryAt[e.id] = now + CreateRetryS; // do not throw and allocate every frame
                    return;
                }
                _createRetryAt.Remove(e.id);
            }

            if (!p.LocalDead && p.Eid.dead) p.OnLocalDeath(); // killed by something other than DeliverDamage (cheats)
            p.Stamp = _tick;
            p.HostDead = dead;
            if (dead) return;

            Vector3 center = map.ToUk(e.boxCenter[0], e.boxCenter[1], e.boxCenter[2]);
            Vector3 half = new Vector3(
                map.ToUkLength(Mathf.Abs(e.boxHalf[0])),
                map.ToUkLength(Mathf.Abs(e.boxHalf[1])),
                map.ToUkLength(Mathf.Abs(e.boxHalf[2])));
            p.SetBox(half);
            p.TargetFeet = center - new Vector3(0f, Mathf.Max(half.y, 0.05f), 0f);
            if (!p.LocalDead) p.SyncHealth(e.hp, maxHp, now, hpPerUk);
        }

        private unsafe EnemyProxy Create(ErmcEntity e, CoordMap map)
        {
            GameObject go = null;
            try
            {
                bool large = e.kind == Protocol.EntLargeMonster;
                string name = ReadName(e.name);
                float hpPerUk = Mathf.Max(BridgeConfig.HostHpPerUkHp.Value, 0.01f);
                int layer = BridgeConfig.SolidEnemies.Value ? 11 : 10;

                go = new GameObject("HostEnemy " + name + " " + e.id.ToString("X"));
                go.SetActive(false); // configure before Awake/Start see it
                var zone = EnsureGoreZone();
                go.transform.SetParent(zone != null ? zone.transform : _parent, false);

                Vector3 center = map.ToUk(e.boxCenter[0], e.boxCenter[1], e.boxCenter[2]);
                float hy = Mathf.Max(map.ToUkLength(Mathf.Abs(e.boxHalf[1])), 0.05f);
                Vector3 feet = center - new Vector3(0f, hy, 0f);
                go.transform.position = feet;

                // root: layer 12 (EnemyTrigger) trigger + kinematic body, like the root of a real enemy
                go.layer = 12;
                go.tag = "IgnorePushes"; // Explosion would otherwise try to shove the kinematic body
                var rootCol = go.AddComponent<BoxCollider>();
                rootCol.isTrigger = true;
                var rb = go.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
                go.AddComponent<BridgeMarker>();

                var body = new GameObject("Body");
                body.transform.SetParent(go.transform, false);
                body.layer = layer;
                body.tag = "Body";
                var bodyCol = body.AddComponent<BoxCollider>();
                var bodyRb = AddKinematicBody(body);

                var head = new GameObject("Head");
                head.transform.SetParent(go.transform, false);
                head.layer = layer;
                head.tag = "Head";
                var headCol = head.AddComponent<BoxCollider>();
                var headRb = AddKinematicBody(head);

                var eid = go.AddComponent<EnemyIdentifier>();
                // Minotaur for both: HookArm treats Filth/Soldier/Stray/Drone/... as "light" (the enemy is pulled to V1,
                // impossible for a host entity); every other type pulls V1 to the enemy. Nothing else keys on it here.
                eid.enemyType = EnemyType.Minotaur;
                eid.enemyClass = large ? EnemyClass.Demon : EnemyClass.Husk;
                eid.dontUnlockBestiary = true;
                eid.dontCountAsKills = true;
                eid.ignorePlayer = true;
                eid.checkingSpawnStatus = false;
                eid.weakPoint = head;
                eid.health = Mathf.Max(e.maxHp / hpPerUk, 0.5f);

                var bodyId = body.AddComponent<EnemyIdentifierIdentifier>();
                bodyId.eid = eid;
                var headId = head.AddComponent<EnemyIdentifierIdentifier>();
                headId.eid = eid;

                var proxy = go.AddComponent<EnemyProxy>();
                proxy.HostId = e.id;
                proxy.ModelName = name;
                proxy.Eid = eid;
                proxy.BodyT = body.transform;
                proxy.HeadT = head.transform;
                proxy.RootCol = rootCol;
                proxy.BodyCol = bodyCol;
                proxy.HeadCol = headCol;
                proxy.Rb = rb;
                proxy.BodyRb = bodyRb;
                proxy.HeadRb = headRb;
                proxy.IsLarge = large;
                proxy.StyleType = large ? "spider" : "zombie";
                proxy.HostMaxHp = e.maxHp;
                proxy.HostHp = e.hp;
                proxy.UkMax = Mathf.Max(e.maxHp / hpPerUk, 0.5f);
                proxy.TargetFeet = feet;
                proxy.SetBox(new Vector3(
                    map.ToUkLength(Mathf.Abs(e.boxHalf[0])), hy, map.ToUkLength(Mathf.Abs(e.boxHalf[2]))));
                proxy.SnapToTarget();

                go.SetActive(true);
                // Awake set health to 999; the host is authoritative.
                eid.health = Mathf.Clamp01(e.hp / e.maxHp) * proxy.UkMax;

                _byId[e.id] = proxy;
                _list.Add(proxy);
                return proxy;
            }
            catch (Exception ex)
            {
                LogCreateFailure(ex);
                if (go != null) UnityEngine.Object.Destroy(go);
                return null;
            }
        }

        private static Rigidbody AddKinematicBody(GameObject go)
        {
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            return rb;
        }

        private void RemoveStale(GuestLink link, CoordMap map, float now)
        {
            for (int i = _list.Count - 1; i >= 0; i--)
            {
                var p = _list[i];
                bool gone = p == null || p.Eid == null;
                if (!gone)
                {
                    bool present = p.Stamp == _tick && !p.HostDead;
                    if (present) continue;
                    // Vanished or dead on the host. A locally killed proxy lingers briefly for its gore and style.
                    if (p.LocalDead && now - p.DeadSince < DeathLinger) continue;
                    Flush(p, link, map);
                }
                // p may be a destroyed (Unity-null) proxy: its managed HostId is still readable.
                if (!ReferenceEquals(p, null)) _byId.Remove(p.HostId);
                _list.RemoveAt(i);
                Destroy(p);
            }
        }

        // ---- damage -------------------------------------------------------------------------

        /// <summary>
        /// Sends the damage accumulated since the last flush as one ring entry. The host applies
        /// ceil(amount * maxHp / mcHealth); sending fraction * mcHealth removes exactly that fraction of max HP.
        /// </summary>
        private void Flush(EnemyProxy p, GuestLink link, CoordMap map)
        {
            if (p == null || !(p.PendingFraction > 0f)) return;
            if (p.HostMaxHp <= 0f) { p.PendingFraction = 0f; return; }
            float mc = Mathf.Clamp(20f * Mathf.Sqrt(p.HostMaxHp / 100f), 10f, 300f);
            float amount = Mathf.Min(p.PendingFraction * mc, 9999f);
            if (!(amount > 0f)) { p.PendingFraction = 0f; return; }
            Vector3 hit = map.ToHost(p.PendingHitUk);
            if (link.PushDamage(p.HostId, amount, hit.x, hit.y, hit.z, 0))
            {
                p.PendingFraction = 0f;
                _hitsSent++;
                _lastAmount = amount;
                _lastName = p.ModelName;
            }
            // ring full: keep it pending for the next tick
        }

        // ---- helpers ------------------------------------------------------------------------

        private void Destroy(EnemyProxy p)
        {
            if (p == null) return;
            try
            {
                if (p.Eid != null)
                {
                    var tracker = MonoSingleton<EnemyTracker>.Instance;
                    if (tracker != null) tracker.RemoveEnemy(p.Eid);
                }
                UnityEngine.Object.Destroy(p.gameObject);
            }
            catch (Exception e)
            {
                LogCreateFailure(e);
            }
        }

        /// <summary>
        /// Proxies live under their own GoreZone so GoreZone.ResolveGoreZone (called by EnemyIdentifier) finds it
        /// instead of inventing an "Automated Gore Zone" and reparenting them. Rebuilt when ULTRAKILL's
        /// BloodsplatterManager changed (scene reload) because the zone caches it.
        /// </summary>
        private GoreZone EnsureGoreZone()
        {
            var bsm = MonoSingleton<BloodsplatterManager>.Instance;
            if (_goreZone != null && _goreZoneBsm == bsm) return _goreZone;
            if (_goreZone != null)
            {
                // proxies parented to the stale zone go with it
                for (int i = _list.Count - 1; i >= 0; i--) Destroy(_list[i]);
                _list.Clear();
                _byId.Clear();
                UnityEngine.Object.Destroy(_goreZone.gameObject);
                _goreZone = null;
            }
            if (bsm == null) return null; // GoreZone.Start needs it; fall back to the plain parent
            var go = new GameObject("UltraRing Gore Zone");
            go.transform.SetParent(_parent, false);
            go.AddComponent<BridgeMarker>();
            _goreZone = go.AddComponent<GoreZone>();
            _goreZoneBsm = bsm;
            return _goreZone;
        }

        private static unsafe string ReadName(byte* name)
        {
            int n = 0;
            while (n < 48 && name[n] != 0) n++;
            var chars = new char[n];
            for (int i = 0; i < n; i++) chars[i] = (char)name[i];
            return new string(chars);
        }

        private void LogCreateFailure(Exception e)
        {
            if (_loggedCreateFailure) return;
            _loggedCreateFailure = true;
            Plugin.Log.LogError("Enemy proxy failure (logged once): " + e);
        }
    }
}
