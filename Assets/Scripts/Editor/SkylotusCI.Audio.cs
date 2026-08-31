using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

namespace Skylotus.Editor
{
    /// <summary>
    /// Audio-side batchmode entry points (WP-9). Kept in its own partial so this package
    /// never contends with <c>SkylotusCI.cs</c> — see the class remarks there.
    ///
    /// <b>Why reflection.</b> Unity ships no public scripting API for authoring an
    /// <see cref="AudioMixer"/>: <c>UnityEditor.Audio.AudioMixerController</c> and its
    /// group / parameter types are <c>internal</c> to <c>UnityEditor.dll</c>, and
    /// "Assets/Create/Audio Mixer" is the only supported route. The types are nevertheless
    /// real and stable — this generator drives the exact calls that menu item makes
    /// (<c>CreateMixerControllerAtPath</c>, <c>CreateNewGroup</c>, <c>AddChildToParent</c>,
    /// <c>AddExposedParameter</c>) through reflection, which is the only way to produce a
    /// valid <c>.mixer</c> without hand-authoring YAML.
    /// </summary>
    public static partial class SkylotusCI
    {
        /// <summary>Project-relative path of the generated mixer asset.</summary>
        public const string AudioMixerAssetPath = "Assets/Resources/Audio/SkylotusAudioMixer.mixer";

        /// <summary>
        /// Mixer group and exposed-parameter names, parent first. Must stay in lockstep with
        /// <c>Skylotus.AudioChannel</c>. "Master" is the root group Unity creates with the
        /// asset; the rest become its direct children.
        /// </summary>
        private static readonly string[] AudioChannelNames =
        {
            "Master", "Music", "SFX", "UI", "Ambience", "Voice"
        };

        // ─── Entry points ───────────────────────────────────────────

