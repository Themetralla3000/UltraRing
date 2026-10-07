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
            ApplyRenderSettings();
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);

            var go = new GameObject("UltraRing Session");
            go.AddComponent<BridgeMarker>();
            DontDestroyOnLoad(go);
            go.AddComponent<BridgeSession>().Init(new GuestLink());
            Log.LogInfo($"UltraRing {Version}: bridge dir {BridgePaths.Dir}");
        }

        private static void ApplyRenderSettings()
        {
            Render.FrameCapture.Scale = Mathf.Clamp(BridgeConfig.CaptureScale.Value, 0.25f, 1f);
            Render.FrameCapture.FlipRows = BridgeConfig.CaptureFlipRows.Value;
            Render.FrameCapture.WorldAlpha = ParseAlpha(BridgeConfig.WorldAlpha, AlphaFix.MaxRgb);
            Render.FrameCapture.HandAlpha = ParseAlpha(BridgeConfig.HandAlpha, AlphaFix.Opaque);
            Render.FrameCapture.GuiAlpha = ParseAlpha(BridgeConfig.GuiAlpha, AlphaFix.MaxRgb);
            Render.CaptureRig.HideMainRender = BridgeConfig.HideMainRender.Value;
        }

        private static AlphaFix ParseAlpha(BepInEx.Configuration.ConfigEntry<string> entry, AlphaFix fallback)
        {
            if (System.Enum.TryParse(entry.Value, true, out AlphaFix value)) return value;
            Log.LogWarning($"Unknown {entry.Definition.Key} '{entry.Value}', using {fallback}.");
            return fallback;
        }
    }
}
