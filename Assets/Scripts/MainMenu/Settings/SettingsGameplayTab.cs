using Skylotus.Core.UI;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// Gameplay settings tab. Rows wrapped in <see cref="SettingsRow"/> for gamepad navigation.
    ///
    /// <b>Expected hierarchy:</b>
    /// <code>
    ///   ScrollView → Viewport → Content (VerticalLayoutGroup)
    ///   ├── Row_Language           (SettingsRow → SettingsCycleRow)
    ///   ├── Row_Vibration          (SettingsRow → ToggleSlider)
    ///   └── Row_Screenshake        (SettingsRow → ToggleSlider)
    /// </code>
    /// </summary>
    [AddComponentMenu("Skylotus/UI/Settings Gameplay Tab")]
    public class SettingsGameplayTab : MonoBehaviour
    {
        [Header("Row Controls")]
        [SerializeField] private SettingsCycleRow _languageRow;
        [SerializeField] private ToggleSlider _vibrationToggle;
        [SerializeField] private ToggleSlider _screenshakeToggle;

        // ─── Prefs Keys ─────────────────────────────────────────────

        private const string PrefLanguage = "Settings_Language";
        private const string PrefVibration = "Settings_Vibration";
        private const string PrefScreenshake = "Settings_Screenshake";

        // ─── Data ────────────────────────────────────────────────────

        private static readonly string[] LanguageOptions =
        {
            "English",
            "Spanish",
            "French",
            "German",
            "Portuguese",
            "Japanese",
            "Korean",
            "Chinese (Simplified)"
        };

        // ─── Lifecycle ──────────────────────────────────────────────

        private void OnEnable()
        {
            SetupLanguageRow();
            LoadAndApplyVibration();
            LoadAndApplyScreenshake();

            if (_vibrationToggle != null) _vibrationToggle.OnValueChanged += OnVibrationChanged;
            if (_screenshakeToggle != null) _screenshakeToggle.OnValueChanged += OnScreenshakeChanged;
        }

        private void OnDisable()
        {
            if (_languageRow != null) _languageRow.OnValueChanged -= OnLanguageChanged;
            if (_vibrationToggle != null) _vibrationToggle.OnValueChanged -= OnVibrationChanged;
            if (_screenshakeToggle != null) _screenshakeToggle.OnValueChanged -= OnScreenshakeChanged;
        }

        // ─── Setup ──────────────────────────────────────────────────

        private void SetupLanguageRow()
        {
            if (_languageRow == null) return;

            int saved = PlayerPrefs.GetInt(PrefLanguage, 0);
            saved = Mathf.Clamp(saved, 0, LanguageOptions.Length - 1);

            _languageRow.SetOptions(LanguageOptions, saved);
            _languageRow.OnValueChanged += OnLanguageChanged;
        }

        private void LoadAndApplyVibration()
        {
            if (_vibrationToggle == null) return;
            bool on = PlayerPrefs.GetInt(PrefVibration, 1) == 1;
            _vibrationToggle.SetWithoutNotify(on);
        }

        private void LoadAndApplyScreenshake()
        {
            if (_screenshakeToggle == null) return;
            bool on = PlayerPrefs.GetInt(PrefScreenshake, 1) == 1;
            _screenshakeToggle.SetWithoutNotify(on);
        }

        // ─── Callbacks ──────────────────────────────────────────────

        private void OnLanguageChanged(int index)
        {
            PlayerPrefs.SetInt(PrefLanguage, index);
            PlayerPrefs.Save();
            EventBus.Publish(new OnSettingsChangedEvent
            {
                Category = "Gameplay",
                Key = "Language",
                Value = index
            });
            GameLogger.Log("Settings", $"Language: {LanguageOptions[index]}");
        }

        private void OnVibrationChanged(bool value)
        {
            PlayerPrefs.SetInt(PrefVibration, value ? 1 : 0);
            PlayerPrefs.Save();
            EventBus.Publish(new OnSettingsChangedEvent
            {
                Category = "Gameplay",
                Key = "Vibration",
                Value = value ? 1f : 0f
            });
            GameLogger.Log("Settings", $"Controller Vibration: {(value ? "On" : "Off")}");
        }

        private void OnScreenshakeChanged(bool value)
        {
            PlayerPrefs.SetInt(PrefScreenshake, value ? 1 : 0);
            PlayerPrefs.Save();
            EventBus.Publish(new OnSettingsChangedEvent
            {
                Category = "Gameplay",
                Key = "Screenshake",
                Value = value ? 1f : 0f
            });
            GameLogger.Log("Settings", $"Screenshake: {(value ? "On" : "Off")}");
        }
    }
}
