using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Scripting;

namespace Skylotus.Core.Rendering
{
    /// <summary>
    /// Realizes the screen-brightness setting against the URP volume stack.
    ///
    /// <b>Why this exists.</b> <see cref="SettingsService"/> owned the brightness pref and the
    /// <see cref="IBrightnessController"/> seam, but nothing occupied it: the slider moved, the
    /// label updated, and the screen never changed. This is the missing half — the piece that
    /// turns a 0–1 number into photons.
    ///
    /// <b>How it works.</b> A single persistent GameObject carries a global
    /// <see cref="Volume"/> at <see cref="VolumePriority"/> whose profile is created at runtime
    /// (never an asset, so nothing on disk is mutated by a slider drag) and overrides exactly one
    /// field: <c>ColorAdjustments.postExposure</c>. Brightness maps linearly onto exposure stops,
    /// from <see cref="DarkestExposure"/> EV at 0 to 0 EV at
    /// <see cref="SettingsService.DefaultBrightness"/> — 0 EV being the authored image, which is
    /// why the slider darkens but never brightens past what the artist shipped.
    ///
    /// <b>Registration.</b> The component installs itself from a
    /// <c>RuntimeInitializeOnLoadMethod</c> at <c>AfterSceneLoad</c> — after
    /// <see cref="Bootstrapper"/>'s <c>Awake</c> has registered
    /// <see cref="SettingsService"/> and before its <c>Start</c> loads the first scene. Assigning
    /// <see cref="SettingsService.BrightnessController"/> immediately pushes the saved value, so
    /// the main menu's first frame already renders at the player's chosen brightness. Nothing
    /// needs to place this on a prefab or in a scene.
    ///
    /// <b>Requires camera post-processing.</b> A color-grading brightness only exists if the URP
    /// post-processing pass runs, which means every base camera needs
    /// <c>Render Post Processing</c> ticked. The scenes are authored that way — see
    /// <c>SkylotusCI.EnableCameraPostProcessing</c>, and
    /// <c>SkylotusCI.ValidateCameraPostProcessing</c> guards it — so this component never touches
    /// camera state. Add a camera with the flag off and brightness silently stops affecting it;
    /// the validator is what catches that.
    ///
    /// <b>Not a <c>SingletonBehaviour</c>.</b> That base class auto-creates on first
    /// <c>Instance</c> access, which would spawn a controller from any thread that merely asked
    /// whether one existed. Creation here is deliberate and happens in exactly one place.
    ///
    /// <b><c>[Preserve]</c>.</b> Nothing in the project references this type — it is reached only
    /// through a <c>RuntimeInitializeOnLoadMethod</c> — which is exactly the shape managed code
    /// stripping removes. Unity roots initialize-on-load methods itself, so this is belt and
    /// braces, but the failure mode it guards against (brightness silently dead in an IL2CPP
    /// player and nowhere else) is expensive to diagnose.
    /// </summary>
    [Preserve]
    [AddComponentMenu("Skylotus/Rendering/Brightness Controller")]
    [DisallowMultipleComponent]
    public class BrightnessController : MonoBehaviour, IBrightnessController
    {
        // ─── Constants ──────────────────────────────────────────────

        /// <summary>Log category used by every message from this component.</summary>
        private const string LogCategory = "Brightness";

        /// <summary>Name given to the GameObject created by <see cref="Ensure"/>.</summary>
        private const string HostName = "[Skylotus Brightness]";

        /// <summary>
        /// Post-exposure applied at brightness 0, in exposure stops. -2 EV is a quarter of the
        /// authored luminance: unmistakably dimmer, and still light enough that a player who
        /// dragged the slider to the floor can find it again.
        /// </summary>
        public const float DarkestExposure = -2f;

        /// <summary>
        /// Priority of the brightness volume. Deliberately far above anything a scene is likely
        /// to author, because a player's accessibility setting must win over art direction.
        /// </summary>
        public const float VolumePriority = 1000f;

        /// <summary>
        /// Brightness within this distance of <see cref="SettingsService.DefaultBrightness"/>
        /// counts as no adjustment at all, so float noise from a slider cannot leave the volume
        /// (and the post-processing pass) switched on at an invisible strength.
        /// </summary>
        private const float NeutralEpsilon = 0.0005f;

        /// <summary>
        /// Seconds to wait for a <see cref="SettingsService"/> before complaining. Long enough to
        /// cover an additively loaded boot scene, short enough that a misconfigured project says
        /// so while the developer is still looking at the console.
        /// </summary>
        private const float RegistrationGrace = 2f;

        // ─── Static State ───────────────────────────────────────────

        /// <summary>The live controller, or null before <see cref="Ensure"/> has run.</summary>
        private static BrightnessController _instance;

        /// <summary>True once the application is tearing down, so nothing re-creates the host.</summary>
        private static bool _quitting;

        // ─── Instance State ─────────────────────────────────────────

        /// <summary>The global volume this component drives.</summary>
        private Volume _volume;

