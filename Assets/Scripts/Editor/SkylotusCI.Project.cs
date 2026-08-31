using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Skylotus.Editor
{
    // WP-11 — project settings configuration.
    //
    // This partial owns everything that writes ProjectSettings/ProjectSettings.asset and
    // ProjectSettings/TagManager.asset. Both are Unity-owned YAML and are never edited as
    // text: every value here goes through PlayerSettings or a SerializedObject over the
    // TagManager asset, exactly as CORE_FIXES.md rule 4 requires.
    //
    // The type-level documentation lives on the primary part in SkylotusCI.cs.
    public static partial class SkylotusCI
    {
        // ─── Per-Project Identity: Environment Variables ────────────

        /// <summary>Environment variable overriding the publisher name.</summary>
        private const string ProjectCompanyVariable = "SKYLOTUS_COMPANY_NAME";

        /// <summary>Environment variable overriding the product (game) name.</summary>
        private const string ProjectProductVariable = "SKYLOTUS_PRODUCT_NAME";

        /// <summary>Environment variable overriding the reverse-DNS bundle identifier.</summary>
        private const string ProjectBundleIdVariable = "SKYLOTUS_BUNDLE_ID";

        /// <summary>Environment variable overriding the human-facing version string.</summary>
        private const string ProjectBundleVersionVariable = "SKYLOTUS_BUNDLE_VERSION";

        /// <summary>
        /// Environment variable overriding the standalone scripting backend. Accepts the
        /// <see cref="ScriptingImplementation"/> member names, e.g. <c>IL2CPP</c> or <c>Mono2x</c>.
        /// </summary>
        private const string ProjectScriptingBackendVariable = "SKYLOTUS_SCRIPTING_BACKEND";

        /// <summary>Environment variable overriding the path of the JSON config file.</summary>
        private const string ProjectConfigPathVariable = "SKYLOTUS_PROJECT_CONFIG";

        // ─── Per-Project Identity: Defaults and Paths ───────────────

        /// <summary>
        /// Default config file name, resolved relative to the project root (the folder that
        /// contains <c>Assets/</c>). The file is optional: it is where a fresh clone records
        /// its own identity.
        /// </summary>
        private const string ProjectConfigFileName = "SkylotusProject.json";

        /// <summary>TagManager settings asset, addressed through the AssetDatabase.</summary>
        private const string TagManagerAssetPath = "ProjectSettings/TagManager.asset";

        /// <summary>
        /// Placeholder company name. <b>Change this per project</b> via
        /// <see cref="ProjectCompanyVariable"/> or the JSON config.
        /// </summary>
        private const string ProjectDefaultCompanyName = "Skylotus Studios";

        /// <summary>
        /// Placeholder product name. <b>Change this per project</b> via
        /// <see cref="ProjectProductVariable"/> or the JSON config.
        /// </summary>
        private const string ProjectDefaultProductName = "CoreProject";

        /// <summary>
        /// Placeholder version. Semantic-version shaped, so the first real release is an edit
        /// rather than a format change. Unity's template default of <c>1.0</c> claims a shipped
        /// product that does not exist yet.
        /// </summary>
        private const string ProjectDefaultBundleVersion = "0.1.0";

        /// <summary>Lowest layer index a project may name; 0-7 are engine-reserved.</summary>
        private const int ProjectFirstUserLayer = 8;

        /// <summary>Layers are a fixed 32-entry array in <c>TagManager.asset</c>.</summary>
        private const int ProjectLayerCount = 32;

        /// <summary>
        /// Starter 2D sorting-layer stack, back to front. Appended after Unity's mandatory
        /// <c>Default</c> layer, so anything still on <c>Default</c> keeps rendering behind
        /// everything else and no already-authored sprite changes depth when this runs.
        /// </summary>
        private static readonly string[] ProjectDefaultSortingLayers =
        {
            "Background",
            "Ground",
            "Entities",
            "Foreground",
            "UI",
            "Overlay"
        };

        /// <summary>
        /// Platforms that receive the resolved bundle identifier. Standalone is what this
        /// template builds today; Android and iOS are included because an empty identifier is a
        /// hard build failure there and costs one line each to prevent.
        /// </summary>
        private static readonly NamedBuildTarget[] ProjectIdentifierTargets =
        {
            NamedBuildTarget.Standalone,
            NamedBuildTarget.Android,
            NamedBuildTarget.iOS
        };

        // ─── Entry Points ───────────────────────────────────────────

        /// <summary>
        /// Configure a fresh clone of this template: bundle identity, release scripting
        /// backend, managed stripping, and the 2D sorting-layer stack.
        ///
        /// Run it as the one post-clone command:
        /// <c>Tools\unity-verify.ps1 -Mode method -Method
        /// Skylotus.Editor.SkylotusCI.ConfigureProject</c>
        ///
        /// <b>Where the per-project values come from.</b> Nothing about a specific game is
        /// hard-coded here. Each value is resolved in this order, first hit wins:
        ///
        /// <list type="number">
        /// <item>an environment variable — <c>SKYLOTUS_COMPANY_NAME</c>,
        ///       <c>SKYLOTUS_PRODUCT_NAME</c>, <c>SKYLOTUS_BUNDLE_ID</c>,
        ///       <c>SKYLOTUS_BUNDLE_VERSION</c>, <c>SKYLOTUS_SCRIPTING_BACKEND</c>;</item>
        /// <item>a JSON file at <c>&lt;project root&gt;/SkylotusProject.json</c> (override the
        ///       path with <c>SKYLOTUS_PROJECT_CONFIG</c>) — see
        ///       <see cref="ProjectIdentityConfig"/> for the schema;</item>
        /// <item>a clearly-marked template default, which the run reports as
        ///       <c>NEEDS A REAL VALUE</c> so nobody ships it by accident.</item>
        /// </list>
        ///
        /// Idempotent: every write is compared first, so a second run reports no changes and
        /// leaves both settings files byte-identical.
        /// </summary>
        public static void ConfigureProject()
        {
            try
            {
                var changes = new List<string>();
                var placeholders = new List<string>();

                var config = LoadProjectConfig(out var configSource);
                Debug.Log($"[{Category}] Project config source: {configSource}");

                var company = ResolveProjectValue(
                    ProjectCompanyVariable, config?.companyName, ProjectDefaultCompanyName,
                    "companyName", placeholders);

                var product = ResolveProjectValue(
                    ProjectProductVariable, config?.productName, ProjectDefaultProductName,
                    "productName", placeholders);

                var version = ResolveProjectValue(
                    ProjectBundleVersionVariable, config?.bundleVersion, ProjectDefaultBundleVersion,
                    "bundleVersion", placeholders);

                // Derived rather than invented: a reverse-DNS id built from the two names above
                // is always well-formed and never claims a studio domain the template does not own.
                var derivedIdentifier = DeriveProjectIdentifier(company, product);

                var identifier = ResolveProjectValue(
                    ProjectBundleIdVariable, config?.applicationIdentifier, derivedIdentifier,
                    "applicationIdentifier", placeholders);

                if (!IsValidProjectIdentifier(identifier))
                {
                    Fail($"'{identifier}' is not a valid application identifier. Expected " +
                         "reverse-DNS form, e.g. com.yourstudio.yourgame — two or more segments, " +
                         "each starting with a letter and containing only letters, digits or '_'.");
                    return;
                }

                if (!TryResolveProjectBackend(config, out var backend))
                    return;

                ApplyProjectIdentity(company, product, version, identifier, changes);
                ApplyProjectBuildSettings(backend, changes);

                if (!ApplyProjectIcons(config, changes))
                    return;

                if (!ConfigureProjectTagManager(config, changes))
                    return;

                AssetDatabase.SaveAssets();

                ReportProjectChanges(changes);
                ReportProjectPlaceholders(placeholders, company, product, identifier);
                WarnIfBackendUnbuildable(backend);

                Succeed(changes.Count == 0
                    ? "Project settings already configured — no changes."
                    : $"Project configured ({changes.Count} change(s)).");
            }
            catch (Exception e)
            {
                Fail($"ConfigureProject threw: {e}");
            }
        }

        /// <summary>
        /// Editor menu wrapper around <see cref="ConfigureProject"/>, for humans who would
        /// rather not open a terminal. Runs the same code against the same environment
        /// variables and the same JSON file; the only difference is that <see cref="Succeed"/>
        /// and <see cref="Fail"/> log instead of exiting the process.
        /// </summary>
        [MenuItem("Skylotus/Configure New Project")]
        public static void ConfigureProjectMenuItem()
        {
            ConfigureProject();
        }

        // ─── Configuration Sources ──────────────────────────────────

        /// <summary>
        /// Optional per-clone configuration, deserialized from JSON. Every field is optional; a
        /// null or blank entry falls through to the environment variable, then to the template
        /// default.
        ///
        /// Field names are the JSON keys, so they are plain camelCase rather than the
        /// <c>_camelCase</c> this codebase uses for private state.
        ///
        /// <code>
        /// {
        ///   "companyName": "Your Studio",
        ///   "productName": "Your Game",
        ///   "applicationIdentifier": "com.yourstudio.yourgame",
        ///   "bundleVersion": "0.1.0",
        ///   "scriptingBackend": "IL2CPP",
        ///   "iconPath": "Assets/Art/Icons/AppIcon.png",
        ///   "sortingLayers": ["Background", "Ground", "Entities", "Foreground", "UI", "Overlay"],
        ///   "tags": ["Player", "Enemy"],
        ///   "layers": [{ "index": 8, "name": "Interactable" }]
        /// }
        /// </code>
        /// </summary>
        [Serializable]
        private sealed class ProjectIdentityConfig
        {
            /// <summary>Publisher name written to <c>PlayerSettings.companyName</c>.</summary>
            public string companyName;

            /// <summary>Product name written to <c>PlayerSettings.productName</c>.</summary>
            public string productName;

            /// <summary>Reverse-DNS bundle identifier, e.g. <c>com.yourstudio.yourgame</c>.</summary>
            public string applicationIdentifier;

            /// <summary>Human-facing version string, e.g. <c>0.1.0</c>.</summary>
            public string bundleVersion;

            /// <summary>Standalone scripting backend name: <c>IL2CPP</c> or <c>Mono2x</c>.</summary>
            public string scriptingBackend;

            /// <summary>Asset path of a square Texture2D to use as the application icon.</summary>
            public string iconPath;

            /// <summary>Sorting layers, back to front. Null or empty keeps the starter set.</summary>
            public string[] sortingLayers;

            /// <summary>Tags to create. Null or empty adds none — tags are a game decision.</summary>
            public string[] tags;

            /// <summary>Named user layers. Null or empty adds none.</summary>
            public ProjectLayerAssignment[] layers;
        }

        /// <summary>
        /// One entry in <see cref="ProjectIdentityConfig.layers"/>. Layers are a fixed 32-slot
        /// array, so a name is meaningless without the slot it occupies.
        /// </summary>
        [Serializable]
        private sealed class ProjectLayerAssignment
        {
            /// <summary>Layer index, 8-31. Indices 0-7 are reserved by the engine.</summary>
            public int index;

            /// <summary>Layer name.</summary>
            public string name;
        }

        /// <summary>
        /// Load the optional JSON config. A missing file is normal and not an error: it means
        /// the clone has not been personalized yet and every value falls back.
        /// </summary>
        /// <param name="source">Human-readable description of where the values came from.</param>
        /// <returns>The parsed config, or null when there is no file to read.</returns>
        private static ProjectIdentityConfig LoadProjectConfig(out string source)
        {
            var path = Environment.GetEnvironmentVariable(ProjectConfigPathVariable);

            if (string.IsNullOrWhiteSpace(path))
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                path = Path.Combine(projectRoot ?? ".", ProjectConfigFileName);
            }

            if (!File.Exists(path))
            {
                source = $"none ({path} not present) — environment variables and defaults only";
                return null;
            }

            var config = JsonUtility.FromJson<ProjectIdentityConfig>(File.ReadAllText(path));

            if (config == null)
                throw new InvalidOperationException($"{path} did not parse as a JSON object.");

            source = path;
            return config;
        }

        /// <summary>
        /// Resolve one value: environment variable, then JSON, then the template default.
        /// Records the field name in <paramref name="placeholders"/> when the default wins, so
        /// the run can tell the operator exactly what is still unset.
        /// </summary>
        /// <param name="variableName">Environment variable consulted first.</param>
        /// <param name="configValue">Value from the JSON config, possibly null.</param>
        /// <param name="fallback">Template default used when nothing else supplies a value.</param>
        /// <param name="label">Field name used in log output.</param>
        /// <param name="placeholders">Accumulator for fields that fell through to the default.</param>
        /// <returns>The resolved value; never null or blank.</returns>
        private static string ResolveProjectValue(string variableName, string configValue,
            string fallback, string label, List<string> placeholders)
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(variableName);

            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                Debug.Log($"[{Category}] {label}: '{fromEnvironment.Trim()}' (from ${variableName})");
                return fromEnvironment.Trim();
            }

            if (!string.IsNullOrWhiteSpace(configValue))
            {
                Debug.Log($"[{Category}] {label}: '{configValue.Trim()}' (from {ProjectConfigFileName})");
                return configValue.Trim();
            }

            placeholders.Add(label);
            Debug.Log($"[{Category}] {label}: '{fallback}' (template default)");
            return fallback;
        }

        /// <summary>
        /// Resolve the standalone scripting backend. Defaults to IL2CPP per WP-11 — release
        /// builds want it — but stays overridable, because IL2CPP needs a build-support module
        /// that a given machine may not have installed.
        /// </summary>
        /// <param name="config">Parsed JSON config, possibly null.</param>
        /// <param name="backend">The resolved backend.</param>
        /// <returns>True on success; false after <see cref="Fail"/> has already been called.</returns>
        private static bool TryResolveProjectBackend(ProjectIdentityConfig config,
            out ScriptingImplementation backend)
        {
            backend = ScriptingImplementation.IL2CPP;

            var raw = Environment.GetEnvironmentVariable(ProjectScriptingBackendVariable);
            var origin = $"${ProjectScriptingBackendVariable}";

            if (string.IsNullOrWhiteSpace(raw))
            {
                raw = config?.scriptingBackend;
                origin = ProjectConfigFileName;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                Debug.Log($"[{Category}] scriptingBackend: 'IL2CPP' (template default)");
                return true;
            }

            if (!Enum.TryParse(raw.Trim(), ignoreCase: true, out backend))
            {
                Fail($"'{raw.Trim()}' ({origin}) is not a scripting backend. Valid values: " +
                     string.Join(", ", Enum.GetNames(typeof(ScriptingImplementation))));
                return false;
            }

            Debug.Log($"[{Category}] scriptingBackend: '{backend}' (from {origin})");
            return true;
        }

        // ─── Identifier Construction and Validation ─────────────────

        /// <summary>
        /// Build a well-formed reverse-DNS identifier from the company and product names. Used
        /// only when nothing else supplies one; it produces a valid, obviously-derived id
        /// rather than inventing a studio domain.
        /// </summary>
        /// <param name="company">Resolved company name.</param>
        /// <param name="product">Resolved product name.</param>
        /// <returns>An identifier of the form <c>com.company.product</c>.</returns>
        private static string DeriveProjectIdentifier(string company, string product)
        {
            return $"com.{SanitizeProjectIdentifierSegment(company)}." +
                   $"{SanitizeProjectIdentifierSegment(product)}";
        }

        /// <summary>
        /// Reduce arbitrary text to one lowercase identifier segment: letters and digits only,
        /// forced to start with a letter.
        /// </summary>
        /// <param name="value">Text to reduce.</param>
        /// <returns>A non-empty segment safe for a bundle identifier.</returns>
        private static string SanitizeProjectIdentifierSegment(string value)
        {
            var builder = new StringBuilder();

            foreach (var c in value ?? string.Empty)
            {
                if (char.IsLetterOrDigit(c))
                    builder.Append(char.ToLowerInvariant(c));
            }

            if (builder.Length == 0 || !char.IsLetter(builder[0]))
                builder.Insert(0, "app");

            return builder.ToString();
        }

        /// <summary>
        /// Check that an identifier is reverse-DNS shaped: two or more dot-separated segments,
        /// each beginning with a letter and made of letters, digits or underscores. This is the
        /// intersection of what Android, iOS and Windows accept.
        /// </summary>
        /// <param name="identifier">Candidate identifier.</param>
        /// <returns>True when the identifier is usable on every platform.</returns>
        private static bool IsValidProjectIdentifier(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
                return false;

            var segments = identifier.Split('.');

            if (segments.Length < 2)
                return false;

            foreach (var segment in segments)
            {
                if (segment.Length == 0 || !char.IsLetter(segment[0]))
                    return false;

                if (segment.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
                    return false;
            }

            return true;
        }

        // ─── PlayerSettings ─────────────────────────────────────────

        /// <summary>
        /// Write the resolved identity to <c>PlayerSettings</c>. The identifier is set per
        /// named build target rather than through the <c>applicationIdentifier</c> property,
        /// which only touches whichever platform happens to be active.
        /// </summary>
        /// <param name="company">Resolved company name.</param>
        /// <param name="product">Resolved product name.</param>
        /// <param name="version">Resolved version string.</param>
        /// <param name="identifier">Validated reverse-DNS bundle identifier.</param>
        /// <param name="changes">Accumulator for the change log.</param>
        private static void ApplyProjectIdentity(string company, string product, string version,
            string identifier, List<string> changes)
        {
            if (PlayerSettings.companyName != company)
            {
                changes.Add($"companyName: '{PlayerSettings.companyName}' -> '{company}'");
                PlayerSettings.companyName = company;
            }

            if (PlayerSettings.productName != product)
            {
                changes.Add($"productName: '{PlayerSettings.productName}' -> '{product}'");
                PlayerSettings.productName = product;
            }

            if (PlayerSettings.bundleVersion != version)
            {
                changes.Add($"bundleVersion: '{PlayerSettings.bundleVersion}' -> '{version}'");
                PlayerSettings.bundleVersion = version;
            }

            foreach (var target in ProjectIdentifierTargets)
            {
                var current = PlayerSettings.GetApplicationIdentifier(target);

                if (current == identifier)
                    continue;

                changes.Add($"applicationIdentifier[{target.TargetName}]: " +
                            $"'{current}' -> '{identifier}'");
                PlayerSettings.SetApplicationIdentifier(target, identifier);
            }
        }

        /// <summary>
        /// Configure the standalone build pipeline: scripting backend, managed stripping, and
        /// the IL2CPP compiler configuration. Mobile backends are left at their platform
        /// defaults — Android already builds IL2CPP and iOS has no other option.
        /// </summary>
        /// <param name="backend">Resolved standalone scripting backend.</param>
        /// <param name="changes">Accumulator for the change log.</param>
        private static void ApplyProjectBuildSettings(ScriptingImplementation backend,
            List<string> changes)
        {
            var standalone = NamedBuildTarget.Standalone;

            var currentBackend = PlayerSettings.GetScriptingBackend(standalone);

            if (currentBackend != backend)
            {
                changes.Add($"scriptingBackend[Standalone]: {currentBackend} -> {backend}");
                PlayerSettings.SetScriptingBackend(standalone, backend);
            }

            // Low is where Unity itself recommends starting: it strips unused assemblies without
            // the reflection surprises Medium and High introduce. Anything more aggressive needs
            // link.xml work that belongs to a real project, not a template.
            var currentStripping = PlayerSettings.GetManagedStrippingLevel(standalone);

            if (currentStripping != ManagedStrippingLevel.Low)
            {
                changes.Add($"managedStrippingLevel[Standalone]: {currentStripping} -> Low");
                PlayerSettings.SetManagedStrippingLevel(standalone, ManagedStrippingLevel.Low);
            }

            // Release is already the implicit default; writing it makes the intent explicit in
            // the YAML, so a later change to Debug shows up in a diff instead of hiding in a
            // default that moved.
            var currentIl2Cpp = PlayerSettings.GetIl2CppCompilerConfiguration(standalone);

            if (currentIl2Cpp != Il2CppCompilerConfiguration.Release)
            {
                changes.Add($"il2cppCompilerConfiguration[Standalone]: {currentIl2Cpp} -> Release");
                PlayerSettings.SetIl2CppCompilerConfiguration(
                    standalone, Il2CppCompilerConfiguration.Release);
            }
        }

        /// <summary>
        /// Assign the application icon when the config names one. No icon is generated: a
        /// placeholder shipped by a template is worse than none, because it looks intentional
        /// and survives to release.
        /// </summary>
        /// <param name="config">Parsed JSON config, possibly null.</param>
        /// <param name="changes">Accumulator for the change log.</param>
        /// <returns>True on success; false after <see cref="Fail"/> has already been called.</returns>
        private static bool ApplyProjectIcons(ProjectIdentityConfig config, List<string> changes)
        {
            var iconPath = config?.iconPath;

            if (string.IsNullOrWhiteSpace(iconPath))
            {
                var existing = PlayerSettings.GetIcons(NamedBuildTarget.Standalone, IconKind.Any);

                if (existing == null || existing.Length == 0 || existing.All(i => i == null))
                {
                    Debug.LogWarning(
                        $"[{Category}] No application icon set — builds will use Unity's default " +
                        $"logo. Add a square texture under Assets/, point \"iconPath\" in " +
                        $"{ProjectConfigFileName} at it, and re-run this method.");
                }

                return true;
            }

            var trimmedPath = iconPath.Trim();
            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(trimmedPath);

            if (icon == null)
            {
                Fail($"iconPath '{trimmedPath}' does not load as a Texture2D. " +
                     "Use a project-relative path starting with 'Assets/'.");
                return false;
            }

            var sizes = PlayerSettings.GetIconSizes(NamedBuildTarget.Standalone, IconKind.Any);

            if (sizes == null || sizes.Length == 0)
            {
                Debug.LogWarning($"[{Category}] Standalone reports no icon slots; icon not set.");
                return true;
            }

            var current = PlayerSettings.GetIcons(NamedBuildTarget.Standalone, IconKind.Any);

            if (current != null && current.Length == sizes.Length && current.All(i => i == icon))
                return true;

            PlayerSettings.SetIcons(
                NamedBuildTarget.Standalone,
                Enumerable.Repeat(icon, sizes.Length).ToArray(),
                IconKind.Any);

            changes.Add($"icons[Standalone]: {sizes.Length} size(s) <- {trimmedPath}");
            return true;
        }

        // ─── TagManager: Sorting Layers, Tags, Layers ───────────────

        /// <summary>
        /// Apply the sorting-layer stack, and any tags or named layers the config asks for, to
        /// <c>TagManager.asset</c> through a <see cref="SerializedObject"/>.
        ///
        /// Unity exposes no public API for sorting layers — the
        /// <c>InternalEditorUtility.AddSortingLayer</c> family is internal in Unity 6 — so the
        /// asset is edited through its serialized representation. That is the supported route
        /// and it still avoids hand-writing YAML.
        /// </summary>
        /// <param name="config">Parsed JSON config, possibly null.</param>
        /// <param name="changes">Accumulator for the change log.</param>
        /// <returns>True on success; false after <see cref="Fail"/> has already been called.</returns>
        private static bool ConfigureProjectTagManager(ProjectIdentityConfig config,
            List<string> changes)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(TagManagerAssetPath);

            if (assets == null || assets.Length == 0 || assets[0] == null)
            {
                Fail($"Could not load {TagManagerAssetPath}.");
                return false;
            }

            var tagManager = new SerializedObject(assets[0]);

            var sortingLayers = config?.sortingLayers != null && config.sortingLayers.Length > 0
                ? config.sortingLayers
                : ProjectDefaultSortingLayers;

            if (!AddProjectSortingLayers(tagManager, sortingLayers, changes))
                return false;

            if (!AddProjectTags(tagManager, config?.tags, changes))
                return false;

            if (!AddProjectLayers(tagManager, config?.layers, changes))
                return false;

            if (!tagManager.ApplyModifiedPropertiesWithoutUndo())
                return true;

            EditorUtility.SetDirty(assets[0]);
            AssetDatabase.SaveAssets();
            RefreshProjectSortingLayerCache();

            return true;
        }

        /// <summary>
        /// Append any missing sorting layers, preserving both the existing order and Unity's
        /// mandatory <c>Default</c> layer at index 0. Existing layers are never renamed or
        /// reordered: a sprite stores its layer by unique id, and shuffling the array would
        /// silently re-sort art that is already authored.
        /// </summary>
        /// <param name="tagManager">Serialized <c>TagManager.asset</c>.</param>
        /// <param name="desired">Layer names, back to front.</param>
        /// <param name="changes">Accumulator for the change log.</param>
        /// <returns>True on success; false after <see cref="Fail"/> has already been called.</returns>
        private static bool AddProjectSortingLayers(SerializedObject tagManager,
            IReadOnlyList<string> desired, List<string> changes)
        {
            var layers = tagManager.FindProperty("m_SortingLayers");

            if (layers == null)
            {
                Fail($"{TagManagerAssetPath} has no 'm_SortingLayers' property.");
                return false;
            }

            var existingNames = new HashSet<string>(StringComparer.Ordinal);
            var usedIds = new HashSet<int>();

            for (var i = 0; i < layers.arraySize; i++)
            {
                var element = layers.GetArrayElementAtIndex(i);
                existingNames.Add(element.FindPropertyRelative("name").stringValue);
                usedIds.Add(element.FindPropertyRelative("uniqueID").intValue);
            }

            foreach (var name in desired)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var trimmed = name.Trim();

                if (!existingNames.Add(trimmed))
                    continue;

                var index = layers.arraySize;
                layers.InsertArrayElementAtIndex(index);

                var element = layers.GetArrayElementAtIndex(index);
                var uniqueId = MakeProjectSortingLayerId(trimmed, usedIds);

                element.FindPropertyRelative("name").stringValue = trimmed;
                element.FindPropertyRelative("uniqueID").intValue = uniqueId;
                SetProjectSortingLayerUnlocked(element);

                usedIds.Add(uniqueId);
                changes.Add($"sortingLayer + '{trimmed}' (uniqueID {uniqueId})");
            }

            return true;
        }

        /// <summary>
        /// Clear the <c>locked</c> flag on a freshly inserted sorting layer. The field is a bool
        /// that serializes as an integer, and <see cref="SerializedProperty"/> has reported it
        /// as either across Unity versions, so both shapes are handled rather than guessed at.
        /// </summary>
        /// <param name="element">Array element for one sorting layer.</param>
        private static void SetProjectSortingLayerUnlocked(SerializedProperty element)
        {
            var locked = element.FindPropertyRelative("locked");

            if (locked == null)
                return;

            if (locked.propertyType == SerializedPropertyType.Boolean)
                locked.boolValue = false;
            else if (locked.propertyType == SerializedPropertyType.Integer)
                locked.intValue = 0;
        }

        /// <summary>
        /// Derive a stable, collision-free unique id for a sorting layer from its name.
        ///
        /// Unity's own editor assigns a random id here. A hash of the name is deterministic
        /// instead, so two clones of this template that run the generator independently produce
        /// identical <c>TagManager.asset</c> files and identical <c>m_SortingLayerID</c> values
        /// in every prefab — which keeps merges sane.
        /// </summary>
        /// <param name="name">Sorting layer name.</param>
        /// <param name="usedIds">Ids already taken, including <c>Default</c>'s zero.</param>
        /// <returns>A positive id not present in <paramref name="usedIds"/>.</returns>
        private static int MakeProjectSortingLayerId(string name, HashSet<int> usedIds)
        {
            // FNV-1a, 32-bit. Chosen over string.GetHashCode because the latter is randomized
            // per process in .NET Core and would write a different asset on every run.
            unchecked
            {
                const uint offsetBasis = 2166136261u;
                const uint prime = 16777619u;

                var hash = offsetBasis;

                foreach (var c in name)
                {
                    hash ^= c;
                    hash *= prime;
                }

                // The sign bit is masked off deliberately. Unity serializes uniqueID as an
                // unsigned field, and a negative value assigned through SerializedProperty is
                // written to the asset as 0 — silently colliding with Default and giving every
                // affected layer the same sort key. Verified against Unity 6000.3.8f1.
                var id = (int)(hash & 0x7FFFFFFF);

                // Zero belongs to Default; walk forward on the vanishingly rare collision,
                // wrapping rather than overflowing back into negative territory.
                while (id == 0 || usedIds.Contains(id))
                    id = id == int.MaxValue ? 1 : id + 1;

                return id;
            }
        }

        /// <summary>
        /// Add any tags the config asks for. Nothing is added by default: which tags a game
        /// needs is a gameplay decision, not a template one, and an unused tag is dead weight
        /// that still shows up in every inspector dropdown.
        /// </summary>
        /// <param name="tagManager">Serialized <c>TagManager.asset</c>.</param>
        /// <param name="desired">Tag names from the config, possibly null.</param>
        /// <param name="changes">Accumulator for the change log.</param>
        /// <returns>True on success; false after <see cref="Fail"/> has already been called.</returns>
        private static bool AddProjectTags(SerializedObject tagManager, string[] desired,
            List<string> changes)
        {
            if (desired == null || desired.Length == 0)
                return true;

            var tags = tagManager.FindProperty("tags");

            if (tags == null)
            {
                Fail($"{TagManagerAssetPath} has no 'tags' property.");
                return false;
            }

            var existing = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < tags.arraySize; i++)
                existing.Add(tags.GetArrayElementAtIndex(i).stringValue);

            foreach (var tag in desired)
            {
                if (string.IsNullOrWhiteSpace(tag))
                    continue;

                var trimmed = tag.Trim();

                if (!existing.Add(trimmed))
                    continue;

                var index = tags.arraySize;
                tags.InsertArrayElementAtIndex(index);
                tags.GetArrayElementAtIndex(index).stringValue = trimmed;

                changes.Add($"tag + '{trimmed}'");
            }

            return true;
        }

        /// <summary>
        /// Name user layers 8-31 from the config. Like tags, none are added by default —
        /// physics-layer allocation is a per-project decision and a wrong guess is worse than
        /// an empty slot.
        /// </summary>
        /// <param name="tagManager">Serialized <c>TagManager.asset</c>.</param>
        /// <param name="desired">Layer assignments from the config, possibly null.</param>
        /// <param name="changes">Accumulator for the change log.</param>
        /// <returns>True on success; false after <see cref="Fail"/> has already been called.</returns>
        private static bool AddProjectLayers(SerializedObject tagManager,
            ProjectLayerAssignment[] desired, List<string> changes)
        {
            if (desired == null || desired.Length == 0)
                return true;

            var layers = tagManager.FindProperty("layers");

            if (layers == null)
            {
                Fail($"{TagManagerAssetPath} has no 'layers' property.");
                return false;
            }

            foreach (var assignment in desired)
            {
                if (assignment == null || string.IsNullOrWhiteSpace(assignment.name))
                    continue;

                if (assignment.index < ProjectFirstUserLayer || assignment.index >= ProjectLayerCount)
                {
                    Fail($"Layer index {assignment.index} ('{assignment.name}') is out of range. " +
                         $"User layers are {ProjectFirstUserLayer}-{ProjectLayerCount - 1}; " +
                         "0-7 are reserved by the engine.");
                    return false;
                }

                var element = layers.GetArrayElementAtIndex(assignment.index);
                var trimmed = assignment.name.Trim();

                if (element.stringValue == trimmed)
                    continue;

                if (!string.IsNullOrEmpty(element.stringValue))
                {
                    Fail($"Layer {assignment.index} is already named '{element.stringValue}'. " +
                         $"Refusing to rename it to '{trimmed}' — every GameObject on that layer " +
                         "would silently change meaning. Pick a free index, or rename by hand.");
                    return false;
                }

                element.stringValue = trimmed;
                changes.Add($"layer[{assignment.index}] + '{trimmed}'");
            }

            return true;
        }

        /// <summary>
        /// Ask the editor to rebuild its in-memory sorting-layer table after the asset changed.
        ///
        /// Best-effort by design: <c>InternalEditorUtility.UpdateSortingLayersOrder</c> is
        /// internal, so it is reached reflectively and skipped if a future Unity version moves
        /// it. Nothing depends on the call — the asset on disk is already correct and the table
        /// rebuilds on the next domain reload. It only matters for the menu-item path, where the
        /// editor stays open and would otherwise show stale layer names.
        /// </summary>
        private static void RefreshProjectSortingLayerCache()
        {
            try
            {
                var type = typeof(EditorApplication).Assembly
                    .GetType("UnityEditorInternal.InternalEditorUtility");

                type?.GetMethod("UpdateSortingLayersOrder",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.Invoke(null, null);
            }
            catch (Exception e)
            {
                Debug.Log($"[{Category}] Sorting-layer cache not refreshed ({e.GetType().Name}). " +
                          "Harmless — the asset is written; reopen the editor to see the layers.");
            }
        }

        // ─── Reporting ──────────────────────────────────────────────

        /// <summary>Log every setting this run modified, or state that nothing changed.</summary>
        /// <param name="changes">Change descriptions collected during the run.</param>
        private static void ReportProjectChanges(List<string> changes)
        {
            if (changes.Count == 0)
            {
                Debug.Log($"[{Category}] No settings changed — already configured.");
                return;
            }

            Debug.Log($"[{Category}] {changes.Count} setting(s) changed:");

            foreach (var change in changes)
                Debug.Log($"[{Category}]   {change}");
        }

        /// <summary>
        /// Name every value still sitting on a template default, and say exactly how to change
        /// it. This is the post-clone checklist, emitted where it cannot be missed: in the log
        /// of the first command a new project runs.
        /// </summary>
        /// <param name="placeholders">Field names that fell through to a default.</param>
        /// <param name="company">Resolved company name, for the worked example.</param>
        /// <param name="product">Resolved product name, for the worked example.</param>
        /// <param name="identifier">Resolved identifier, for the worked example.</param>
        private static void ReportProjectPlaceholders(List<string> placeholders, string company,
            string product, string identifier)
        {
            if (placeholders.Count == 0)
            {
                Debug.Log($"[{Category}] Every per-project value came from the environment or " +
                          $"{ProjectConfigFileName}. Nothing left to personalize.");
                return;
            }

            var builder = new StringBuilder();

            builder.AppendLine($"[{Category}] NEEDS A REAL VALUE — still on template defaults: " +
                               string.Join(", ", placeholders));
            builder.AppendLine($"[{Category}]   companyName           = {company}");
            builder.AppendLine($"[{Category}]   productName           = {product}");
            builder.AppendLine($"[{Category}]   applicationIdentifier = {identifier}");
            builder.AppendLine($"[{Category}] Fix by writing {ProjectConfigFileName} beside " +
                               "Assets/ and re-running this method:");
            builder.AppendLine($"[{Category}]   {{ \"companyName\": \"Your Studio\", " +
                               "\"productName\": \"Your Game\", " +
                               "\"applicationIdentifier\": \"com.yourstudio.yourgame\", " +
                               "\"bundleVersion\": \"0.1.0\" }");
            builder.AppendLine($"[{Category}] Or export ${ProjectCompanyVariable}, " +
                               $"${ProjectProductVariable}, ${ProjectBundleIdVariable} and " +
                               $"${ProjectBundleVersionVariable}, then re-run.");
            builder.Append($"[{Category}] The application icon is a separate manual step — see " +
                           "the iconPath field.");

            Debug.LogWarning(builder.ToString());
        }

        /// <summary>
        /// Warn when the configured backend has no build-support module installed locally.
        /// Setting IL2CPP always succeeds; <i>building</i> with it needs the platform's IL2CPP
        /// variations, and Unity's failure message at that point is far away from this decision.
        /// </summary>
        /// <param name="backend">Resolved standalone scripting backend.</param>
        private static void WarnIfBackendUnbuildable(ScriptingImplementation backend)
        {
            if (backend != ScriptingImplementation.IL2CPP)
                return;

            var variations = Path.Combine(
                EditorApplication.applicationContentsPath,
                "PlaybackEngines", "windowsstandalonesupport", "Variations");

            if (!Directory.Exists(variations))
                return;

            var hasIl2Cpp = Directory.EnumerateDirectories(variations).Any(
                d => Path.GetFileName(d).IndexOf("il2cpp", StringComparison.OrdinalIgnoreCase) >= 0);

            if (hasIl2Cpp)
                return;

            Debug.LogWarning(
                $"[{Category}] Standalone is set to IL2CPP, but this editor install has no IL2CPP " +
                $"variations under {variations} — only Mono. A Windows build will fail until " +
                "'Windows Build Support (IL2CPP)' is added via Unity Hub > Installs > Add Modules. " +
                $"To build with Mono instead, set ${ProjectScriptingBackendVariable}=Mono2x " +
                $"(or \"scriptingBackend\": \"Mono2x\" in {ProjectConfigFileName}) and re-run.");
        }
    }
}
