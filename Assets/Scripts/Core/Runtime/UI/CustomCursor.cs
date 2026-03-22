using LitMotion;
using LitMotion.Extensions;
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
    /// </summary>
    [AddComponentMenu("Skylotus/UI/Custom Cursor")]
    [RequireComponent(typeof(RectTransform))]
    public class CustomCursor : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The Image component that displays the cursor sprite.")]
        [SerializeField] private Image _cursorImage;

        [Tooltip("The parent Canvas (must be Screen Space – Overlay).")]
        [SerializeField] private Canvas _canvas;

        [Header("Settings")]
        [Tooltip("Duration of the smooth snap tween in gamepad mode.")]
        [SerializeField] private float _snapDuration = 0.12f;

        [Tooltip("Offset from the selected element's center (screen pixels).")]
        [SerializeField] private Vector2 _selectionOffset = Vector2.zero;

        [Tooltip("Hide the cursor entirely when no UI is selected in gamepad mode.")]
        [SerializeField] private bool _hideWhenNoSelection;

        // ─── Internal State ─────────────────────────────────────────

        private RectTransform _rect;
        private RectTransform _canvasRect;
        private bool _isGamepadMode;
        private GameObject _lastSelected;
        private MotionHandle _snapTween;
        private Camera _uiCamera;

        // ─── Unity Lifecycle ────────────────────────────────────────

        private void Awake()
        {
            _rect = GetComponent<RectTransform>();
            if (_canvas != null)
                _canvasRect = _canvas.GetComponent<RectTransform>();
        }

        private void OnEnable()
        {
            // Hide OS cursor
            Cursor.visible = false;

            // Subscribe to device changes
            EventBus.Subscribe<OnInputDeviceChangedEvent>(OnDeviceChanged);

            // Determine initial mode
            var inputManager = ServiceLocator.Get<InputManager>();
            if (inputManager != null)
                _isGamepadMode = inputManager.CurrentDevice == InputDeviceType.Gamepad;

            // Camera for ScreenPointToLocalPoint (null for overlay canvas)
            if (_canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                _uiCamera = _canvas.worldCamera;
        }

        private void OnDisable()
        {
            Cursor.visible = true;
            EventBus.Unsubscribe<OnInputDeviceChangedEvent>(OnDeviceChanged);
            CancelSnap();
        }

        private void LateUpdate()
        {
            if (_isGamepadMode)
                UpdateGamepadCursor();
            else
                UpdateMouseCursor();
        }

        // ─── Device Switching ───────────────────────────────────────

        private void OnDeviceChanged(OnInputDeviceChangedEvent evt)
        {
            _isGamepadMode = evt.DeviceType == InputDeviceType.Gamepad;

            if (!_isGamepadMode)
            {
                // Returning to mouse — make sure cursor is visible and cancel any snap
                SetCursorVisible(true);
                CancelSnap();
            }
            else
            {
                // Entering gamepad — snap to current selection immediately
                SnapToSelection(true);
            }
        }

        // ─── Mouse Mode ────────────────────────────────────────────

        private void UpdateMouseCursor()
        {
            SetCursorVisible(true);

            var mousePos = Mouse.current != null
                ? Mouse.current.position.ReadValue()
                : (Vector2)Input.mousePosition;

            if (_canvasRect != null)
            {
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvasRect, mousePos, _uiCamera, out var localPoint);
                _rect.anchoredPosition = localPoint;
            }
            else
            {
                _rect.position = mousePos;
            }
        }

        // ─── Gamepad Mode ───────────────────────────────────────────

        private void UpdateGamepadCursor()
        {
            var selected = EventSystem.current != null
                ? EventSystem.current.currentSelectedGameObject
                : null;

            // Nothing selected
            if (selected == null)
            {
                if (_hideWhenNoSelection)
                    SetCursorVisible(false);
                return;
            }

            SetCursorVisible(true);

            // Only re-snap when the selection changes
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

            // Convert the selected element's world center to our canvas local space
            var worldPos = targetRect.position;
            Vector2 targetLocal;

            if (_canvasRect != null)
            {
                var screenPos = RectTransformUtility.WorldToScreenPoint(_uiCamera, worldPos);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _canvasRect, screenPos, _uiCamera, out targetLocal);
            }
            else
            {
                targetLocal = worldPos;
            }

            targetLocal += _selectionOffset;

            CancelSnap();

            if (instant || _snapDuration <= 0f)
            {
                _rect.anchoredPosition = targetLocal;
                return;
            }

            _snapTween = LMotion.Create(_rect.anchoredPosition, targetLocal, _snapDuration)
                .WithEase(Ease.OutCubic)
                .BindToAnchoredPosition(_rect);
        }

        // ─── Helpers ────────────────────────────────────────────────

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