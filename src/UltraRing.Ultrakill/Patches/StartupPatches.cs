using HarmonyLib;

namespace UltraRing.Ultrakill.Patches
{
    /// <summary>Boot straight into the sandbox, skipping intro, menu and tutorial (GameBuildSettings.SandboxOnly).</summary>
    [HarmonyPatch(typeof(GameBuildSettings), nameof(GameBuildSettings.GetInstance))]
    internal static class BootIntoSandbox
    {
        private static void Postfix(ref GameBuildSettings __result)
        {
            if (BridgeConfig.Enabled.Value && BridgeConfig.StartInSandbox.Value)
                __result = GameBuildSettings.SandboxOnly;
        }
    }

    /// <summary>
    /// Death screen restart (R / Fire1): revive V1 where it stands instead of reloading the scene, which would
    /// destroy the bridge's colliders and proxies. The host decides where V1 really respawns (recall on hostLife).
    /// </summary>
    [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.Restart))]
    internal static class RestartInPlace
    {
        private static bool Prefix()
        {
            if (!BridgeConfig.Enabled.Value || BridgeSession.Instance == null || !BridgeSession.Instance.InBridgeScene)
                return true;
            V1.RespawnInPlace();
            return false;
        }
    }
}
