using System;
using HarmonyLib;
using UnityEngine;

namespace UltraRing.Ultrakill.Combat
{
    /// <summary>
    /// EnemyIdentifier.DeliverDamage for host enemy proxies: everything the original does for a Husk/Machine/Demon
    /// (Enemy.GetHurt + the bits of DeliverDamage around it) minus the Enemy component we do not have, plus
    /// forwarding the damage to the host. Instances that are not proxies run the original untouched.
    /// </summary>
    [HarmonyPatch(typeof(EnemyIdentifier), nameof(EnemyIdentifier.DeliverDamage))]
    internal static class EnemyDamagePatch
    {
        private static bool _loggedError;

        [HarmonyPrefix]
        private static bool Prefix(EnemyIdentifier __instance, GameObject target, Vector3 force, Vector3 hitPoint,
            float multiplier, bool tryForExplode, float critMultiplier, GameObject sourceWeapon,
            bool ignoreTotalDamageTakenMultiplier, bool fromExplosion)
        {
            EnemyProxy proxy;
            try
            {
                if (__instance == null || !__instance.TryGetComponent(out proxy)) return true;
            }
            catch
            {
                return true;
            }

            try
            {
                Apply(proxy, __instance, target, hitPoint, multiplier, critMultiplier, sourceWeapon,
                    ignoreTotalDamageTakenMultiplier, fromExplosion);
            }
            catch (Exception e)
            {
                if (!_loggedError)
                {
                    _loggedError = true;
                    Plugin.Log.LogError("Host enemy damage failed (logged once): " + e);
                }
            }
            return false;
        }

        private static void Apply(EnemyProxy proxy, EnemyIdentifier eid, GameObject target, Vector3 hitPoint,
            float multiplier, float critMultiplier, GameObject sourceWeapon, bool ignoreTotalDamageTakenMultiplier,
            bool fromExplosion)
        {
            // Like the original, the damage list of attributes never outlives a call.
            try
            {
                if (proxy.LocalDead || eid.dead) return;
                if (float.IsNaN(multiplier) || float.IsInfinity(multiplier)) return;

                if (target == null || target == eid.gameObject) target = proxy.BodyT.gameObject;

                // --- the modifiers DeliverDamage applies before it reaches Enemy.GetHurt ---
                if (!ignoreTotalDamageTakenMultiplier) multiplier *= eid.totalDamageTakenMultiplier;
                if (eid.totalHealthModifier > 0f) multiplier /= eid.totalHealthModifier;

                // --- Enemy.CalculateLimbMultiplier / CalculateDamage / DetermineLimbType ---
                bool head = target.CompareTag("Head");
                bool limb = !head && (target.CompareTag("Limb") || target.CompareTag("EndLimb"));
                float limbMultiplier = head ? 1f : (limb ? 0.5f : 0f);
                string hitLimb = head ? "head" : (limb ? "limb" : "body");
                float damage = multiplier + multiplier * limbMultiplier * critMultiplier;
                if (!(damage > 0f)) return;

                Vector3 point = hitPoint == Vector3.zero ? target.transform.position : hitPoint;
                string hitter = eid.hitter ?? "";

                // --- health, host forwarding ---
                if (!eid.blessed) eid.health -= damage;
                bool killed = eid.health <= 0f;
                proxy.AddDamage(damage / Mathf.Max(proxy.UkMax, 0.01f), point);

                // --- blood (V1 heals from it) ---
                SpawnBlood(proxy, eid, target, point, damage, hitter, killed, fromExplosion);

                // --- style, like Enemy.SendStyleInformation ---
                if (hitter != "enemy" && hitter != "secret" && hitter != "terminalvelocity" && !eid.puppet)
                {
                    var calc = MonoSingleton<StyleCalculator>.Instance;
                    if (calc != null) calc.HitCalculator(hitter, proxy.StyleType, hitLimb, killed, eid, sourceWeapon);
                }

                // --- death: the host decides for real, this is the local reaction (kill streak, "arsenal" style) ---
                if (killed)
                {
                    // EnemyIdentifier.Death/ProcessDeath expect a real Enemy brain (NRE on proxies); the host plays the
                    // real death, the proxy just stops taking hits until the host confirms or revives it.
                    proxy.OnLocalDeath();
                }
            }
            finally
            {
                eid.hitterAttributes.Clear();
            }
        }

        /// <summary>Enemy.HandleBloodSelection + ProcessBloodEffects for a non-Statue, non-Sisyphus enemy.</summary>
        private static void SpawnBlood(EnemyProxy proxy, EnemyIdentifier eid, GameObject target, Vector3 point,
            float damage, string hitter, bool killed, bool fromExplosion)
        {
            if (hitter == "fire") return;
            var bsm = MonoSingleton<BloodsplatterManager>.Instance;
            if (bsm == null) return;

            bool enough = damage >= 1f || killed;
            bool headBlood = (target.CompareTag("Head") && enough) || hitter == "hammer" || hitter == "heavypunch";
            bool bodyBlood = (hitter == "explosion" && target.CompareTag("EndLimb")) || (enough && hitter != "explosion");
            GoreType got;
            if (headBlood) got = GoreType.Head;
            else if (bodyBlood) got = target.CompareTag("Body") ? GoreType.Body : GoreType.Limb;
            else if (hitter != "explosion") got = GoreType.Small;
            else return;

            GameObject blood = bsm.GetGore(got, eid, fromExplosion);
            if (blood == null) return;
            blood.transform.position = point;
            if (hitter == "drill") blood.transform.localScale *= 2f;

            var zone = eid.GetGoreZone();
            if (zone != null && zone.goreZone != null) blood.transform.SetParent(zone.goreZone, true);

            var splatter = blood.GetComponent<Bloodsplatter>();
            if (splatter == null) return;
            if (hitter == "shotgun" || hitter == "shotgunzone" || hitter == "explosion")
            {
                if (UnityEngine.Random.Range(0f, 1f) > 0.5f)
                {
                    var collision = splatter.GetComponent<ParticleSystem>().collision;
                    collision.enabled = false;
                }
                splatter.hpAmount = 3;
            }
            else if (hitter == "nail")
            {
                splatter.hpAmount = 1;
                var aud = splatter.GetComponent<AudioSource>();
                if (aud != null) aud.volume *= 0.8f;
            }
            splatter.GetReady();
        }
    }
}