        /// <summary>
        /// Create (or repair) <c>Assets/Resources/Audio/SkylotusAudioMixer.mixer</c> with one
        /// group per <c>AudioChannel</c> and one exposed volume parameter per group, named
        /// <c>&lt;Channel&gt;Volume</c> so <c>AudioManager.SetVolume</c> can drive it.
        ///
        /// Idempotent: an existing mixer is repaired in place rather than recreated, so the
        /// asset GUID survives and references to it are not broken.
        /// </summary>
        public static void GenerateAudioMixer()
        {
            try
            {
                var api = new MixerApi();

                string dir = System.IO.Path.GetDirectoryName(AudioMixerAssetPath).Replace('\\', '/');
                if (!AssetDatabase.IsValidFolder(dir))
                {
                    Fail($"Folder does not exist: {dir}");
                    return;
                }

                var existing = AssetDatabase.LoadAssetAtPath<AudioMixer>(AudioMixerAssetPath);
                object controller;
                bool created;

                if (existing != null)
                {
                    controller = existing;
                    created = false;
                }
                else
                {
                    controller = api.CreateAtPath(AudioMixerAssetPath);
                    created = true;

                    if (controller == null)
                    {
                        Fail($"CreateMixerControllerAtPath returned null for {AudioMixerAssetPath}");
                        return;
                    }
                }

                object master = api.GetMasterGroup(controller);
                if (master == null)
                {
                    Fail("Mixer has no master group — the asset is malformed.");
                    return;
                }

                api.SetName(master, AudioChannelNames[0]);

                // One child group per non-master channel, created only when absent.
                var groups = new Dictionary<string, object> { [AudioChannelNames[0]] = master };
                var addedGroups = new List<string>();

                foreach (string channel in AudioChannelNames.Skip(1))
                {
                    object group = api.FindChild(master, channel);

                    if (group == null)
                    {
                        group = api.CreateGroup(controller, channel);
                        api.AddChildToParent(controller, group, master);
                        addedGroups.Add(channel);
                    }

                    groups[channel] = group;
                }

                api.EnsureSubAssets(controller, groups.Values);
                api.OnSubAssetChanged(controller);

                string viewState = api.ShowGroupsInCurrentView(controller, groups.Values);

                var exposed = api.ExposeVolumeParameters(controller, AudioChannelNames, groups);

                EditorUtility.SetDirty((UnityEngine.Object)controller);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(AudioMixerAssetPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();

                string problem = ValidateMixerAtPath(AudioMixerAssetPath, out string summary);
                if (problem != null)
                {
                    Fail(problem);
                    return;
                }

                string verb = created ? "created" : "repaired";
                string added = addedGroups.Count > 0
                    ? $" (+groups: {string.Join(", ", addedGroups)})"
                    : string.Empty;

                Succeed($"{verb} {AudioMixerAssetPath}{added}; {summary}; " +
                        $"exposed: {string.Join(", ", exposed)}; view: {viewState}");
            }
            catch (Exception e)
            {
                var root = Unwrap(e);
                Fail($"GenerateAudioMixer threw {root.GetType().Name}: {root.Message}\n{root.StackTrace}");
            }
        }

        /// <summary>
        /// Read-only check that the generated mixer still carries a group and an exposed
        /// <c>&lt;Channel&gt;Volume</c> parameter for every <c>AudioChannel</c>. Cheap enough
        /// to run as a regression gate after any audio change.
        /// </summary>
        public static void ValidateAudioMixer()
        {
            try
            {
                string problem = ValidateMixerAtPath(AudioMixerAssetPath, out string summary);

                if (problem != null) Fail(problem);
                else Succeed($"{AudioMixerAssetPath} — {summary}");
            }
            catch (Exception e)
            {
                var root = Unwrap(e);
                Fail($"ValidateAudioMixer threw {root.GetType().Name}: {root.Message}");
            }
        }

        // ─── Validation ─────────────────────────────────────────────

        /// <summary>
        /// Peel <see cref="TargetInvocationException"/> wrappers off a reflection failure so
        /// the CI log shows the real cause instead of "Exception has been thrown by the
        /// target of an invocation."
        /// </summary>
        /// <param name="e">The exception to unwrap.</param>
        /// <returns>The innermost non-wrapper exception.</returns>
        private static Exception Unwrap(Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null)
                e = e.InnerException;

            return e;
        }

        /// <summary>Validate the mixer at a path against the channel list.</summary>
        /// <param name="path">Project-relative path of the <c>.mixer</c> asset.</param>
        /// <param name="summary">Human-readable description of what was found.</param>
        /// <returns>Null when valid, otherwise the first problem found.</returns>
        private static string ValidateMixerAtPath(string path, out string summary)
        {
            summary = string.Empty;

            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(path);
            if (mixer == null) return $"No AudioMixer at {path}";

            var api = new MixerApi();

            foreach (string channel in AudioChannelNames)
            {
                var group = mixer.FindMatchingGroups(channel).FirstOrDefault(g => g.name == channel);
                if (group == null) return $"Mixer group '{channel}' is missing from {path}";
            }

            var names = api.GetExposedParameterNames(mixer);

            foreach (string channel in AudioChannelNames)
            {
                string expected = channel + "Volume";
                if (!names.Contains(expected))
                    return $"Exposed parameter '{expected}' is missing from {path} (found: {string.Join(", ", names)})";
            }

            summary = $"{AudioChannelNames.Length} groups, {names.Count} exposed parameters";
            return null;
        }

        // ─── Internal AudioMixer API shim ───────────────────────────

        /// <summary>
        /// Thin reflection wrapper over the internal <c>UnityEditor.Audio</c> mixer types.
        /// Every member resolves eagerly in the constructor, so a Unity version that moved or
        /// renamed something fails loudly at the top of the generator rather than half way
        /// through writing an asset.
        /// </summary>
        private sealed class MixerApi
        {
            private readonly Type _controllerType;
            private readonly Type _groupType;
            private readonly Type _groupPathType;
            private readonly Type _exposedParamType;

