using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Skylotus.Core.UI
{
    /// <summary>
    /// A settings row control that cycles through a list of string options via
    /// left (◀) and right (▶) buttons. Use this wherever a dropdown would normally
    /// appear — resolution, quality level, fullscreen mode, language, etc.
    ///
    /// <b>Expected children (assign in inspector):</b>
    /// <code>
    ///   Row_Xxx
    ///   ├── Label_Text    (TMP_Text — static row label, e.g. "Resolution")
    ///   ├── Btn_Prev      (Button — ◀ triangle)
    ///   ├── Value_Text    (TMP_Text — currently selected option, ref _valueLabel)
    ///   └── Btn_Next      (Button — ▶ triangle)
    /// </code>
    /// </summary>
    [AddComponentMenu("Skylotus/UI/Settings Cycle Row")]
    public class SettingsCycleRow : MonoBehaviour
    {
        [SerializeField] private Button _prevButton;
        [SerializeField] private Button _nextButton;
        [SerializeField] private TMP_Text _valueLabel;

        [Header("Audio")]
        [SerializeField] private AudioClip _cycleSound;

        private string[] _options;
        private int _index;
        private AudioManager _audio;

        /// <summary>Currently selected index.</summary>
        public int Index => _index;

        /// <summary>Fired when the selected index changes.</summary>
        public event Action<int> OnValueChanged;

        private void OnEnable()
        {
            _prevButton?.onClick.AddListener(StepPrev);
            _nextButton?.onClick.AddListener(StepNext);
        }

        private void OnDisable()
        {
            _prevButton?.onClick.RemoveListener(StepPrev);
            _nextButton?.onClick.RemoveListener(StepNext);
        }

        /// <summary>Set the option list and jump to <paramref name="initialIndex"/> without firing the event.</summary>
        public void SetOptions(string[] options, int initialIndex = 0)
        {
            _options = options;
            SetIndex(initialIndex, notify: false);
        }

        /// <summary>Programmatically move to a specific index.</summary>
        /// <param name="notify">If true, fires <see cref="OnValueChanged"/>.</param>
        public void SetIndex(int index, bool notify = true)
        {
            if (_options == null || _options.Length == 0) return;
            _index = Mathf.Clamp(index, 0, _options.Length - 1);
            if (_valueLabel != null) _valueLabel.text = _options[_index];
            if (notify) OnValueChanged?.Invoke(_index);
        }

        /// <summary>Cycle to the previous option (wraps). Called by SettingsRow on left.</summary>
        public void StepPrev()
        {
            if (_options == null || _options.Length == 0) return;
            PlaySound();
            SetIndex((_index - 1 + _options.Length) % _options.Length);
        }

        /// <summary>Cycle to the next option (wraps). Called by SettingsRow on right.</summary>
        public void StepNext()
        {
            if (_options == null || _options.Length == 0) return;
            PlaySound();
            SetIndex((_index + 1) % _options.Length);
        }

        private void PlaySound()
        {
            if (_cycleSound == null) return;
            if (_audio == null) ServiceLocator.TryGet(out _audio);
            _audio?.PlayUI(_cycleSound);
        }
    }
}