        /// <summary>Runtime-only profile backing <see cref="_volume"/>. Never a project asset.</summary>
        private VolumeProfile _profile;

        /// <summary>The single override this component writes to.</summary>
        private ColorAdjustments _colorAdjustments;

        /// <summary>Last brightness handed to <see cref="SetBrightness"/>.</summary>
        private float _brightness = SettingsService.DefaultBrightness;

        /// <summary>True while brightness differs from the default and the volume is doing work.</summary>
        private bool _adjusting;

        /// <summary>True once this component has been handed to <see cref="SettingsService"/>.</summary>
        private bool _registered;

        /// <summary>True once the "no SettingsService" warning has been logged, so it is logged once.</summary>
        private bool _warnedAboutSettings;

        /// <summary>Unscaled time at which this component came up, for the registration grace period.</summary>
        private float _createdAt;

        // ─── Public Surface ─────────────────────────────────────────

        /// <summary>
        /// The live controller, or null when none has been created yet. Reading this never
        /// creates one — call <see cref="Ensure"/> for that.
        /// </summary>
        public static BrightnessController Instance => _instance;

        /// <summary>The brightness currently in effect, 0–1.</summary>
        public float Brightness => _brightness;

        /// <summary>
        /// The post-exposure, in stops, currently written onto the volume. 0 is the authored
        /// image. Exposed so a headless check can read back what the setting actually produced
        /// rather than trusting that it did.
        /// </summary>
        public float PostExposure => _colorAdjustments == null ? 0f : _colorAdjustments.postExposure.value;

        /// <summary>
        /// True while brightness differs from <see cref="SettingsService.DefaultBrightness"/>,
        /// which is also exactly when the volume and the forced camera post-processing are live.
        /// </summary>
        public bool IsAdjusting => _adjusting;

        /// <summary>
        /// Push a brightness setting onto the volume stack, switching the whole mechanism on or
        /// off as the value leaves or returns to the default.
        /// </summary>
        /// <param name="brightness">Normalized brightness, clamped to 0–1.</param>
        public void SetBrightness(float brightness)
        {
            _brightness = Mathf.Clamp01(brightness);

            // Defensive: Awake builds the volume, and nothing can call this before Awake on a
            // component created at runtime. It can, however, be called on a component added in
            // Edit Mode, where Unity never runs Awake at all.
            if (_colorAdjustments == null)
                BuildVolume();

            _colorAdjustments.postExposure.value = ExposureFor(_brightness);

            bool adjusting = _brightness < SettingsService.DefaultBrightness - NeutralEpsilon;

            if (adjusting != _adjusting)
            {
                _adjusting = adjusting;
                _volume.enabled = adjusting;
            }
        }

        /// <summary>
        /// Return the live controller, creating its persistent host GameObject if there is none.
        /// Idempotent, and a no-op outside play mode — an Edit Mode caller would leave an
        /// untracked object in whatever scene happened to be open.
        /// </summary>
        /// <returns>The controller, or null while the application is quitting or not playing.</returns>
        public static BrightnessController Ensure()
        {
            if (_quitting || !Application.isPlaying)
                return null;

            if (_instance != null)
                return _instance;

            var host = new GameObject(HostName);
            _instance = host.AddComponent<BrightnessController>();

            return _instance;
        }

        // ─── Installation ───────────────────────────────────────────

        /// <summary>
        /// Clear static state on domain reload. Entering play mode with Unity's domain reload
        /// disabled leaves statics from the previous session alive, and a stale
        /// <see cref="_instance"/> pointing at a destroyed object would stop
        /// <see cref="Ensure"/> from creating a working one.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
            _quitting = false;
        }

