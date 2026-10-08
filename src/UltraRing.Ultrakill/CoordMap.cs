using UnityEngine;

namespace UltraRing.Ultrakill
{
    /// <summary>
    /// Host stable frame (metres) &lt;-&gt; ULTRAKILL world (units). Both are left-handed, Y-up, +Z forward,
    /// so the mapping is a translation and a uniform scale. The anchor (a host point, normally the stand-in's
    /// feet when the zone was first seen) maps to <see cref="Origin"/>, keeping V1 near the Unity origin.
    /// </summary>
    internal sealed class CoordMap
    {
        public static readonly Vector3 Origin = Vector3.zero;

        public readonly uint Zone;
        public readonly double AnchorX, AnchorY, AnchorZ;
        public readonly float MetresPerUnit;

        public CoordMap(uint zone, double ax, double ay, double az, float metresPerUnit)
        {
            Zone = zone;
            AnchorX = ax;
            AnchorY = ay;
            AnchorZ = az;
            MetresPerUnit = float.IsNaN(metresPerUnit) || metresPerUnit < 0.001f ? 0.5f : metresPerUnit;
        }

        public Vector3 ToUk(double x, double y, double z)
        {
            double s = 1.0 / MetresPerUnit;
            return new Vector3((float)((x - AnchorX) * s) + Origin.x, (float)((y - AnchorY) * s) + Origin.y,
                (float)((z - AnchorZ) * s) + Origin.z);
        }

        public Vector3 ToUk(Vector3 host) => ToUk(host.x, host.y, host.z);

        public Vector3 ToHost(Vector3 uk) => new Vector3(
            (float)((uk.x - Origin.x) * (double)MetresPerUnit + AnchorX),
            (float)((uk.y - Origin.y) * (double)MetresPerUnit + AnchorY),
            (float)((uk.z - Origin.z) * (double)MetresPerUnit + AnchorZ));

        /// <summary>Lengths and directions: directions are unchanged, lengths scale.</summary>
        public float ToHostLength(float uk) => uk * MetresPerUnit;
        public float ToUkLength(float metres) => metres / MetresPerUnit;

        /// <summary>
        /// The host's yaw convention is Minecraft's: facing = (-sin y, -cos y). A Unity yaw psi faces (sin psi, cos psi),
        /// so the host yaw is psi + 180.
        /// </summary>
        public static float HostYawFromUnity(float unityYawDeg) => Mathf.Repeat(unityYawDeg + 180f, 360f);

        /// <summary>Unity yaw (degrees) of a host character quaternion (x,y,z,w): theta = 2 atan2(qy, qw), psi = theta + 180.</summary>
        public static float UnityYawFromHostQuat(float qy, float qw) =>
            Mathf.Repeat(2f * Mathf.Atan2(qy, qw) * Mathf.Rad2Deg + 180f, 360f);
    }
}
