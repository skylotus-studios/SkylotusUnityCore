using Skylotus.Core.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Skylotus
{
    /// <summary>
    /// Video/Display settings tab. Scroll list of rows — each row has a label and one control.
    ///
    /// Pure view code: the option lists, the saved indices, the pref keys and the application of
    /// each change to <c>Screen</c> / <c>QualitySettings</c> all live in
    /// <see cref="SettingsService"/>. This tab touches <c>PlayerPrefs</c> nowhere, and the values
    /// it shows are the same ones the service already applied at boot.
    ///
    /// <b>Brightness.</b> The slider's only job is to call <c>SettingsService.SetBrightness</c>.
    /// The service persists it and hands it to whatever is registered as its
    /// <c>BrightnessController</c> — <c>Skylotus.Core.Rendering.BrightnessController</c>, which
    /// drives a <c>ColorAdjustments.postExposure</c> override on the URP volume stack and installs
    /// itself at boot. This tab holds no reference to it and must not acquire one: the slider is
    /// the view, the service is the owner, and the controller is the implementation.
    ///
    /// <b>Expected hierarchy:</b>
    /// <code>
    ///   ScrollView → Viewport → Content (VerticalLayoutGroup)
    ///   ├── Row_Resolution   (SettingsCycleRow — ◀ value ▶)
    ///   ├── Row_Fullscreen   (SettingsCycleRow — ◀ value ▶)
    ///   ├── Row_Quality      (SettingsCycleRow — ◀ value ▶)
    ///   ├── Row_VSync        (ToggleSlider — slider styled as a switch)
    ///   └── Row_Brightness   (Slider)
    /// </code>
    /// </summary>
    [AddComponentMenu("Skylotus/UI/Settings Video Tab")]
    public class SettingsVideoTab : MonoBehaviour
    {
        [Header("Row Controls")]
        [SerializeField] private SettingsCycleRow _resolutionRow;
        [SerializeField] private SettingsCycleRow _fullscreenRow;
        [SerializeField] private SettingsCycleRow _qualityRow;
        [SerializeField] private ToggleSlider _vsyncToggle;
        [SerializeField] private Slider _brightnessSlider;
        [SerializeField] private TMP_Text _brightnessValueLabel;

        /// <summary>Owner of the pref keys, the option lists, and the write-through to Unity.</summary>
        private SettingsService _settings;

        // ─── Lifecycle ──────────────────────────────────────────────

        private void OnEnable()
        {
            if (!ServiceLocator.TryGet<SettingsService>(out _settings))
            {
                GameLogger.LogWarning("Settings",
                    "No SettingsService registered — video settings will not persist. " +
                    "Enter play mode from the boot scene.");
                return;
            }

            SetupResolutionRow();
            SetupFullscreenRow();
            SetupQualityRow();
            SetupVSyncToggle();
            SetupBrightnessSlider();
        }

        private void OnDisable()
        {
            if (_resolutionRow != null) _resolutionRow.OnValueChanged -= OnResolutionChanged;
            if (_fullscreenRow != null) _fullscreenRow.OnValueChanged -= OnFullscreenChanged;
            if (_qualityRow != null) _qualityRow.OnValueChanged -= OnQualityChanged;

            if (_vsyncToggle != null) _vsyncToggle.OnValueChanged -= OnVSyncChanged;
            _brightnessSlider?.onValueChanged.RemoveListener(OnBrightnessChanged);

            // Closing the tab (or the whole settings screen) is the moment to pay for the disk
            // write that every individual slider tick deliberately skipped.
            _settings?.Flush();
        }

        // ─── Setup ──────────────────────────────────────────────────

        private void SetupResolutionRow()
        {
            if (_resolutionRow == null) return;

            _resolutionRow.SetOptions(_settings.ResolutionLabels, _settings.ResolutionIndex);
            _resolutionRow.OnValueChanged += OnResolutionChanged;
        }

        private void SetupFullscreenRow()
        {
            if (_fullscreenRow == null) return;

            _fullscreenRow.SetOptions(SettingsService.FullscreenOptions, _settings.FullscreenIndex);
            _fullscreenRow.OnValueChanged += OnFullscreenChanged;
        }

        private void SetupQualityRow()
        {
            if (_qualityRow == null) return;

            _qualityRow.SetOptions(QualitySettings.names, _settings.QualityIndex);
            _qualityRow.OnValueChanged += OnQualityChanged;
        }

        private void SetupVSyncToggle()
        {
            if (_vsyncToggle == null) return;

            _vsyncToggle.SetWithoutNotify(_settings.VSync);
            _vsyncToggle.OnValueChanged += OnVSyncChanged;
        }

        private void SetupBrightnessSlider()
        {
            if (_brightnessSlider == null) return;

            float brightness = _settings.Brightness;
            _brightnessSlider.SetValueWithoutNotify(brightness);
            UpdateLabel(_brightnessValueLabel, brightness);

            _brightnessSlider.onValueChanged.AddListener(OnBrightnessChanged);
        }

        // ─── Callbacks ──────────────────────────────────────────────

        private void OnResolutionChanged(int index) => _settings?.SetResolutionIndex(index);

        private void OnFullscreenChanged(int index) => _settings?.SetFullscreenIndex(index);

        private void OnQualityChanged(int index) => _settings?.SetQualityIndex(index);

        private void OnVSyncChanged(bool value) => _settings?.SetVSync(value);

        private void OnBrightnessChanged(float value)
        {
            _settings?.SetBrightness(value);
            UpdateLabel(_brightnessValueLabel, value);
        }

        // ─── Helpers ────────────────────────────────────────────────

        private static void UpdateLabel(TMP_Text label, float value)
        {
            if (label != null)
                label.text = Mathf.RoundToInt(value * 100f).ToString();
        }
    }
}