            private readonly MethodInfo _createAtPath;
            private readonly MethodInfo _createNewGroup;
            private readonly MethodInfo _addChildToParent;
            private readonly MethodInfo _setCurrentViewVisibility;
            private readonly MethodInfo _sanitizeGroupViews;
            private readonly MethodInfo _addNewSubAsset;
            private readonly MethodInfo _onSubAssetChanged;
            private readonly MethodInfo _addExposedParameter;
            private readonly MethodInfo _getGuidForVolume;

            private readonly PropertyInfo _masterGroup;
            private readonly PropertyInfo _children;
            private readonly PropertyInfo _exposedParameters;
            private readonly PropertyInfo _views;
            private readonly PropertyInfo _currentViewIndex;
            private readonly PropertyInfo _groupId;

            private readonly FieldInfo _paramGuidField;
            private readonly FieldInfo _paramNameField;

            /// <summary>Resolve every internal member the generator needs.</summary>
            /// <exception cref="MissingMemberException">A required internal member is absent.</exception>
            public MixerApi()
            {
                var editorAssembly = typeof(AudioImporter).Assembly;

                const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

                _controllerType = RequireType(editorAssembly, "UnityEditor.Audio.AudioMixerController");
                _groupType = RequireType(editorAssembly, "UnityEditor.Audio.AudioMixerGroupController");
                _groupPathType = RequireType(editorAssembly, "UnityEditor.Audio.AudioGroupParameterPath");

                _createAtPath = RequireMethod(_controllerType, "CreateMixerControllerAtPath", Any);
                _createNewGroup = RequireMethod(_controllerType, "CreateNewGroup", Any);
                _addChildToParent = RequireMethod(_controllerType, "AddChildToParent", Any);
                _setCurrentViewVisibility = RequireMethod(_controllerType, "SetCurrentViewVisibility", Any);
                _sanitizeGroupViews = RequireMethod(_controllerType, "SanitizeGroupViews", Any);
                _addNewSubAsset = RequireMethod(_controllerType, "AddNewSubAsset", Any);
                _onSubAssetChanged = RequireMethod(_controllerType, "OnSubAssetChanged", Any);
                _addExposedParameter = RequireMethod(_controllerType, "AddExposedParameter", Any);
                _getGuidForVolume = RequireMethod(_groupType, "GetGUIDForVolume", Any);

                _masterGroup = RequireProperty(_controllerType, "masterGroup", Any);
                _children = RequireProperty(_groupType, "children", Any);
                _exposedParameters = RequireProperty(_controllerType, "exposedParameters", Any);
                _views = RequireProperty(_controllerType, "views", Any);
                _currentViewIndex = RequireProperty(_controllerType, "currentViewIndex", Any);
                _groupId = RequireProperty(_groupType, "groupID", Any);

                _exposedParamType = _exposedParameters.PropertyType.GetElementType();
                if (_exposedParamType == null)
                    throw new MissingMemberException("AudioMixerController.exposedParameters is not an array");

                var fields = _exposedParamType.GetFields(BindingFlags.Public | BindingFlags.Instance);

                _paramGuidField = fields.FirstOrDefault(f => f.Name == "guid")
                                  ?? fields.FirstOrDefault(f => f.FieldType == typeof(GUID));
                _paramNameField = fields.FirstOrDefault(f => f.Name == "name")
                                  ?? fields.FirstOrDefault(f => f.FieldType == typeof(string));

                if (_paramGuidField == null || _paramNameField == null)
                    throw new MissingMemberException($"{_exposedParamType.FullName} has no guid/name fields");
            }

            /// <summary>Create a mixer asset at a path, master group and snapshot included.</summary>
            /// <param name="path">Project-relative <c>.mixer</c> path.</param>
            /// <returns>The new <c>AudioMixerController</c>, boxed.</returns>
            public object CreateAtPath(string path) => _createAtPath.Invoke(null, new object[] { path });

