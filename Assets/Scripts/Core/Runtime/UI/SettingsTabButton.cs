using LitMotion;
using Skylotus.Core.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Skylotus
{
    /// <summary>
    /// Tab button for the settings screen. NOT a ButtonExtended — it lives only in tab headers.
    ///
    /// Visual behaviour:
    /// • Normal      — palette.primary background, palette.textPrimary label, 0° Z rotation.
    /// • Hover/Select — palette.secondary background, palette.textSecondary label, +2° Z tilt.
    /// • Active tab  — forced into the selected look via <see cref="SetTabActive"/>.
    ///
    /// Gamepad navigation cycles through tabs via L/R bumpers (handled by SettingsTabGroup);
    /// no cursor needs to follow individual tab buttons.
    /// </summary>
    [AddComponentMenu("Skylotus/UI/Settings Tab Button")]
    [RequireComponent(typeof(Image))]
    public class SettingsTabButton : Selectable,
        IPointerClickHandler, ISubmitHandler
    {
        [Header("Tween")]
        [SerializeField] private float _tweenDuration = 0.15f;
        [SerializeField] private float _hoverTiltDeg = 2f;

        [Header("Audio")]
        [Tooltip("Sound played when the tab is selected / cycled.")]
        [SerializeField] private AudioClip _tabSound;

        [Header("Events")]
        [SerializeField] private UnityEvent _onClick = new();

        /// <summary>Invoked when the tab is clicked or submitted via gamepad.</summary>
        public UnityEvent OnClick => _onClick;

        // ─── Runtime ────────────────────────────────────────────────

        private Image _image;
        private TMP_Text _label;

        private Color _primaryColor = Color.white;
        private Color _secondaryColor = new(0.85f, 0.85f, 0.85f, 1f);
        private Color _textPrimaryColor = Color.black;
        private Color _textSecondaryColor = Color.white;

        private MotionHandle _colorTween;
        private MotionHandle _tiltTween;
        private SelectionState _lastState = SelectionState.Normal;
        private AudioManager _audio;

        // ─── Lifecycle ──────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();
            _image = GetComponent<Image>();
            _label = GetComponentInChildren<TMP_Text>();

            if (ServiceLocator.TryGet<ColorPalette>(out var palette))
            {
                _primaryColor = palette.primary;
                _secondaryColor = palette.secondary;
                _textPrimaryColor = palette.textPrimary;
                _textSecondaryColor = palette.textSecondary;
            }

            ApplyColors(_primaryColor, _textPrimaryColor, instant: true);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (_colorTween.IsActive()) _colorTween.TryCancel();
            if (_tiltTween.IsActive()) _tiltTween.TryCancel();
        }

        // ─── Selectable overrides ───────────────────────────────────

        protected override void DoStateTransition(SelectionState state, bool instant)
        {
            base.DoStateTransition(state, instant);
            if (!Application.isPlaying) return;
            if (state == _lastState) return;
            _lastState = state;

            bool highlighted = state == SelectionState.Highlighted
                            || state == SelectionState.Selected
                            || state == SelectionState.Pressed;

            SetVisual(highlighted, instant);
        }

        // ─── Input ──────────────────────────────────────────────────

        public void OnPointerClick(PointerEventData _)
        {
            if (!IsInteractable()) return;
            PlaySound();
            _onClick?.Invoke();
        }

        public void OnSubmit(BaseEventData _)
        {
            if (!IsInteractable()) return;
            PlaySound();
            _onClick?.Invoke();
        }

        // ─── Public API ─────────────────────────────────────────────

        /// <summary>
        /// Force the active/inactive visual state from SettingsTabGroup without
        /// going through the EventSystem selection state.
        /// </summary>
        public void SetTabActive(bool active)
        {
            SetVisual(active, instant: false);
        }

        // ─── Internals ──────────────────────────────────────────────

        private void SetVisual(bool highlighted, bool instant)
        {
            float tiltTarget = highlighted ? _hoverTiltDeg : 2f;

            // ── Tilt ───────────────────────────────────────────────
            if (_tiltTween.IsActive()) _tiltTween.TryCancel();

            float currentZ = transform.localEulerAngles.z;
            if (currentZ > 180f) currentZ -= 360f;   // normalise 358° → -2°

            if (instant)
            {
                var e = transform.localEulerAngles;
                e.z = tiltTarget;
                transform.localEulerAngles = e;
            }
            else
            {
                _tiltTween = LMotion.Create(currentZ, tiltTarget, _tweenDuration)
                    .WithEase(Ease.OutBack)
                    .Bind(transform, static (z, t) =>
                    {
                        var angles = t.localEulerAngles;
                        angles.z = z;
                        t.localEulerAngles = angles;
                    });
            }

            // ── Colors ─────────────────────────────────────────────
            var imgTarget = highlighted ? _secondaryColor : _primaryColor;
            var txtTarget = highlighted ? _textSecondaryColor : _textPrimaryColor;
            ApplyColors(imgTarget, txtTarget, instant);
        }

        private void ApplyColors(Color imgColor, Color txtColor, bool instant)
        {
            if (_colorTween.IsActive()) _colorTween.TryCancel();

            if (instant)
            {
                if (_image != null) _image.color = imgColor;
                if (_label != null) _label.color = txtColor;
                return;
            }

            if (_image != null)
            {
                _colorTween = LMotion.Create(_image.color, imgColor, _tweenDuration)
                    .WithEase(Ease.OutQuad)
                    .Bind(_image, static (c, img) => img.color = c);
            }

            if (_label != null)
            {
                LMotion.Create(_label.color, txtColor, _tweenDuration)
                    .WithEase(Ease.OutQuad)
                    .Bind(_label, static (c, txt) => txt.color = c);
            }
        }

        private void PlaySound()
        {
            if (_tabSound == null) return;
            if (_audio == null) ServiceLocator.TryGet(out _audio);
            _audio?.PlayUI(_tabSound);
        }
    }
}
