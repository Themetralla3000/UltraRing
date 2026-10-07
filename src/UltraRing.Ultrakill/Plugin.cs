using BepInEx;
using BepInEx.Logging;
using UltraRing.Link;
using UnityEngine;

namespace UltraRing.Ultrakill
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "dev.ultraring.ultrakill";
        public const string Name = "UltraRing";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static GuestLink Link;

        private void Awake()
        {
            Log = Logger;
            Link = new GuestLink();
            // The host owns the final frame and usually has focus-independent input through our window;
            // ULTRAKILL must keep simulating while another window is in front.
            Application.runInBackground = true;
            Log.LogInfo($"UltraRing {Version}: bridge dir {BridgePaths.Dir}");
        }

        private bool _wasAlive;

        private void Update()
        {
            bool alive = Link.Poll();
            if (alive != _wasAlive)
            {
                _wasAlive = alive;
                Log.LogInfo(alive ? $"Host connected (pid {Link.HostProcessId})" : "Host not running");
            }
            Link.BumpGuestHeartbeat();
        }

        private void OnDestroy() => Link?.Dispose();
    }
}
