using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Skylotus.Editor
{
    /// <summary>
    /// Batchmode entry points driven by <c>Tools/unity-verify.ps1</c>.
    ///
    /// Every method here is invoked headlessly via
    /// <c>Unity.exe -batchmode -quit -executeMethod Skylotus.Editor.SkylotusCI.&lt;Method&gt;</c>
    /// and must therefore:
    ///
    /// <list type="bullet">
    /// <item>be <c>public static void</c> with no parameters,</item>
    /// <item>call <see cref="Fail"/> on any error so the process exits non-zero
    ///       (an uncaught exception also works, but the message is noisier),</item>
    /// <item>call <see cref="Succeed"/> on the happy path so the log is greppable,</item>
    /// <item>never assume a graphics device — pass <c>-Graphics</c> to the wrapper
    ///       for anything that needs one.</item>
    /// </list>
    ///
    /// Asset-generating work packages (core systems prefab, audio mixer, project
    /// settings) add their generator here rather than hand-authoring YAML.
    /// </summary>
    /// <remarks>
    /// <b>Partial by design.</b> Several work packages need to add their own
    /// <c>-executeMethod</c> targets, and a single file would serialize them behind one
    /// another. Each package adds <c>SkylotusCI.&lt;Area&gt;.cs</c> beside this file
    /// (<c>SkylotusCI.Audio.cs</c>, <c>SkylotusCI.Project.cs</c>, <c>SkylotusCI.Build.cs</c>)
    /// and never edits this one. The shared helpers below — <see cref="Succeed"/>,
    /// <see cref="Fail"/>, <see cref="SetObjectReference"/>, <see cref="RequireReference"/>,
    /// <see cref="RequireComponent{T}"/> — are available to every partial.
    /// </remarks>
    public static partial class SkylotusCI
    {
        /// <summary>Log category used by every method in this class.</summary>
        private const string Category = "CI";

        /// <summary>Asset path of the generated core systems prefab.</summary>
        private const string CoreSystemsPrefabPath = "Assets/Resources/Prefabs/SkylotusCoreSystems.prefab";

        /// <summary>Boot scene whose <c>Bootstrapper</c> receives the generated prefab.</summary>
        private const string BootScenePath = "Assets/Scenes/BootScene.unity";

        /// <summary>Input Action Asset wired onto the prefab's <c>InputManager</c>.</summary>
        private const string InputActionsPath = "Assets/InputSystem_Actions.inputactions";

        /// <summary>Asset path of the mixer built by <c>GenerateAudioMixer</c>.</summary>
        private const string AudioMixerPath = "Assets/Resources/Audio/SkylotusAudioMixer.mixer";

        /// <summary>Design resolution the generated overlay canvases scale against.</summary>
        private static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);

        // ─── Entry Points ───────────────────────────────────────────

        /// <summary>
        /// Smoke test for the batchmode plumbing: confirms the project compiled,
        /// the editor assemblies loaded, and <c>-executeMethod</c> dispatch works.
        /// Run this first when setting up the harness on a new machine.
        /// </summary>
        public static void CompileCheck()
        {
            var playerAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player);
            var editorAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor);

            Debug.Log($"[{Category}] Unity {Application.unityVersion}");
            Debug.Log($"[{Category}] Player assemblies: {playerAssemblies.Length}");
            Debug.Log($"[{Category}] Editor assemblies: {editorAssemblies.Length}");

            // The core runtime assembly must exist and must not be empty — a missing
            // asmdef reference silently produces an assembly with no source files.
            var core = playerAssemblies.FirstOrDefault(a => a.name == "Skylotus.Core.Runtime");

            if (core == null)
            {
                Fail("Skylotus.Core.Runtime assembly not found. Check the asmdef.");
                return;
            }

            Debug.Log($"[{Category}] Skylotus.Core.Runtime: {core.sourceFiles.Length} source files, " +
                      $"{core.assemblyReferences.Length} assembly references");

            Succeed("Compile check passed.");
        }

        /// <summary>
        /// Reports any asmdef reference under <c>Assets/</c> that does not resolve to a real
        /// assembly. Unity logs these as console errors that are easy to miss in a busy editor,
        /// and they compile "successfully" while silently dropping the reference.
        ///
        /// Deliberately scoped to <c>Assets/</c>. Unity's own packages routinely reference
        /// assemblies that are not installed — optional integrations gated by
        /// <c>versionDefines</c>, platform-specific modules, and test-only dependencies.
        /// Scanning <c>Packages/</c> produces ~25 false positives on this project and makes
        /// the check useless as a gate.
        /// </summary>
        public static void ValidateAssemblyReferences()
        {
            var all = CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .Concat(CompilationPipeline.GetAssemblies(AssembliesType.Player))
                .ToArray();

            var known = new System.Collections.Generic.HashSet<string>(all.Select(a => a.name));
            int broken = 0;

            var projectAsmdefs = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal))
                .ToArray();

            Debug.Log($"[{Category}] Scanning {projectAsmdefs.Length} asmdef(s) under Assets/");

            foreach (var asmdefPath in projectAsmdefs)
            {
                var json = System.IO.File.ReadAllText(asmdefPath);

                // Cheap extraction — asmdef "references" is a flat string array.
                var match = System.Text.RegularExpressions.Regex.Match(
                    json, "\"references\"\\s*:\\s*\\[(.*?)\\]",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                if (!match.Success) continue;

                foreach (System.Text.RegularExpressions.Match r in
                         System.Text.RegularExpressions.Regex.Matches(match.Groups[1].Value, "\"([^\"]+)\""))
                {
                    var name = r.Groups[1].Value;

                    // GUID-form references resolve through the AssetDatabase instead.
                    if (name.StartsWith("GUID:", StringComparison.Ordinal)) continue;

                    if (!known.Contains(name))
                    {
                        Debug.LogError($"[{Category}] {asmdefPath}: unresolved reference '{name}'");
                        broken++;
                    }
                }
            }

            if (broken > 0)
            {
                Fail($"{broken} unresolved asmdef reference(s).");
                return;
            }

            Succeed("All asmdef references resolve.");
        }

        /// <summary>
        /// Build (or rebuild) the core systems prefab at <see cref="CoreSystemsPrefabPath"/> and
        /// assign it to the <c>Bootstrapper</c> in <see cref="BootScenePath"/>.
        ///
        /// This exists because a component added at runtime with <c>AddComponent</c> gets the
        /// compile-time default of every <c>[SerializeField]</c> and there is no way to author
        /// those values — which is why <c>SkylotusSceneManager</c>'s loading screen and
        /// <c>UIManager</c>'s containers were permanently null. Putting the systems on a prefab
        /// makes their serialized state real and editable.
        ///
        /// Requires a graphics device (the overlay canvases build real UI), so run it as:
        /// <c>Tools\unity-verify.ps1 -Mode method -Graphics -Method
        /// Skylotus.Editor.SkylotusCI.GenerateCoreSystemsPrefab</c>
        ///
        /// Re-running is safe: the prefab is overwritten in place and the boot scene is only
        /// re-saved when the reference actually changes.
        /// </summary>
        public static void GenerateCoreSystemsPrefab()
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(CoreSystemsPrefabPath);
                if (!AssetDatabase.IsValidFolder(directory))
                {
                    Fail($"Prefab folder does not exist: {directory}");
                    return;
                }

                GameObject prefab;
                var root = BuildCoreSystemsHierarchy();

                try
                {
                    prefab = PrefabUtility.SaveAsPrefabAsset(root, CoreSystemsPrefabPath, out var saved);

                    if (!saved || prefab == null)
                    {
                        Fail($"PrefabUtility.SaveAsPrefabAsset failed for {CoreSystemsPrefabPath}");
                        return;
                    }
                }
                finally
                {
                    // Always tear the scratch hierarchy down; it lives in whatever scene batchmode
                    // happened to open and must never be carried into the boot scene.
                    UnityEngine.Object.DestroyImmediate(root);
                }

                AssetDatabase.SaveAssets();
                Debug.Log($"[{Category}] Wrote prefab: {CoreSystemsPrefabPath}");

                if (!AssignPrefabToBootScene(prefab))
                    return;

                Succeed("Core systems prefab generated and wired into BootScene.");
            }
            catch (Exception e)
            {
                Fail($"GenerateCoreSystemsPrefab threw: {e}");
            }
        }

        /// <summary>
        /// Regression check for <see cref="GenerateCoreSystemsPrefab"/>: asserts that the prefab
        /// exists, carries every system component the bootstrapper expects, has its previously
        /// unreachable references (loading screen, progress bar, UI containers, input actions)
        /// actually assigned, and is referenced by the boot scene's <c>Bootstrapper</c>.
        ///
        /// Structural only. Runtime behaviour — registration order, the loading-screen fade —
        /// needs a PlayMode test, which this harness cannot substitute for.
        /// </summary>
        public static void ValidateCoreSystemsPrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CoreSystemsPrefabPath);

            if (prefab == null)
            {
                Fail($"No prefab at {CoreSystemsPrefabPath}. Run GenerateCoreSystemsPrefab.");
                return;
            }

            int problems = 0;

            problems += RequireComponent<AudioManager>(prefab);
            problems += RequireComponent<ObjectPool>(prefab);
            problems += RequireComponent<GameStateMachine>(prefab);
            problems += RequireComponent<TimeManager>(prefab);
            problems += RequireComponent<DialogueSystem>(prefab);
            problems += RequireComponent<NotificationSystem>(prefab);
            problems += RequireComponent<DebugConsole>(prefab);

            var sceneManager = prefab.GetComponentInChildren<SkylotusSceneManager>(true);
            var uiManager = prefab.GetComponentInChildren<UIManager>(true);
            var inputManager = prefab.GetComponentInChildren<InputManager>(true);

            problems += RequireComponent<SkylotusSceneManager>(prefab);
            problems += RequireComponent<UIManager>(prefab);
            problems += RequireComponent<InputManager>(prefab);

            if (sceneManager != null)
            {
                problems += RequireReference(sceneManager, "_loadingScreen");
                problems += RequireReference(sceneManager, "_progressBar");
            }

            if (uiManager != null)
            {
                problems += RequireReference(uiManager, "_screenContainer");
                problems += RequireReference(uiManager, "_modalContainer");
            }

            if (inputManager != null)
                problems += RequireReference(inputManager, "_inputActions");

            // The prefab is useless unless the boot scene actually points at it.
            var scene = EditorSceneManager.OpenScene(BootScenePath, OpenSceneMode.Single);
            var bootstrapper = UnityEngine.Object.FindFirstObjectByType<Bootstrapper>(
                FindObjectsInactive.Include);

            if (bootstrapper == null)
            {
                Debug.LogError($"[{Category}] No Bootstrapper in {BootScenePath}");
                problems++;
            }
            else
            {
                var assigned = new SerializedObject(bootstrapper)
                    .FindProperty("_coreSystemsPrefab")?.objectReferenceValue;

                if (assigned != prefab)
                {
                    Debug.LogError($"[{Category}] {BootScenePath}: Bootstrapper._coreSystemsPrefab " +
                                   $"is '{(assigned == null ? "null" : assigned.name)}', expected " +
                                   $"'{prefab.name}'");
                    problems++;
                }
            }

            if (problems > 0)
            {
                Fail($"{problems} problem(s) with the core systems prefab.");
                return;
            }

            Succeed("Core systems prefab is complete and wired into BootScene.");
        }

        /// <summary>Log an error and return 1 when the prefab is missing a system component.</summary>
        /// <typeparam name="T">The system component type the prefab must carry.</typeparam>
        /// <param name="prefab">The core systems prefab root.</param>
        /// <returns>0 when present, 1 when missing.</returns>
        private static int RequireComponent<T>(GameObject prefab) where T : MonoBehaviour
        {
            if (prefab.GetComponentInChildren<T>(true) != null)
                return 0;

            Debug.LogError($"[{Category}] Core systems prefab has no {typeof(T).Name}");
            return 1;
        }

        /// <summary>Log an error and return 1 when a serialized reference is missing or unassigned.</summary>
        /// <param name="target">Component to inspect.</param>
        /// <param name="fieldName">Backing field name, including its underscore prefix.</param>
        /// <returns>0 when assigned, 1 otherwise.</returns>
        private static int RequireReference(UnityEngine.Object target, string fieldName)
        {
            var property = new SerializedObject(target).FindProperty(fieldName);

            if (property == null)
            {
                Debug.LogError($"[{Category}] {target.GetType().Name} has no field '{fieldName}'");
                return 1;
            }

            if (property.objectReferenceValue == null)
            {
                Debug.LogError($"[{Category}] {target.GetType().Name}.{fieldName} is unassigned");
                return 1;
            }

            return 0;
        }

        // ─── Core Systems Prefab Construction ───────────────────────

        /// <summary>
        /// Construct the full core systems hierarchy in the open scene, ready to be saved as a
        /// prefab. Child order mirrors the bootstrapper's initialization order so the prefab
        /// reads like the documented sequence.
        /// </summary>
        /// <returns>The scratch root GameObject. The caller owns it and must destroy it.</returns>
        private static GameObject BuildCoreSystemsHierarchy()
        {
            var root = new GameObject("SkylotusCoreSystems");

            // ─── Systems with no serialized references ──────────────
            var audioManager = NewChild(root.transform, "AudioManager").AddComponent<AudioManager>();
            NewChild(root.transform, "ObjectPool").AddComponent<ObjectPool>();
            var sceneManager = NewChild(root.transform, "SceneManager").AddComponent<SkylotusSceneManager>();
            NewChild(root.transform, "GameState").AddComponent<GameStateMachine>();
            NewChild(root.transform, "TimeManager").AddComponent<TimeManager>();
            var inputManager = NewChild(root.transform, "InputManager").AddComponent<InputManager>();
            var uiManager = NewChild(root.transform, "UIManager").AddComponent<UIManager>();
            NewChild(root.transform, "DialogueSystem").AddComponent<DialogueSystem>();
            NewChild(root.transform, "NotificationSystem").AddComponent<NotificationSystem>();
            NewChild(root.transform, "DebugConsole").AddComponent<DebugConsole>();

            // ─── Persistent UI canvas: screen and modal containers ──
            var uiCanvas = NewOverlayCanvas(root.transform, "UI Root", sortingOrder: 100);
            var screenContainer = NewStretchedChild(uiCanvas.transform, "Screens");
            var modalContainer = NewStretchedChild(uiCanvas.transform, "Modals");

            // ─── Loading screen overlay ─────────────────────────────
            var loadingCanvas = NewOverlayCanvas(root.transform, "Loading Screen", sortingOrder: 200);
            var loadingGroup = loadingCanvas.gameObject.AddComponent<CanvasGroup>();
            loadingGroup.alpha = 0f;

            var backdrop = NewStretchedChild(loadingCanvas.transform, "Backdrop");
            var backdropImage = backdrop.gameObject.AddComponent<Image>();
            backdropImage.color = new Color(0.04f, 0.04f, 0.06f, 1f);

            var progressBar = BuildProgressBar(loadingCanvas.transform);

            // Authored inactive so nothing flashes on screen during boot. SkylotusSceneManager.Awake
            // deactivates it too, but only after one Awake's worth of frames.
            loadingCanvas.gameObject.SetActive(false);

            // ─── Wire the serialized references ─────────────────────
            SetObjectReference(sceneManager, "_loadingScreen", loadingGroup);
            SetObjectReference(sceneManager, "_progressBar", progressBar);
            SetObjectReference(uiManager, "_screenContainer", screenContainer);
            SetObjectReference(uiManager, "_modalContainer", modalContainer);

            var inputActions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputActionsPath);
            if (inputActions == null)
                throw new InvalidOperationException($"InputActionAsset not found at {InputActionsPath}");

            SetObjectReference(inputManager, "_inputActions", inputActions);

            // The mixer is generated by GenerateAudioMixer, which may not have run yet on a fresh
            // clone — so a missing asset is a warning, not a failure. Wiring it here rather than by
            // hand is deliberate: this method rebuilds the hierarchy from scratch on every run, so a
            // manual assignment in the Inspector would be silently discarded the next time anyone
            // regenerates the prefab. Without this, AudioManager falls back to Resources.Load.
            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(AudioMixerPath);
            if (mixer != null)
                SetObjectReference(audioManager, "_mixer", mixer);
            else
                Debug.LogWarning($"[{Category}] AudioMixer not found at {AudioMixerPath} — " +
                                 "AudioManager will fall back to Resources.Load at runtime. " +
                                 "Run SkylotusCI.GenerateAudioMixer, then regenerate this prefab.");

            return root;
        }

        /// <summary>
        /// Build a non-interactive 0–1 progress bar Slider anchored near the bottom of its parent.
        /// </summary>
        /// <param name="parent">Transform the bar is parented to.</param>
        /// <returns>The configured Slider.</returns>
        private static Slider BuildProgressBar(Transform parent)
        {
            var barGo = new GameObject("Progress Bar", typeof(RectTransform));
            barGo.transform.SetParent(parent, worldPositionStays: false);

            var rect = (RectTransform)barGo.transform;
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 120f);
            rect.sizeDelta = new Vector2(760f, 12f);

            var background = NewStretchedChild(rect, "Background");
            var backgroundImage = background.gameObject.AddComponent<Image>();
            backgroundImage.color = new Color(1f, 1f, 1f, 0.15f);

            var fillArea = NewStretchedChild(rect, "Fill Area");
            var fill = NewStretchedChild(fillArea, "Fill");
            var fillImage = fill.gameObject.AddComponent<Image>();
            fillImage.color = new Color(0.87f, 0.87f, 0.92f, 1f);

            var slider = barGo.AddComponent<Slider>();
            slider.fillRect = fill;
            slider.targetGraphic = fillImage;
            slider.transition = Selectable.Transition.None;
            slider.navigation = new Navigation { mode = Navigation.Mode.None };
            slider.direction = Slider.Direction.LeftToRight;
            slider.wholeNumbers = false;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.SetValueWithoutNotify(0f);
            slider.interactable = false;

            return slider;
        }

        /// <summary>Create an empty child GameObject with a plain Transform.</summary>
        /// <param name="parent">Transform to parent the child to.</param>
        /// <param name="name">Name for the child GameObject.</param>
        /// <returns>The created child GameObject.</returns>
        private static GameObject NewChild(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            return go;
        }

        /// <summary>
        /// Create a screen-space-overlay Canvas that scales against
        /// <see cref="ReferenceResolution"/>, with a raycaster so it can block input.
        /// </summary>
        /// <param name="parent">Transform to parent the canvas to.</param>
        /// <param name="name">Name for the canvas GameObject.</param>
        /// <param name="sortingOrder">Draw order relative to other overlay canvases.</param>
        /// <returns>The created Canvas.</returns>
        private static Canvas NewOverlayCanvas(Transform parent, string name, int sortingOrder)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, worldPositionStays: false);

            // Note: the saved prefab records a zero scale and size for this RectTransform. That
            // is expected and harmless — a ScreenSpaceOverlay Canvas drives its own transform
            // from the screen dimensions every frame, and batchmode has no screen to lay out
            // against. Writing an identity scale here does not survive the save.
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            go.AddComponent<GraphicRaycaster>();

            return canvas;
        }

        /// <summary>Create a child RectTransform stretched to fill its parent.</summary>
        /// <param name="parent">Transform to parent the child to.</param>
        /// <param name="name">Name for the child GameObject.</param>
        /// <returns>The created RectTransform.</returns>
        private static RectTransform NewStretchedChild(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, worldPositionStays: false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            return rect;
        }

        /// <summary>
        /// Assign an object reference to a private <c>[SerializeField]</c> through
        /// <see cref="SerializedObject"/> — the supported way to author serialized state that has
        /// no public setter.
        /// </summary>
        /// <param name="target">Component whose serialized field is being set.</param>
        /// <param name="fieldName">Exact backing field name, including its underscore prefix.</param>
        /// <param name="value">Reference to assign.</param>
        private static void SetObjectReference(UnityEngine.Object target, string fieldName,
            UnityEngine.Object value)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(fieldName);

            if (property == null)
            {
                throw new InvalidOperationException(
                    $"{target.GetType().Name} has no serialized field '{fieldName}'. " +
                    "The generator and the runtime field name have drifted apart.");
            }

            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Open the boot scene, point its <c>Bootstrapper</c> at the generated prefab, and save.
        /// The scene is only marked dirty and re-saved when the reference actually changes, so a
        /// no-op re-run leaves the scene file byte-identical.
        /// </summary>
        /// <param name="prefab">The generated core systems prefab asset.</param>
        /// <returns>True on success; false after <see cref="Fail"/> has already been called.</returns>
        private static bool AssignPrefabToBootScene(GameObject prefab)
        {
            var scene = EditorSceneManager.OpenScene(BootScenePath, OpenSceneMode.Single);

            if (!scene.IsValid())
            {
                Fail($"Could not open scene: {BootScenePath}");
                return false;
            }

            var bootstrapper = UnityEngine.Object.FindFirstObjectByType<Bootstrapper>(
                FindObjectsInactive.Include);

            if (bootstrapper == null)
            {
                Fail($"No Bootstrapper component found in {BootScenePath}");
                return false;
            }

            var serialized = new SerializedObject(bootstrapper);
            var property = serialized.FindProperty("_coreSystemsPrefab");

            if (property == null)
            {
                Fail("Bootstrapper has no serialized field '_coreSystemsPrefab'.");
                return false;
            }

            if (property.objectReferenceValue == prefab)
            {
                Debug.Log($"[{Category}] {BootScenePath} already references the prefab — not re-saved.");
                return true;
            }

            property.objectReferenceValue = prefab;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(scene);

            if (!EditorSceneManager.SaveScene(scene))
            {
                Fail($"Failed to save {BootScenePath}");
                return false;
            }

            Debug.Log($"[{Category}] Assigned prefab to Bootstrapper in {BootScenePath}");
            return true;
        }

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>
        /// Log a success marker and exit cleanly. Safe to call outside batchmode,
        /// where it logs without terminating the editor.
        /// </summary>
        /// <param name="message">Human-readable summary of what succeeded.</param>
        public static void Succeed(string message)
        {
            Debug.Log($"[{Category}] PASS: {message}");
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }

        /// <summary>
        /// Log an error marker and exit non-zero so the calling script fails.
        /// Safe to call outside batchmode, where it logs without terminating.
        /// </summary>
        /// <param name="message">Human-readable description of the failure.</param>
        public static void Fail(string message)
        {
            Debug.LogError($"[{Category}] FAIL: {message}");
            if (Application.isBatchMode)
                EditorApplication.Exit(1);
        }
    }
}