            /// <summary>Get a controller's master (root) group.</summary>
            /// <param name="controller">The mixer controller.</param>
            /// <returns>The master group, or null.</returns>
            public object GetMasterGroup(object controller) => _masterGroup.GetValue(controller);

            /// <summary>Find a direct child group by name.</summary>
            /// <param name="parent">The parent group.</param>
            /// <param name="name">Child group name to look for.</param>
            /// <returns>The child group, or null when absent.</returns>
            public object FindChild(object parent, string name)
            {
                if (_children.GetValue(parent) is not Array children) return null;

                foreach (var child in children)
                {
                    if (child is UnityEngine.Object o && o.name == name) return child;
                }

                return null;
            }

            /// <summary>Create a detached mixer group owned by a controller.</summary>
            /// <param name="controller">The owning mixer controller.</param>
            /// <param name="name">Group name.</param>
            /// <returns>The new group.</returns>
            public object CreateGroup(object controller, string name) =>
                _createNewGroup.Invoke(controller, new object[] { name, false });

            /// <summary>Parent a group under another group.</summary>
            /// <param name="controller">The owning mixer controller.</param>
            /// <param name="child">The group to reparent.</param>
            /// <param name="parent">The new parent group.</param>
            public void AddChildToParent(object controller, object child, object parent) =>
                _addChildToParent.Invoke(controller, new[] { child, parent });

            /// <summary>
            /// Make every group visible in the mixer window's current view. Purely cosmetic —
            /// views only control what the Audio Mixer window draws, never routing or mixing —
            /// so a failure here is reported, not fatal. Batchmode has no mixer window and the
            /// per-group <c>AddGroupToCurrentView</c> throws without one.
            /// </summary>
            /// <param name="controller">The owning mixer controller.</param>
            /// <param name="groups">Every group in the mixer.</param>
            /// <returns>A short description of what happened, for the CI log.</returns>
            public string ShowGroupsInCurrentView(object controller, IEnumerable<object> groups)
            {
                try
                {
                    if (_views.GetValue(controller) is not Array views || views.Length == 0)
                        return "no views defined (all groups visible)";

                    int index = (int)_currentViewIndex.GetValue(controller);
                    if (index < 0 || index >= views.Length)
                        _currentViewIndex.SetValue(controller, 0);

                    var guids = groups
                        .Select(g => (GUID)_groupId.GetValue(g))
                        .ToArray();

                    _setCurrentViewVisibility.Invoke(controller, new object[] { guids });
                    _sanitizeGroupViews.Invoke(controller, Array.Empty<object>());

                    return $"{guids.Length} groups visible in view {_currentViewIndex.GetValue(controller)}";
                }
                catch (Exception e)
                {
                    return $"skipped ({Unwrap(e).GetType().Name}: {Unwrap(e).Message})";
                }
            }

            /// <summary>Notify the controller that its sub-asset set changed.</summary>
            /// <param name="controller">The owning mixer controller.</param>
            public void OnSubAssetChanged(object controller) =>
                _onSubAssetChanged.Invoke(controller, Array.Empty<object>());

            /// <summary>Rename a mixer node (group or snapshot).</summary>
            /// <param name="node">The node to rename.</param>
            /// <param name="name">The name to apply.</param>
            public void SetName(object node, string name)
            {
                if (node is UnityEngine.Object o && o.name != name) o.name = name;
            }

            /// <summary>
            /// Guarantee every group is stored inside the mixer asset. <c>CreateNewGroup</c>
            /// leaves the group detached in some Unity versions, and adding one twice is an
            /// error, so the asset path is checked first.
            /// </summary>
            /// <param name="controller">The owning mixer controller.</param>
            /// <param name="groups">Groups that must live inside the asset.</param>
            public void EnsureSubAssets(object controller, IEnumerable<object> groups)
            {
                foreach (var group in groups)
                {
                    if (group is not UnityEngine.Object o) continue;
                    if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(o))) continue;

