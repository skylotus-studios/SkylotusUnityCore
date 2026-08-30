using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace Skylotus.Editor
{
    /// <summary>
    /// One-shot repair tool. Run once after a bad play session that baked
    /// "enabled = false" into the scene for ButtonExtended components.
    /// </summary>
    public static class ButtonExtendedRepair
    {
        [MenuItem("Skylotus/Fix Disabled ButtonExtended (Scene)")]
        private static void FixInScene()
        {
            int count = 0;

            // Search every loaded scene
            for (int s = 0; s < UnitySceneManager.sceneCount; s++)
            {
                var scene = UnitySceneManager.GetSceneAt(s);
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var btn in root.GetComponentsInChildren<ButtonExtended>(includeInactive: true))
                    {
                        if (!btn.enabled)
                        {
                            btn.enabled = true;
                            EditorUtility.SetDirty(btn);
                            count++;
                        }
                    }
                }
            }

            EditorSceneManager.SaveOpenScenes();
            Debug.Log($"[ButtonExtendedRepair] Re-enabled {count} ButtonExtended component(s) and saved scene(s).");
        }
    }
}
