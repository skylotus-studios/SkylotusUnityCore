using Skylotus.Core.UI;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// Gameplay settings tab. Rows wrapped in <see cref="SettingsRow"/> for gamepad navigation.
    ///
    /// Pure view code: the language table, the pref keys and the write-through to
    /// <see cref="LocalizationSystem"/> live in <see cref="SettingsService"/>. This tab touches
    /// <c>PlayerPrefs</c> nowhere.
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

        /// <summary>Owner of the pref keys, the language table, and the write-through to Unity.</summary>
        private SettingsService _settings;

        // ─── Lifecycle ──────────────────────────────────────────────

        private void OnEnable()
        {
            if (!ServiceLocator.TryGet<SettingsService>(out _settings))
            {
                GameLogger.LogWarning("Settings",
                    "No SettingsService registered — gameplay settings will not persist. " +
                    "Enter play mode from the boot scene.");
                return;
            }

            SetupLanguageRow();
            SetupVibrationToggle();
            SetupScreenshakeToggle();
        }

        private void OnDisable()
        {
            if (_languageRow != null) _languageRow.OnValueChanged -= OnLanguageChanged;
            if (_vibrationToggle != null) _vibrationToggle.OnValueChanged -= OnVibrationChanged;
            if (_screenshakeToggle != null) _screenshakeToggle.OnValueChanged -= OnScreenshakeChanged;

            // Closing the tab (or the whole settings screen) is the moment to pay for the disk
            // write that every individual toggle deliberately skipped.
            _settings?.Flush();
        }

        // ─── Setup ──────────────────────────────────────────────────

        private void SetupLanguageRow()
        {
            if (_languageRow == null) return;

            _languageRow.SetOptions(SettingsService.LanguageLabels, _settings.LanguageIndex);
            _languageRow.OnValueChanged += OnLanguageChanged;
        }

        private void SetupVibrationToggle()
        {
            if (_vibrationToggle == null) return;

            _vibrationToggle.SetWithoutNotify(_settings.Vibration);
            _vibrationToggle.OnValueChanged += OnVibrationChanged;
        }

        private void SetupScreenshakeToggle()
        {
            if (_screenshakeToggle == null) return;

            _screenshakeToggle.SetWithoutNotify(_settings.Screenshake);
            _screenshakeToggle.OnValueChanged += OnScreenshakeChanged;
        }

        // ─── Callbacks ──────────────────────────────────────────────

        private void OnLanguageChanged(int index) => _settings?.SetLanguageIndex(index);

        private void OnVibrationChanged(bool value) => _settings?.SetVibration(value);

        private void OnScreenshakeChanged(bool value) => _settings?.SetScreenshake(value);
    }
}
