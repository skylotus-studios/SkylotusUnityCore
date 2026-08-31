using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// CLDR plural categories. A language uses a subset of these; the ordered subset a
    /// language uses is declared by its <see cref="PluralRuleSet"/> and defines the order
    /// of the pipe-separated forms in the translation file.
    /// </summary>
    public enum PluralCategory
    {
        /// <summary>Used by Arabic for exactly zero.</summary>
        Zero,

        /// <summary>The singular form in most languages.</summary>
        One,

        /// <summary>The dual form (Arabic, Slovenian).</summary>
        Two,

        /// <summary>The "paucal" form used by the Slavic languages for small counts.</summary>
        Few,

        /// <summary>The form used by the Slavic languages for larger counts.</summary>
        Many,

        /// <summary>The catch-all form. Every rule set must contain this or a superset of forms.</summary>
        Other
    }

    /// <summary>
    /// Maps a count to a <see cref="PluralCategory"/> for one language, and declares the
    /// order in which that language's forms appear in a pipe-separated translation value.
    ///
    /// The built-in rule sets follow the CLDR integer rules. Register additional or
    /// replacement rule sets with <see cref="LocalizationSystem.RegisterPluralRule"/>.
    /// </summary>
    /// <remarks>
    /// Only integer counts are modelled. CLDR distinguishes further categories for
    /// fractional values (Russian "1.5" is <c>other</c>, not <c>one</c>); if the project
    /// ever pluralizes a decimal, this type needs a <c>double</c> overload.
    /// </remarks>
    public sealed class PluralRuleSet
    {
        /// <summary>Ordered categories this language uses; index into the pipe-separated forms.</summary>
        private readonly PluralCategory[] _forms;

        /// <summary>The rule function mapping an integer count to a category.</summary>
        private readonly Func<int, PluralCategory> _select;

        /// <summary>
        /// Create a rule set.
        /// </summary>
        /// <param name="name">Human-readable name used in log messages (e.g. "polish").</param>
        /// <param name="forms">Ordered categories, matching the order of pipe-separated forms in the file.</param>
        /// <param name="select">Function mapping an integer count to one of <paramref name="forms"/>.</param>
        public PluralRuleSet(string name, PluralCategory[] forms, Func<int, PluralCategory> select)
        {
            if (forms == null || forms.Length == 0)
                throw new ArgumentException("A plural rule set needs at least one form.", nameof(forms));

            Name = name ?? "custom";
            _forms = forms;
            _select = select ?? throw new ArgumentNullException(nameof(select));
        }

        /// <summary>Human-readable name of this rule set, used in warnings.</summary>
        public string Name { get; }

        /// <summary>How many pipe-separated forms a translation value must supply for this language.</summary>
        public int FormCount => _forms.Length;

        /// <summary>The ordered categories this language uses.</summary>
        public IReadOnlyList<PluralCategory> Forms => _forms;

        /// <summary>Pick the plural category for a count.</summary>
        /// <param name="count">The count being described.</param>
        /// <returns>The CLDR category for that count in this language.</returns>
        public PluralCategory Select(int count) => _select(count);

        /// <summary>
        /// Index of the pipe-separated form to use for a count.
        /// Falls back to the <see cref="PluralCategory.Other"/> slot, then to the last form.
        /// </summary>
        /// <param name="count">The count being described.</param>
        /// <returns>Zero-based index into the pipe-separated forms.</returns>
        public int FormIndex(int count)
        {
            var category = Select(count);

            for (var i = 0; i < _forms.Length; i++)
                if (_forms[i] == category) return i;

            for (var i = 0; i < _forms.Length; i++)
                if (_forms[i] == PluralCategory.Other) return i;

            return _forms.Length - 1;
        }

        /// <summary>Render the expected form order, e.g. "one|few|many". Used in diagnostics.</summary>
        /// <returns>The form order as a pipe-separated string of lowercase category names.</returns>
        public string DescribeForms()
        {
            var parts = new string[_forms.Length];
            for (var i = 0; i < _forms.Length; i++)
                parts[i] = _forms[i].ToString().ToLowerInvariant();
            return string.Join("|", parts);
        }

        // ─── Built-in rule sets ─────────────────────────────────────

        /// <summary>
        /// English and the many languages sharing its rule (de, nl, es, it, pt, sv, da, el, he, tr).
        /// Forms: <c>one|other</c>.
        /// </summary>
        public static PluralRuleSet English { get; } = new PluralRuleSet(
            "english", new[] { PluralCategory.One, PluralCategory.Other },
            count => Math.Abs(count) == 1 ? PluralCategory.One : PluralCategory.Other);

        /// <summary>
        /// French (and Brazilian Portuguese): zero and one share the singular.
        /// Forms: <c>one|other</c>.
        /// </summary>
        public static PluralRuleSet French { get; } = new PluralRuleSet(
            "french", new[] { PluralCategory.One, PluralCategory.Other },
            count => Math.Abs(count) <= 1 ? PluralCategory.One : PluralCategory.Other);

        /// <summary>
        /// Languages with no grammatical plural (ja, zh, ko, vi, th, id, ms).
        /// Forms: <c>other</c> — a single form, no pipe.
        /// </summary>
        public static PluralRuleSet SingleForm { get; } = new PluralRuleSet(
            "single-form", new[] { PluralCategory.Other },
            _ => PluralCategory.Other);

        /// <summary>
        /// Polish. Forms: <c>one|few|many</c>.
        /// </summary>
        public static PluralRuleSet Polish { get; } = new PluralRuleSet(
            "polish", new[] { PluralCategory.One, PluralCategory.Few, PluralCategory.Many },
            SelectPolish);

        /// <summary>
        /// Russian and Ukrainian. Forms: <c>one|few|many</c>.
        /// </summary>
        public static PluralRuleSet Russian { get; } = new PluralRuleSet(
            "russian", new[] { PluralCategory.One, PluralCategory.Few, PluralCategory.Many },
            SelectRussian);

        /// <summary>
        /// Arabic. Forms: <c>zero|one|two|few|many|other</c> — all six, in that order.
        /// </summary>
        public static PluralRuleSet Arabic { get; } = new PluralRuleSet(
            "arabic",
            new[]
            {
                PluralCategory.Zero, PluralCategory.One, PluralCategory.Two,
                PluralCategory.Few, PluralCategory.Many, PluralCategory.Other
            },
            SelectArabic);

        /// <summary>
        /// Czech and Slovak. Forms: <c>one|few|other</c>.
        /// </summary>
        public static PluralRuleSet Czech { get; } = new PluralRuleSet(
            "czech", new[] { PluralCategory.One, PluralCategory.Few, PluralCategory.Other },
            SelectCzech);

        /// <summary>CLDR integer rule for Polish.</summary>
        /// <param name="count">The count being described.</param>
        /// <returns>one for 1; few for x2-x4 outside the teens; many otherwise.</returns>
        private static PluralCategory SelectPolish(int count)
        {
            var n = Math.Abs(count);
            if (n == 1) return PluralCategory.One;

            var mod10 = n % 10;
            var mod100 = n % 100;
            if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14))
                return PluralCategory.Few;

            return PluralCategory.Many;
        }

        /// <summary>CLDR integer rule for Russian and Ukrainian.</summary>
        /// <param name="count">The count being described.</param>
        /// <returns>one for x1 outside the teens; few for x2-x4 outside the teens; many otherwise.</returns>
        private static PluralCategory SelectRussian(int count)
        {
            var n = Math.Abs(count);
            var mod10 = n % 10;
            var mod100 = n % 100;

            if (mod10 == 1 && mod100 != 11) return PluralCategory.One;
            if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return PluralCategory.Few;

            return PluralCategory.Many;
        }

        /// <summary>CLDR integer rule for Arabic.</summary>
        /// <param name="count">The count being described.</param>
        /// <returns>zero, one, two, few (x03-x10), many (x11-x99) or other.</returns>
        private static PluralCategory SelectArabic(int count)
        {
            var n = Math.Abs(count);
            if (n == 0) return PluralCategory.Zero;
            if (n == 1) return PluralCategory.One;
            if (n == 2) return PluralCategory.Two;

            var mod100 = n % 100;
            if (mod100 >= 3 && mod100 <= 10) return PluralCategory.Few;
            if (mod100 >= 11 && mod100 <= 99) return PluralCategory.Many;

            return PluralCategory.Other;
        }

        /// <summary>CLDR integer rule for Czech and Slovak.</summary>
        /// <param name="count">The count being described.</param>
        /// <returns>one for 1; few for 2-4; other otherwise.</returns>
        private static PluralCategory SelectCzech(int count)
        {
            var n = Math.Abs(count);
            if (n == 1) return PluralCategory.One;
            if (n >= 2 && n <= 4) return PluralCategory.Few;

            return PluralCategory.Other;
        }
    }

    /// <summary>
    /// Thrown when a localization file is not valid JSON, or is valid JSON that a
    /// localization file may not contain (arrays, numbers, duplicate keys).
    ///
    /// The message always names the source, the line, the column, and what was expected —
    /// a broken translation file must never half-load silently.
    /// </summary>
    public class LocalizationParseException : Exception
    {
        /// <summary>
        /// Create a parse exception.
        /// </summary>
        /// <param name="source">Name of the file or stream being parsed (e.g. "en.json").</param>
        /// <param name="line">One-based line number of the offending character.</param>
        /// <param name="column">One-based column number of the offending character.</param>
        /// <param name="detail">What went wrong and what the author should do about it.</param>
        public LocalizationParseException(string source, int line, int column, string detail)
            : base($"{source}({line},{column}): {detail}")
        {
            SourceName = source;
            Line = line;
            Column = column;
            Detail = detail;
        }

        /// <summary>Name of the file or stream being parsed.</summary>
        public string SourceName { get; }

        /// <summary>One-based line number of the offending character.</summary>
        public int Line { get; }

        /// <summary>One-based column number of the offending character.</summary>
        public int Column { get; }

        /// <summary>The message without the source/line/column prefix.</summary>
        public string Detail { get; }
    }

    /// <summary>
    /// Result of <see cref="LocalizationSystem.ValidateLanguages"/>: which keys are missing
    /// from which loaded language, and which values have the wrong number of plural forms.
    /// </summary>
    public sealed class LocalizationValidationReport
    {
        /// <summary>Missing keys per language code, each array sorted ordinally.</summary>
        private readonly Dictionary<string, string[]> _missingKeys;

        /// <summary>Plural-form-count problems, one human-readable line each.</summary>
        private readonly string[] _pluralFormIssues;

        /// <summary>
        /// Create a report. Built by <see cref="LocalizationSystem.ValidateLanguages"/>.
        /// </summary>
        /// <param name="referenceLanguage">The language treated as the source of truth.</param>
        /// <param name="languages">Every loaded language code, sorted.</param>
        /// <param name="expectedKeyCount">Size of the union of keys across all loaded languages.</param>
        /// <param name="missingKeys">Per-language list of keys absent from that language.</param>
        /// <param name="pluralFormIssues">Values whose form count does not match the language's rule set.</param>
        public LocalizationValidationReport(
            string referenceLanguage,
            string[] languages,
            int expectedKeyCount,
            Dictionary<string, string[]> missingKeys,
            string[] pluralFormIssues)
        {
            ReferenceLanguage = referenceLanguage;
            Languages = languages ?? Array.Empty<string>();
            ExpectedKeyCount = expectedKeyCount;
            _missingKeys = missingKeys ?? new Dictionary<string, string[]>(StringComparer.Ordinal);
            _pluralFormIssues = pluralFormIssues ?? Array.Empty<string>();

            var total = 0;
            foreach (var pair in _missingKeys)
                total += pair.Value.Length;
            TotalMissingKeys = total;
        }

        /// <summary>The language treated as the source of truth for the report header.</summary>
        public string ReferenceLanguage { get; }

        /// <summary>Every loaded language code, sorted ordinally.</summary>
        public string[] Languages { get; }

        /// <summary>Size of the union of keys across all loaded languages.</summary>
        public int ExpectedKeyCount { get; }

        /// <summary>Total number of missing key/language pairs.</summary>
        public int TotalMissingKeys { get; }

        /// <summary>Values whose pipe-separated form count does not match their language's rule set.</summary>
        public string[] PluralFormIssues => _pluralFormIssues;

        /// <summary>True when every loaded language has every key and every plural value is well-formed.</summary>
        public bool IsValid => TotalMissingKeys == 0 && _pluralFormIssues.Length == 0;

        /// <summary>Keys absent from one language.</summary>
        /// <param name="languageCode">ISO language code.</param>
        /// <returns>Sorted array of missing keys; empty if the language is complete or not loaded.</returns>
        public string[] GetMissingKeys(string languageCode)
        {
            return languageCode != null && _missingKeys.TryGetValue(languageCode, out var keys)
                ? keys
                : Array.Empty<string>();
        }

        /// <summary>Render the report as a multi-line, log-friendly string.</summary>
        /// <returns>A human-readable summary.</returns>
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("Localization validation: ")
              .Append(Languages.Length).Append(" language(s), ")
              .Append(ExpectedKeyCount).Append(" key(s) expected, reference '")
              .Append(ReferenceLanguage).Append("'.");

            if (IsValid)
            {
                sb.Append(" No problems found.");
                return sb.ToString();
            }

            foreach (var language in Languages)
            {
                var missing = GetMissingKeys(language);
                if (missing.Length == 0) continue;

                sb.Append('\n').Append("  ").Append(language).Append(" is missing ")
                  .Append(missing.Length).Append(" key(s): ")
                  .Append(string.Join(", ", missing));
            }

            foreach (var issue in _pluralFormIssues)
                sb.Append('\n').Append("  ").Append(issue);

            return sb.ToString();
        }
    }

    /// <summary>
    /// Localization system supporting JSON language files, variable interpolation,
    /// per-language pluralization, and runtime language switching.
    ///
    /// Language files live in Resources/Localization/ as a JSON object. Values may be
    /// strings or nested objects; a nested object flattens to dotted keys, so the two
    /// files below are equivalent:
    /// <code>
    /// // Resources/Localization/en.json
    /// {
    ///   "menu.play": "Play Game",
    ///   "items.count": "{count} item|{count} items",
    ///   "greeting": "Hello, {name}!"
    /// }
    ///
    /// // the same file, nested
    /// {
    ///   "menu": { "play": "Play Game" },
    ///   "items": { "count": "{count} item|{count} items" },
    ///   "greeting": "Hello, {name}!"
    /// }
    /// </code>
    ///
    /// Parsing is strict: anything that is not a quoted string or a nested object — an
    /// array, a number, a bare <c>true</c>, a duplicate key, an unknown escape — raises a
    /// <see cref="LocalizationParseException"/> naming the line and column, and the file is
    /// rejected whole rather than half-loaded.
    /// </summary>
    /// <remarks>
    /// <b>Font atlases for non-Latin scripts.</b> Nothing in this class draws anything; the
    /// glyphs come from whatever TMP font asset the <see cref="LocalizedText"/> component
    /// happens to reference, and the shipped font assets cover Latin-1 only. Switching to a
    /// language this project has no atlas for renders every character as the TMP missing
    /// glyph, with no error from here. Before shipping a non-Latin language:
    ///
    /// <list type="bullet">
    /// <item><b>Latin + Cyrillic + Greek</b> (ru, uk, el): one static atlas can hold all
    ///       three. Add the code points to the source font asset's character set and
    ///       regenerate; roughly 600 glyphs, a single 1024x1024 atlas page.</item>
    /// <item><b>CJK</b> (ja, zh, ko): a full atlas is 7,000-20,000+ glyphs and will not fit a
    ///       static texture at a usable size. Use a <i>Dynamic</i> atlas font asset with
    ///       multi-atlas textures enabled, and accept the runtime rasterization cost — or
    ///       generate a static subset from the shipped translation strings as a build step.
    ///       Either way the CJK face is a separate TMP_FontAsset added to the primary font's
    ///       fallback list (or to TMP Settings' global fallback list).</item>
    /// <item><b>RTL</b> (ar, he): <b>not supported.</b> TMP's per-component "Enable RTL"
    ///       toggle reverses the character order but performs no Unicode bidi resolution and
    ///       no Arabic contextual shaping or ligature substitution, so Arabic renders as
    ///       disconnected isolated letterforms. Real RTL needs a shaping pass
    ///       (HarfBuzz-class) ahead of TMP. Do not promise Arabic without budgeting for it —
    ///       the plural rules in <see cref="PluralRuleSet.Arabic"/> are correct, the
    ///       rendering is not.</item>
    /// </list>
    ///
    /// No font swapping happens on <see cref="OnLanguageChangedEvent"/> today. Whoever adds
    /// a non-Latin language owns wiring a per-language font asset into
    /// <see cref="LocalizedText"/>, which this class deliberately does not reach into.
    /// </remarks>
    public class LocalizationSystem
    {
        /// <summary>Log category used by every message from this system.</summary>
        private const string LogCategory = "Localization";

        /// <summary>Guard against pathological nesting in a hand-edited or downloaded file.</summary>
        private const int MaxNestingDepth = 32;

        /// <summary>All loaded languages: language code → (key → localized string).</summary>
        private readonly Dictionary<string, Dictionary<string, string>> _languages = new();

        /// <summary>Plural rule sets by language code (exact match first, then primary subtag).</summary>
        private readonly Dictionary<string, PluralRuleSet> _pluralRules =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Cached reference to the current language's string dictionary.</summary>
        private Dictionary<string, string> _currentStrings = new();

        /// <summary>ISO code of the currently active language.</summary>
        private string _currentLanguage = "en";

        /// <summary>ISO code of the fallback language used when a key is missing from the current language.</summary>
        private string _fallbackLanguage = "en";

        /// <summary>
        /// Create a localization system pre-populated with the built-in CLDR plural rules.
        /// </summary>
        public LocalizationSystem()
        {
            RegisterDefaultPluralRules();
        }

        /// <summary>The currently active language code.</summary>
        public string CurrentLanguage => _currentLanguage;

        /// <summary>Fired when the active language changes.</summary>
        public event Action<string> OnLanguageChanged;

        /// <summary>
        /// Load a language file from Resources/Localization/{languageCode}.json.
        /// No-op if the language is already loaded. A file that fails to parse is rejected
        /// whole and logged as an error; nothing partial is registered.
        /// </summary>
        /// <param name="languageCode">ISO language code (e.g. "en", "fr", "ja").</param>
        /// <returns>True if the language is loaded and usable after this call.</returns>
        public bool LoadLanguage(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode))
            {
                GameLogger.LogError(LogCategory, "LoadLanguage called with an empty language code.");
                return false;
            }

            if (_languages.ContainsKey(languageCode)) return true;

            var asset = Resources.Load<TextAsset>($"Localization/{languageCode}");
            if (asset == null)
            {
                GameLogger.LogWarning(LogCategory, $"Language file not found: {languageCode}");
                return false;
            }

            return LoadLanguageFromString(languageCode, asset.text, $"{languageCode}.json");
        }

        /// <summary>
        /// Load a language from a raw JSON string (useful for runtime-downloaded translations).
        /// Replaces the language if it is already loaded. A file that fails to parse is
        /// rejected whole: the previously loaded copy, if any, is left untouched.
        /// </summary>
        /// <param name="languageCode">ISO language code.</param>
        /// <param name="json">JSON object with string values, optionally nested.</param>
        /// <param name="sourceName">Name used in parse errors; defaults to the language code.</param>
        /// <returns>True if the JSON parsed and the language is now loaded.</returns>
        public bool LoadLanguageFromString(string languageCode, string json, string sourceName = null)
        {
            if (string.IsNullOrEmpty(languageCode))
            {
                GameLogger.LogError(LogCategory, "LoadLanguageFromString called with an empty language code.");
                return false;
            }

            try
            {
                var dict = ParseLanguageJson(json, sourceName ?? languageCode);
                _languages[languageCode] = dict;

                if (languageCode == _currentLanguage)
                    _currentStrings = dict;

                GameLogger.Log(LogCategory, $"Loaded language: {languageCode} ({dict.Count} keys)");
                return true;
            }
            catch (LocalizationParseException e)
            {
                GameLogger.LogError(LogCategory,
                    $"Language '{languageCode}' was NOT loaded — {e.Message}");
                return false;
            }
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
                GameLogger.LogError(LogCategory, $"Cannot switch to '{languageCode}': not loaded");
                return;
            }

            _currentLanguage = languageCode;
            _currentStrings = _languages[languageCode];

            OnLanguageChanged?.Invoke(languageCode);
            EventBus.Publish(new OnLanguageChangedEvent { LanguageCode = languageCode });
            GameLogger.Log(LogCategory, $"Language set to: {languageCode}");
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

            GameLogger.LogWarning(LogCategory, $"Missing key: {key}");
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
            return Interpolate(text, variables);
        }

        /// <summary>
        /// Get a pluralized string. The localization value holds the language's plural forms
        /// separated by pipe characters, in the order declared by that language's
        /// <see cref="PluralRuleSet"/> — <c>one|other</c> for English, <c>one|few|many</c> for
        /// Polish and Russian, all six categories for Arabic, a single form for Japanese.
        /// Variables are substituted after form selection, and a bare <c>{count}</c> left
        /// over is filled in with <paramref name="count"/>.
        /// </summary>
        /// <param name="key">The localization key.</param>
        /// <param name="count">The count used to choose the plural form.</param>
        /// <param name="variables">Name-value pairs to substitute.</param>
        /// <returns>The correct plural form with variables replaced.</returns>
        public string GetPlural(string key, int count, params (string name, object value)[] variables)
        {
            var raw = Get(key);
            var text = SelectPluralForm(key, raw, count);

            text = Interpolate(text, variables);
            return text.Replace("{count}", count.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Install or replace the plural rule set for a language. Registering "pt-BR"
        /// overrides "pt" for that locale only; registering "pt" covers every pt-* locale
        /// with no exact entry.
        /// </summary>
        /// <param name="languageCode">ISO language code or full locale (e.g. "pl", "pt-BR").</param>
        /// <param name="rules">The rule set to use for that language.</param>
        public void RegisterPluralRule(string languageCode, PluralRuleSet rules)
        {
            if (string.IsNullOrEmpty(languageCode))
                throw new ArgumentException("Language code must not be empty.", nameof(languageCode));
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));

            _pluralRules[languageCode] = rules;
        }

        /// <summary>
        /// Get the plural rule set for a language: exact locale match, then primary subtag,
        /// then <see cref="PluralRuleSet.English"/> as the last resort.
        /// </summary>
        /// <param name="languageCode">ISO language code or full locale.</param>
        /// <returns>The rule set that will be used for that language.</returns>
        public PluralRuleSet GetPluralRules(string languageCode)
        {
            if (!string.IsNullOrEmpty(languageCode))
            {
                if (_pluralRules.TryGetValue(languageCode, out var exact))
                    return exact;

                var primary = PrimarySubtag(languageCode);
                if (primary != languageCode && _pluralRules.TryGetValue(primary, out var baseRule))
                    return baseRule;
            }

            return PluralRuleSet.English;
        }

        /// <summary>Get the CLDR plural category a count falls into for the active language.</summary>
        /// <param name="count">The count being described.</param>
        /// <returns>The plural category.</returns>
        public PluralCategory GetPluralCategory(int count) =>
            GetPluralRules(_currentLanguage).Select(count);

        /// <summary>Get the CLDR plural category a count falls into for a specific language.</summary>
        /// <param name="languageCode">ISO language code or full locale.</param>
        /// <param name="count">The count being described.</param>
        /// <returns>The plural category.</returns>
        public PluralCategory GetPluralCategory(string languageCode, int count) =>
            GetPluralRules(languageCode).Select(count);

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
        /// Cross-check every loaded language against the union of all keys, and every
        /// pipe-separated value against that language's plural rule set. Loading only one
        /// language always validates; the pass earns its keep once a second language exists
        /// or a translator returns a partial file.
        /// </summary>
        /// <param name="referenceLanguage">
        /// Language named as the source of truth in the report header; defaults to the fallback language.
        /// </param>
        /// <returns>A report listing missing keys per language and plural form-count problems.</returns>
        public LocalizationValidationReport ValidateLanguages(string referenceLanguage = null)
        {
            var reference = string.IsNullOrEmpty(referenceLanguage) ? _fallbackLanguage : referenceLanguage;

            var languages = GetAvailableLanguages();
            Array.Sort(languages, StringComparer.Ordinal);

            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var language in languages)
                foreach (var key in _languages[language].Keys)
                    expected.Add(key);

            var missingKeys = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var pluralIssues = new List<string>();

            foreach (var language in languages)
            {
                var strings = _languages[language];

                var lacking = new List<string>();
                foreach (var key in expected)
                    if (!strings.ContainsKey(key)) lacking.Add(key);
                lacking.Sort(StringComparer.Ordinal);
                missingKeys[language] = lacking.ToArray();

                var rules = GetPluralRules(language);
                foreach (var pair in strings)
                {
                    if (pair.Value.IndexOf('|') < 0) continue;

                    var formCount = pair.Value.Split('|').Length;
                    if (formCount == rules.FormCount) continue;

                    pluralIssues.Add(
                        $"{language}: '{pair.Key}' has {formCount} plural form(s) but the " +
                        $"'{rules.Name}' rule set needs {rules.FormCount} ({rules.DescribeForms()}).");
                }
            }

            pluralIssues.Sort(StringComparer.Ordinal);

            return new LocalizationValidationReport(
                reference, languages, expected.Count, missingKeys, pluralIssues.ToArray());
        }

        /// <summary>
        /// Run <see cref="ValidateLanguages"/> and write the report to <c>GameLogger</c> —
        /// as a warning when anything is missing, otherwise as an informational line.
        /// </summary>
        /// <param name="referenceLanguage">Language named as the source of truth; defaults to the fallback.</param>
        /// <returns>True if no problems were found.</returns>
        public bool LogValidationReport(string referenceLanguage = null)
        {
            var report = ValidateLanguages(referenceLanguage);

            if (report.IsValid)
                GameLogger.Log(LogCategory, report.ToString());
            else
                GameLogger.LogWarning(LogCategory, report.ToString());

            return report.IsValid;
        }

        /// <summary>
        /// Parse a localization JSON object into a flat key → value dictionary. Nested
        /// objects flatten to dotted keys. Throws rather than returning partial results.
        /// </summary>
        /// <param name="json">The JSON text. A leading UTF-8 byte order mark is tolerated.</param>
        /// <param name="sourceName">Name used in error messages (e.g. "en.json").</param>
        /// <returns>The flattened key-value dictionary.</returns>
        /// <exception cref="LocalizationParseException">
        /// The text is not a JSON object, contains a value that is not a string or object,
        /// contains a duplicate key, or contains an invalid escape sequence.
        /// </exception>
        public static Dictionary<string, string> ParseLanguageJson(string json, string sourceName = "localization JSON")
        {
            var reader = new JsonReader(json, string.IsNullOrEmpty(sourceName) ? "localization JSON" : sourceName);
            return reader.ReadFlatObject();
        }

        // ─── Internal ───────────────────────────────────────────────

        /// <summary>Substitute {name} placeholders with the supplied values.</summary>
        /// <param name="text">The text containing placeholders.</param>
        /// <param name="variables">Name-value pairs to substitute.</param>
        /// <returns>The text with every supplied placeholder replaced.</returns>
        private static string Interpolate(string text, (string name, object value)[] variables)
        {
            if (variables == null) return text;

            foreach (var (name, value) in variables)
            {
                if (string.IsNullOrEmpty(name)) continue;
                text = text.Replace($"{{{name}}}", value?.ToString() ?? string.Empty);
            }

            return text;
        }

        /// <summary>
        /// Split a pipe-separated value and pick the form the active language's rule set
        /// calls for. A value with no pipe is returned unchanged.
        /// </summary>
        /// <param name="key">The key, used only in the mismatch warning.</param>
        /// <param name="raw">The raw localized value.</param>
        /// <param name="count">The count being described.</param>
        /// <returns>The selected plural form, trimmed.</returns>
        private string SelectPluralForm(string key, string raw, int count)
        {
            var forms = raw.Split('|');
            if (forms.Length < 2) return raw;

            var rules = GetPluralRules(_currentLanguage);
            var index = rules.FormIndex(count);

            if (index >= forms.Length)
            {
                GameLogger.LogWarning(LogCategory,
                    $"Key '{key}' supplies {forms.Length} plural form(s) but language " +
                    $"'{_currentLanguage}' uses the '{rules.Name}' rule set, which needs " +
                    $"{rules.FormCount} ({rules.DescribeForms()}). Using the last form.");
                index = forms.Length - 1;
            }

            return forms[index].Trim();
        }

        /// <summary>Populate the built-in CLDR rule sets. Callers may override any of them.</summary>
        private void RegisterDefaultPluralRules()
        {
            // one|other — English and the languages that share its rule.
            foreach (var code in new[] { "en", "de", "nl", "sv", "da", "nb", "no", "es", "it", "pt", "fi", "el", "he", "tr", "hu" })
                _pluralRules[code] = PluralRuleSet.English;

            // one|other, with zero taking the singular.
            _pluralRules["fr"] = PluralRuleSet.French;

            // other — languages with no grammatical plural.
            foreach (var code in new[] { "ja", "zh", "ko", "vi", "th", "id", "ms" })
                _pluralRules[code] = PluralRuleSet.SingleForm;

            // one|few|many
            _pluralRules["pl"] = PluralRuleSet.Polish;
            _pluralRules["ru"] = PluralRuleSet.Russian;
            _pluralRules["uk"] = PluralRuleSet.Russian;

            // one|few|other
            _pluralRules["cs"] = PluralRuleSet.Czech;
            _pluralRules["sk"] = PluralRuleSet.Czech;

            // zero|one|two|few|many|other
            _pluralRules["ar"] = PluralRuleSet.Arabic;
        }

        /// <summary>Reduce a locale to its primary language subtag, lowercased ("pt-BR" → "pt").</summary>
        /// <param name="languageCode">ISO language code or full locale.</param>
        /// <returns>The primary subtag.</returns>
        private static string PrimarySubtag(string languageCode)
        {
            var separator = languageCode.IndexOfAny(new[] { '-', '_' });
            var primary = separator > 0 ? languageCode.Substring(0, separator) : languageCode;
            return primary.ToLowerInvariant();
        }

        /// <summary>
        /// Strict recursive-descent JSON reader for localization files. Accepts only a root
        /// object whose values are strings or further objects, and reports the exact line and
        /// column of anything else. Replaces the regex scanner that silently dropped nested
        /// objects and unicode escapes.
        /// </summary>
        private sealed class JsonReader
        {
            /// <summary>The JSON text being read.</summary>
            private readonly string _text;

            /// <summary>Name used in error messages.</summary>
            private readonly string _source;

            /// <summary>Current read offset into <see cref="_text"/>.</summary>
            private int _index;

            /// <summary>One-based line number of <see cref="_index"/>.</summary>
            private int _line = 1;

            /// <summary>Offset of the first character on the current line.</summary>
            private int _lineStart;

            /// <summary>
            /// Create a reader.
            /// </summary>
            /// <param name="text">The JSON text; null is treated as empty and reported as such.</param>
            /// <param name="source">Name used in error messages.</param>
            public JsonReader(string text, string source)
            {
                _text = text ?? string.Empty;
                _source = source;
            }

            /// <summary>One-based column of the current read offset.</summary>
            private int Column => _index - _lineStart + 1;

            /// <summary>True when the reader has consumed everything.</summary>
            private bool AtEnd => _index >= _text.Length;

            /// <summary>
            /// Read the whole document as a flat dictionary, flattening nested objects into
            /// dotted keys.
            /// </summary>
            /// <returns>The flattened key-value dictionary.</returns>
            /// <exception cref="LocalizationParseException">The document is not a valid localization object.</exception>
            public Dictionary<string, string> ReadFlatObject()
            {
                SkipByteOrderMark();
                SkipWhitespace();

                if (AtEnd)
                    throw Error("The localization file is empty. It must contain a JSON object, e.g. {\"menu.play\": \"Play\"}.");

                if (_text[_index] != '{')
                    throw Error($"Expected '{{' at the start of the localization file but found {Describe(_text[_index])}.");

                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                ReadObjectInto(result, null, 0);

                SkipWhitespace();
                if (!AtEnd)
                    throw Error($"Unexpected {Describe(_text[_index])} after the closing '}}'. A localization file holds exactly one JSON object.");

                return result;
            }

            /// <summary>
            /// Read one object, writing its string members into the flat dictionary and
            /// recursing into nested objects.
            /// </summary>
            /// <param name="into">Destination dictionary.</param>
            /// <param name="prefix">Dotted key prefix from enclosing objects, or null at the root.</param>
            /// <param name="depth">Current nesting depth, used to bound recursion.</param>
            private void ReadObjectInto(Dictionary<string, string> into, string prefix, int depth)
            {
                if (depth > MaxNestingDepth)
                    throw Error($"Objects are nested more than {MaxNestingDepth} levels deep. This is almost certainly a malformed file.");

                Expect('{');
                SkipWhitespace();

                if (!AtEnd && _text[_index] == '}')
                {
                    Advance();
                    return;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd)
                        throw Error("Unexpected end of file: an object was left unclosed.");

                    if (_text[_index] != '"')
                        throw Error($"Expected a quoted key but found {Describe(_text[_index])}. Keys must be double-quoted strings.");

                    var keyLine = _line;
                    var keyColumn = Column;
                    var name = ReadString();

                    if (name.Length == 0)
                        throw ErrorAt(keyLine, keyColumn, "Empty key. Every localization key must have a name.");

                    var fullKey = prefix == null ? name : prefix + "." + name;

                    SkipWhitespace();
                    Expect(':');
                    SkipWhitespace();

                    if (AtEnd)
                        throw Error($"Unexpected end of file after the ':' for key '{fullKey}'.");

                    var valueStart = _text[_index];
                    if (valueStart == '"')
                    {
                        var value = ReadString();
                        if (into.ContainsKey(fullKey))
                            throw ErrorAt(keyLine, keyColumn,
                                $"Duplicate key '{fullKey}'. A later definition would silently replace the earlier one, so the file is rejected.");
                        into[fullKey] = value;
                    }
                    else if (valueStart == '{')
                    {
                        ReadObjectInto(into, fullKey, depth + 1);
                    }
                    else if (valueStart == '[')
                    {
                        throw Error($"Key '{fullKey}' has an array value. Localization values must be strings; " +
                                    "use pipe-separated plural forms (\"one form|other form\") or a nested object.");
                    }
                    else
                    {
                        throw Error($"Key '{fullKey}' has a value starting with {Describe(valueStart)}. " +
                                    "Every localization value must be a double-quoted string or a nested object — " +
                                    "numbers, booleans and null are not translatable text.");
                    }

                    SkipWhitespace();
                    if (AtEnd)
                        throw Error($"Unexpected end of file after the value for '{fullKey}': the object is never closed.");

                    var separator = _text[_index];
                    if (separator == ',')
                    {
                        Advance();
                        SkipWhitespace();
                        if (!AtEnd && _text[_index] == '}')
                            throw Error("Trailing comma before '}'. JSON does not allow one.");
                        continue;
                    }

                    if (separator == '}')
                    {
                        Advance();
                        return;
                    }

                    throw Error($"Expected ',' or '}}' after the value for '{fullKey}' but found {Describe(separator)}.");
                }
            }

            /// <summary>
            /// Read a double-quoted JSON string, resolving every escape sequence including
            /// <c>\uXXXX</c> (surrogate pairs come through as two escapes and compose naturally).
            /// </summary>
            /// <returns>The decoded string value.</returns>
            private string ReadString()
            {
                Expect('"');
                var sb = new StringBuilder();

                while (true)
                {
                    if (AtEnd)
                        throw Error("Unterminated string: no closing '\"' before the end of the file.");

                    var c = _text[_index];

                    if (c == '"')
                    {
                        Advance();
                        return sb.ToString();
                    }

                    if (c == '\\')
                    {
                        Advance();
                        if (AtEnd)
                            throw Error("Unterminated escape sequence at the end of the file.");

                        var escape = _text[_index];
                        switch (escape)
                        {
                            case '"': sb.Append('"'); Advance(); break;
                            case '\\': sb.Append('\\'); Advance(); break;
                            case '/': sb.Append('/'); Advance(); break;
                            case 'b': sb.Append('\b'); Advance(); break;
                            case 'f': sb.Append('\f'); Advance(); break;
                            case 'n': sb.Append('\n'); Advance(); break;
                            case 'r': sb.Append('\r'); Advance(); break;
                            case 't': sb.Append('\t'); Advance(); break;
                            case 'u': Advance(); sb.Append(ReadUnicodeEscape()); break;
                            default:
                                throw Error($"Unknown escape sequence '\\{escape}'. " +
                                            "Valid escapes are \\\" \\\\ \\/ \\b \\f \\n \\r \\t and \\uXXXX.");
                        }

                        continue;
                    }

                    if (c < ' ')
                        throw Error($"Raw control character U+{(int)c:X4} inside a string. Escape it (\\n, \\t or \\uXXXX).");

                    sb.Append(c);
                    Advance();
                }
            }

            /// <summary>Read the four hexadecimal digits of a <c>\uXXXX</c> escape.</summary>
            /// <returns>The decoded UTF-16 code unit.</returns>
            private char ReadUnicodeEscape()
            {
                var value = 0;

                for (var i = 0; i < 4; i++)
                {
                    if (AtEnd)
                        throw Error("Truncated \\u escape: four hexadecimal digits are required.");

                    var digit = HexValue(_text[_index]);
                    if (digit < 0)
                        throw Error($"Invalid hexadecimal digit {Describe(_text[_index])} in a \\u escape. " +
                                    "It needs exactly four digits, e.g. \\u00e9.");

                    value = (value << 4) | digit;
                    Advance();
                }

                return (char)value;
            }

            /// <summary>Consume the expected character or throw a specific error.</summary>
            /// <param name="expected">The character required at the current position.</param>
            private void Expect(char expected)
            {
                if (AtEnd)
                    throw Error($"Unexpected end of file: expected '{expected}'.");

                if (_text[_index] != expected)
                    throw Error($"Expected '{expected}' but found {Describe(_text[_index])}.");

                Advance();
            }

            /// <summary>Skip any leading UTF-8 byte order marks Unity did not strip.</summary>
            private void SkipByteOrderMark()
            {
                while (!AtEnd && _text[_index] == '﻿')
                {
                    _index++;
                    _lineStart++;
                }
            }

            /// <summary>Skip JSON whitespace (space, tab, carriage return, newline).</summary>
            private void SkipWhitespace()
            {
                while (!AtEnd)
                {
                    var c = _text[_index];
                    if (c != ' ' && c != '\t' && c != '\n' && c != '\r') return;
                    Advance();
                }
            }

            /// <summary>Consume one character, tracking line and column.</summary>
            private void Advance()
            {
                if (_text[_index] == '\n')
                {
                    _line++;
                    _lineStart = _index + 1;
                }

                _index++;
            }

            /// <summary>Build an exception positioned at the current read offset.</summary>
            /// <param name="detail">What went wrong and how to fix it.</param>
            /// <returns>The exception to throw.</returns>
            private LocalizationParseException Error(string detail) =>
                new LocalizationParseException(_source, _line, Column, detail);

            /// <summary>Build an exception positioned at a remembered offset.</summary>
            /// <param name="line">One-based line number.</param>
            /// <param name="column">One-based column number.</param>
            /// <param name="detail">What went wrong and how to fix it.</param>
            /// <returns>The exception to throw.</returns>
            private LocalizationParseException ErrorAt(int line, int column, string detail) =>
                new LocalizationParseException(_source, line, column, detail);

            /// <summary>Render a character for an error message, printable or not.</summary>
            /// <param name="c">The character to describe.</param>
            /// <returns>A quoted character or a U+XXXX code point.</returns>
            private static string Describe(char c) =>
                c < ' ' || c == '﻿' ? $"U+{(int)c:X4}" : $"'{c}'";

            /// <summary>Value of a hexadecimal digit, or -1 if the character is not one.</summary>
            /// <param name="c">The candidate digit.</param>
            /// <returns>0-15, or -1.</returns>
            private static int HexValue(char c)
            {
                if (c >= '0' && c <= '9') return c - '0';
                if (c >= 'a' && c <= 'f') return c - 'a' + 10;
                if (c >= 'A' && c <= 'F') return c - 'A' + 10;
                return -1;
            }
        }
    }
}
