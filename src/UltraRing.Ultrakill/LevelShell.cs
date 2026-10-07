using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UltraRing.Ultrakill
{
    /// <summary>
    /// Turns the sandbox (uk_construct) into an empty shell: V1, its cameras, HUD and managers stay; every piece of
    /// level geometry, lighting volume, trigger and shop goes. The host's terrain replaces the geometry.
    /// </summary>
    internal static class LevelShell
    {
        public const string SceneName = "uk_construct";

        /// <summary>Root objects that must survive (the Player is unparented from "FirstRoom Pit" at runtime).</summary>
        private static readonly HashSet<string> Keep = new HashSet<string>
        {
            "Player", "StatsManager", "Level Info", "Cheats Enabler", "EventSystem", "Navigation Manager",
            "OutdoorsLighting", "IndoorsLighting", "GameController", "Canvas",
        };

        public static IEnumerator Prepare(MonoBehaviour host)
        {
            // Wait for the player, its activator and the camera pipeline to exist.
            float waited = 0f;
            while ((V1.Movement == null || Object.FindObjectOfType<PlayerActivator>() == null) && waited < 30f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            yield return null;

            var nm = V1.Movement;
            if (nm == null)
            {
                Plugin.Log.LogError("Sandbox loaded but no player found; bridge shell not prepared.");
                yield break;
            }

            var activator = Object.FindObjectOfType<PlayerActivator>();
            if (activator != null) activator.Activate();

            Scene scene = SceneManager.GetActiveScene();
            var disabled = new List<string>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root == nm.gameObject || root.GetComponentInChildren<NewMovement>(true) != null)
                {
                    StripSiblingsOfPlayer(root, nm.transform, disabled);
                    continue;
                }
                if (Keep.Contains(root.name) || root.GetComponent<BridgeMarker>() != null) continue;
                if (HostsSingleton(root))
                {
                    // Managers living on a scene root must keep running: strip only what is physical or visible.
                    StripPhysical(root);
                    disabled.Add(root.name + " (stripped)");
                    continue;
                }
                root.SetActive(false);
                disabled.Add(root.name);
            }
            Plugin.Log.LogInfo($"Bridge shell: disabled {disabled.Count} sandbox objects: {string.Join(", ", disabled)}");

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = BridgeConfig.TargetFrameRate.Value;
        }

        /// <summary>True if any behaviour under <paramref name="root"/> derives from MonoSingleton&lt;T&gt;.</summary>
        private static bool HostsSingleton(GameObject root)
        {
            foreach (MonoBehaviour mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                for (System.Type t = mb.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                {
                    if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(MonoSingleton<>)) return true;
                }
            }
            return false;
        }

        /// <summary>Disables colliders, renderers and the sandbox triggers that would teleport or kill V1.</summary>
        private static void StripPhysical(GameObject root)
        {
            foreach (Collider c in root.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            foreach (OutOfBounds o in root.GetComponentsInChildren<OutOfBounds>(true)) o.gameObject.SetActive(false);
            foreach (DeathZone d in root.GetComponentsInChildren<DeathZone>(true)) d.gameObject.SetActive(false);
        }

        /// <summary>If the Player is still under an authored root, keep only the branch that leads to it.</summary>
        private static void StripSiblingsOfPlayer(GameObject root, Transform player, List<string> disabled)
        {
            Transform t = player;
            while (t != null && t.parent != null)
            {
                foreach (Transform sibling in t.parent)
                {
                    if (sibling == t || sibling.name == "PlayerLoadoutTarget") continue;
                    if (!sibling.gameObject.activeSelf) continue;
                    sibling.gameObject.SetActive(false);
                    disabled.Add(sibling.name);
                }
                t = t.parent;
            }
        }
    }

    /// <summary>Marks objects created by the bridge so the shell never disables them.</summary>
    internal sealed class BridgeMarker : MonoBehaviour
    {
    }
}
