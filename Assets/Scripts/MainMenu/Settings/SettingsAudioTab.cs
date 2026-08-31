using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Skylotus
{
    /// <summary>
    /// Audio settings tab. Scroll list — each row has a label and a Slider (0–1).
    ///
    /// Pure view code: every value is read from and written to <see cref="SettingsService"/>,
    /// which owns the pref keys, applies each change to <see cref="AudioManager"/>, and decides
    /// when to flush to disk. This tab touches <c>PlayerPrefs</c> nowhere.
    ///
    /// <b>Expected hierarchy:</b>
    /// <code>
    ///   ScrollView → Viewport → Content (VerticalLayoutGroup)
    ///   ├── Row_Master   (Slider 0-1, optional TMP_Text value label)
    ///   ├── Row_Music    (Slider 0-1, optional TMP_Text value label)
    ///   ├── Row_SFX      (Slider 0-1, optional TMP_Text value label)
    ///   ├── Row_UI       (Slider 0-1, optional TMP_Text value label)
    ///   └── Row_Ambience (Slider 0-1, optional TMP_Text value label)
    /// </code>
    /// </summary>
    [AddComponentMenu("Skylotus/UI/Settings Audio Tab")]
    public class SettingsAudioTab : MonoBehaviour
    {
        [Header("Volume Sliders")]
        [SerializeField] private Slider _masterSlider;
        [SerializeField] private Slider _musicSlider;
        [SerializeField] private Slider _ambienceSlider;
        [SerializeField] private Slider _sfxSlider;
        [SerializeField] private Slider _uiSlider;

        [Header("Value Labels (optional)")]
        [SerializeField] private TMP_Text _masterLabel;
        [SerializeField] private TMP_Text _musicLabel;
        [SerializeField] private TMP_Text _ambienceLabel;
        [SerializeField] private TMP_Text _sfxLabel;
        [SerializeField] private TMP_Text _uiLabel;

        /// <summary>Owner of the pref keys, the defaults, and the write-through to AudioManager.</summary>
        private SettingsService _settings;

        // ─── Lifecycle ──────────────────────────────────────────────

        private void OnEnable()
        {
            if (!ServiceLocator.TryGet<SettingsService>(out _settings))
            {
                GameLogger.LogWarning("Settings",
                    "No SettingsService registered — audio sliders will not persist. " +
                    "Enter play mode from the boot scene.");
                return;
            }

            Load(AudioChannel.Master, _masterSlider, _masterLabel);
            Load(AudioChannel.Music, _musicSlider, _musicLabel);
            Load(AudioChannel.SFX, _sfxSlider, _sfxLabel);
            Load(AudioChannel.UI, _uiSlider, _uiLabel);
            Load(AudioChannel.Ambience, _ambienceSlider, _ambienceLabel);

            WireCallbacks();
        }

        private void OnDisable()
        {
            UnwireCallbacks();

            // Closing the tab (or the whole settings screen) is the moment to pay for the disk
            // write that every individual slider tick deliberately skipped.
            _settings?.Flush();
        }

        // ─── Load ───────────────────────────────────────────────────

        /// <summary>Show the saved volume for a channel without firing the change callback.</summary>
        /// <param name="channel">The channel this row controls.</param>
        /// <param name="slider">The row's slider, or null if the row is absent.</param>
        /// <param name="label">Optional percentage label for the row.</param>
        private void Load(AudioChannel channel, Slider slider, TMP_Text label)
        {
            if (slider == null) return;

            float saved = _settings.GetVolume(channel);

            slider.SetValueWithoutNotify(saved);
            UpdateLabel(label, saved);
        }

        // ─── Callbacks ──────────────────────────────────────────────

        private void WireCallbacks()
        {
            _masterSlider?.onValueChanged.AddListener(v => OnVolumeChanged(AudioChannel.Master, v, _masterLabel));
            _musicSlider?.onValueChanged.AddListener(v => OnVolumeChanged(AudioChannel.Music, v, _musicLabel));
            _sfxSlider?.onValueChanged.AddListener(v => OnVolumeChanged(AudioChannel.SFX, v, _sfxLabel));
            _uiSlider?.onValueChanged.AddListener(v => OnVolumeChanged(AudioChannel.UI, v, _uiLabel));
            _ambienceSlider?.onValueChanged.AddListener(v => OnVolumeChanged(AudioChannel.Ambience, v, _ambienceLabel));
        }

        private void UnwireCallbacks()
        {
            _masterSlider?.onValueChanged.RemoveAllListeners();
            _musicSlider?.onValueChanged.RemoveAllListeners();
            _sfxSlider?.onValueChanged.RemoveAllListeners();
            _uiSlider?.onValueChanged.RemoveAllListeners();
            _ambienceSlider?.onValueChanged.RemoveAllListeners();
        }

        private void OnVolumeChanged(AudioChannel channel, float value, TMP_Text label)
        {
            _settings?.SetVolume(channel, value);
            UpdateLabel(label, value);
        }

        // ─── Helpers ────────────────────────────────────────────────

        private static void UpdateLabel(TMP_Text label, float value)
        {
            if (label != null)
                label.text = Mathf.RoundToInt(value * 100f).ToString();
        }
    }
}
