using Skylotus;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Main menu screen with Play, Settings, and Quit buttons.
/// Lives in the MainMenu scene and registers itself with UIManager on Awake.
///
/// The first button is auto-selected on show so gamepad navigation works
/// immediately without requiring a mouse click.
/// </summary>
public class MainMenuScreen : UIScreen
{
    [Header("Buttons")]
    [Tooltip("Play / Start Game button.")]
    [SerializeField] private ButtonExtended _playButton;

    [Tooltip("Settings button.")]
    [SerializeField] private ButtonExtended _settingsButton;

    [Tooltip("Quit / Exit button.")]
    [SerializeField] private ButtonExtended _quitButton;

    [Header("Navigation")]
    [Tooltip("Scene to load when Play is pressed. Must be in Build Settings.")]
    [SerializeField] private string _gameplayScene = "Gameplay";

    [Header("Screens")]
    [Tooltip("Reference to the Settings screen so it can be registered before the hierarchy is deactivated.")]
    [SerializeField] private SettingsScreen _settingsScreen;

    private void Awake()
    {
        // Wire button events first (before registration deactivates us)
        _playButton?.OnClick.AddListener(OnPlay);
        _settingsButton?.OnClick.AddListener(OnSettings);
        _quitButton?.OnClick.AddListener(OnQuit);

        var ui = ServiceLocator.Get<UIManager>();
        if (ui != null)
        {
            // Register child / peer screens FIRST — RegisterScreen deactivates GameObjects,
            // so any screen that is a child of this canvas must be registered before this
            // screen is registered (which deactivates the entire hierarchy).
            if (_settingsScreen != null)
                _settingsScreen.Register(ui);

            ui.RegisterScreen("MainMenu", this);
            ui.ShowScreenImmediate("MainMenu");
        }
    }

    /// <summary>Called by UIManager when this screen becomes visible.</summary>
    public override void OnShow()
    {
        base.OnShow();

        // Transition game state to MainMenu
        ServiceLocator.Get<GameStateMachine>()?.TransitionTo(GameStateType.MainMenu);

        // Enable the UI input action map
        ServiceLocator.Get<InputManager>()?.SwitchActionMap("UI");
    }

    /// <summary>Called by UIManager when this screen becomes the top-most screen.</summary>
    public override void OnFocus()
    {
        base.OnFocus();

        // Auto-select the first button for immediate gamepad navigation
        if (_playButton != null && EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(_playButton.gameObject);
    }

    /// <summary>Prevent back from closing the main menu (there's nothing behind it).</summary>
    public override bool OnBackPressed() => false;

    // ─── Button Handlers ────────────────────────────────────────

    private void OnPlay()
    {
        GameLogger.Log("MainMenu", "Play pressed");

        var sceneManager = ServiceLocator.Get<SceneManager>();
        if (sceneManager != null)
            sceneManager.LoadScene(_gameplayScene);
        else
            GameLogger.LogError("MainMenu", "SkylotusSceneManager not found");
    }

    private void OnSettings()
    {
        GameLogger.Log("MainMenu", "Settings pressed");

        var ui = ServiceLocator.Get<UIManager>();
        ui?.ShowScreen("Settings");
    }

    private void OnQuit()
    {
        GameLogger.Log("MainMenu", "Quit pressed");

#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}