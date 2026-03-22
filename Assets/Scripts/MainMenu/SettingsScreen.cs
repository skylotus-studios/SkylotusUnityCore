using Skylotus;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Placeholder settings screen. Currently only contains a Back button.
/// Extend with volume sliders, resolution dropdowns, keybind UI, etc.
/// </summary>
public class SettingsScreen : UIScreen
{
    [Header("Buttons")]
    [Tooltip("Back / Close button that returns to the previous screen.")]
    [SerializeField] private ButtonExtended _backButton;

    private void Awake()
    {
        var ui = ServiceLocator.Get<UIManager>();
        ui?.RegisterScreen("Settings", this);

        _backButton?.OnClick.AddListener(OnBack);
    }

    public override void OnFocus()
    {
        base.OnFocus();

        // Auto-select back button for gamepad
        if (_backButton != null && EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(_backButton.gameObject);
    }

    private void OnBack()
    {
        ServiceLocator.Get<UIManager>()?.GoBack();
    }
}