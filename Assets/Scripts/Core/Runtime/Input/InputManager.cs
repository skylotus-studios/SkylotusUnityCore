using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Users;

namespace Skylotus
{
    /// <summary>Categories of input devices for UI prompt switching.</summary>
    public enum InputDeviceType { KeyboardMouse, Gamepad, Touch }

    /// <summary>
    /// Input management layer wrapping Unity's Input System. Provides:
    /// - Automatic device detection with <see cref="OnDeviceChanged"/> events
    /// - Action map context switching (Gameplay / UI / Vehicle / etc.)
    /// - Runtime rebinding with persistent save/load via PlayerPrefs
    /// - Display string and glyph path helpers for UI prompts
    ///
    /// Requires an <see cref="InputActionAsset"/> assigned via the inspector or bootstrapper.
    /// </summary>
    public class InputManager : MonoBehaviour
    {
        /// <summary>The project's Input Action Asset. Instantiated at runtime to avoid modifying the asset.</summary>
        [SerializeField] private InputActionAsset _inputActions;

        /// <summary>Tracks the last-used device type for prompt/glyph switching.</summary>
        private InputDeviceType _currentDevice = InputDeviceType.KeyboardMouse;

        /// <summary>Cache of registered performed callbacks so they can be unbound later.</summary>
        private readonly Dictionary<string, Action<InputAction.CallbackContext>> _actionCallbacks = new();

        /// <summary>Name of the currently active (enabled) action map.</summary>
        private string _activeMapName;

        /// <summary>PlayerPrefs key used to persist rebinding overrides across sessions.</summary>
        private const string RebindSaveKey = "InputRebinds";

        /// <summary>The runtime input action asset instance.</summary>
        public InputActionAsset Actions => _inputActions;

        /// <summary>The most recently detected device type.</summary>
        public InputDeviceType CurrentDevice => _currentDevice;

        /// <summary>Fired whenever the player switches between device types.</summary>
        public event Action<InputDeviceType> OnDeviceChanged;

        /// <summary>
        /// Initialize the InputManager. Called by the bootstrapper after the InputActionAsset
        /// has been assigned. Clones the asset and loads saved rebinds.
        /// </summary>
        public void Initialize()
        {
            if (_inputActions == null)
            {
                GameLogger.LogError("Input", "InputActionAsset not assigned!");
                return;
            }

            // Work on an instance so we never modify the project asset
            _inputActions = Instantiate(_inputActions);
            LoadRebinds();

            // Listen for device changes at the system level
            InputSystem.onActionChange += HandleActionChange;
            InputUser.onChange += HandleUserChange;
        }

        /// <summary>Unity OnDestroy — unsubscribe from global callbacks and disable all maps.</summary>
        private void OnDestroy()
        {
            InputSystem.onActionChange -= HandleActionChange;
            InputUser.onChange -= HandleUserChange;

            if (_inputActions != null)
                _inputActions.Disable();
        }

        // ─── Context Switching ──────────────────────────────────────

        /// <summary>
        /// Disable the current action map and enable a new one by name.
        /// Use for hard context switches (Gameplay → UI → Vehicle).
        /// </summary>
        /// <param name="mapName">Exact name of the action map in the Input Action Asset.</param>
        public void SwitchActionMap(string mapName)
        {
            if (_inputActions == null) return;

            // Disable the currently active map
            if (!string.IsNullOrEmpty(_activeMapName))
            {
                var current = _inputActions.FindActionMap(_activeMapName);
                current?.Disable();
            }

            var map = _inputActions.FindActionMap(mapName);
            if (map == null)
            {
                GameLogger.LogError("Input", $"Action map '{mapName}' not found");
                return;
            }

            map.Enable();
            _activeMapName = mapName;
            GameLogger.Log("Input", $"Switched to action map: {mapName}");
        }

        /// <summary>
        /// Enable an additional action map without disabling the current one.
        /// Useful for layered input (e.g. enable "Debug" on top of "Gameplay").
        /// </summary>
        /// <param name="mapName">Name of the map to enable.</param>
        public void EnableActionMap(string mapName)
        {
            _inputActions.FindActionMap(mapName)?.Enable();
        }

        /// <summary>
        /// Disable a specific action map without affecting others.
        /// </summary>
        /// <param name="mapName">Name of the map to disable.</param>
        public void DisableActionMap(string mapName)
        {
            _inputActions.FindActionMap(mapName)?.Disable();
        }

        // ─── Action Binding ─────────────────────────────────────────

