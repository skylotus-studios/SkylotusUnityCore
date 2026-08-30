using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Skylotus
{
    /// <summary>
    /// Audio settings tab. Scroll list — each row has a label and a Slider (0–1).
    /// Values are read from and written to <see cref="AudioManager"/> and persist via PlayerPrefs.
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

        // ─── Prefs Keys ─────────────────────────────────────────────

        private const string PrefPrefix = "Settings_Audio_";

        private AudioManager _audio;

        // ─── Lifecycle ──────────────────────────────────────────────

        private void OnEnable()
        {
            _audio = ServiceLocator.Get<AudioManager>();

            LoadAndApply(AudioChannel.Master, _masterSlider, _masterLabel);
            LoadAndApply(AudioChannel.Music, _musicSlider, _musicLabel);
            LoadAndApply(AudioChannel.SFX, _sfxSlider, _sfxLabel);
            LoadAndApply(AudioChannel.UI, _uiSlider, _uiLabel);
            LoadAndApply(AudioChannel.Ambience, _ambienceSlider, _ambienceLabel);

            WireCallbacks();
        }

        private void OnDisable()
        {
            UnwireCallbacks();
        }

        // ─── Load ───────────────────────────────────────────────────

        private void LoadAndApply(AudioChannel channel, Slider slider, TMP_Text label)
        {
            if (slider == null) return;

            float defaultVol = _audio != null ? _audio.GetVolume(channel) : 1f;
            float saved = PlayerPrefs.GetFloat(PrefPrefix + channel, defaultVol);

            slider.SetValueWithoutNotify(saved);
            UpdateLabel(label, saved);

            _audio?.SetVolume(channel, saved);
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
            _audio?.SetVolume(channel, value);
            UpdateLabel(label, value);

            PlayerPrefs.SetFloat(PrefPrefix + channel, value);
            PlayerPrefs.Save();
        }

        // ─── Helpers ────────────────────────────────────────────────

        private static void UpdateLabel(TMP_Text label, float value)
        {
            if (label != null)
                label.text = Mathf.RoundToInt(value * 100f).ToString();
        }
    }
}