                    _addNewSubAsset.Invoke(controller, new object[] { o, false });
                }
            }

            /// <summary>
            /// Expose each group's volume parameter and rename it to <c>&lt;Channel&gt;Volume</c>.
            /// Unity's default name is display-oriented ("Music (Volume)") and unusable as a
            /// stable scripting key, so the names are rewritten after exposure.
            /// </summary>
            /// <param name="controller">The owning mixer controller.</param>
            /// <param name="channels">Channel names, in group order.</param>
            /// <param name="groups">Channel name to group lookup.</param>
            /// <returns>The exposed parameter names in asset order.</returns>
            public List<string> ExposeVolumeParameters(object controller, string[] channels,
                Dictionary<string, object> groups)
            {
                var guidToName = new Dictionary<GUID, string>();

                foreach (string channel in channels)
                {
                    object group = groups[channel];
                    var guid = (GUID)_getGuidForVolume.Invoke(group, Array.Empty<object>());
                    guidToName[guid] = channel + "Volume";

                    if (IsExposed(controller, guid)) continue;

                    object path = Activator.CreateInstance(_groupPathType, group, guid);
                    _addExposedParameter.Invoke(controller, new[] { path });
                }

                // Rewrite the auto-generated display names into stable scripting keys.
                var parameters = (Array)_exposedParameters.GetValue(controller);
                var names = new List<string>();

                for (int i = 0; i < parameters.Length; i++)
                {
                    object entry = parameters.GetValue(i);
                    var guid = (GUID)_paramGuidField.GetValue(entry);

                    if (guidToName.TryGetValue(guid, out string wanted))
                        _paramNameField.SetValue(entry, wanted);

                    parameters.SetValue(entry, i);
                    names.Add((string)_paramNameField.GetValue(entry));
                }

                _exposedParameters.SetValue(controller, parameters);
                return names;
            }

            /// <summary>Read the exposed parameter names off any mixer instance.</summary>
            /// <param name="mixer">The mixer to inspect.</param>
            /// <returns>The exposed parameter names.</returns>
            public List<string> GetExposedParameterNames(AudioMixer mixer)
            {
                var names = new List<string>();
                var parameters = (Array)_exposedParameters.GetValue(mixer);

                if (parameters == null) return names;

                for (int i = 0; i < parameters.Length; i++)
                    names.Add((string)_paramNameField.GetValue(parameters.GetValue(i)));

                return names;
            }

            /// <summary>Test whether a parameter GUID is already exposed on a controller.</summary>
            /// <param name="controller">The mixer controller.</param>
            /// <param name="guid">The parameter GUID.</param>
            /// <returns>True when the GUID is already exposed.</returns>
            private bool IsExposed(object controller, GUID guid)
            {
                var parameters = (Array)_exposedParameters.GetValue(controller);
                if (parameters == null) return false;

                for (int i = 0; i < parameters.Length; i++)
                {
                    if ((GUID)_paramGuidField.GetValue(parameters.GetValue(i)) == guid) return true;
                }

                return false;
            }

            /// <summary>Resolve a type, or throw naming what went missing.</summary>
            private static Type RequireType(Assembly assembly, string name) =>
                assembly.GetType(name, false)
                ?? throw new MissingMemberException($"Internal type not found: {name}");

            /// <summary>Resolve a method, or throw naming what went missing.</summary>
            private static MethodInfo RequireMethod(Type type, string name, BindingFlags flags) =>
                type.GetMethod(name, flags)
                ?? throw new MissingMemberException($"{type.FullName}.{name} not found");

            /// <summary>Resolve a property, or throw naming what went missing.</summary>
            private static PropertyInfo RequireProperty(Type type, string name, BindingFlags flags) =>
                type.GetProperty(name, flags)
                ?? throw new MissingMemberException($"{type.FullName}.{name} not found");
        }
    }
}
