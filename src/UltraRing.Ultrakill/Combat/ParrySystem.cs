using System;
using HarmonyLib;
using UltraRing.Link;
using UnityEngine;

namespace UltraRing.Ultrakill.Combat
{
    /// <summary>
    /// Melee "reactive" parry. Host projectiles do not exist in ULTRAKILL, so a punch thrown shortly before a host hit
    /// lands turns that hit into a parry: V1 takes no damage, gets ULTRAKILL's own parry feedback (parry flash +
    /// hitstop, full heal, stamina, "ultrakill.parry" style - all inside <c>Punch.Parry</c> / <c>NewMovement.Parry</c>)
    /// and the host entity that hit V1 takes heavy damage through the normal proxy damage path.
    /// </summary>
    internal static class ParrySystem
    {
        /// <summary>A punch counts when it started at most this long (real seconds) before the hit was detected.</summary>
        private const float WindowBefore = 0.25f;
        /// <summary>A punch started this much after the detection still counts (clock jitter between ticks).</summary>
        private const float WindowAfter = 0.05f;
        /// <summary>Attacker must be this close to V1 (host metres, box surface) ...</summary>
        private const float RangeMetres = 6f;
        /// <summary>... and this close to the position the host reports the hit came from.</summary>
        private const float SourceSearchMetres = 6f;
        private const float DamageFraction = 0.25f;
        private const float BossDamageFraction = 0.08f;
        /// <summary>Host max HP from which an entity is treated as a boss (clamped parry damage).</summary>
        private const float BossMaxHp = 1000f;

        private static float _punchTime = -100f;
        private static Vector3 _punchDir = Vector3.forward;
        private static bool _loggedError;
        private static bool _loggedFirst;

        /// <summary>Records the moment and aim of a punch (called from the PunchStart patch).</summary>
        internal static void NotePunch()
        {
            _punchTime = Time.unscaledTime;
            var cc = V1.Camera;
            if (cc != null) _punchDir = cc.transform.forward;
        }

        /// <summary>
        /// Decides whether the host hit described by <paramref name="ev"/> is parried. True: the parry (feedback and
        /// attacker damage) has been applied and V1 must not be hurt.
        /// </summary>
        public static unsafe bool TryParry(ErmcHunterEvents ev, float hostDamage)
        {
            try
            {
                float age = Time.unscaledTime - _punchTime;
                if (age > WindowBefore || age < -WindowAfter) return false;
                if (!(hostDamage > 0f)) return false;

                var mgr = EnemyProxyManager.Active;
                CoordMap map = mgr != null ? mgr.LastMap : null;
                var cc = V1.Camera;
                var nm = V1.Movement;
                if (mgr == null || map == null || cc == null || nm == null || nm.dead) return false;

                Vector3 eye = cc.transform.position;
                float sx = ev.lastHitFrom[0], sy = ev.lastHitFrom[1], sz = ev.lastHitFrom[2];
                bool hasSource = !(sx == 0f && sy == 0f && sz == 0f) &&
                                 !float.IsNaN(sx + sy + sz) && !float.IsInfinity(sx + sy + sz);

                EnemyProxy attacker = hasSource
                    ? mgr.Nearest(map.ToUk(sx, sy, sz), map.ToUkLength(SourceSearchMetres))
                    : mgr.Nearest(eye, map.ToUkLength(RangeMetres));
                if (attacker == null) return false;

                // roughly in front of V1 and within punching distance of the entity's box surface
                Vector3 surface = attacker.RootCol != null ? attacker.RootCol.ClosestPoint(eye) : attacker.CenterWorld;
                Vector3 toAttacker = surface - eye;
                float dist = toAttacker.magnitude;
                if (map.ToHostLength(dist) > RangeMetres) return false;
                if (dist > 0.05f)
                {
                    Vector3 dir = toAttacker / dist;
                    // 90 degrees from where V1 looked when punching, or where V1 looks now
                    if (Vector3.Dot(dir, _punchDir) <= 0f && Vector3.Dot(dir, cc.transform.forward) <= 0f) return false;
                }

                _punchTime = -100f; // one punch parries one hit

                // ULTRAKILL feedback: Punch.Parry ends the punch and calls NewMovement.Parry (flash, hitstop, heal, style).
                var fist = MonoSingleton<FistControl>.TryGetInstance(out var fc) ? fc.currentPunch : null;
                if (fist != null) fist.Parry(false, attacker.Eid);
                else nm.Parry(attacker.Eid);

                float frac = attacker.HostMaxHp >= BossMaxHp ? BossDamageFraction : DamageFraction;
                attacker.ApplyFractionDamage(frac, surface);

                if (!_loggedFirst)
                {
                    _loggedFirst = true;
                    Plugin.Log.LogInfo($"Parried a host hit from '{attacker.ModelName}' ({frac:P0} of its max HP).");
                }
                return true;
            }
            catch (Exception e)
            {
                if (!_loggedError)
                {
                    _loggedError = true;
                    Plugin.Log.LogError("Parry check failed (logged once): " + e);
                }
                return false;
            }
        }
    }

    /// <summary>Notes every punch that really starts (Punch.PunchStart only acts when the fist is ready).</summary>
    [HarmonyPatch(typeof(Punch), nameof(Punch.PunchStart))]
    internal static class PunchStartPatch
    {
        private static bool _loggedError;

        [HarmonyPrefix]
        private static void Prefix(Punch __instance, out bool __state)
        {
            __state = false;
            try { __state = __instance != null && __instance.ready; }
            catch { }
        }

        [HarmonyPostfix]
        private static void Postfix(bool __state)
        {
            if (!__state) return;
            try { ParrySystem.NotePunch(); }
            catch (Exception e)
            {
                if (_loggedError) return;
                _loggedError = true;
                Plugin.Log.LogError("Punch tracking failed (logged once): " + e);
            }
        }
    }
}
