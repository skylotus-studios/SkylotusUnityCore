using LitMotion;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Skylotus
{
    /// <summary>
    /// Renders a custom cursor sprite in place of the OS cursor.
    /// <list type="bullet">
    ///   <item><b>KeyboardMouse mode</b> — follows the mouse position instantly.</item>
    ///   <item><b>Gamepad mode</b> — smoothly tweens to the currently selected UI element.</item>
    /// </list>
    ///
    /// Place this on a UI Image that lives on its own Screen Space – Overlay Canvas
    /// with the highest sort order. Disable the <c>GraphicRaycaster</c> on that canvas
    /// so the cursor image does not intercept clicks.
    ///
    /// The cursor image should be a child of this GameObject.
    /// </summary>
    [AddComponentMenu("Skylotus/UI/Custom Cursor")]
    public class CustomCursor : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The Image component that displays the cursor sprite (child object).")]
        [SerializeField] private Image _cursorImage;

        [Tooltip("The parent Canvas (must be Screen Space – Overlay).")]
        [SerializeField] private Canvas _canvas;

        [Tooltip("Fallback selectable to auto-select when entering gamepad mode with nothing selected.")]
        [SerializeField] private GameObject _defaultSelection;

        [Header("Settings")]
        [Tooltip("Duration of the smooth snap tween in gamepad mode.")]
        [SerializeField] private float _snapDuration = 0.12f;

        [Tooltip("Offset from the selected element's center (canvas-scaled pixels).")]
        [SerializeField] private Vector2 _selectionOffset = Vector2.zero;

        [Tooltip("Hide the cursor entirely when no UI is selected in gamepad mode.")]
        [SerializeField] private bool _hideWhenNoSelection;

        // ─── Internal State ─────────────────────────────────────────

        /// <summary>The RectTransform we actually move — the cursor image, not this parent.</summary>
        private RectTransform _cursorRect;
        private RectTransform _canvasRect;
        private Camera _uiCamera;
        private bool _isGamepadMode;
        private GameObject _lastSelected;
        private MotionHandle _snapTween;

        // ─── Unity Lifecycle ────────────────────────────────────────

        private void Awake()
        {
            if (_cursorImage != null)
                _cursorRect = _cursorImage.rectTransform;

            if (_canvas != null)
            {
                _canvasRect = _canvas.GetComponent<RectTransform>();

                // For overlay canvases pass null camera to ScreenPointToLocalPointInRectangle
                if (_canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    _uiCamera = _canvas.worldCamera;
            }
        }

        private void OnEnable()
        {
            Cursor.visible = false;
            EventBus.Subscribe<OnInputDeviceChangedEvent>(OnDeviceChanged);

            var inputManager = ServiceLocator.Get<InputManager>();
            if (inputManager != null)
                _isGamepadMode = inputManager.CurrentDevice == InputDeviceType.Gamepad;
        }

        private void OnDisable()
        {
            Cursor.visible = true;
            EventBus.Unsubscribe<OnInputDeviceChangedEvent>(OnDeviceChanged);
            CancelSnap();
        }

        private void LateUpdate()
        {
            if (_cursorRect == null) return;

            if (_isGamepadMode)
                UpdateGamepadCursor();
            else
                UpdateMouseCursor();
        }

        // ─── Device Switching ───────────────────────────────────────

        private void OnDeviceChanged(OnInputDeviceChangedEvent evt)
        {
            bool wasGamepad = _isGamepadMode;
            _isGamepadMode = evt.DeviceType == InputDeviceType.Gamepad;

            if (!_isGamepadMode)
            {
                // Gamepad → Mouse: clear the gamepad selection so the old button
                // deselects. Pointer hover events will highlight as needed.
                if (wasGamepad && EventSystem.current != null)
                    EventSystem.current.SetSelectedGameObject(null);

                SetCursorVisible(true);
                CancelSnap();
                _lastSelected = null;
            }
            else
            {
                // Mouse → Gamepad: force PointerExit on every selectable that is
                // currently in a highlighted / hovered state so only the gamepad-
                // selected button shows as active.
                ClearAllPointerHovers();

                // If nothing is selected, auto-select a default so d-pad navigation works.
                if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject == null)
                {
                    var target = _defaultSelection != null ? _defaultSelection : FindFirstSelectable();
                    if (target != null)
                        EventSystem.current.SetSelectedGameObject(target);
                }

                SnapToSelection(true);
            }
        }

        // ─── Mouse Mode ────────────────────────────────────────────

        private void UpdateMouseCursor()
        {
            if (!TryReadPointerPosition(out var screenPos))
            {
                // No pointing device is present. Leaving the cursor visible would strand it
                // wherever it happened to be last frame, so hide it until a mouse appears.
                SetCursorVisible(false);
                return;
            }

            SetCursorVisible(true);

            // Convert the raw screen-pixel position into the canvas's local coordinate
            // space. ScreenPointToLocalPointInRectangle handles the Canvas Scaler math
            // internally — pass null camera for Screen Space Overlay.
            if (_canvasRect != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvasRect, screenPos, _uiCamera, out var localPoint))
            {
                _cursorRect.localPosition = localPoint;
            }
        }

        /// <summary>
        /// Read the pointer position from whichever input backend is actually compiled in.
        /// </summary>
        /// <param name="position">The pointer position in screen pixels, or default when none exists.</param>
        /// <returns>False when no pointing device is available, so the caller can hide the cursor.</returns>
        /// <remarks>
        /// The legacy branch is behind <c>ENABLE_LEGACY_INPUT_MANAGER</c> deliberately. This project
        /// ships <c>activeInputHandler: 1</c> (Input System only), and under that setting **every**
        /// access to <c>UnityEngine.Input</c> throws <see cref="System.InvalidOperationException"/>.
        /// An unguarded <c>Input.mousePosition</c> fallback therefore threw once per frame on any
        /// device without a mouse — headless CI, a gamepad-only console, a touch device — turning a
        /// missing cursor into an exception storm.
        /// </remarks>
        private static bool TryReadPointerPosition(out Vector2 position)
        {
            if (Mouse.current != null)
            {
                position = Mouse.current.position.ReadValue();
                return true;
            }

            if (Pointer.current != null)
            {
                // Covers pens and touchscreens, which report through Pointer but not Mouse.
                position = Pointer.current.position.ReadValue();
                return true;
            }

#if ENABLE_LEGACY_INPUT_MANAGER
            position = Input.mousePosition;
            return true;
#else
            position = default;
            return false;
#endif
        }

        // ─── Gamepad Mode ───────────────────────────────────────────

        private void UpdateGamepadCursor()
        {
            var selected = EventSystem.current != null
                ? EventSystem.current.currentSelectedGameObject
                : null;

            if (selected == null)
            {
                if (_hideWhenNoSelection)
                    SetCursorVisible(false);
                return;
            }

            SetCursorVisible(true);

            if (selected != _lastSelected)
            {
                _lastSelected = selected;
                SnapToSelection(false);
            }
        }

        private void SnapToSelection(bool instant)
        {
            var selected = EventSystem.current != null
                ? EventSystem.current.currentSelectedGameObject
                : null;

            if (selected == null) return;

            var targetRect = selected.GetComponent<RectTransform>();
            if (targetRect == null) return;

            // Get the screen-space bottom-right corner of the selected element,
            // then convert to our canvas's local space.
            Vector2 screenPos = GetScreenBottomRight(targetRect);
            if (_canvasRect == null) return;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvasRect, screenPos, _uiCamera, out var localPoint))
                return;

            localPoint += _selectionOffset;
            var targetPos = new Vector3(localPoint.x, localPoint.y, 0f);

            CancelSnap();

            if (instant || _snapDuration <= 0f)
            {
                _cursorRect.localPosition = targetPos;
                return;
            }

            _snapTween = LMotion.Create(_cursorRect.localPosition, targetPos, _snapDuration)
                .WithEase(Ease.OutCubic)
                .Bind(_cursorRect, static (v, rt) => rt.localPosition = v);
        }

        /// <summary>
        /// Get the screen-pixel bottom-right corner of a RectTransform.
        /// GetWorldCorners order: [0]=BL, [1]=TL, [2]=TR, [3]=BR.
        /// For overlay canvases these are already in screen pixels.
        /// </summary>
        private Vector2 GetScreenBottomRight(RectTransform targetRect)
        {
            Vector3[] corners = new Vector3[4];
            targetRect.GetWorldCorners(corners);

            // For non-overlay canvases, convert world→screen through the camera.
            if (_canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                var cam = _canvas.worldCamera != null ? _canvas.worldCamera : Camera.main;
                if (cam != null)
                    return RectTransformUtility.WorldToScreenPoint(cam, corners[3]);
            }

            // Overlay canvas — world corners are screen pixels
            return corners[3];
        }

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>
        /// Send a PointerExit event to every Selectable that the pointer is currently
        /// hovering over. This forces them back to Normal state immediately.
        /// </summary>
        private static void ClearAllPointerHovers()
        {
            var pointerData = new PointerEventData(EventSystem.current);

            foreach (var selectable in Selectable.allSelectablesArray)
            {
                if (selectable == null || !selectable.gameObject.activeInHierarchy)
                    continue;

                // IsHighlighted is not public, but we can send the exit unconditionally —
                // it's harmless on non-hovered objects and costs very little.
                ExecuteEvents.Execute(selectable.gameObject, pointerData, ExecuteEvents.pointerExitHandler);
            }
        }

        private static GameObject FindFirstSelectable()
        {
            foreach (var s in Selectable.allSelectablesArray)
            {
                if (s != null && s.IsInteractable() && s.gameObject.activeInHierarchy)
                    return s.gameObject;
            }
            return null;
        }

        private void SetCursorVisible(bool visible)
        {
            if (_cursorImage != null && _cursorImage.enabled != visible)
                _cursorImage.enabled = visible;
        }

        private void CancelSnap()
        {
            if (_snapTween.IsActive()) _snapTween.TryCancel();
        }
    }
}