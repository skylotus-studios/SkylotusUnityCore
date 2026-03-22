using LitMotion;
using LitMotion.Extensions;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Skylotus
{
    /// <summary>
    /// Custom UI button with sprite swapping, LitMotion scale/punch tweens on
    /// select/hover, and UI sound effects. Works with both mouse and gamepad
    /// navigation through the Unity EventSystem.
    ///
    /// Inherits <see cref="Selectable"/> for full EventSystem integration and
    /// implements click/submit handlers manually for maximum control.
    ///
    /// <code>
    /// // Wire in code:
    /// button.OnClick.AddListener(() => Debug.Log("Clicked!"));
    /// </code>
    /// </summary>
    [AddComponentMenu("UI/ButtonExtended")]
    [RequireComponent(typeof(Image))]
    public class ButtonExtended : Selectable,
        IPointerClickHandler, ISubmitHandler
    {
        // ─── Sprite Swapping ────────────────────────────────────────

        [Header("Sprite States")]
        [Tooltip("Sprite shown in the normal (idle) state.")]
        [SerializeField] private Sprite _normalSprite;

        [Tooltip("Sprite shown when highlighted / selected.")]
        [SerializeField] private Sprite _highlightedSprite;

        [Tooltip("Sprite shown while pressed.")]
        [SerializeField] private Sprite _pressedSprite;

        [Tooltip("Sprite shown when the button is disabled.")]
        [SerializeField] private Sprite _disabledSprite;

        // ─── Tween Settings ────────────────────────────────────────

        [Header("Tween")]
        [Tooltip("Target scale when highlighted (e.g. 1.08 for 8 % enlarge).")]
        [SerializeField] private float _highlightScale = 1.08f;

        [Tooltip("Duration of the scale tween in seconds.")]
        [SerializeField] private float _tweenDuration = 0.2f;

        [Tooltip("Punch strength applied as a jiggle on select.")]
        [SerializeField] private Vector3 _punchStrength = new(0.06f, 0.06f, 0f);

        [Tooltip("Duration of the punch jiggle in seconds.")]
        [SerializeField] private float _punchDuration = 0.35f;

        // ─── Audio ──────────────────────────────────────────────────

        [Header("Audio")]
        [Tooltip("Sound played when the button is highlighted / selected.")]
        [SerializeField] private AudioClip _hoverSound;

        [Tooltip("Sound played when the button is clicked / submitted.")]
        [SerializeField] private AudioClip _clickSound;

        // ─── Events ─────────────────────────────────────────────────

        [Header("Events")]
        [SerializeField] private UnityEvent _onClick = new();

        /// <summary>Invoked on pointer click or gamepad submit.</summary>
        public UnityEvent OnClick => _onClick;

        // ─── Internal State ─────────────────────────────────────────

        /// <summary>The Image component used for sprite display.</summary>
        private Image _image;

        /// <summary>Handle to the currently playing scale tween (for cancellation).</summary>
        private MotionHandle _scaleTween;

        /// <summary>Handle to the currently playing punch tween (for cancellation).</summary>
        private MotionHandle _punchTween;

        /// <summary>Cached reference to the AudioManager (resolved once).</summary>
        private AudioManager _audio;

        /// <summary>Tracks the previous Selectable state to avoid redundant transitions.</summary>
        private SelectionState _lastState = SelectionState.Normal;

        // ─── Unity Lifecycle ────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();
            _image = GetComponent<Image>();

            // Apply normal sprite on startup
            if (_normalSprite != null && _image != null)
                _image.sprite = _normalSprite;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            CancelTweens();
        }

        // ─── Selectable Transition Override ─────────────────────────

        /// <summary>
        /// Called by the EventSystem whenever the selectable state changes.
        /// We swap sprites, play scale/punch tweens, and trigger hover audio.
        /// </summary>
        protected override void DoStateTransition(SelectionState state, bool instant)
        {
            base.DoStateTransition(state, instant);
            if (!Application.isPlaying) return;

            // Swap sprite
            if (_image != null)
            {
                _image.sprite = state switch
                {
                    SelectionState.Highlighted => _highlightedSprite ? _highlightedSprite : _normalSprite,
                    SelectionState.Pressed     => _pressedSprite     ? _pressedSprite     : _normalSprite,
                    SelectionState.Selected    => _highlightedSprite ? _highlightedSprite : _normalSprite,
                    SelectionState.Disabled    => _disabledSprite    ? _disabledSprite    : _normalSprite,
                    _                          => _normalSprite
                };
            }

            // Tween & audio on state enter (skip if same state fires again)
            if (state == _lastState) return;
            _lastState = state;

            switch (state)
            {
                case SelectionState.Highlighted:
                case SelectionState.Selected:
                    PlayHighlight(instant);
                    break;

                case SelectionState.Pressed:
                    PlayPress(instant);
                    break;

                case SelectionState.Normal:
                case SelectionState.Disabled:
                default:
                    PlayNormal(instant);
                    break;
            }
        }

        // ─── Click / Submit ─────────────────────────────────────────

        /// <summary>Mouse / touch click.</summary>
        public void OnPointerClick(PointerEventData eventData)
        {
            if (!IsInteractable()) return;
            PerformClick();
        }

        /// <summary>Gamepad / keyboard submit (A button, Enter, Space).</summary>
        public void OnSubmit(BaseEventData eventData)
        {
            if (!IsInteractable()) return;
            PerformClick();
        }

        /// <summary>Common click logic: sound + event.</summary>
        private void PerformClick()
        {
            PlaySound(_clickSound);
            _onClick?.Invoke();
        }

        // ─── Tween Helpers ──────────────────────────────────────────

        private void PlayHighlight(bool instant)
        {
            CancelTweens();

            if (instant)
            {
                transform.localScale = Vector3.one * _highlightScale;
                return;
            }

            PlaySound(_hoverSound);

            // Scale up
            _scaleTween = LMotion.Create(transform.localScale, Vector3.one * _highlightScale, _tweenDuration)
                .WithEase(Ease.OutBack)
                .BindToLocalScale(transform);

            // Jiggle punch on top
            _punchTween = LMotion.Punch.Create(Vector3.one * _highlightScale, _punchStrength, _punchDuration)
                .WithDelay(_tweenDuration * 0.5f)
                .BindToLocalScale(transform);
        }

        private void PlayPress(bool instant)
        {
            CancelTweens();

            var pressedScale = Vector3.one * (_highlightScale * 0.95f);

            if (instant)
            {
                transform.localScale = pressedScale;
                return;
            }

            _scaleTween = LMotion.Create(transform.localScale, pressedScale, _tweenDuration * 0.5f)
                .WithEase(Ease.OutQuad)
                .BindToLocalScale(transform);
        }

        private void PlayNormal(bool instant)
        {
            CancelTweens();

            if (instant)
            {
                transform.localScale = Vector3.one;
                return;
            }

            _scaleTween = LMotion.Create(transform.localScale, Vector3.one, _tweenDuration)
                .WithEase(Ease.OutQuad)
                .BindToLocalScale(transform);
        }

        private void CancelTweens()
        {
            if (_scaleTween.IsActive()) _scaleTween.TryCancel();
            if (_punchTween.IsActive()) _punchTween.TryCancel();
        }

        // ─── Audio ──────────────────────────────────────────────────

        private void PlaySound(AudioClip clip)
        {
            if (clip == null) return;

            _audio ??= ServiceLocator.Get<AudioManager>();
            _audio?.PlayUI(clip);
        }
    }
}