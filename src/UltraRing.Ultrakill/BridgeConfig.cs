using BepInEx.Configuration;
using UnityEngine;

namespace UltraRing.Ultrakill
{
    /// <summary>All user-tunable settings (BepInEx/config/dev.ultraring.ultrakill.cfg).</summary>
    internal static class BridgeConfig
    {
        // General
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<float> MetresPerUnit;
        public static ConfigEntry<bool> StartInSandbox;
        public static ConfigEntry<int> TargetFrameRate;

        // Combat
        public static ConfigEntry<float> HostHpPerUkHp;
        public static ConfigEntry<float> HostDamageScale;
        public static ConfigEntry<bool> SolidEnemies;

        // Rendering / window
        public static ConfigEntry<bool> Composite;
        public static ConfigEntry<bool> InputOverlay;
        public static ConfigEntry<KeyCode> SwitchKey;
        public static ConfigEntry<KeyCode> InteractKey;
        public static ConfigEntry<string> WindowMode;
        public static ConfigEntry<float> CaptureScale;
        public static ConfigEntry<bool> CaptureFlipRows;
        public static ConfigEntry<string> WorldAlpha, HandAlpha, GuiAlpha;
        public static ConfigEntry<bool> HideMainRender;

        // Terrain
        public static ConfigEntry<float> TerrainRadius;
        public static ConfigEntry<float> TerrainCell;
        public static ConfigEntry<float> TerrainStepHeight;
        public static ConfigEntry<bool> TerrainCache;

        // Debug
        public static ConfigEntry<bool> DebugOverlay;

        public static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind("General", "Enabled", true, "Run the bridge. When false ULTRAKILL behaves normally.");
            MetresPerUnit = cfg.Bind("General", "MetresPerUnit", 0.5f,
                "Host metres per ULTRAKILL unit. V1 is 3.5 units tall; 0.5 makes it 1.75 m, close to the Tarnished.");
            StartInSandbox = cfg.Bind("General", "StartInSandbox", true,
                "Boot straight into the sandbox (uk_construct) used as the bridge's empty shell.");
            TargetFrameRate = cfg.Bind("General", "TargetFrameRate", 120,
                "ULTRAKILL frame cap while bridged (vSync is turned off). The Elden Ring host is capped at 144.");

            HostHpPerUkHp = cfg.Bind("Combat", "HostHpPerUkHp", 60f,
                "How many host HP one point of ULTRAKILL enemy health is worth (a 221 HP soldier ~ 3.7 UK HP, like a Stray).");
            HostDamageScale = cfg.Bind("Combat", "HostDamageScale", 1f,
                "Damage V1 takes when the stand-in loses a share of its max HP: share * 100 * scale.");
            SolidEnemies = cfg.Bind("Combat", "SolidEnemies", false,
                "Host enemies block V1 and can be stood on (proxy hitboxes on layer 11 instead of 10).");

            Composite = cfg.Bind("Rendering", "Composite", true,
                "Send V1's viewmodel, effects and HUD to the host to be drawn into its frame.");
            InputOverlay = cfg.Bind("Rendering", "InputOverlay", true,
                "Glue ULTRAKILL's window, nearly transparent, on top of the host window so it receives keyboard and mouse.");
            SwitchKey = cfg.Bind("Rendering", "SwitchKey", KeyCode.F8, "Hand control to the host game and back.");
            InteractKey = cfg.Bind("Rendering", "InteractKey", KeyCode.V,
                "Use what the host offers (open doors, pull levers, pick up items, rest at graces). E/Q/R/F/G are ULTRAKILL's.");
            WindowMode = cfg.Bind("Rendering", "WindowMode", "Layered",
                "How the input window hides itself above the host: Layered (constant opacity 1/255), Region (full-size window clipped to one pixel; use if Layered shows ULTRAKILL opaque) or Tiny (a 1x1 window). Env ULTRARING_WINDOW_MODE overrides.");

            CaptureScale = cfg.Bind("Rendering", "CaptureScale", 1f,
                "Capture resolution as a fraction of the host window (lower = faster, blurrier V1 layer).");
            CaptureFlipRows = cfg.Bind("Rendering", "CaptureFlipRows", false,
                "Flip captured frames vertically. Turn on if V1's arm and HUD appear upside down in the host.");
            WorldAlpha = cfg.Bind("Rendering", "WorldAlpha", "MaxRgb",
                "Alpha repair for the effects layer: None, Opaque or MaxRgb (alpha from the brightest channel).");
            HandAlpha = cfg.Bind("Rendering", "HandAlpha", "Opaque", "Alpha repair for the viewmodel layer: None, Opaque or MaxRgb.");
            GuiAlpha = cfg.Bind("Rendering", "GuiAlpha", "MaxRgb", "Alpha repair for the HUD layer: None, Opaque or MaxRgb.");
            HideMainRender = cfg.Bind("Rendering", "HideMainRender", false,
                "Stop ULTRAKILL's own camera from drawing the (hidden) world, to save GPU time. Experimental.");

            TerrainRadius = cfg.Bind("Terrain", "Radius", 24f, "Host terrain is sampled this far (metres) around V1.");
            TerrainCell = cfg.Bind("Terrain", "CellSize", 0.5f, "Horizontal sampling resolution in metres.");
            TerrainStepHeight = cfg.Bind("Terrain", "StepHeight", 0.6f,
                "Height difference (metres) between neighbouring samples that becomes a wall instead of a slope.");
            TerrainCache = cfg.Bind("Terrain", "PersistentCache", true,
                "Keep every sampled terrain cell on disk (<ERMC_DIR>/terrain-cache/<zone>/) so revisited areas have collision instantly; cached cells are re-validated in the background.");

            DebugOverlay =cfg.Bind("Debug", "Overlay", false, "Show bridge diagnostics on screen (toggle with F9).");
        }
    }
}
