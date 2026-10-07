using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
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

        private void Awake()
        {
            Log = Logger;
            BridgeConfig.Bind(Config);
            if (!BridgeConfig.Enabled.Value)
            {
                Log.LogInfo("UltraRing disabled in config.");
                return;
            }
            // ULTRAKILL ships with runInBackground off: the whole player loop would stop whenever the host window
            // has focus (F8) or is clicked.
            Application.runInBackground = true;
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);

            var go = new GameObject("UltraRing Session");
            go.AddComponent<BridgeMarker>();
            DontDestroyOnLoad(go);
            go.AddComponent<BridgeSession>().Init(new GuestLink());
            Log.LogInfo($"UltraRing {Version}: bridge dir {BridgePaths.Dir}");
        }
    }
}
