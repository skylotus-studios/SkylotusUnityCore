using System;
using System.Collections.Generic;
using System.Linq;
using Skylotus.Core.Rendering;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Skylotus.Editor
{
    /// <summary>
    /// Batchmode entry points for WP-3 — the brightness setting.
    ///
    /// <see cref="GenerateBrightnessProfile"/> is the <c>[gen]</c> half: it guarantees the
    /// volume profile URP actually uses carries a <c>ColorAdjustments</c> override, written
    /// through the Volume API rather than by editing the asset's YAML.
    /// <see cref="ValidateBrightness"/> is the regression check for the whole chain, including
    /// the one thing a structural check can normally not reach — that a value saved under
    /// <see cref="SettingsService.BrightnessKey"/> comes back out as a post-exposure on the
    /// component that renders it.
    /// </summary>
    /// <remarks>
    /// Run with:
    /// <c>Tools\unity-verify.ps1 -Mode method -Graphics -Method
    /// Skylotus.Editor.SkylotusCI.GenerateBrightnessProfile</c> and
    /// <c>Tools\unity-verify.ps1 -Mode method -Graphics -Method
    /// Skylotus.Editor.SkylotusCI.ValidateBrightness</c>
    /// </remarks>
    public static partial class SkylotusCI
    {
        /// <summary>
        /// Fallback path to the URP default volume profile, used only when the render pipeline
        /// settings cannot be queried (no active pipeline in a stripped-down batchmode run).
        /// </summary>
        private const string DefaultVolumeProfilePath = "Assets/Settings/URP/DefaultVolumeProfile.asset";

        /// <summary>Collected failure descriptions for the current brightness run.</summary>
        private static readonly List<string> _brightnessFailures = new List<string>();

        /// <summary>
        /// Prefs whose presence changes what <c>ApplyAll</c> does. The brightness leg is tested
        /// with these cleared so the check never writes a resolution or quality level onto the
        /// machine running it.
        /// </summary>
        private static readonly string[] _brightnessSuppressedKeys =
        {
            SettingsService.ResolutionKey,
            SettingsService.FullscreenKey,
            SettingsService.QualityKey,
            SettingsService.VSyncKey
        };

        // ─── Generator ──────────────────────────────────────────────

        /// <summary>
        /// Ensure the volume profile URP uses as its global default carries a
        /// <c>ColorAdjustments</c> component with <c>postExposure</c> overridden.
        ///
        /// This is the neutral baseline the runtime brightness volume blends on top of: the
        /// override exists in the stack at 0 EV (the authored image) whether or not anything has
        /// touched the setting. It is written through <c>VolumeProfile.Add</c> and
        /// <c>AssetDatabase.AddObjectToAsset</c> — the asset is YAML with sub-objects addressed by
        /// <c>fileID</c>, and hand-editing it is how a serialized reference silently rots.
        ///
        /// Idempotent, and deliberately conservative: an existing <c>ColorAdjustments</c> keeps
        /// its authored <c>postExposure</c> value, because a project may legitimately have graded
        /// its default look there. Only a component this method creates is initialized to 0.
        /// </summary>
        public static void GenerateBrightnessProfile()
        {
            try
            {
                var profile = ResolveDefaultVolumeProfile(out string source);

                if (profile == null)
                {
                    Fail("Could not resolve URP's default volume profile. Is the Universal Render " +
                         "Pipeline assigned in Graphics settings?");
                    return;
                }

                string path = AssetDatabase.GetAssetPath(profile);
                Debug.Log($"[{Category}] Default volume profile: {path} (resolved via {source})");

                bool changed = false;

                if (!profile.TryGet<ColorAdjustments>(out var colorAdjustments))
                {
                    // overrides:false so only the field this system owns is marked as overriding;
                    // contrast, saturation and the rest stay at stack defaults.
                    colorAdjustments = profile.Add<ColorAdjustments>(overrides: false);
                    colorAdjustments.postExposure.value = 0f;

                    if (EditorUtility.IsPersistent(profile))
                        AssetDatabase.AddObjectToAsset(colorAdjustments, profile);

                    changed = true;
                    Debug.Log($"[{Category}] Added a ColorAdjustments override to {path}");
                }

                if (!colorAdjustments.active)
                {
                    colorAdjustments.active = true;
                    changed = true;
                    Debug.Log($"[{Category}] Enabled the existing ColorAdjustments override");
                }

                if (!colorAdjustments.postExposure.overrideState)
                {
                    colorAdjustments.postExposure.overrideState = true;
                    changed = true;
                    Debug.Log($"[{Category}] Marked postExposure as overriding");
                }

                if (changed)
                {
                    EditorUtility.SetDirty(colorAdjustments);
                    EditorUtility.SetDirty(profile);
                    AssetDatabase.SaveAssets();
                    Succeed($"Brightness baseline written to {path}.");
                }
                else
                {
                    Succeed($"{path} already carries an active ColorAdjustments override with " +
                            $"postExposure overriding (postExposure = " +
                            $"{colorAdjustments.postExposure.value}) — asset not re-saved.");
                }
            }
            catch (Exception e)
            {
                Fail($"GenerateBrightnessProfile threw: {e}");
            }
        }

        // ─── Validator ──────────────────────────────────────────────

        /// <summary>
        /// Verify WP-3's chain end to end, as far as a headless editor can reach:
        ///
        /// <list type="number">
        /// <item>the profile URP uses at runtime carries the <c>ColorAdjustments</c> baseline;</item>
        /// <item><c>BrightnessController</c> implements <see cref="IBrightnessController"/> and
        ///       lives in a <b>player</b> assembly, so it ships in a build;</item>
        /// <item>assigning it to <see cref="SettingsService.BrightnessController"/> pushes the
        ///       saved value onto the volume as a post-exposure — this is the boot path, since
        ///       the component installs itself after <c>ApplyAll</c> has run;</item>
        /// <item><c>ApplyAll</c> pushes the saved value onto an already-registered controller;</item>
        /// <item>returning to the default brightness takes the override back to 0 EV and stands
        ///       the mechanism down.</item>
        /// </list>
        ///
        /// Every pref this touches is captured and restored before the method returns.
        ///
        /// <b>What this cannot prove.</b> That the screen visibly changes. Post-exposure reaching
        /// the volume stack is necessary but not sufficient — the pixels also need URP's
        /// post-processing pass, which is why the controller switches
        /// <c>renderPostProcessing</c> on. Confirming the image actually dims needs a human, or a
        /// frame capture this harness has no way to take.
        /// </summary>
        public static void ValidateBrightness()
        {
            _brightnessFailures.Clear();

            var originalPrefs = CaptureBrightnessPrefs();

            try
            {
                CheckProfileBaseline();
                CheckControllerShipsInPlayer();
                CheckSeamPushesSavedValue();
            }
            catch (Exception e)
            {
                Fail($"ValidateBrightness threw: {e}");
                return;
            }
            finally
            {
                RestoreBrightnessPrefs(originalPrefs);
            }

            if (_brightnessFailures.Count > 0)
            {
                foreach (string failure in _brightnessFailures)
                    Debug.LogError($"[{Category}] {failure}");

                Fail($"{_brightnessFailures.Count} brightness check(s) failed.");
                return;
            }

            Succeed("Brightness is wired: profile baseline, shipping controller, and the saved " +
                    "value reaches the volume stack.");
        }

        // ─── Checks ─────────────────────────────────────────────────

        /// <summary>
        /// The profile URP resolves at runtime must carry an active <c>ColorAdjustments</c> with
        /// <c>postExposure</c> overriding, and it must be a real asset under <c>Assets/</c>.
        ///
        /// That last part is the build-inclusion argument: the profile is referenced by the URP
        /// global settings, which <c>ProjectSettings/GraphicsSettings.asset</c> maps for the
        /// Universal pipeline, and graphics settings are serialized into every player — so the
        /// asset travels without needing to be in a scene, in <c>Resources/</c>, or in a bundle.
        /// </summary>
        private static void CheckProfileBaseline()
        {
            var profile = ResolveDefaultVolumeProfile(out string source);

            if (profile == null)
            {
                BrightnessFail("default volume profile", "URP resolves no default volume profile.");
                return;
            }

            string path = AssetDatabase.GetAssetPath(profile);
            Debug.Log($"[{Category}] Default volume profile: {path} (resolved via {source})");

            BrightnessTrue("profile is a project asset",
                !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal),
                $"asset path was '{path}'");

            if (!profile.TryGet<ColorAdjustments>(out var colorAdjustments))
            {
                BrightnessFail("profile has ColorAdjustments",
                    "no ColorAdjustments component. Run GenerateBrightnessProfile.");
                return;
            }

            BrightnessTrue("ColorAdjustments is active", colorAdjustments.active,
                "the component is present but disabled");

            BrightnessTrue("postExposure overrides", colorAdjustments.postExposure.overrideState,
                "postExposure is present but not marked as overriding");

            // Cross-check that the asset the pipeline hands out is the one on disk we edited,
            // rather than a runtime clone that would make the generator's write meaningless.
            var onDisk = AssetDatabase.LoadAssetAtPath<VolumeProfile>(DefaultVolumeProfilePath);
            if (onDisk != null)
            {
                BrightnessTrue("pipeline profile is the tracked asset", onDisk == profile,
                    $"pipeline resolved '{path}', repo tracks '{DefaultVolumeProfilePath}'");
            }
        }

        /// <summary>
        /// The controller must be a MonoBehaviour implementing <see cref="IBrightnessController"/>
        /// and must compile into a <b>player</b> assembly. An editor-only implementation would
        /// pass every other check here and then not exist in a build.
        /// </summary>
        private static void CheckControllerShipsInPlayer()
        {
            var type = typeof(BrightnessController);

            BrightnessTrue("controller implements IBrightnessController",
                typeof(IBrightnessController).IsAssignableFrom(type),
                $"{type.FullName} does not implement the interface");

            BrightnessTrue("controller is a MonoBehaviour",
                typeof(MonoBehaviour).IsAssignableFrom(type),
                $"{type.FullName} is not a MonoBehaviour");

            string assemblyName = type.Assembly.GetName().Name;

            bool inPlayer = CompilationPipeline.GetAssemblies(AssembliesType.Player)
                .Any(a => string.Equals(a.name, assemblyName, StringComparison.Ordinal));

            BrightnessTrue("controller ships in the player", inPlayer,
                $"'{assemblyName}' is not among the player assemblies — it would not exist in a build");

            Debug.Log($"[{Category}] {type.FullName} lives in player assembly '{assemblyName}'");
        }

        /// <summary>
        /// Drive the real seam: save a brightness, hand a live controller to a fresh
        /// <see cref="SettingsService"/>, and read the post-exposure back off the volume.
        ///
        /// The controller is added in Edit Mode, where Unity never calls <c>Awake</c> — which is
        /// exactly why <c>SetBrightness</c> builds its volume lazily. That makes this check
        /// possible without a play-mode session.
        /// </summary>
        private static void CheckSeamPushesSavedValue()
        {
            const float saved = 0.4f;
            const float viaSetter = 0.75f;

            foreach (string key in _brightnessSuppressedKeys)
                PlayerPrefs.DeleteKey(key);

            PlayerPrefs.SetFloat(SettingsService.BrightnessKey, saved);

            var host = new GameObject("[Brightness Check]");
            host.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                var controller = host.AddComponent<BrightnessController>();
                var settings = new SettingsService();

                // 1. Assignment alone must push the saved value — the boot ordering the runtime
                //    component relies on, where it comes up after ApplyAll has already run.
                settings.BrightnessController = controller;

                BrightnessTrue("assignment pushes saved brightness",
                    Mathf.Approximately(controller.Brightness, saved),
                    $"controller holds {controller.Brightness}, saved {saved}");

                float expected = Mathf.Lerp(BrightnessController.DarkestExposure, 0f, saved);

                BrightnessTrue("saved brightness becomes post-exposure",
                    Mathf.Approximately(controller.PostExposure, expected),
                    $"post-exposure is {controller.PostExposure} EV, expected {expected} EV");

                BrightnessTrue("a dimmed screen switches the volume on", controller.IsAdjusting,
                    "the controller reports no adjustment at 0.4 brightness");

                // 2. ApplyAll must reach an already-registered controller. The video keys were
                //    cleared above, so this exercises the brightness leg and nothing else.
                PlayerPrefs.SetFloat(SettingsService.BrightnessKey, 0.1f);
                settings.ApplyAll();

                BrightnessTrue("ApplyAll pushes saved brightness",
                    Mathf.Approximately(controller.Brightness, 0.1f),
                    $"controller holds {controller.Brightness} after ApplyAll, saved 0.1");

                // 3. The setter the video tab calls must reach it too.
                settings.SetBrightness(viaSetter);

                BrightnessTrue("SetBrightness reaches the controller",
                    Mathf.Approximately(controller.Brightness, viaSetter),
                    $"controller holds {controller.Brightness}, set {viaSetter}");

                // 4. Back to the default: neutral exposure, mechanism stood down.
                settings.SetBrightness(SettingsService.DefaultBrightness);

                BrightnessTrue("default brightness is neutral",
                    Mathf.Approximately(controller.PostExposure, 0f),
                    $"post-exposure is {controller.PostExposure} EV at the default brightness");

                BrightnessTrue("default brightness stands the volume down", !controller.IsAdjusting,
                    "the controller still reports an adjustment at the default brightness");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>
        /// Find the volume profile URP uses as its global default, preferring the live pipeline
        /// settings over a hard-coded path so the check follows a moved or replaced asset.
        /// </summary>
        /// <param name="source">Filled with how the profile was found, for the log.</param>
        /// <returns>The profile, or null when neither route finds one.</returns>
        private static VolumeProfile ResolveDefaultVolumeProfile(out string source)
        {
            try
            {
                var settings = GraphicsSettings.GetRenderPipelineSettings<URPDefaultVolumeProfileSettings>();

                if (settings != null && settings.volumeProfile != null)
                {
                    source = "GraphicsSettings.GetRenderPipelineSettings<URPDefaultVolumeProfileSettings>()";
                    return settings.volumeProfile;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{Category}] Could not query the URP volume profile settings " +
                                 $"({e.GetType().Name}); falling back to the asset path.");
            }

            source = $"asset path {DefaultVolumeProfilePath}";
            return AssetDatabase.LoadAssetAtPath<VolumeProfile>(DefaultVolumeProfilePath);
        }

        /// <summary>Record a passing or failing brightness check.</summary>
        /// <param name="what">Short description of the property being asserted.</param>
        /// <param name="condition">The assertion.</param>
        /// <param name="detail">What was actually observed, logged only on failure.</param>
        private static void BrightnessTrue(string what, bool condition, string detail)
        {
            if (condition)
            {
                Debug.Log($"[{Category}]   ok: {what}");
                return;
            }

            BrightnessFail(what, detail);
        }

        /// <summary>Record a failing brightness check.</summary>
        /// <param name="what">Short description of the property being asserted.</param>
        /// <param name="detail">What was actually observed.</param>
        private static void BrightnessFail(string what, string detail)
        {
            _brightnessFailures.Add($"{what} — {detail}");
        }

        /// <summary>
        /// Snapshot every pref this check writes, so a developer's own settings survive a run.
        /// Values are kept as round-trippable strings and a null marks a key that did not exist.
        /// </summary>
        /// <returns>Key to stored value, or null for "was not set".</returns>
        private static Dictionary<string, string> CaptureBrightnessPrefs()
        {
            var captured = new Dictionary<string, string>
            {
                [SettingsService.BrightnessKey] = PlayerPrefs.HasKey(SettingsService.BrightnessKey)
                    ? PlayerPrefs.GetFloat(SettingsService.BrightnessKey).ToString("R")
                    : null
            };

            foreach (string key in _brightnessSuppressedKeys)
            {
                captured[key] = PlayerPrefs.HasKey(key)
                    ? PlayerPrefs.GetInt(key).ToString()
                    : null;
            }

            return captured;
        }

        /// <summary>Put back every pref <see cref="CaptureBrightnessPrefs"/> recorded.</summary>
        /// <param name="captured">The snapshot to restore.</param>
        private static void RestoreBrightnessPrefs(Dictionary<string, string> captured)
        {
            foreach (var pair in captured)
            {
                if (pair.Value == null)
                {
                    PlayerPrefs.DeleteKey(pair.Key);
                }
                else if (pair.Key == SettingsService.BrightnessKey)
                {
                    PlayerPrefs.SetFloat(pair.Key, float.Parse(pair.Value,
                        System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    PlayerPrefs.SetInt(pair.Key, int.Parse(pair.Value));
                }
            }

            PlayerPrefs.Save();
        }
    }
}
