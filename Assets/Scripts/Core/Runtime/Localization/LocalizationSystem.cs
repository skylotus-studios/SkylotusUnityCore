using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// Localization system supporting JSON language files, variable interpolation,
    /// pluralization, and runtime language switching.
    ///
    /// Language files should be placed in Resources/Localization/ as flat JSON:
    /// <code>
    /// // Resources/Localization/en.json
    /// {
    ///   "menu.play": "Play Game",
    ///   "items.count": "{count} item|{count} items",
    ///   "greeting": "Hello, {name}!"
    /// }
    /// </code>
    /// </summary>
    public class LocalizationSystem
    {
        /// <summary>All loaded languages: language code → (key → localized string).</summary>
        private readonly Dictionary<string, Dictionary<string, string>> _languages = new();

        /// <summary>Cached reference to the current language's string dictionary.</summary>
        private Dictionary<string, string> _currentStrings = new();

        /// <summary>ISO code of the currently active language.</summary>
        private string _currentLanguage = "en";

        /// <summary>ISO code of the fallback language used when a key is missing from the current language.</summary>
        private string _fallbackLanguage = "en";

        /// <summary>The currently active language code.</summary>
        public string CurrentLanguage => _currentLanguage;

        /// <summary>Fired when the active language changes.</summary>
        public event Action<string> OnLanguageChanged;

        /// <summary>
        /// Load a language file from Resources/Localization/{languageCode}.json.
        /// No-op if the language is already loaded.
        /// </summary>
        /// <param name="languageCode">ISO language code (e.g. "en", "fr", "ja").</param>
        public void LoadLanguage(string languageCode)
        {
            if (_languages.ContainsKey(languageCode)) return;

            var asset = Resources.Load<TextAsset>($"Localization/{languageCode}");
            if (asset == null)
            {
                GameLogger.LogWarning("Localization", $"Language file not found: {languageCode}");
                return;
            }

            var dict = ParseJson(asset.text);
            _languages[languageCode] = dict;
            GameLogger.Log("Localization", $"Loaded language: {languageCode} ({dict.Count} keys)");
        }

        /// <summary>
        /// Load a language from a raw JSON string (useful for runtime-downloaded translations).
        /// </summary>
        /// <param name="languageCode">ISO language code.</param>
        /// <param name="json">JSON string with flat key-value pairs.</param>
        public void LoadLanguageFromString(string languageCode, string json)
        {
            _languages[languageCode] = ParseJson(json);
        }

        /// <summary>
        /// Set the active language. Loads from Resources if not already loaded.
        /// Publishes <see cref="OnLanguageChangedEvent"/> via EventBus.
        /// </summary>
        /// <param name="languageCode">ISO language code to activate.</param>
        public void SetLanguage(string languageCode)
        {
            if (!_languages.ContainsKey(languageCode))
                LoadLanguage(languageCode);

            if (!_languages.ContainsKey(languageCode))
            {
                GameLogger.LogError("Localization", $"Cannot switch to '{languageCode}': not loaded");
                return;
            }

            _currentLanguage = languageCode;
            _currentStrings = _languages[languageCode];

            OnLanguageChanged?.Invoke(languageCode);
            EventBus.Publish(new OnLanguageChangedEvent { LanguageCode = languageCode });
            GameLogger.Log("Localization", $"Language set to: {languageCode}");
        }

        /// <summary>
        /// Get a localized string by key. Returns the fallback language string if missing
        /// from the current language, or "[key]" if missing from both.
        /// </summary>
        /// <param name="key">The localization key (e.g. "menu.play").</param>
        /// <returns>The localized string.</returns>
        public string Get(string key)
        {
            if (_currentStrings.TryGetValue(key, out var value))
                return value;

            // Try the fallback language
            if (_languages.TryGetValue(_fallbackLanguage, out var fallback) &&
                fallback.TryGetValue(key, out var fbValue))
                return fbValue;

            GameLogger.LogWarning("Localization", $"Missing key: {key}");
            return $"[{key}]";
        }

        /// <summary>
        /// Get a localized string with variable interpolation.
        /// Variables are referenced in the string as {name} and replaced at runtime.
        /// </summary>
        /// <param name="key">The localization key.</param>
        /// <param name="variables">Name-value pairs to substitute into the string.</param>
        /// <returns>The localized string with variables replaced.</returns>
        public string Get(string key, params (string name, object value)[] variables)
        {
            var text = Get(key);
            foreach (var (name, value) in variables)
                text = text.Replace($"{{{name}}}", value.ToString());
            return text;
        }

        /// <summary>
        /// Get a pluralized string. The localization value should contain singular|plural forms
        /// separated by a pipe character. Variables are substituted after form selection.
        /// </summary>
        /// <param name="key">The localization key.</param>
        /// <param name="count">The count used to choose singular (1) or plural (other).</param>
        /// <param name="variables">Name-value pairs to substitute.</param>
        /// <returns>The correct plural form with variables replaced.</returns>
        public string GetPlural(string key, int count, params (string name, object value)[] variables)
        {
            var raw = Get(key);
            var forms = raw.Split('|');

            string text;
            if (forms.Length >= 2)
                text = count == 1 ? forms[0].Trim() : forms[1].Trim();
            else
                text = raw;

            foreach (var (name, value) in variables)
                text = text.Replace($"{{{name}}}", value.ToString());
            return text;
        }

        /// <summary>Check if a key exists in the current language.</summary>
        /// <param name="key">The localization key.</param>
        /// <returns>True if the key is defined.</returns>
        public bool HasKey(string key) => _currentStrings.ContainsKey(key);

        /// <summary>Get all loaded language codes.</summary>
        /// <returns>Array of ISO language codes.</returns>
        public string[] GetAvailableLanguages()
        {
            var codes = new string[_languages.Count];
            _languages.Keys.CopyTo(codes, 0);
            return codes;
        }

        /// <summary>
        /// Set the fallback language used when a key is missing from the active language.
        /// </summary>
        /// <param name="languageCode">ISO language code for the fallback.</param>
        public void SetFallbackLanguage(string languageCode)
        {
            _fallbackLanguage = languageCode;
        }

        /// <summary>
        /// Parse a flat JSON object ({"key": "value", ...}) into a dictionary.
        /// Handles escaped newlines, quotes, and backslashes.
        /// </summary>
        private Dictionary<string, string> ParseJson(string json)
        {
            var dict = new Dictionary<string, string>();
            var matches = Regex.Matches(json, "\"([^\"]+)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");

            foreach (Match match in matches)
            {
                var k = match.Groups[1].Value;
                var v = match.Groups[2].Value
                    .Replace("\\n", "\n")
                    .Replace("\\\"", "\"")
                    .Replace("\\\\", "\\");
                dict[k] = v;
            }

            return dict;
        }
    }
}
