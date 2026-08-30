using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Skylotus
{
    /// <summary>
    /// Manages a horizontal row of tab header buttons and their associated content panels.
    /// Supports LB/RB (TabLeft/TabRight) switching via InputManager, as well as mouse clicks
    /// on the tab headers. Only one tab is visible at a time.
    ///
    /// <b>Unity Hierarchy (expected):</b>
    /// <code>
    ///   SettingsTabGroup (this component)
    ///   ├── TabHeaders (horizontal layout)
    ///   │   ├── Tab_Video   (ButtonExtended)
    ///   │   ├── Tab_Audio   (ButtonExtended)
    ///   │   └── Tab_Controls(ButtonExtended)
    ///   └── TabContents
    ///       ├── Content_Video   (GameObject)
    ///       ├── Content_Audio   (GameObject)
    ///       └── Content_Controls(GameObject)
    /// </code>
    ///
    /// Tab headers and content panels are matched by array index.
    /// </summary>
    [AddComponentMenu("Skylotus/UI/Settings Tab Group")]
    public class SettingsTabGroup : MonoBehaviour
    {
        [Header("Tabs")]
        [Tooltip("Tab header buttons — order must match Content Panels.")]
        [SerializeField] private SettingsTabButton[] _tabHeaders;

        [Tooltip("Content panels — one per tab, matched by index to Tab Headers.")]
        [SerializeField] private GameObject[] _contentPanels;

        [Header("First Selectables")]
        [Tooltip("Optional: first selectable in each content panel for gamepad auto-focus. Matched by index.")]
        [SerializeField] private Selectable[] _firstSelectables;

        /// <summary>Currently active tab index.</summary>
        private int _currentIndex;

        /// <summary>Cached input actions.</summary>
        private InputAction _tabLeftAction;
        private InputAction _tabRightAction;

        /// <summary>Raised when the active tab changes. Arg is the new index.</summary>
        public event Action<int> OnTabChanged;

        /// <summary>The currently active tab index.</summary>
        public int CurrentIndex => _currentIndex;

        // ─── Lifecycle ──────────────────────────────────────────────

        private void OnEnable()
        {
            // Wire header button clicks
            for (int i = 0; i < _tabHeaders.Length; i++)
            {
                int index = i; // capture for closure
                _tabHeaders[i]?.OnClick.AddListener(() => SelectTab(index));
            }

            // Bind bumper / keyboard tab actions
            if (ServiceLocator.TryGet<InputManager>(out var input))
            {
                _tabLeftAction = input.GetAction("TabLeft");
                _tabRightAction = input.GetAction("TabRight");

                if (_tabLeftAction != null) _tabLeftAction.performed += OnTabLeft;
                if (_tabRightAction != null) _tabRightAction.performed += OnTabRight;
            }

            // Show the first tab
            SelectTab(0, true);
        }

        private void OnDisable()
        {
            // Unwire header button clicks
            for (int i = 0; i < _tabHeaders.Length; i++)
                _tabHeaders[i]?.OnClick.RemoveAllListeners();

            if (_tabLeftAction != null) _tabLeftAction.performed -= OnTabLeft;
            if (_tabRightAction != null) _tabRightAction.performed -= OnTabRight;
        }

        // ─── Public API ─────────────────────────────────────────────

        /// <summary>
        /// Switch to a tab by index. Deactivates the old content panel and activates the new one.
        /// </summary>
        /// <param name="index">Zero-based tab index.</param>
        /// <param name="instant">If true, skip any transition (used on initial show).</param>
        public void SelectTab(int index, bool instant = false)
        {
            if (_contentPanels.Length == 0 || _tabHeaders.Length == 0) return;
            index = Mathf.Clamp(index, 0, _contentPanels.Length - 1);

            // Deactivate all content panels, activate the target
            for (int i = 0; i < _contentPanels.Length; i++)
            {
                if (_contentPanels[i] != null)
                    _contentPanels[i].SetActive(i == index);
            }

            // Tell each tab button whether it is the active one
            for (int i = 0; i < _tabHeaders.Length; i++)
                _tabHeaders[i]?.SetTabActive(i == index);

            _currentIndex = index;
            OnTabChanged?.Invoke(index);

            // Auto-select the first selectable in the new tab for gamepad navigation
            AutoSelectFirstInTab(index);
        }

        /// <summary>Move to the next tab (wraps around).</summary>
        public void NextTab() => SelectTab((_currentIndex + 1) % _contentPanels.Length);

        /// <summary>Move to the previous tab (wraps around).</summary>
        public void PreviousTab() => SelectTab((_currentIndex - 1 + _contentPanels.Length) % _contentPanels.Length);

        // ─── Input Callbacks ────────────────────────────────────────

        private void OnTabLeft(InputAction.CallbackContext ctx) => PreviousTab();
        private void OnTabRight(InputAction.CallbackContext ctx) => NextTab();

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>
        /// Auto-select the first selectable in the active tab for gamepad.
        /// Uses the explicit _firstSelectables array if set, otherwise searches children.
        /// </summary>
        private void AutoSelectFirstInTab(int index)
        {
            if (EventSystem.current == null) return;

            // Only auto-select in gamepad mode
            if (!ServiceLocator.TryGet<InputManager>(out var input)) return;
            if (input.CurrentDevice != InputDeviceType.Gamepad) return;

            Selectable target = null;

            if (_firstSelectables != null && index < _firstSelectables.Length && _firstSelectables[index] != null)
            {
                target = _firstSelectables[index];
            }
            else if (index < _contentPanels.Length && _contentPanels[index] != null)
            {
                target = _contentPanels[index].GetComponentInChildren<Selectable>();
            }

            if (target != null && target.IsInteractable())
                EventSystem.current.SetSelectedGameObject(target.gameObject);
        }
    }
}