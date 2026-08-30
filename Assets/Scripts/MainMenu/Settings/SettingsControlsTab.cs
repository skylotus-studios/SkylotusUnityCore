using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// Controls/keybinding settings tab. Scroll list — each row has an action-name label
    /// and a button showing the current binding; clicking it starts interactive rebinding.
    /// Rows are spawned at runtime from <see cref="_bindingRowPrefab"/>.
    ///
    /// <b>Expected hierarchy:</b>
    /// <code>
    ///   ScrollView → Viewport → Content (VerticalLayoutGroup)
    ///   ├── BindingRows_Container  (Vertical Layout Group — rows spawned here)
    ///   ├── ResetAll_Button        (ButtonExtended)
    ///   └── RebindOverlay          (hidden by default — shown during rebinding)
    ///       └── RebindPromptText   (TMP_Text — "Press a key…")
    /// </code>
    ///
    /// Binding row prefab structure:
    ///   ├── ActionName_Text  (TMP_Text — action display name)
    ///   └── RebindBtn        (ButtonExtended — shows current binding, triggers rebind)
    /// </summary>
    [AddComponentMenu("Skylotus/UI/Settings Controls Tab")]
    public class SettingsControlsTab : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Container to parent binding row instances into.")]
        [SerializeField] private Transform _rowContainer;

        [Tooltip("Prefab for a single binding row. Must have a TMP_Text (action name) and ButtonExtended (binding display / rebind trigger).")]
        [SerializeField] private GameObject _bindingRowPrefab;

        [Tooltip("Button to reset all bindings to defaults.")]
        [SerializeField] private ButtonExtended _resetAllButton;

        [Header("Rebind Overlay")]
        [Tooltip("Panel shown while waiting for the player to press a key.")]
        [SerializeField] private GameObject _rebindOverlay;

        [Tooltip("Text displayed in the rebind overlay (e.g. 'Press a key…').")]
        [SerializeField] private TMP_Text _rebindPromptText;

        [Header("Configuration")]
        [Tooltip("Action map name to display bindings for.")]
        [SerializeField] private string _actionMapName = "Player";

        [Tooltip("Actions to exclude from the rebind list (e.g. 'Look' for mouse delta).")]
        [SerializeField] private string[] _excludedActions = { "Look" };

        // ─── Internal ───────────────────────────────────────────────

        private InputManager _input;
        private readonly List<BindingRowData> _rows = new();

        private struct BindingRowData
        {
            public string ActionName;
            public int BindingIndex;
            public TMP_Text BindingLabel;
        }

        // ─── Lifecycle ──────────────────────────────────────────────

        private void OnEnable()
        {
            _input = ServiceLocator.Get<InputManager>();
            if (_input == null || _input.Actions == null)
            {
                GameLogger.LogWarning("Settings", "InputManager not available — controls tab disabled.");
                return;
            }

            BuildRows();

            if (_resetAllButton != null)
                _resetAllButton.OnClick.AddListener(OnResetAll);

            if (_rebindOverlay != null)
                _rebindOverlay.SetActive(false);
        }

        private void OnDisable()
        {
            if (_resetAllButton != null)
                _resetAllButton.OnClick.RemoveListener(OnResetAll);

            ClearRows();
        }

        // ─── Row Building ───────────────────────────────────────────

        private void BuildRows()
        {
            ClearRows();

            if (_input?.Actions == null || _bindingRowPrefab == null || _rowContainer == null) return;

            var map = _input.Actions.FindActionMap(_actionMapName);
            if (map == null)
            {
                GameLogger.LogWarning("Settings", $"Action map '{_actionMapName}' not found.");
                return;
            }

            var excludeSet = new HashSet<string>(_excludedActions ?? System.Array.Empty<string>());

            foreach (var action in map.actions)
            {
                if (excludeSet.Contains(action.name)) continue;

                // Find the first non-composite binding (the primary binding)
                for (int i = 0; i < action.bindings.Count; i++)
                {
                    var binding = action.bindings[i];
                    if (binding.isComposite) continue; // skip composite parents
                    if (binding.isPartOfComposite) continue; // skip composite parts — rebind the whole composite or skip

                    CreateRow(action.name, i);
                    break; // only show one binding per action
                }
            }
        }

        private void CreateRow(string actionName, int bindingIndex)
        {
            var go = Instantiate(_bindingRowPrefab, _rowContainer);
            go.SetActive(true);

            // Find the action name label (first TMP_Text child)
            var texts = go.GetComponentsInChildren<TMP_Text>();
            if (texts.Length >= 1)
                texts[0].text = FormatActionName(actionName);

            // Find the binding button (ButtonExtended)
            var button = go.GetComponentInChildren<ButtonExtended>();
            TMP_Text bindingLabel = null;

            if (button != null)
            {
                // The button's own label shows the current binding
                bindingLabel = button.GetComponentInChildren<TMP_Text>();
                if (bindingLabel != null && texts.Length >= 1 && bindingLabel == texts[0])
                {
                    // If the button label IS the action name label, use the second text
                    bindingLabel = texts.Length >= 2 ? texts[1] : null;
                }

                var display = _input.GetBindingDisplayString(actionName, bindingIndex);
                if (bindingLabel != null) bindingLabel.text = display;

                // Wire the button to start rebinding
                string capturedAction = actionName;
                int capturedIndex = bindingIndex;
                TMP_Text capturedLabel = bindingLabel;
                button.OnClick.AddListener(() => StartRebind(capturedAction, capturedIndex, capturedLabel));
            }

            _rows.Add(new BindingRowData
            {
                ActionName = actionName,
                BindingIndex = bindingIndex,
                BindingLabel = bindingLabel
            });
        }

        private void ClearRows()
        {
            if (_rowContainer != null)
            {
                for (int i = _rowContainer.childCount - 1; i >= 0; i--)
                    Destroy(_rowContainer.GetChild(i).gameObject);
            }
            _rows.Clear();
        }

        // ─── Rebinding ─────────────────────────────────────────────

        private void StartRebind(string actionName, int bindingIndex, TMP_Text label)
        {
            if (_rebindOverlay != null)
                _rebindOverlay.SetActive(true);

            if (_rebindPromptText != null)
                _rebindPromptText.text = $"Press a key for {FormatActionName(actionName)}…";

            _input.StartRebind(actionName, bindingIndex,
                onComplete: () =>
                {
                    if (_rebindOverlay != null) _rebindOverlay.SetActive(false);
                    if (label != null) label.text = _input.GetBindingDisplayString(actionName, bindingIndex);
                    GameLogger.Log("Settings", $"Rebound {actionName}");
                },
                onCanceled: () =>
                {
                    if (_rebindOverlay != null) _rebindOverlay.SetActive(false);
                    GameLogger.Log("Settings", $"Rebind canceled for {actionName}");
                });
        }

        private void OnResetAll()
        {
            _input?.ResetAllBindings();
            RefreshAllLabels();
            GameLogger.Log("Settings", "All bindings reset to defaults");
        }

        private void RefreshAllLabels()
        {
            foreach (var row in _rows)
            {
                if (row.BindingLabel != null)
                    row.BindingLabel.text = _input.GetBindingDisplayString(row.ActionName, row.BindingIndex);
            }
        }

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>Convert PascalCase action name to spaced readable form ("LeftShift" → "Left Shift").</summary>
        private static string FormatActionName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new System.Text.StringBuilder(name.Length + 4);
            sb.Append(name[0]);
            for (int i = 1; i < name.Length; i++)
            {
                if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                    sb.Append(' ');
                sb.Append(name[i]);
            }
            return sb.ToString();
        }
    }
}