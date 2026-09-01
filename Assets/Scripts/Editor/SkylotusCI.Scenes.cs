using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Skylotus.Editor
{
    /// <summary>
    /// Scene-level maintenance entry points for <c>-executeMethod</c>.
    /// </summary>
    public static partial class SkylotusCI
    {
        /// <summary>
        /// Turn on <c>Render Post Processing</c> for every base camera in every scene listed in
        /// Build Settings.
        /// </summary>
        /// <remarks>
        /// URP only runs the post-processing pass — and therefore only applies the colour
        /// grading that <c>BrightnessController</c> drives — on cameras with this flag set. Every
        /// camera in this project shipped with it off, so brightness had no visible effect at all.
        ///
        /// The controller used to force the flag on at runtime and restore it afterwards. Setting
        /// it in the scenes is the honest fix: the cameras are authored the way the feature needs,
        /// and the runtime no longer touches rendering state that belongs to the scene.
        ///
        /// Idempotent — a scene whose cameras are already correct is not re-saved, so running this
        /// twice produces no diff the second time.
        /// </remarks>
        public static void EnableCameraPostProcessing()
        {
            var scenePaths = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();

            if (scenePaths.Length == 0)
            {
                Fail("No enabled scenes in Build Settings — nothing to configure.");
                return;
            }

            var changed = new List<string>();
            var alreadyCorrect = new List<string>();

            foreach (var path in scenePaths)
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

                if (!scene.IsValid())
                {
                    Fail($"Could not open {path}.");
                    return;
                }

                var touched = 0;
                var cameras = 0;

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var cam in root.GetComponentsInChildren<Camera>(includeInactive: true))
                    {
                        var data = cam.GetUniversalAdditionalCameraData();
                        if (data == null) continue;

                        cameras++;

                        // Overlay cameras inherit the base camera's post-processing setting; the
                        // flag is meaningless on them and writing it would be noise in the diff.
                        if (data.renderType != CameraRenderType.Base) continue;
                        if (data.renderPostProcessing) continue;

                        Undo.RecordObject(data, "Enable post-processing");
                        data.renderPostProcessing = true;
                        EditorUtility.SetDirty(data);
                        touched++;
                    }
                }

                if (touched > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    if (!EditorSceneManager.SaveScene(scene))
                    {
                        Fail($"Failed to save {path}.");
                        return;
                    }

                    changed.Add($"{System.IO.Path.GetFileNameWithoutExtension(path)} ({touched}/{cameras})");
                }
                else
                {
                    alreadyCorrect.Add(System.IO.Path.GetFileNameWithoutExtension(path));
                }
            }

            AssetDatabase.SaveAssets();

            if (changed.Count > 0)
                Debug.Log($"[{Category}] Enabled post-processing in: {string.Join(", ", changed)}");

            if (alreadyCorrect.Count > 0)
                Debug.Log($"[{Category}] Already correct, not re-saved: {string.Join(", ", alreadyCorrect)}");

            Succeed($"Camera post-processing configured across {scenePaths.Length} scene(s).");
        }

        /// <summary>
        /// Assert that every base camera in every Build Settings scene renders post-processing.
        ///
        /// This is the regression guard for the brightness feature. If someone unticks the flag —
        /// or adds a scene with a camera that has it off — brightness silently stops working, with
        /// no error anywhere. That is exactly how the original defect went unnoticed.
        /// </summary>
        public static void ValidateCameraPostProcessing()
        {
            var problems = 0;
            var checkedCameras = 0;

            foreach (var entry in EditorBuildSettings.scenes.Where(s => s.enabled))
            {
                var scene = EditorSceneManager.OpenScene(entry.path, OpenSceneMode.Single);

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var cam in root.GetComponentsInChildren<Camera>(includeInactive: true))
                    {
                        var data = cam.GetUniversalAdditionalCameraData();
                        if (data == null || data.renderType != CameraRenderType.Base) continue;

                        checkedCameras++;

                        if (!data.renderPostProcessing)
                        {
                            Debug.LogError(
                                $"[{Category}] {entry.path}: camera '{cam.name}' has Render Post " +
                                "Processing off — brightness will have no visible effect.");
                            problems++;
                        }
                    }
                }
            }

            if (checkedCameras == 0)
            {
                Fail("No base cameras found in any Build Settings scene — the check proved nothing.");
                return;
            }

            if (problems > 0)
            {
                Fail($"{problems} base camera(s) render no post-processing.");
                return;
            }

            Succeed($"All {checkedCameras} base camera(s) render post-processing.");
        }
    }
}