        /// <summary>
        /// Create the controller once every scene in the first load is up.
        ///
        /// <c>AfterSceneLoad</c> is the correct moment on both boot paths: it runs after
        /// <see cref="Bootstrapper"/>'s <c>Awake</c> (which registers
        /// <see cref="SettingsService"/> and calls <c>ApplyAll</c>) and before its <c>Start</c>
        /// hands off to the first real scene. <c>ApplyAll</c> having already run with an empty
        /// seam costs nothing — assigning <see cref="SettingsService.BrightnessController"/>
        /// re-pushes the saved value by design.
        /// </summary>
        [Preserve]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            Ensure();
        }

        // ─── Unity Lifecycle ────────────────────────────────────────

        /// <summary>
        /// Unity Awake — claim the singleton slot, persist across scenes, build the volume, and
        /// take the saved brightness from the settings service.
        /// </summary>
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            _createdAt = Time.unscaledTime;

            DontDestroyOnLoad(gameObject);

            BuildVolume();
            TryRegister();
        }

        /// <summary>
        /// Unity LateUpdate — finish registering if the settings service was not up yet, and
        /// re-assert camera post-processing while an adjustment is live. Costs one branch per
        /// frame at the default brightness, which is the case that must stay free.
        /// </summary>
        private void LateUpdate()
        {
            if (!_registered)
                TryRegister();
        }

        /// <summary>Unity OnApplicationQuit — stop <see cref="Ensure"/> resurrecting the host during teardown.</summary>
        private void OnApplicationQuit()
        {
            _quitting = true;
        }

        /// <summary>
        /// Unity OnDestroy — hand the cameras back, drop the settings service's reference to a
        /// dead object, and destroy the runtime profile, which is not owned by any scene and
        /// would otherwise leak for the life of the process.
        /// </summary>
        private void OnDestroy()
        {
            if (_instance != this)
                return;

            if (_registered && ServiceLocator.TryGet<SettingsService>(out var settings) && settings != null)
            {
                if (ReferenceEquals(settings.BrightnessController, this))
                    settings.BrightnessController = null;
            }

            DestroyObject(_colorAdjustments);
            DestroyObject(_profile);

            _instance = null;
        }

        /// <summary>
        /// Destroy an object this component owns, from either play mode or Edit Mode.
        /// <c>Object.Destroy</c> throws in Edit Mode, and the headless brightness check adds this
        /// component to a scratch GameObject without ever entering play mode.
        /// </summary>
        /// <param name="target">The object to destroy; null and already-destroyed are no-ops.</param>
        private static void DestroyObject(UnityEngine.Object target)
        {
            if (target == null)
                return;

            if (Application.isPlaying)
                Destroy(target);
            else
                DestroyImmediate(target);
        }

        // ─── Volume ─────────────────────────────────────────────────

        /// <summary>
        /// Build the global volume and its runtime-only profile.
        ///
        /// The profile is a <c>CreateInstance</c> rather than the project's
        /// <c>DefaultVolumeProfile</c> asset on purpose: writing brightness into an asset would
        /// dirty a tracked file on every slider tick in the editor and would be a shared, global
        /// mutation in a player. The default profile's own <c>ColorAdjustments</c> stays the
        /// neutral baseline this volume blends on top of.
        ///
        /// Only <c>postExposure</c> is overridden, so contrast, saturation, hue and colour filter
        /// still come from whatever lower-priority volume a scene authors.
        /// </summary>
        private void BuildVolume()
        {
            if (_colorAdjustments != null)
                return;

            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "SkylotusBrightness";
            _profile.hideFlags = HideFlags.HideAndDontSave;

            _colorAdjustments = _profile.Add<ColorAdjustments>(overrides: false);
            _colorAdjustments.hideFlags = HideFlags.HideAndDontSave;
            _colorAdjustments.active = true;
            _colorAdjustments.postExposure.overrideState = true;
            _colorAdjustments.postExposure.value = 0f;

            _volume = gameObject.GetComponent<Volume>();
            if (_volume == null)
                _volume = gameObject.AddComponent<Volume>();

            _volume.isGlobal = true;
            _volume.priority = VolumePriority;
            _volume.weight = 1f;

            // sharedProfile, not profile: the profile getter clones whatever it is given, and
            // this instance is already private to this component.
            _volume.sharedProfile = _profile;

            // Off until something actually asks for a non-default brightness.
            _volume.enabled = false;
        }

        /// <summary>
        /// Map a 0–1 brightness onto exposure stops: 0 gives <see cref="DarkestExposure"/>,
        /// <see cref="SettingsService.DefaultBrightness"/> gives 0 EV — the authored image.
        /// Linear in stops rather than in luminance, because stops are already the logarithmic
        /// scale perception works on, so the slider feels even end to end.
        /// </summary>
        /// <param name="brightness">Normalized brightness, 0–1.</param>
        /// <returns>Post-exposure in stops, at most 0.</returns>
        private static float ExposureFor(float brightness)
        {
            float neutral = SettingsService.DefaultBrightness;
            float t = neutral <= 0f ? 1f : Mathf.Clamp01(brightness / neutral);

            return Mathf.Lerp(DarkestExposure, 0f, t);
        }

        /// <summary>
        /// Hand this component to <see cref="SettingsService"/>, which immediately pushes the
        /// saved brightness onto it. Retried from <c>LateUpdate</c> so a boot scene loaded
        /// additively (or any path that registers the service late) still ends up connected,
        /// and complains once if no service ever appears.
        /// </summary>
        private void TryRegister()
        {
            if (!ServiceLocator.TryGet<SettingsService>(out var settings) || settings == null)
            {
                if (!_warnedAboutSettings && Time.unscaledTime - _createdAt > RegistrationGrace)
                {
                    _warnedAboutSettings = true;
                    GameLogger.LogWarning(LogCategory,
                        "No SettingsService registered — the brightness setting will not be " +
                        "applied. Enter play mode from the boot scene.");
                }

                return;
            }

            settings.BrightnessController = this;
            _registered = true;

            GameLogger.Log(LogCategory,
                $"Brightness controller registered; applied {_brightness:0.###} " +
                $"({PostExposure:0.###} EV).");
        }
    }
}
