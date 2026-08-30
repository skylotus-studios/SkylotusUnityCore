using LitMotion;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Skylotus.Core.UI
{
    /// <summary>
    /// A navigable row in a settings scroll view. The row itself is a <see cref="Selectable"/>
    /// so gamepad/keyboard up-down moves between rows. Left-right and Submit input is
    /// delegated to whichever child control is assigned.
    ///
    /// <b>Supported child controls (assign exactly one):</b>
    /// <list type="bullet">
    ///   <item><see cref="ToggleSlider"/>      — Submit toggles on/off.</item>
    ///   <item><see cref="Slider"/>            — Left/Right adjusts value by <see cref="_sliderStep"/>.</item>
    ///   <item><see cref="SettingsCycleRow"/>  — Left/Right cycles options.</item>
    ///   <item><see cref="ButtonExtended"/>    — Submit clicks the button (e.g. rebind).</item>
    /// </list>
    ///
    /// <b>Expected hierarchy:</b>
    /// <code>
    ///   Row (SettingsRow — Image background, Selectable)
    ///   ├── Label_Text   (TMP_Text — static row name, e.g. "Language")
    ///   └── Control      (one of the above)
    /// </code>
    /// </summary>
    [AddComponentMenu("Skylotus/UI/Settings Row")]
    [RequireComponent(typeof(Image))]
    public class SettingsRow : Selectable, ISubmitHandler
    {
        [Header("Child Control (assign exactly one)")]
        [SerializeField] private ToggleSlider _toggleSlider;
        [SerializeField] private Slider _slider;
        [SerializeField] private SettingsCycleRow _cycleRow;
        [SerializeField] private ButtonExtended _rebindButton;

        [Header("Slider Step")]
        [Tooltip("Amount the slider moves per left/right press.")]
        [SerializeField] private float _sliderStep = 0.05f;

        [Header("Highlight")]
        [SerializeField] private float _tweenDuration = 0.15f;

        private Image _background;
        private Color _normalColor;
        private Color _highlightColor;
        private MotionHandle _colorTween;
        private ScrollRect _parentScrollRect;
        private RectTransform _rectTransform;

        // ─── Lifecycle ──────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();
            _background = GetComponent<Image>();

            if (ServiceLocator.TryGet<ColorPalette>(out var palette))
            {
                _normalColor = palette.background;
                _highlightColor = palette.tertiary;
            }

            if (_background != null) _background.color = _normalColor;

            _rectTransform = (RectTransform)transform;
            _parentScrollRect = GetComponentInParent<ScrollRect>();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (_colorTween.IsActive()) _colorTween.TryCancel();
        }

        // ─── Highlight ─────────────────────────────────────────────

        protected override void DoStateTransition(SelectionState state, bool instant)
        {
            base.DoStateTransition(state, instant);
            if (!Application.isPlaying || _background == null) return;

            bool highlighted = state == SelectionState.Highlighted
                            || state == SelectionState.Selected
                            || state == SelectionState.Pressed;

            if (highlighted) ScrollIntoView();

            var target = highlighted ? _highlightColor : _normalColor;

            if (_colorTween.IsActive()) _colorTween.TryCancel();

            if (instant || !gameObject.activeInHierarchy)
            {
                _background.color = target;
                return;
            }

            _colorTween = LMotion.Create(_background.color, target, _tweenDuration)
                .WithEase(Ease.OutQuad)
                .Bind(_background, static (c, img) => img.color = c);
        }

        // ─── Directional Input ──────────────────────────────────────

        public override void OnMove(AxisEventData eventData)
        {
            switch (eventData.moveDir)
            {
                case MoveDirection.Left:
                    if (_cycleRow != null) _cycleRow.StepPrev();
                    else if (_slider != null) AdjustSlider(-_sliderStep);
                    else base.OnMove(eventData);
                    break;

                case MoveDirection.Right:
                    if (_cycleRow != null) _cycleRow.StepNext();
                    else if (_slider != null) AdjustSlider(_sliderStep);
                    else base.OnMove(eventData);
                    break;

                default: // Up / Down — navigate between rows
                    base.OnMove(eventData);
                    break;
            }
        }

        // ─── Submit ─────────────────────────────────────────────────

        public void OnSubmit(BaseEventData eventData)
        {
            if (!IsInteractable()) return;

            if (_toggleSlider != null)
                _toggleSlider.PerformToggle();
            else if (_rebindButton != null)
                ExecuteEvents.Execute(_rebindButton.gameObject, eventData, ExecuteEvents.submitHandler);
        }

        // ─── Helpers ────────────────────────────────────────────────

        private void AdjustSlider(float delta)
        {
            if (_slider == null) return;
            _slider.value = Mathf.Clamp(_slider.value + delta, _slider.minValue, _slider.maxValue);
        }

        private void ScrollIntoView()
        {
            if (_parentScrollRect == null || _rectTransform == null) return;

            var viewport = _parentScrollRect.viewport ?? (RectTransform)_parentScrollRect.transform;
            var content = _parentScrollRect.content;
            if (content == null) return;

            // Get this row's position relative to the content
            var rowLocal = content.InverseTransformPoint(_rectTransform.position);
            var viewportLocal = content.InverseTransformPoint(viewport.position);

            float viewHeight = viewport.rect.height;
            float rowHeight = _rectTransform.rect.height;
            float contentHeight = content.rect.height;
            float scrollableHeight = contentHeight - viewHeight;
            if (scrollableHeight <= 0f) return;

            // Row top/bottom relative to content top
            float rowTop = -rowLocal.y - rowHeight * 0.5f;
            float rowBottom = rowTop + rowHeight;

            // Current visible window top/bottom
            float currentScrollTop = (1f - _parentScrollRect.verticalNormalizedPosition) * scrollableHeight;
            float currentScrollBottom = currentScrollTop + viewHeight;

            // Scroll only if out of view
            float newScrollTop = currentScrollTop;
            if (rowTop < currentScrollTop)
                newScrollTop = rowTop;
            else if (rowBottom > currentScrollBottom)
                newScrollTop = rowBottom - viewHeight;

            _parentScrollRect.verticalNormalizedPosition = 1f - Mathf.Clamp01(newScrollTop / scrollableHeight);
        }
    }
}
