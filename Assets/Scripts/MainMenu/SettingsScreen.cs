using Skylotus;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Settings screen with tabbed navigation (Video, Audio, Controls).
/// Uses <see cref="SettingsTabGroup"/> for LB/RB tab switching and a Back button
/// to return to the previous screen.
///
/// <b>Expected Unity Hierarchy:</b>
/// <code>
///   SettingsScreen (UIScreen + this component + CanvasGroup)
///   ├── Header
///   │   ├── Title_Text ("Settings")
///   │   └── TabHint_Text ("[LB] / [RB]" — optional prompt)
///   ├── TabGroup (SettingsTabGroup component)
///   │   ├── TabHeaders (Horizontal Layout Group)
///   │   │   ├── Tab_Video    (SettingsTabButton, label "Video")
///   │   │   ├── Tab_Audio    (SettingsTabButton, label "Audio")
///   │   │   └── Tab_Controls (SettingsTabButton, label "Controls")
///   │   └── TabContents
///   │       ├── Content_Video    (ScrollView → Content, SettingsVideoTab)
///   │       │   ├── Row_Resolution   (SettingsCycleRow — ◀ value ▶)
///   │       │   ├── Row_Fullscreen   (SettingsCycleRow — ◀ value ▶)
///   │       │   ├── Row_Quality      (SettingsCycleRow — ◀ value ▶)
///   │       │   ├── Row_VSync        (ToggleSlider — slider styled as a switch)
///   │       │   └── Row_Brightness   (Slider)
///   │       ├── Content_Audio    (ScrollView → Content, SettingsAudioTab)
///   │       │   ├── Row_Master   (Slider + optional % TMP_Text)
///   │       │   ├── Row_Music    (Slider + optional % TMP_Text)
///   │       │   ├── Row_SFX      (Slider + optional % TMP_Text)
///   │       │   ├── Row_UI       (Slider + optional % TMP_Text)
///   │       │   └── Row_Ambience (Slider + optional % TMP_Text)
///   │       └── Content_Controls (ScrollView → Content, SettingsControlsTab)
///   │           ├── BindingRows_Container (Vertical Layout — rows spawned here)
///   │           ├── ResetAllButton (ButtonExtended)
///   │           └── RebindOverlay (hidden panel + prompt text)
///   └── Footer
///       ├── SaveButton  (ButtonExtended — writes PlayerPrefs and shows confirmation)
///       └── ExitButton  (ButtonExtended — GoBack; returns to game or main menu)
/// </code>
/// </summary>
public class SettingsScreen : UIScreen
{
    [Header("Buttons")]
    [Tooltip("Saves all settings (PlayerPrefs.Save) and shows a brief confirmation.")]
    [SerializeField] private ButtonExtended _saveButton;

    [Tooltip("Closes the settings screen. Returns to game if in-game, or main menu if in menu.")]
    [SerializeField] private ButtonExtended _exitButton;

    [Header("Tabs")]
    [Tooltip("The tab group managing Video / Audio / Controls tabs.")]
    [SerializeField] private SettingsTabGroup _tabGroup;

    /// <summary>
    /// Called by the parent screen (MainMenuScreen) to register with UIManager
    /// before the shared canvas hierarchy is deactivated.
    /// </summary>
    public void Register(UIManager ui)
    {
        ui.RegisterScreen("Settings", this);
        _saveButton?.OnClick.AddListener(OnSave);
        _exitButton?.OnClick.AddListener(OnExit);
    }

    public override void OnFocus()
    {
        base.OnFocus();

        // Show the first tab when the settings screen gains focus
        if (_tabGroup != null)
            _tabGroup.SelectTab(0, true);

        // Auto-select the save button for gamepad when the screen opens
        var input = ServiceLocator.Get<InputManager>();
        if (input != null && input.CurrentDevice == InputDeviceType.Gamepad)
        {
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == null)
            {
                if (_saveButton != null)
                    EventSystem.current.SetSelectedGameObject(_saveButton.gameObject);
            }
        }
    }

    private void OnSave()
    {
        PlayerPrefs.Save();
        GameLogger.Log("Settings", "Settings saved.");
        // Optionally publish an event so other systems can react (e.g. show a toast)
        EventBus.Publish(new OnSettingsChangedEvent { Category = "All", Key = "Save", Value = 1f });
    }

    private void OnExit()
    {
        ServiceLocator.Get<UIManager>()?.GoBack();
    }
}