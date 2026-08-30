using System;
using LitMotion;
using Skylotus.Core.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Skylotus.Core.UI
{
    /// <summary>
    /// A toggle control built on top of a <see cref="Slider"/> (value 0 = off, 1 = on).
    /// Clicking or pressing Submit flips the state and tweens the handle smoothly.
    /// The internal <c>_isOn</c> bool is the source of truth — slider value follows it,
    /// never the other way around. Dragging the slider handle is blocked entirely.
    /// </summary>
    [AddComponentMenu("Skylotus/UI/Toggle Slider")]
    [RequireComponent(typeof(Slider))]
    public class ToggleSlider : MonoBehaviour,
        IPointerClickHandler, ISubmitHandler,
        IDragHandler, IInitializePotentialDragHandler, IBeginDragHandler, IEndDragHandler
    {
        [Header("Tween")]
        [SerializeField] private float _tweenDuration = 0.25f;

        [Header("Audio")]
        [Tooltip("Sound played when the toggle changes state.")]
        [SerializeField] private AudioClip _toggleSound;

        /// <summary>Fired after each toggle completes with the new IsOn value.</summary>
        public event Action<bool> OnValueChanged;

        /// <summary>Source-of-truth toggle state.</summary>
        public bool IsOn => _isOn;

        private bool _isOn;
        private bool _tweening;
        private Slider _slider;
        private MotionHandle _tween;
        private Image _fillImage;
        private Image _handleImage;
        private Image _backgroundImage;
        private AudioManager _audio;

        private Color _handleColor = Color.black;
        private Color _fillColor = Color.white;
        private Color _backgroundColor = Color.grey;

        // ─── Lifecycle ──────────────────────────────────────────────

        private void Awake() => EnsureInitialized();

        private void EnsureInitialized()
        {
            if (_slider != null) return;

            _slider = GetComponent<Slider>();
            _slider.minValue = 0f;
            _slider.maxValue = 1f;
            _slider.wholeNumbers = false;
            _slider.interactable = false; // we handle all input ourselves

            _fillImage = _slider.fillRect != null ? _slider.fillRect.GetComponent<Image>() : null;
            _handleImage = _slider.handleRect != null ? _slider.handleRect.GetComponent<Image>() : null;

            foreach (var img in GetComponentsInChildren<Image>())
            {
                if (img == _fillImage || img == _handleImage) continue;
                _backgroundImage = img;
                break;
            }

            if (ServiceLocator.TryGet<ColorPalette>(out var palette))
            {
                _handleColor = palette.textPrimary;
                _fillColor = palette.primary;
                _backgroundColor = palette.tertiary;
            }

            ApplyColor();
        }

        private void OnDestroy()
        {
            if (_tween.IsActive()) _tween.Cancel();
        }

        // ─── Public API ─────────────────────────────────────────────

        /// <summary>
        /// Set the toggle state immediately without animating or firing the event.
        /// </summary>
        public void SetWithoutNotify(bool on)
        {
            if (_tween.IsActive()) _tween.Cancel();
            _tweening = false;
            _isOn = on;
            EnsureInitialized();
            _slider.SetValueWithoutNotify(on ? 1f : 0f);
            ApplyColor();
        }

        // ─── Public API ─────────────────────────────────────────────

        /// <summary>
        /// Programmatically toggle the switch. Called by <see cref="SettingsRow"/> on Submit.
        /// </summary>
        public void PerformToggle() => Toggle();

        // ─── Input — click / submit toggle ──────────────────────────

        public void OnPointerClick(PointerEventData _) => Toggle();
        public void OnSubmit(BaseEventData _) => Toggle();

        // Block all drag so the slider handle never moves from pointer position
        public void OnInitializePotentialDrag(PointerEventData _) { }
        public void OnBeginDrag(PointerEventData _) { }
        public void OnDrag(PointerEventData _) { }
        public void OnEndDrag(PointerEventData _) { }

        // ─── Internal ───────────────────────────────────────────────

        private void Toggle()
        {
            if (_tweening) return;

            _isOn = !_isOn;
            _tweening = true;
            float from = _slider.value;
            float to = _isOn ? 1f : 0f;

            PlaySound();

            _tween = LMotion.Create(from, to, _tweenDuration)
                .WithEase(LitMotion.Ease.InOutSine)
                .WithOnComplete(() => OnTweenComplete(_isOn))
                .Bind(v =>
                {
                    _slider.SetValueWithoutNotify(v);
                    ApplyColor();
                })
                .AddTo(gameObject);
        }

        private void OnTweenComplete(bool result)
        {
            _tweening = false;
            ApplyColor();
            OnValueChanged?.Invoke(result);

            // Re-select ourselves for gamepad so the user can press Submit again
            if (EventSystem.current != null &&
                EventSystem.current.currentSelectedGameObject == gameObject)
            {
                EventSystem.current.SetSelectedGameObject(null);
                EventSystem.current.SetSelectedGameObject(gameObject);
            }
        }

        private void PlaySound()
        {
            if (_toggleSound == null) return;
            if (_audio == null) ServiceLocator.TryGet(out _audio);
            _audio?.PlayUI(_toggleSound);
        }

        private void ApplyColor()
        {
            if (_fillImage != null) _fillImage.color = _fillColor;
            if (_handleImage != null) _handleImage.color = _handleColor;
            if (_backgroundImage != null) _backgroundImage.color = _backgroundColor;
        }
    }
}
