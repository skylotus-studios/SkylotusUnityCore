using System.Collections.Generic;
using Skylotus.Core.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Skylotus
{
    /// <summary>
    /// Video/Display settings tab. Scroll list of rows — each row has a label and one control.
    /// All changes apply immediately and are saved to PlayerPrefs on change.
    /// No Apply button; the global Save &amp; Exit on the settings screen commits the session.
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

        // ─── Prefs Keys ─────────────────────────────────────────────

        private const string PrefResolution = "Settings_Resolution";
        private const string PrefFullscreen = "Settings_Fullscreen";
        private const string PrefVSync = "Settings_VSync";
        private const string PrefQuality = "Settings_Quality";
        private const string PrefBrightness = "Settings_Brightness";

        // ─── Data ────────────────────────────────────────────────────

        private readonly List<Resolution> _resolutions = new();

        private static readonly string[] FullscreenOptions =
        {
            "Fullscreen",
            "Borderless",
            "Windowed"
        };

        private static readonly FullScreenMode[] FullscreenModes =
        {
            FullScreenMode.ExclusiveFullScreen,
            FullScreenMode.FullScreenWindow,
            FullScreenMode.Windowed
        };

        // ─── Lifecycle ──────────────────────────────────────────────

        private void OnEnable()
        {
            SetupResolutionRow();
            SetupFullscreenRow();
            SetupQualityRow();
            LoadAndApplyVSync();
            LoadAndApplyBrightness();

            if (_vsyncToggle != null) _vsyncToggle.OnValueChanged += OnVSyncChanged;
            _brightnessSlider?.onValueChanged.AddListener(OnBrightnessChanged);
        }

        private void OnDisable()
        {
            if (_resolutionRow != null) _resolutionRow.OnValueChanged -= OnResolutionChanged;
            if (_fullscreenRow != null) _fullscreenRow.OnValueChanged -= OnFullscreenChanged;
            if (_qualityRow != null) _qualityRow.OnValueChanged -= OnQualityChanged;

            if (_vsyncToggle != null) _vsyncToggle.OnValueChanged -= OnVSyncChanged;
            _brightnessSlider?.onValueChanged.RemoveListener(OnBrightnessChanged);
        }

        // ─── Setup ──────────────────────────────────────────────────

        private void SetupResolutionRow()
        {
            if (_resolutionRow == null) return;

            _resolutions.Clear();
            var labels = new List<string>();
            var seen = new HashSet<string>();

            foreach (var res in Screen.resolutions)
            {
                string key = $"{res.width}x{res.height}";
                if (seen.Add(key))
                {
                    _resolutions.Add(res);
                    labels.Add($"{res.width} × {res.height}");
                }
            }

            int saved = PlayerPrefs.GetInt(PrefResolution, -1);
            if (saved < 0 || saved >= _resolutions.Count)
            {
                saved = 0;
                for (int i = 0; i < _resolutions.Count; i++)
                {
                    if (_resolutions[i].width == Screen.currentResolution.width &&
                        _resolutions[i].height == Screen.currentResolution.height)
                    {
                        saved = i;
                        break;
                    }
                }
            }

            _resolutionRow.SetOptions(labels.ToArray(), saved);
            _resolutionRow.OnValueChanged += OnResolutionChanged;
        }

        private void SetupFullscreenRow()
        {
            if (_fullscreenRow == null) return;

            int saved = PlayerPrefs.GetInt(PrefFullscreen, -1);
            if (saved < 0 || saved >= FullscreenOptions.Length)
            {
                saved = Screen.fullScreenMode switch
                {
                    FullScreenMode.ExclusiveFullScreen => 0,
                    FullScreenMode.FullScreenWindow => 1,
                    FullScreenMode.Windowed => 2,
                    _ => 1
                };
            }

            _fullscreenRow.SetOptions(FullscreenOptions, saved);
            _fullscreenRow.OnValueChanged += OnFullscreenChanged;
        }

        private void SetupQualityRow()
        {
            if (_qualityRow == null) return;

            int saved = PlayerPrefs.GetInt(PrefQuality, QualitySettings.GetQualityLevel());
            saved = Mathf.Clamp(saved, 0, QualitySettings.names.Length - 1);

            _qualityRow.SetOptions(QualitySettings.names, saved);
            _qualityRow.OnValueChanged += OnQualityChanged;
        }

        private void LoadAndApplyVSync()
        {
            if (_vsyncToggle == null) return;
            bool vsync = PlayerPrefs.GetInt(PrefVSync, QualitySettings.vSyncCount > 0 ? 1 : 0) == 1;
            _vsyncToggle.SetWithoutNotify(vsync);
            QualitySettings.vSyncCount = vsync ? 1 : 0;
        }

        private void LoadAndApplyBrightness()
        {
            if (_brightnessSlider == null) return;
            float brightness = PlayerPrefs.GetFloat(PrefBrightness, 1f);
            _brightnessSlider.SetValueWithoutNotify(brightness);
            UpdateLabel(_brightnessValueLabel, brightness);
        }

        // ─── Callbacks ──────────────────────────────────────────────

        private void OnResolutionChanged(int index)
        {
            if (index < 0 || index >= _resolutions.Count) return;
            var res = _resolutions[index];
            var mode = FullscreenModes[Mathf.Clamp(_fullscreenRow != null ? _fullscreenRow.Index : 1, 0, FullscreenModes.Length - 1)];
            Screen.SetResolution(res.width, res.height, mode);
            PlayerPrefs.SetInt(PrefResolution, index);
            PlayerPrefs.Save();
            GameLogger.Log("Settings", $"Resolution: {res.width}×{res.height}");
        }

        private void OnFullscreenChanged(int index)
        {
            if (index < 0 || index >= FullscreenModes.Length) return;
            Screen.fullScreenMode = FullscreenModes[index];
            PlayerPrefs.SetInt(PrefFullscreen, index);
            PlayerPrefs.Save();
            GameLogger.Log("Settings", $"Fullscreen: {FullscreenOptions[index]}");
        }

        private void OnQualityChanged(int index)
        {
            QualitySettings.SetQualityLevel(index, true);
            PlayerPrefs.SetInt(PrefQuality, index);
            PlayerPrefs.Save();
            GameLogger.Log("Settings", $"Quality: {QualitySettings.names[index]}");
        }

        private void OnVSyncChanged(bool value)
        {
            QualitySettings.vSyncCount = value ? 1 : 0;
            PlayerPrefs.SetInt(PrefVSync, value ? 1 : 0);
            PlayerPrefs.Save();
            GameLogger.Log("Settings", $"VSync: {(value ? "On" : "Off")}");
        }

        private void OnBrightnessChanged(float value)
        {
            PlayerPrefs.SetFloat(PrefBrightness, value);
            PlayerPrefs.Save();
            EventBus.Publish(new OnSettingsChangedEvent
            {
                Category = "Video",
                Key = "Brightness",
                Value = value
            });
            UpdateLabel(_brightnessValueLabel, value);
        }

        private static void UpdateLabel(TMP_Text label, float value)
        {
            if (label != null)
                label.text = Mathf.RoundToInt(value * 100f).ToString();
        }
    }
}