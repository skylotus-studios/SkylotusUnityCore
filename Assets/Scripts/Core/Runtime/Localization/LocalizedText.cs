using TMPro;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// Attach to any TextMeshPro component to automatically display a localized string.
    /// The text updates whenever the active language changes.
    ///
    /// Set the <see cref="_localizationKey"/> in the inspector (e.g. "menu.play").
    /// </summary>
    [RequireComponent(typeof(TMP_Text))]
    public class LocalizedText : MonoBehaviour
    {
        [Tooltip("The localization key to look up (e.g. 'menu.play').")]
        [SerializeField] private string _localizationKey;

        /// <summary>Cached reference to the TMP_Text component.</summary>
        private TMP_Text _text;

        /// <summary>Unity Awake — cache the text component.</summary>
        private void Awake()
        {
            _text = GetComponent<TMP_Text>();
        }

        /// <summary>Unity OnEnable — subscribe to language changes and update text immediately.</summary>
        private void OnEnable()
        {
            EventBus.Subscribe<OnLanguageChangedEvent>(OnLanguageChanged);
            UpdateText();
        }

        /// <summary>Unity OnDisable — unsubscribe from language change events.</summary>
        private void OnDisable()
        {
            EventBus.Unsubscribe<OnLanguageChangedEvent>(OnLanguageChanged);
        }

        /// <summary>
        /// Change the localization key at runtime and refresh the display.
        /// </summary>
        /// <param name="key">The new localization key.</param>
        public void SetKey(string key)
        {
            _localizationKey = key;
            UpdateText();
        }

        /// <summary>React to language changes by re-fetching the localized string.</summary>
        private void OnLanguageChanged(OnLanguageChangedEvent evt)
        {
            UpdateText();
        }

        /// <summary>Fetch the localized string and apply it to the TMP component.</summary>
        private void UpdateText()
        {
            if (string.IsNullOrEmpty(_localizationKey) || _text == null) return;

            if (ServiceLocator.TryGet<LocalizationSystem>(out var loc))
                _text.text = loc.Get(_localizationKey);
        }
    }
}
