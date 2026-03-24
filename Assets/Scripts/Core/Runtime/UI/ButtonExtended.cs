using LitMotion;
using LitMotion.Extensions;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Skylotus
{
    /// <summary>
    /// Custom UI button with LitMotion tweened transitions — scale, color, and press pulse.
    /// All visual feedback is driven by tweens between a primary (normal) and secondary
    /// (highlighted) color pair for both the background Image and the label Text.
    ///
    /// On press a ghost copy of the button's own Image expands outward and fades to zero,
    /// creating a shockwave / pulse ring effect using the secondary color.
    ///
    /// Inherits <see cref="Selectable"/> for full EventSystem integration (mouse + gamepad)
    /// and implements click/submit handlers manually.
    /// </summary>
    [AddComponentMenu("UI (Canvas)/ButtonExtended")]
    [RequireComponent(typeof(Image))]
    public class ButtonExtended : Selectable,
        IPointerClickHandler, ISubmitHandler
    {
        // ─── Color ──────────────────────────────────────────────────

        [Header("Colors — Image")]
        [Tooltip("Background color in the normal / idle state.")]
        [SerializeField] private Color _imagePrimaryColor = Color.white;

        [Tooltip("Background color when highlighted / selected.")]
        [SerializeField] private Color _imageSecondaryColor = new(0.85f, 0.85f, 0.85f, 1f);

        [Header("Colors — Text")]
        [Tooltip("Label color in the normal / idle state.")]
        [SerializeField] private Color _textPrimaryColor = Color.black;

        [Tooltip("Label color when highlighted / selected.")]
        [SerializeField] private Color _textSecondaryColor = Color.white;

        [Header("Materials")]
        [Tooltip("Material for the image in the normal / idle state.")]
        [SerializeField] private Material _normalImageMaterial;

        [Tooltip("Material for the image when highlighted / selected.")]
        [SerializeField] private Material _activeImageMaterial;

        [Header("Colors — Disabled")]
        [Tooltip("Background color when the button is disabled.")]
        [SerializeField] private Color _imageDisabledColor = new(0.6f, 0.6f, 0.6f, 0.5f);

        [Tooltip("Label color when the button is disabled.")]
        [SerializeField] private Color _textDisabledColor = new(0.4f, 0.4f, 0.4f, 1f);

        // ─── Scale ──────────────────────────────────────────────────

        [Header("Scale Tween")]
        [Tooltip("Target scale when highlighted (e.g. 1.08 for 8% enlarge).")]
        [SerializeField] private float _highlightScale = 1.08f;

        [Tooltip("Duration of the scale & color tweens in seconds.")]
        [SerializeField] private float _tweenDuration = 0.2f;

        // ─── Pulse ──────────────────────────────────────────────────

        [Header("Press Pulse")]
        [Tooltip("Image used as the pulse ghost.")]
        [SerializeField] private Image _pulseGhostImage;

        [Tooltip("How many pixels the pulse ghost expands beyond the button on each axis.")]
        [SerializeField] private float _pulseExpand = 30f;

        [Tooltip("Duration of the pulse expand + fade animation.")]
        [SerializeField] private float _pulseDuration = 0.35f;

        [Tooltip("Easing for the pulse expansion.")]
        [SerializeField] private Ease _pulseEase = Ease.OutSine;

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

        // ─── References ─────────────────────────────────────────────

        [Header("References")]
        [Tooltip("Optional TMP label — auto-found on children if not assigned.")]
        [SerializeField] private TMP_Text _label;

        // ─── Internal State ─────────────────────────────────────────

        private Image _image;
        private AudioManager _audio;
        private SelectionState _lastState = SelectionState.Normal;

        // Tweens for state transitions
        private MotionHandle _scaleTween;
        private MotionHandle _imageColorTween;
        private MotionHandle _textColorTween;

        // Pulse ghost
        private RectTransform _pulseRect;
        private CompositeMotionHandle _pulseHandles = new(2);

        // ─── Unity Lifecycle ────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();
            _image = GetComponent<Image>();
            if (_label == null) _label = GetComponentInChildren<TMP_Text>();

            // Apply primary colors on startup
            if (_image != null) {
                _image.color = _imagePrimaryColor;
                _image.material = _normalImageMaterial;
            }
            if (_label != null) _label.color = _textPrimaryColor;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            CancelAllTweens();
            _pulseHandles.Cancel();
        }

        // ─── Selectable Transition Override ─────────────────────────

        protected override void DoStateTransition(SelectionState state, bool instant)
        {
            base.DoStateTransition(state, instant);
            if (!Application.isPlaying) return;

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

                case SelectionState.Disabled:
                    PlayDisabled(instant);
                    break;

                case SelectionState.Normal:
                default:
                    PlayNormal(instant);
                    break;
            }
        }

        // ─── Click / Submit ─────────────────────────────────────────

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!IsInteractable()) return;
            PerformClick();
        }

        public void OnSubmit(BaseEventData eventData)
        {
            if (!IsInteractable()) return;
            PerformClick();
        }

        private void PerformClick()
        {
            PlaySound(_clickSound);
            FirePulse();
            _onClick?.Invoke();
        }

        // ─── State Tweens ───────────────────────────────────────────

        private void PlayHighlight(bool instant)
        {
            CancelAllTweens();

            var targetScale = Vector3.one * _highlightScale;

            if (instant)
            {
                transform.localScale = targetScale;
                SetState(_imageSecondaryColor, _textSecondaryColor, _activeImageMaterial);
                return;
            }

            PlaySound(_hoverSound);

            if (_image != null) _image.material = _activeImageMaterial;

            _scaleTween = LMotion.Create(transform.localScale, targetScale, _tweenDuration)
                .WithEase(Ease.OutBack)
                .BindToLocalScale(transform);

            TweenColors(_imageSecondaryColor, _textSecondaryColor);
        }

        private void PlayPress(bool instant)
        {
            CancelAllTweens();

            var pressedScale = Vector3.one * (_highlightScale * 0.95f);

            if (instant)
            {
                transform.localScale = pressedScale;
                SetState(_imageSecondaryColor, _textSecondaryColor, _activeImageMaterial);
                return;
            }

            if (_image != null) _image.material = _activeImageMaterial;

            _scaleTween = LMotion.Create(transform.localScale, pressedScale, _tweenDuration * 0.5f)
                .WithEase(Ease.OutQuad)
                .BindToLocalScale(transform);

            TweenColors(_imageSecondaryColor, _textSecondaryColor);
        }

        private void PlayNormal(bool instant)
        {
            CancelAllTweens();

            if (instant)
            {
                transform.localScale = Vector3.one;
                SetState(_imagePrimaryColor, _textPrimaryColor, _normalImageMaterial);
                return;
            }

            if (_image != null) _image.material = _normalImageMaterial;

            _scaleTween = LMotion.Create(transform.localScale, Vector3.one, _tweenDuration)
                .WithEase(Ease.OutQuad)
                .BindToLocalScale(transform);

            TweenColors(_imagePrimaryColor, _textPrimaryColor);
        }

        private void PlayDisabled(bool instant)
        {
            CancelAllTweens();

            transform.localScale = Vector3.one;
            SetState(_imageDisabledColor, _textDisabledColor, _normalImageMaterial);
        }

        /// <summary>
        /// Fire the pulse: reset the ghost to the button's size with the secondary color,
        /// then expand outward and fade alpha to 0.
        /// </summary>
        private void FirePulse()
        {
            if (_pulseGhostImage == null) return;
            _pulseRect = _pulseGhostImage.rectTransform;

            // Complete any in-flight pulse so it doesn't stack
            _pulseHandles.Complete();

            // Reset to match button size (anchors already handle this, just clear offsets)
            _pulseRect.offsetMin = Vector2.zero;
            _pulseRect.offsetMax = Vector2.zero;

            // Start with the secondary color at full alpha
            var startColor = _imageSecondaryColor;
            _pulseGhostImage.color = startColor;
            _pulseRect.gameObject.SetActive(true);

            // Expand: push offsets outward (negative min, positive max)
            var expandedMin = new Vector2(-_pulseExpand, -_pulseExpand);
            var expandedMax = new Vector2(_pulseExpand, _pulseExpand);

            // Tween offsetMin (bottom-left pushes outward)
            LMotion.Create(Vector2.zero, expandedMin, _pulseDuration)
                .WithEase(_pulseEase)
                .Bind(_pulseRect, static (v, rt) => rt.offsetMin = v)
                .AddTo(_pulseHandles);

            // Tween offsetMax (top-right pushes outward) + fade alpha to 0, deactivate on complete
            LMotion.Create(Vector2.zero, expandedMax, _pulseDuration)
                .WithEase(_pulseEase)
                .Bind(_pulseRect, static (v, rt) => rt.offsetMax = v)
                .AddTo(_pulseHandles);

            LMotion.Create(startColor.a, 0f, _pulseDuration)
                .WithEase(_pulseEase)
                .WithOnComplete(() => _pulseRect.gameObject.SetActive(false))
                .Bind(_pulseGhostImage, static (a, img) =>
                {
                    var c = img.color;
                    c.a = a;
                    img.color = c;
                })
                .AddTo(_pulseHandles);
        }

        // ─── Color Helpers ──────────────────────────────────────────

        private void TweenColors(Color targetImageColor, Color targetTextColor)
        {
            if (_image != null)
            {
                _imageColorTween = LMotion.Create(_image.color, targetImageColor, _tweenDuration)
                    .WithEase(Ease.OutQuad)
                    .Bind(_image, static (c, img) => img.color = c);
            }

            if (_label != null)
            {
                _textColorTween = LMotion.Create(_label.color, targetTextColor, _tweenDuration)
                    .WithEase(Ease.OutQuad)
                    .Bind(_label, static (c, txt) => txt.color = c);
            }
        }

        private void SetState(Color imageColor, Color textColor, Material material)
        {
            if (_image != null) {
                _image.color = imageColor;
                _image.material = material;
            }
            if (_label != null) _label.color = textColor;
        }

        // ─── Cleanup ────────────────────────────────────────────────

        private void CancelAllTweens()
        {
            if (_scaleTween.IsActive()) _scaleTween.TryCancel();
            if (_imageColorTween.IsActive()) _imageColorTween.TryCancel();
            if (_textColorTween.IsActive()) _textColorTween.TryCancel();
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