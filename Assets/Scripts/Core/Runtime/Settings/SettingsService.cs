using System.Collections.Generic;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// Receiver for the screen-brightness setting.
    ///
    /// <see cref="SettingsService"/> owns the persisted 0–1 brightness value and knows when it
    /// changes; it deliberately knows nothing about <i>how</i> brightness is realized. WP-3 lands
    /// the URP volume-stack implementation as a component that implements this interface and
    /// assigns itself to <see cref="SettingsService.BrightnessController"/>; until then the seam
    /// is simply unoccupied and the saved value is stored, restored and ignored.
    /// </summary>
    public interface IBrightnessController
    {
        /// <summary>
        /// Push the current brightness setting onto whatever renders it.
        /// </summary>
        /// <param name="brightness">Normalized brightness, 0–1, where 1 is the authored default.</param>
        void SetBrightness(float brightness);
    }

    /// <summary>
    /// One display resolution the player can choose, paired with the label shown for it.
    /// </summary>
    public readonly struct ResolutionOption
    {
        /// <summary>Horizontal resolution in pixels.</summary>
        public readonly int Width;

        /// <summary>Vertical resolution in pixels.</summary>
        public readonly int Height;

        /// <summary>
        /// Create a resolution option.
        /// </summary>
        /// <param name="width">Horizontal resolution in pixels.</param>
        /// <param name="height">Vertical resolution in pixels.</param>
        public ResolutionOption(int width, int height)
        {
            Width = width;
            Height = height;
        }

        /// <summary>Label shown in the settings UI, for example <c>"1920 × 1080"</c>.</summary>
        public string Label => $"{Width} × {Height}";
    }

    /// <summary>
    /// One selectable language, pairing the name shown in the UI with the ISO code
    /// <see cref="LocalizationSystem"/> loads.
    /// </summary>
    public readonly struct LanguageOption
    {
        /// <summary>Name shown in the settings UI.</summary>
        public readonly string DisplayName;

        /// <summary>ISO code passed to <see cref="LocalizationSystem.SetLanguage"/>.</summary>
        public readonly string Code;

        /// <summary>
        /// Create a language option.
        /// </summary>
        /// <param name="displayName">Name shown in the settings UI.</param>
        /// <param name="code">ISO language code.</param>
        public LanguageOption(string displayName, string code)
        {
            DisplayName = displayName;
            Code = code;
        }
    }

    /// <summary>
    /// Single owner of every persisted player setting: the PlayerPrefs key names, the typed
    /// defaults, the option lists the settings UI cycles through, and the code that pushes a
    /// stored value onto the system that realizes it.
    ///
    /// <b>Why this exists.</b> Before this service, every setting was read exactly once —
    /// inside a settings tab's <c>OnEnable</c>. A player who set master volume to 20%, quit and
    /// relaunched got 100% volume until they happened to open Settings → Audio, at which point
    /// it jumped mid-session. <see cref="ApplyAll"/> is the boot-time step that was missing;
    /// <see cref="Bootstrapper"/> calls it after the MonoBehaviour systems are registered and
    /// before the first scene loads.
    ///
    /// <b>Both directions.</b> Reads (<see cref="GetVolume"/>, <see cref="QualityIndex"/>, …)
    /// return the saved value or a typed default. Writes (<see cref="SetVolume"/>,
    /// <see cref="SetQualityIndex"/>, …) persist <i>and</i> apply <i>and</i> publish
    /// <see cref="OnSettingsChangedEvent"/>. The settings tabs are pure view code that call
    /// through this service and never touch <c>PlayerPrefs</c>.
    ///
    /// <b>Disk writes.</b> No setter calls <c>PlayerPrefs.Save()</c> — that is a synchronous
    /// disk flush, and calling it per slider tick writes hundreds of times across one drag.
    /// Setters mark the service <see cref="IsDirty"/>; <see cref="Flush"/> writes at most once
    /// and is called when a settings tab closes and from the bootstrapper's
    /// <c>OnApplicationPause</c> / <c>OnApplicationQuit</c>.
    ///
    /// This is a pure-C# system with no serialized state, constructed and registered by
    /// <see cref="Bootstrapper"/> alongside <see cref="SaveSystem"/> and
    /// <see cref="LocalizationSystem"/>:
    /// <code>ServiceLocator.Get&lt;SettingsService&gt;().SetVolume(AudioChannel.Music, 0.5f);</code>
    /// </summary>
    public class SettingsService
    {
        /// <summary>Log category used by every message from this system.</summary>
        private const string LogCategory = "Settings";

        // ─── Prefs Keys (the single source of truth) ────────────────

        /// <summary>Prefix for per-channel volume keys; the <see cref="AudioChannel"/> name completes it.</summary>
        public const string AudioVolumeKeyPrefix = "Settings_Audio_";

        /// <summary>Key holding the index into <see cref="ResolutionOptions"/>.</summary>
        public const string ResolutionKey = "Settings_Resolution";

        /// <summary>Key holding the index into <see cref="FullscreenOptions"/>.</summary>
        public const string FullscreenKey = "Settings_Fullscreen";

        /// <summary>Key holding vertical sync as 0 or 1.</summary>
        public const string VSyncKey = "Settings_VSync";

        /// <summary>Key holding the index into <c>QualitySettings.names</c>.</summary>
        public const string QualityKey = "Settings_Quality";

        /// <summary>Key holding normalized screen brightness, 0–1.</summary>
        public const string BrightnessKey = "Settings_Brightness";

        /// <summary>Key holding the index into <see cref="LanguageOptions"/>.</summary>
        public const string LanguageKey = "Settings_Language";

        /// <summary>Key holding controller vibration as 0 or 1.</summary>
        public const string VibrationKey = "Settings_Vibration";

        /// <summary>Key holding screen shake as 0 or 1.</summary>
        public const string ScreenshakeKey = "Settings_Screenshake";

        // ─── Defaults ───────────────────────────────────────────────

        /// <summary>Brightness used when nothing is saved: the authored, un-adjusted image.</summary>
        public const float DefaultBrightness = 1f;

        /// <summary>Controller vibration is on unless the player turns it off.</summary>
        public const bool DefaultVibration = true;

        /// <summary>Screen shake is on unless the player turns it off.</summary>
        public const bool DefaultScreenshake = true;

        // ─── Option Tables ──────────────────────────────────────────

        /// <summary>Selectable fullscreen modes, in UI order. Parallel to <see cref="_fullscreenModes"/>.</summary>
        private static readonly string[] _fullscreenOptions =
        {
            "Fullscreen",
            "Borderless",
            "Windowed"
        };

        /// <summary>Unity fullscreen modes matching <see cref="_fullscreenOptions"/> index for index.</summary>
        private static readonly FullScreenMode[] _fullscreenModes =
        {
            FullScreenMode.ExclusiveFullScreen,
            FullScreenMode.FullScreenWindow,
            FullScreenMode.Windowed
        };

        /// <summary>
        /// Selectable languages, in UI order. The saved setting is an index into this table, so
        /// entries may be appended but must never be reordered or removed without a migration —
        /// an existing player's saved index would silently point at a different language.
        /// </summary>
        private static readonly LanguageOption[] _languageOptions =
        {
            new LanguageOption("English", "en"),
            new LanguageOption("Spanish", "es"),
            new LanguageOption("French", "fr"),
            new LanguageOption("German", "de"),
            new LanguageOption("Portuguese", "pt"),
            new LanguageOption("Japanese", "ja"),
            new LanguageOption("Korean", "ko"),
            new LanguageOption("Chinese (Simplified)", "zh-Hans")
        };

        /// <summary>Cached display names for <see cref="_languageOptions"/>, built once.</summary>
        private static string[] _languageLabels;

        // ─── State ──────────────────────────────────────────────────

        /// <summary>Deduplicated display resolutions, built lazily from <c>Screen.resolutions</c>.</summary>
        private readonly List<ResolutionOption> _resolutions = new List<ResolutionOption>();

        /// <summary>Labels for <see cref="_resolutions"/>, in the same order.</summary>
        private string[] _resolutionLabels;

        /// <summary>True once <see cref="_resolutions"/> has been populated.</summary>
        private bool _resolutionsBuilt;

        /// <summary>True when a setter has written a pref that has not yet been flushed to disk.</summary>
        private bool _dirty;

        /// <summary>Backing field for <see cref="BrightnessController"/>.</summary>
        private IBrightnessController _brightnessController;

        // ─── Public Surface ─────────────────────────────────────────

        /// <summary>
        /// True when a setting has changed since the last <see cref="Flush"/>. Flushing a clean
        /// service is a no-op, which is what keeps a full slider drag to one disk write.
        /// </summary>
        public bool IsDirty => _dirty;

        /// <summary>
        /// The component that realizes screen brightness, or null while nothing does (WP-3).
        /// Assigning a controller immediately pushes the saved brightness onto it, so a
        /// controller that comes up after <see cref="ApplyAll"/> still gets the boot value.
        /// </summary>
        public IBrightnessController BrightnessController
        {
            get => _brightnessController;
            set
            {
                _brightnessController = value;
                _brightnessController?.SetBrightness(Brightness);
            }
        }

        /// <summary>Deduplicated display resolutions the player can choose from, in UI order.</summary>
        public IReadOnlyList<ResolutionOption> ResolutionOptions
        {
            get
            {
                EnsureResolutions();
                return _resolutions;
            }
        }

        /// <summary>Labels for <see cref="ResolutionOptions"/>, ready for a cycle row.</summary>
        public string[] ResolutionLabels
        {
            get
            {
                EnsureResolutions();
                return _resolutionLabels;
            }
        }

        /// <summary>Fullscreen mode labels, in UI order.</summary>
        public static string[] FullscreenOptions => _fullscreenOptions;

        /// <summary>Unity fullscreen modes matching <see cref="FullscreenOptions"/> index for index.</summary>
        public static IReadOnlyList<FullScreenMode> FullscreenModes => _fullscreenModes;

        /// <summary>Selectable languages, in UI order.</summary>
        public static IReadOnlyList<LanguageOption> LanguageOptions => _languageOptions;

        /// <summary>Language display names, ready for a cycle row.</summary>
        public static string[] LanguageLabels
        {
            get
            {
                if (_languageLabels == null)
                {
                    _languageLabels = new string[_languageOptions.Length];
                    for (int i = 0; i < _languageOptions.Length; i++)
                        _languageLabels[i] = _languageOptions[i].DisplayName;
                }

                return _languageLabels;
            }
        }

        /// <summary>
        /// PlayerPrefs key carrying a channel's volume.
        /// </summary>
        /// <param name="channel">The audio channel to name.</param>
        /// <returns>For example <c>"Settings_Audio_Music"</c>.</returns>
        public static string AudioVolumeKey(AudioChannel channel) => AudioVolumeKeyPrefix + channel;

        // ─── Apply ──────────────────────────────────────────────────

        /// <summary>
        /// Read every saved setting and push it onto the system that realizes it: volumes onto
        /// <see cref="AudioManager"/>, quality and vertical sync onto <c>QualitySettings</c>,
        /// resolution and fullscreen mode onto <c>Screen</c>, brightness onto
        /// <see cref="BrightnessController"/>, and language onto <see cref="LocalizationSystem"/>.
        ///
        /// Call once at boot, after the MonoBehaviour systems are registered (this needs
        /// <see cref="AudioManager"/>) and before the first scene loads. A setting with no saved
        /// value is left exactly as the project authored it rather than being forced to a
        /// synthetic default — notably, an untouched install keeps the platform's own resolution
        /// and window mode.
        ///
        /// No <see cref="OnSettingsChangedEvent"/> is published here: this is the initial state,
        /// not a change, and nothing has had a chance to subscribe yet.
        /// </summary>
        public void ApplyAll()
        {
            ApplyAudio();
            ApplyVideo();
            ApplyGameplay();

            GameLogger.Log(LogCategory, "Saved settings applied.");
        }

        /// <summary>Push every saved channel volume onto the audio manager.</summary>
        private void ApplyAudio()
        {
            if (!ServiceLocator.TryGet<AudioManager>(out var audio) || audio == null)
            {
                GameLogger.LogWarning(LogCategory,
                    "No AudioManager registered — saved volumes were not applied.");
                return;
            }

            foreach (AudioChannel channel in System.Enum.GetValues(typeof(AudioChannel)))
                audio.SetVolume(channel, GetVolume(channel));
        }

        /// <summary>
        /// Push saved display settings onto Unity. Quality is applied first because
        /// <c>SetQualityLevel</c> overwrites <c>vSyncCount</c> from the quality level's own
        /// setting; applying vertical sync afterwards lets the player's choice win.
        /// </summary>
        private void ApplyVideo()
        {
            if (PlayerPrefs.HasKey(QualityKey))
                QualitySettings.SetQualityLevel(QualityIndex, true);

            if (PlayerPrefs.HasKey(VSyncKey))
                QualitySettings.vSyncCount = VSync ? 1 : 0;

            bool hasResolution = PlayerPrefs.HasKey(ResolutionKey);
            bool hasFullscreen = PlayerPrefs.HasKey(FullscreenKey);

            if (hasResolution || hasFullscreen)
            {
                var mode = _fullscreenModes[FullscreenIndex];

                if (hasResolution)
                {
                    EnsureResolutions();
                    int index = ResolutionIndex;
                    if (index >= 0 && index < _resolutions.Count)
                    {
                        var res = _resolutions[index];
                        Screen.SetResolution(res.Width, res.Height, mode);
                    }
                }
                else
                {
                    Screen.fullScreenMode = mode;
                }
            }

            _brightnessController?.SetBrightness(Brightness);
        }

        /// <summary>
        /// Push saved gameplay settings. Language is the only one with a consumer today;
        /// vibration and screen shake are stored and readable, and the gameplay layer that
        /// honours them does not exist yet (see WP-17).
        /// </summary>
        private void ApplyGameplay()
        {
            if (!PlayerPrefs.HasKey(LanguageKey)) return;

            if (!ServiceLocator.TryGet<LocalizationSystem>(out var localization)) return;

            string code = _languageOptions[LanguageIndex].Code;
            if (code != localization.CurrentLanguage)
                localization.SetLanguage(code);
        }

        // ─── Audio ──────────────────────────────────────────────────

        /// <summary>
        /// Saved volume for a channel, or the audio manager's authored value when the player
        /// has never set one.
        /// </summary>
        /// <param name="channel">The channel to read.</param>
        /// <returns>Linear volume, 0–1.</returns>
        public float GetVolume(AudioChannel channel)
        {
            float fallback = ServiceLocator.TryGet<AudioManager>(out var audio) && audio != null
                ? audio.GetVolume(channel)
                : 1f;
            return Mathf.Clamp01(PlayerPrefs.GetFloat(AudioVolumeKey(channel), fallback));
        }

        /// <summary>
        /// Set, persist and apply a channel volume. The decibel curve lives in
        /// <see cref="AudioManager.LinearToDecibels"/> and is not duplicated here.
        /// </summary>
        /// <param name="channel">The channel to adjust.</param>
        /// <param name="volume">Linear volume, clamped to 0–1.</param>
        public void SetVolume(AudioChannel channel, float volume)
        {
            volume = Mathf.Clamp01(volume);

            Write(AudioVolumeKey(channel), volume);

            if (ServiceLocator.TryGet<AudioManager>(out var audio) && audio != null)
                audio.SetVolume(channel, volume);

            Publish("Audio", channel.ToString(), volume);
        }

        // ─── Video ──────────────────────────────────────────────────

        /// <summary>
        /// Index into <see cref="ResolutionOptions"/>. With nothing saved, resolves to the entry
        /// matching the current screen resolution, or the first entry if none matches.
        /// </summary>
        public int ResolutionIndex
        {
            get
            {
                EnsureResolutions();

                int saved = PlayerPrefs.GetInt(ResolutionKey, -1);
                if (saved >= 0 && saved < _resolutions.Count) return saved;

                for (int i = 0; i < _resolutions.Count; i++)
                {
                    if (_resolutions[i].Width == Screen.currentResolution.width &&
                        _resolutions[i].Height == Screen.currentResolution.height)
                        return i;
                }

                return 0;
            }
        }

        /// <summary>
        /// Set, persist and apply the display resolution. The current fullscreen mode is carried
        /// through, because <c>Screen.SetResolution</c> requires one and passing the wrong value
        /// would silently drop the player out of fullscreen.
        /// </summary>
        /// <param name="index">Index into <see cref="ResolutionOptions"/>; out-of-range is ignored.</param>
        public void SetResolutionIndex(int index)
        {
            EnsureResolutions();
            if (index < 0 || index >= _resolutions.Count) return;

            var res = _resolutions[index];
            Screen.SetResolution(res.Width, res.Height, _fullscreenModes[FullscreenIndex]);

            Write(ResolutionKey, index);
            Publish("Video", "Resolution", index);
            GameLogger.Log(LogCategory, $"Resolution: {res.Width}×{res.Height}");
        }

        /// <summary>
        /// Index into <see cref="FullscreenOptions"/>. With nothing saved, resolves to whichever
        /// option matches the window's current mode.
        /// </summary>
        public int FullscreenIndex
        {
            get
            {
                int saved = PlayerPrefs.GetInt(FullscreenKey, -1);
                if (saved >= 0 && saved < _fullscreenModes.Length) return saved;

                return Screen.fullScreenMode switch
                {
                    FullScreenMode.ExclusiveFullScreen => 0,
                    FullScreenMode.FullScreenWindow => 1,
                    FullScreenMode.Windowed => 2,
                    _ => 1
                };
            }
        }

        /// <summary>
        /// Set, persist and apply the fullscreen mode.
        /// </summary>
        /// <param name="index">Index into <see cref="FullscreenOptions"/>; out-of-range is ignored.</param>
        public void SetFullscreenIndex(int index)
        {
            if (index < 0 || index >= _fullscreenModes.Length) return;

            Screen.fullScreenMode = _fullscreenModes[index];

            Write(FullscreenKey, index);
            Publish("Video", "Fullscreen", index);
            GameLogger.Log(LogCategory, $"Fullscreen: {_fullscreenOptions[index]}");
        }

        /// <summary>Vertical sync. With nothing saved, reports the project's current setting.</summary>
        public bool VSync => PlayerPrefs.GetInt(VSyncKey, QualitySettings.vSyncCount > 0 ? 1 : 0) == 1;

        /// <summary>
        /// Set, persist and apply vertical sync.
        /// </summary>
        /// <param name="enabled">True to sync presentation to the display's refresh rate.</param>
        public void SetVSync(bool enabled)
        {
            QualitySettings.vSyncCount = enabled ? 1 : 0;

            Write(VSyncKey, enabled ? 1 : 0);
            Publish("Video", "VSync", enabled ? 1f : 0f);
            GameLogger.Log(LogCategory, $"VSync: {(enabled ? "On" : "Off")}");
        }

        /// <summary>
        /// Index into <c>QualitySettings.names</c>. With nothing saved, reports the project's
        /// current quality level.
        /// </summary>
        public int QualityIndex =>
            Mathf.Clamp(PlayerPrefs.GetInt(QualityKey, QualitySettings.GetQualityLevel()),
                        0, QualitySettings.names.Length - 1);

        /// <summary>
        /// Set, persist and apply the quality level.
        /// </summary>
        /// <param name="index">Index into <c>QualitySettings.names</c>; out-of-range is ignored.</param>
        public void SetQualityIndex(int index)
        {
            if (index < 0 || index >= QualitySettings.names.Length) return;

            QualitySettings.SetQualityLevel(index, true);

            Write(QualityKey, index);
            Publish("Video", "Quality", index);
            GameLogger.Log(LogCategory, $"Quality: {QualitySettings.names[index]}");
        }

        /// <summary>Normalized screen brightness, 0–1, defaulting to <see cref="DefaultBrightness"/>.</summary>
        public float Brightness => Mathf.Clamp01(PlayerPrefs.GetFloat(BrightnessKey, DefaultBrightness));

        /// <summary>
        /// Set, persist and apply screen brightness. Applying means handing the value to
        /// <see cref="BrightnessController"/>; while that seam is empty (WP-3) the value is
        /// stored and restored but nothing renders it.
        /// </summary>
        /// <param name="brightness">Normalized brightness, clamped to 0–1.</param>
        public void SetBrightness(float brightness)
        {
            brightness = Mathf.Clamp01(brightness);

            Write(BrightnessKey, brightness);
            _brightnessController?.SetBrightness(brightness);
            Publish("Video", "Brightness", brightness);
        }

        // ─── Gameplay ───────────────────────────────────────────────

        /// <summary>Index into <see cref="LanguageOptions"/>, defaulting to the first entry.</summary>
        public int LanguageIndex =>
            Mathf.Clamp(PlayerPrefs.GetInt(LanguageKey, 0), 0, _languageOptions.Length - 1);

        /// <summary>
        /// Set, persist and apply the interface language.
        /// </summary>
        /// <param name="index">Index into <see cref="LanguageOptions"/>; out-of-range is ignored.</param>
        public void SetLanguageIndex(int index)
        {
            if (index < 0 || index >= _languageOptions.Length) return;

            var option = _languageOptions[index];

            if (ServiceLocator.TryGet<LocalizationSystem>(out var localization))
                localization.SetLanguage(option.Code);


            Write(LanguageKey, index);
            Publish("Gameplay", "Language", index);
            GameLogger.Log(LogCategory, $"Language: {option.DisplayName}");
        }

        /// <summary>Controller vibration, defaulting to <see cref="DefaultVibration"/>.</summary>
        public bool Vibration => PlayerPrefs.GetInt(VibrationKey, DefaultVibration ? 1 : 0) == 1;

        /// <summary>
        /// Set and persist controller vibration. Nothing consumes it yet — read it from here
        /// rather than re-reading PlayerPrefs when the gameplay layer lands.
        /// </summary>
        /// <param name="enabled">True to allow gamepad rumble.</param>
        public void SetVibration(bool enabled)
        {
            Write(VibrationKey, enabled ? 1 : 0);
            Publish("Gameplay", "Vibration", enabled ? 1f : 0f);
            GameLogger.Log(LogCategory, $"Controller Vibration: {(enabled ? "On" : "Off")}");
        }

        /// <summary>Screen shake, defaulting to <see cref="DefaultScreenshake"/>.</summary>
        public bool Screenshake => PlayerPrefs.GetInt(ScreenshakeKey, DefaultScreenshake ? 1 : 0) == 1;

        /// <summary>
        /// Set and persist screen shake. Nothing consumes it yet — read it from here rather than
        /// re-reading PlayerPrefs when the gameplay layer lands.
        /// </summary>
        /// <param name="enabled">True to allow camera shake effects.</param>
        public void SetScreenshake(bool enabled)
        {
            Write(ScreenshakeKey, enabled ? 1 : 0);
            Publish("Gameplay", "Screenshake", enabled ? 1f : 0f);
            GameLogger.Log(LogCategory, $"Screenshake: {(enabled ? "On" : "Off")}");
        }

        // ─── Persistence ────────────────────────────────────────────

        /// <summary>
        /// Commit pending changes to disk, if there are any. <c>PlayerPrefs.Save()</c> blocks on
        /// a synchronous write, so it is called here — on tab close, application pause and quit —
        /// and never from a setter. Flushing a clean service does nothing.
        /// </summary>
        public void Flush()
        {
            if (!_dirty) return;

            PlayerPrefs.Save();
            _dirty = false;
        }

        /// <summary>
        /// Delete every key this service owns and re-apply, returning the game to its authored
        /// defaults. Flushes immediately, since a reset is a deliberate, one-off action.
        /// </summary>
        public void ResetToDefaults()
        {
            foreach (AudioChannel channel in System.Enum.GetValues(typeof(AudioChannel)))
                PlayerPrefs.DeleteKey(AudioVolumeKey(channel));

            PlayerPrefs.DeleteKey(ResolutionKey);
            PlayerPrefs.DeleteKey(FullscreenKey);
            PlayerPrefs.DeleteKey(VSyncKey);
            PlayerPrefs.DeleteKey(QualityKey);
            PlayerPrefs.DeleteKey(BrightnessKey);
            PlayerPrefs.DeleteKey(LanguageKey);
            PlayerPrefs.DeleteKey(VibrationKey);
            PlayerPrefs.DeleteKey(ScreenshakeKey);

            PlayerPrefs.Save();
            _dirty = false;

            ApplyAll();
            GameLogger.Log(LogCategory, "Settings reset to defaults.");
        }

        // ─── Internals ──────────────────────────────────────────────

        /// <summary>Write a float pref and mark the service dirty. Never flushes.</summary>
        /// <param name="key">The pref key.</param>
        /// <param name="value">The value to store.</param>
        private void Write(string key, float value)
        {
            PlayerPrefs.SetFloat(key, value);
            _dirty = true;
        }

        /// <summary>Write an int pref and mark the service dirty. Never flushes.</summary>
        /// <param name="key">The pref key.</param>
        /// <param name="value">The value to store.</param>
        private void Write(string key, int value)
        {
            PlayerPrefs.SetInt(key, value);
            _dirty = true;
        }

        /// <summary>Publish a settings change so interested systems can react.</summary>
        /// <param name="category">Settings category, matching the UI tab.</param>
        /// <param name="key">The specific setting that changed.</param>
        /// <param name="value">The new value; booleans are published as 0 or 1.</param>
        private static void Publish(string category, string key, float value)
        {
            EventBus.Publish(new OnSettingsChangedEvent
            {
                Category = category,
                Key = key,
                Value = value
            });
        }

        /// <summary>
        /// Build the deduplicated resolution list once. Unity reports one entry per
        /// width × height × refresh-rate combination; the settings UI only offers dimensions, so
        /// duplicates collapse to the first occurrence. A platform that reports no resolutions
        /// (a headless batchmode run, for instance) still gets one entry — the current one — so
        /// index arithmetic elsewhere never has to special-case an empty list.
        /// </summary>
        private void EnsureResolutions()
        {
            if (_resolutionsBuilt) return;
            _resolutionsBuilt = true;

            var seen = new HashSet<long>();

            foreach (var res in Screen.resolutions)
            {
                if (seen.Add(((long)res.width << 32) | (uint)res.height))
                    _resolutions.Add(new ResolutionOption(res.width, res.height));
            }

            if (_resolutions.Count == 0)
            {
                var current = Screen.currentResolution;
                _resolutions.Add(new ResolutionOption(current.width, current.height));
            }

            _resolutionLabels = new string[_resolutions.Count];
            for (int i = 0; i < _resolutions.Count; i++)
                _resolutionLabels[i] = _resolutions[i].Label;
        }
    }
}