        /// <summary>
        /// Find an InputAction by name across all maps.
        /// </summary>
        /// <param name="actionName">The action name (e.g. "Jump", "Move").</param>
        /// <returns>The InputAction, or null if not found.</returns>
        public InputAction GetAction(string actionName)
        {
            return _inputActions.FindAction(actionName);
        }

        /// <summary>
        /// Register a performed callback for an action. The callback fires once per press.
        /// </summary>
        /// <param name="actionName">The action name to bind to.</param>
        /// <param name="callback">The callback to invoke on performed.</param>
        public void BindAction(string actionName, Action<InputAction.CallbackContext> callback)
        {
            var action = _inputActions.FindAction(actionName);
            if (action == null)
            {
                GameLogger.LogError("Input", $"Action '{actionName}' not found");
                return;
            }

            action.performed += callback;
            _actionCallbacks[$"{actionName}_performed"] = callback;
        }

        /// <summary>
        /// Register started, performed, and/or canceled callbacks for an action.
        /// Useful for continuous inputs like movement.
        /// </summary>
        /// <param name="actionName">The action name.</param>
        /// <param name="onStarted">Called when the input begins (optional).</param>
        /// <param name="onPerformed">Called when the input is fully actuated (optional).</param>
        /// <param name="onCanceled">Called when the input is released (optional).</param>
        public void BindAction(string actionName,
            Action<InputAction.CallbackContext> onStarted = null,
            Action<InputAction.CallbackContext> onPerformed = null,
            Action<InputAction.CallbackContext> onCanceled = null)
        {
            var action = _inputActions.FindAction(actionName);
            if (action == null) return;

            if (onStarted != null) action.started += onStarted;
            if (onPerformed != null) action.performed += onPerformed;
            if (onCanceled != null) action.canceled += onCanceled;
        }

        /// <summary>
        /// Unbind all cached performed callbacks for an action.
        /// </summary>
        /// <param name="actionName">The action name to unbind.</param>
        public void UnbindAction(string actionName)
        {
            var action = _inputActions.FindAction(actionName);
            if (action == null) return;

            if (_actionCallbacks.TryGetValue($"{actionName}_performed", out var cb))
            {
                action.performed -= cb;
                _actionCallbacks.Remove($"{actionName}_performed");
            }
        }

        /// <summary>
        /// Read the current composite value of an action (e.g. Vector2 for movement).
        /// Returns default if the action is not found.
        /// </summary>
        /// <typeparam name="T">The value type (float, Vector2, etc.).</typeparam>
        /// <param name="actionName">The action name.</param>
        /// <returns>The current value, or default(T) if not found.</returns>
        public T ReadValue<T>(string actionName) where T : struct
        {
            var action = _inputActions.FindAction(actionName);
            return action != null ? action.ReadValue<T>() : default;
        }

        /// <summary>
        /// Check if an action was fully performed during this frame.
        /// </summary>
        /// <param name="actionName">The action name.</param>
        /// <returns>True if the action was performed this frame.</returns>
        public bool WasPerformed(string actionName)
        {
            var action = _inputActions.FindAction(actionName);
            return action?.WasPerformedThisFrame() ?? false;
        }

        // ─── Rebinding ─────────────────────────────────────────────

        /// <summary>
        /// Start an interactive rebinding operation. The system listens for the next
        /// input and assigns it to the specified binding index.
        /// Escape cancels the rebind. Mouse position/delta are excluded.
        /// </summary>
        /// <param name="actionName">The action to rebind.</param>
        /// <param name="bindingIndex">The binding index within the action (0-based).</param>
        /// <param name="onComplete">Called when rebinding completes successfully.</param>
        /// <param name="onCanceled">Called if the user cancels the rebind.</param>
        public void StartRebind(string actionName, int bindingIndex,
            Action onComplete = null, Action onCanceled = null)
        {
            var action = _inputActions.FindAction(actionName);
            if (action == null) return;

            // Disable the action during rebinding so current bindings don't interfere
            action.Disable();

            var rebind = action.PerformInteractiveRebinding(bindingIndex)
                .WithControlsExcluding("Mouse/position")
                .WithControlsExcluding("Mouse/delta")
                .WithCancelingThrough("<Keyboard>/escape")
                .OnComplete(op =>
                {
                    op.Dispose();
                    action.Enable();
                    SaveRebinds();
                    onComplete?.Invoke();
                    GameLogger.Log("Input",
                        $"Rebound '{actionName}' to '{action.bindings[bindingIndex].effectivePath}'");
                })
                .OnCancel(op =>
                {
                    op.Dispose();
                    action.Enable();
                    onCanceled?.Invoke();
                });

            rebind.Start();
        }

