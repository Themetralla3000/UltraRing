using System;
using System.Threading;
using ULTRAKILL.Enemy;
using ULTRAKILL.Portal;
using UnityEngine;

namespace UltraRing.Ultrakill.Combat
{
    /// <summary>
    /// The ULTRAKILL stand-in of one hostile host entity: an EnemyIdentifier with box hitboxes (no Enemy component,
    /// no renderers). DeliverDamage on it is handled by <see cref="EnemyDamagePatch"/>; this component keeps the
    /// bookkeeping (host id, pending damage, smoothing) and registers as a coin target like Enemy does.
    /// </summary>
    internal sealed class EnemyProxy : MonoBehaviour, ITarget
    {
        // ---- identity (set by the manager before activation) ----
        public ulong HostId;
        public string ModelName = "";
        public EnemyIdentifier Eid;
        public Transform BodyT, HeadT;
        public BoxCollider RootCol, BodyCol, HeadCol;
        public Rigidbody Rb;
        public string StyleType = "zombie";

        // ---- host mirror ----
        public float HostMaxHp;
        public float HostHp;
        /// <summary>Total ULTRAKILL health this enemy is worth (maxHp / HostHpPerUkHp, at least 0.5).</summary>
        public float UkMax = 1f;
        public int Stamp;
        public bool HostDead;
        public Vector3 TargetFeet;
        private Vector3 _half = new Vector3(-1f, -1f, -1f);

        // ---- damage bookkeeping ----
        public float PendingFraction;
        public Vector3 PendingHitUk;
        public float LastHitTime = -100f;
        public bool LocalDead;
        public float DeadSince;

        // ---- coin target ----
        private CancellationTokenSource _cts;
        private Vector3 _cachedPos, _cachedHead;
        private static bool _loggedRegisterFailure;

        public bool IsAlive => this != null && Eid != null && !LocalDead && !Eid.dead;

        private void Start()
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            UpdateCachedTransformData();
            try
            {
                var pm = MonoSingleton<PortalManagerV2>.Instance;
                if (pm != null && pm.TargetTracker != null) pm.TargetTracker.RegisterTarget(this, _cts.Token);
            }
            catch (Exception e)
            {
                if (!_loggedRegisterFailure)
                {
                    _loggedRegisterFailure = true;
                    Plugin.Log.LogWarning("Enemy proxy could not register as a coin target (coins will not auto-aim at host enemies): " + e);
                }
            }
        }

        private void OnDestroy()
        {
            try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
            _cts = null;
        }

        /// <summary>Applies the (world aligned) host box, in ULTRAKILL units, to the colliders.</summary>
        public void SetBox(Vector3 half)
        {
            half.x = Mathf.Max(half.x, 0.05f);
            half.y = Mathf.Max(half.y, 0.05f);
            half.z = Mathf.Max(half.z, 0.05f);
            if ((half - _half).sqrMagnitude < 1e-6f && _half.x > 0f) return;
            _half = half;
            // Root trigger covers the whole box (Explosion needs a collider on the EnemyIdentifier's own object).
            RootCol.center = new Vector3(0f, half.y, 0f);
            RootCol.size = half * 2f;
            // Body: lower 80 %, Head: top 20 %.
            float bodyH = 1.6f * half.y, headH = 0.4f * half.y;
            BodyT.localPosition = new Vector3(0f, bodyH * 0.5f, 0f);
            BodyCol.center = Vector3.zero;
            BodyCol.size = new Vector3(half.x * 2f, bodyH, half.z * 2f);
            HeadT.localPosition = new Vector3(0f, bodyH + headH * 0.5f, 0f);
            HeadCol.center = Vector3.zero;
            HeadCol.size = new Vector3(half.x * 2f, headH, half.z * 2f);
        }

        /// <summary>Teleports the proxy to its target immediately (creation, big jumps).</summary>
        public void SnapToTarget()
        {
            transform.position = TargetFeet;
            if (Rb != null) Rb.position = TargetFeet;
        }

        private void FixedUpdate()
        {
            if (Rb == null) return;
            Vector3 cur = Rb.position;
            Vector3 d = TargetFeet - cur;
            if (d.sqrMagnitude > 36f)
            {
                transform.position = TargetFeet;
                Rb.position = TargetFeet;
            }
            else if (d.sqrMagnitude > 1e-8f)
            {
                // The host publishes ~60 Hz; blend half way each 50 Hz physics step.
                Rb.MovePosition(Vector3.Lerp(cur, TargetFeet, 0.5f));
            }
        }

        /// <summary>
        /// Brings the local health in line with the host. Damage we dealt is applied locally at once, so a stale
        /// (higher) host value must not undo it: while hits are recent only the host's downward moves count.
        /// </summary>
        public void SyncHealth(float hostHp, float hostMaxHp, float now, float hostHpPerUk)
        {
            if (hostMaxHp <= 0f || Eid == null || LocalDead || Eid.dead) return;
            bool maxChanged = Mathf.Abs(hostMaxHp - HostMaxHp) > 0.5f;
            HostMaxHp = hostMaxHp;
            UkMax = Mathf.Max(hostMaxHp / Mathf.Max(hostHpPerUk, 0.01f), 0.5f);
            float hostUk = Mathf.Clamp01(hostHp / hostMaxHp) * UkMax;
            if (maxChanged || now - LastHitTime > 1.0f) Eid.health = hostUk;
            else if (hostHp < HostHp - 0.01f) Eid.health = Mathf.Min(Eid.health, hostUk);
            HostHp = hostHp;
        }

        /// <summary>Records damage to be sent to the host (aggregated and flushed by the manager).</summary>
        public void AddDamage(float fractionOfMax, Vector3 hitPointUk)
        {
            if (!(fractionOfMax > 0f) || float.IsInfinity(fractionOfMax)) return;
            PendingFraction += fractionOfMax;
            PendingHitUk = hitPointUk;
            LastHitTime = Time.unscaledTime;
        }

        /// <summary>Called by the patch when local health reaches zero.</summary>
        public void OnLocalDeath()
        {
            LocalDead = true;
            DeadSince = Time.unscaledTime;
            try { _cts?.Cancel(); } catch { }
        }

        // ---- ITarget (same shape as Enemy's) ----

        public int Id => GetInstanceID();
        public TargetType Type => TargetType.ENEMY;
        public EnemyIdentifier EID => Eid;
        public GameObject GameObject => this == null ? null : gameObject;
        public Rigidbody Rigidbody => Rb;
        public Transform Transform => transform;
        public Vector3 Position => _cachedPos;
        public Vector3 HeadPosition => _cachedHead;

        public void SetData(ref TargetData data)
        {
            data.position = _cachedPos;
            data.realPosition = _cachedPos;
            data.headPosition = _cachedHead;
            data.realHeadPosition = _cachedHead;
            data.rotation = Quaternion.identity;
            data.velocity = Vector3.zero;
        }

        public void UpdateCachedTransformData()
        {
            if (this == null) return;
            _cachedPos = BodyT != null ? BodyT.position : transform.position;
            _cachedHead = HeadT != null && HeadT.gameObject.activeInHierarchy ? HeadT.position : _cachedPos;
        }
    }
}