        /// <summary>
        /// Reset a specific binding to its default value defined in the Input Action Asset.
        /// </summary>
        /// <param name="actionName">The action name.</param>
        /// <param name="bindingIndex">The binding index to reset.</param>
        public void ResetBinding(string actionName, int bindingIndex)
        {
            var action = _inputActions.FindAction(actionName);
            if (action == null) return;

            action.RemoveBindingOverride(bindingIndex);
            SaveRebinds();
        }

        /// <summary>
        /// Reset every binding in every action map to its default and clear saved overrides.
        /// </summary>
        public void ResetAllBindings()
        {
            foreach (var map in _inputActions.actionMaps)
                map.RemoveAllBindingOverrides();

            PlayerPrefs.DeleteKey(RebindSaveKey);
            GameLogger.Log("Input", "All bindings reset to defaults");
        }

        /// <summary>
        /// Get a human-readable display string for an action's binding (e.g. "Space", "A Button").
        /// </summary>
        /// <param name="actionName">The action name.</param>
        /// <param name="bindingIndex">The binding index (default 0).</param>
        /// <returns>The display string, or "???" if not found.</returns>
        public string GetBindingDisplayString(string actionName, int bindingIndex = 0)
        {
            var action = _inputActions.FindAction(actionName);
            return action?.GetBindingDisplayString(bindingIndex) ?? "???";
        }

        /// <summary>
        /// Get a resource path for loading glyph sprites based on the current binding.
        /// Returns paths like "Glyphs/Gamepad/buttonSouth" for sprite atlas lookup.
        /// </summary>
        /// <param name="actionName">The action name.</param>
        /// <param name="bindingIndex">The binding index (default 0).</param>
        /// <returns>A categorized glyph path, or null if the action is not found.</returns>
        public string GetGlyphPath(string actionName, int bindingIndex = 0)
        {
            var action = _inputActions.FindAction(actionName);
            if (action == null) return null;

            var binding = action.bindings[bindingIndex];
            var path = binding.effectivePath;

            // Categorize by device family for sprite sheet lookup
            if (path.Contains("<Gamepad>")) return $"Glyphs/Gamepad/{path.Split('/')[^1]}";
            if (path.Contains("<Keyboard>")) return $"Glyphs/Keyboard/{path.Split('/')[^1]}";
            if (path.Contains("<Mouse>")) return $"Glyphs/Mouse/{path.Split('/')[^1]}";

            return $"Glyphs/Unknown/{path.Split('/')[^1]}";
        }

        // ─── Device Detection ───────────────────────────────────────

        /// <summary>
        /// Global callback: fires on every input action. Detects device switches
        /// and publishes <see cref="OnInputDeviceChangedEvent"/>.
        /// </summary>
        private void HandleActionChange(object obj, InputActionChange change)
        {
            if (change != InputActionChange.ActionPerformed) return;
            if (obj is not InputAction action) return;

            var device = action.activeControl?.device;
            if (device == null) return;

            // Map the physical device to our simplified enum
            InputDeviceType newType;
            if (device is Gamepad) newType = InputDeviceType.Gamepad;
            else if (device is Touchscreen) newType = InputDeviceType.Touch;
            else newType = InputDeviceType.KeyboardMouse;

            if (newType != _currentDevice)
            {
                _currentDevice = newType;
                OnDeviceChanged?.Invoke(newType);
                EventBus.Publish(new OnInputDeviceChangedEvent { DeviceType = newType });
                GameLogger.Log("Input", $"Device changed to: {newType}");
            }
        }

        /// <summary>Log device pairing events for diagnostics.</summary>
        private void HandleUserChange(InputUser user, InputUserChange change, InputDevice device)
        {
            if (change == InputUserChange.DevicePaired)
                GameLogger.Log("Input", $"Device paired: {device.displayName}");
        }

        /// <summary>Serialize all binding overrides to PlayerPrefs for persistence.</summary>
        private void SaveRebinds()
        {
            var overrides = _inputActions.SaveBindingOverridesAsJson();
            PlayerPrefs.SetString(RebindSaveKey, overrides);
            PlayerPrefs.Save();
        }

        /// <summary>Restore saved binding overrides from PlayerPrefs on startup.</summary>
        private void LoadRebinds()
        {
            var json = PlayerPrefs.GetString(RebindSaveKey, null);
            if (!string.IsNullOrEmpty(json))
            {
                _inputActions.LoadBindingOverridesFromJson(json);
                GameLogger.Log("Input", "Loaded saved rebinds");
            }
        }
    }
}