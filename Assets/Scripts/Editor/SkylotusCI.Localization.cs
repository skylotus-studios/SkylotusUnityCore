using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skylotus.Editor
{
    /// <summary>
    /// Batchmode checks for <see cref="LocalizationSystem"/> (WP-15): the strict JSON
    /// parser, the per-language plural rules, and the cross-language validation pass.
    ///
    /// These live here rather than in <c>Assets/Tests/</c> because that folder is owned by
    /// WP-12 and does not exist yet. Once an EditMode test assembly lands, move the bodies
    /// of the <c>CheckXxx</c> methods into NUnit cases and delete this file.
    /// </summary>
    /// <remarks>
    /// Run with:
    /// <c>Tools\unity-verify.ps1 -Mode method -Method Skylotus.Editor.SkylotusCI.ValidateLocalization</c>
    /// </remarks>
    public static partial class SkylotusCI
    {
        /// <summary>Collected failure descriptions for the current localization run.</summary>
        private static readonly List<string> _localizationFailures = new List<string>();

        /// <summary>A well-formed file exercising unicode escapes, escaped quotes and nesting.</summary>
        private const string ValidLocalizationJson = @"{
  ""accented"": ""\u00e9t\u00e9"",
  ""quoted"": ""She said \""no\"", then left."",
  ""escapes"": ""line1\nline2\tend\\done"",
  ""slash"": ""a\/b"",
  ""astral"": ""\ud83d\ude00"",
  ""menu"": {
    ""play"": ""Play"",
    ""nested"": { ""deep"": ""Deep"" }
  },
  ""empty"": """"
}";

        // ─── Entry Point ────────────────────────────────────────────

        /// <summary>
        /// Verify WP-15's acceptance criteria: a file with unicode escapes, nested objects
        /// and escaped quotes loads correctly; a broken file fails with a specific,
        /// actionable error; plural selection is correct for non-English rule sets; and the
        /// shipped <c>en.json</c> still resolves every key it did before.
        /// </summary>
        public static void ValidateLocalization()
        {
            _localizationFailures.Clear();

            try
            {
                CheckParserAcceptsValidFile();
                CheckParserRejectsBrokenFiles();
                CheckParseErrorsCarryPosition();
                CheckPluralRules();
                CheckValidationPass();
                CheckShippedEnglishFile();
            }
            catch (Exception e)
            {
                Fail($"ValidateLocalization threw: {e}");
                return;
            }

            if (_localizationFailures.Count > 0)
            {
                foreach (var failure in _localizationFailures)
                    Debug.LogError($"[{Category}] {failure}");

                Fail($"{_localizationFailures.Count} localization check(s) failed.");
                return;
            }

            Succeed("Localization: parser, plural rules, validation pass and en.json all check out.");
        }

        // ─── Checks ─────────────────────────────────────────────────

        /// <summary>
        /// The parser must decode every JSON escape, flatten nested objects to dotted keys,
        /// and keep surrogate pairs intact.
        /// </summary>
        private static void CheckParserAcceptsValidFile()
        {
            var parsed = LocalizationSystem.ParseLanguageJson(ValidLocalizationJson, "valid.json");

            LocEqual("\\u escape", "\u00e9t\u00e9", Lookup(parsed, "accented"));
            LocEqual("escaped quotes", "She said \"no\", then left.", Lookup(parsed, "quoted"));
            LocEqual("control escapes", "line1\nline2\tend\\done", Lookup(parsed, "escapes"));
            LocEqual("escaped solidus", "a/b", Lookup(parsed, "slash"));
            LocEqual("surrogate pair", char.ConvertFromUtf32(0x1F600), Lookup(parsed, "astral"));
            LocEqual("nested object", "Play", Lookup(parsed, "menu.play"));
            LocEqual("doubly nested object", "Deep", Lookup(parsed, "menu.nested.deep"));
            LocEqual("empty value", string.Empty, Lookup(parsed, "empty"));

            LocTrue("key count", parsed.Count == 8, $"expected 8 keys, got {parsed.Count}");
        }

        /// <summary>
        /// Every malformed or unsupported construct must throw
        /// <see cref="LocalizationParseException"/> with a message naming the problem —
        /// never a silent partial load.
        /// </summary>
        private static void CheckParserRejectsBrokenFiles()
        {
            LocRejects("number value", "{\"a\": 1}", "double-quoted string");
            LocRejects("boolean value", "{\"a\": true}", "double-quoted string");
            LocRejects("null value", "{\"a\": null}", "double-quoted string");
            LocRejects("array value", "{\"a\": [\"x\"]}", "array value");
            LocRejects("duplicate key", "{\"a\": \"x\", \"a\": \"y\"}", "Duplicate key");
            LocRejects("nested key collides", "{\"a\": {\"b\": \"1\"}, \"a.b\": \"2\"}", "Duplicate key 'a.b'");
            LocRejects("trailing comma", "{\"a\": \"x\",}", "Trailing comma");
            LocRejects("unknown escape", "{\"a\": \"c:\\qux\"}", "Unknown escape");
            LocRejects("truncated \\u", "{\"a\": \"\\u12\"}", "hexadecimal");
            LocRejects("unterminated string", "{\"a\": \"no end", "Unterminated string");
            LocRejects("missing colon", "{\"a\" \"b\"}", "Expected ':'");
            LocRejects("unquoted key", "{a: \"b\"}", "quoted key");
            LocRejects("empty file", "", "empty");
            LocRejects("root array", "[\"a\"]", "Expected '{'");
            LocRejects("trailing content", "{\"a\": \"b\"} junk", "after the closing");
            LocRejects("raw newline in string", "{\"a\": \"one\ntwo\"}", "control character");

            // A rejected file must not half-load: the previously loaded copy survives.
            var previous = GameLogger.GlobalLevel;
            GameLogger.SetCategoryLevel("Localization", LogLevel.Off);
            try
            {
                var system = new LocalizationSystem();
                LocTrue("good file loads", system.LoadLanguageFromString("xx", "{\"a\": \"first\"}"), "load returned false");
                LocTrue("broken file rejected", !system.LoadLanguageFromString("xx", "{\"a\": 2}"), "load returned true");

                system.SetLanguage("xx");
                LocEqual("previous copy survives a rejected reload", "first", system.Get("a"));
            }
            finally
            {
                GameLogger.SetCategoryLevel("Localization", previous);
            }
        }

        /// <summary>Parse errors must point at the offending line and column, not at the file as a whole.</summary>
        private static void CheckParseErrorsCarryPosition()
        {
            const string json = "{\n  \"ok\": \"fine\",\n  \"bad\": 42\n}";

            try
            {
                LocalizationSystem.ParseLanguageJson(json, "positions.json");
                _localizationFailures.Add("position: expected a parse error, got none.");
            }
            catch (LocalizationParseException e)
            {
                LocTrue("error line", e.Line == 3, $"expected line 3, got {e.Line}");
                LocTrue("error column", e.Column == 10, $"expected column 10, got {e.Column}");
                LocTrue("error source", e.SourceName == "positions.json", $"got '{e.SourceName}'");
                LocTrue("error message includes position",
                    e.Message.StartsWith("positions.json(3,10):", StringComparison.Ordinal),
                    $"got '{e.Message}'");
            }
        }

        /// <summary>
        /// Plural selection must follow the target language's CLDR rule set, not English's.
        /// Polish, Russian and Arabic all disagree with English; Japanese has one form.
        /// </summary>
        private static void CheckPluralRules()
        {
            var system = new LocalizationSystem();

            system.LoadLanguageFromString("en", "{\"files\": \"{count} file|{count} files\"}");
            system.LoadLanguageFromString("fr", "{\"files\": \"{count} fichier|{count} fichiers\"}");
            system.LoadLanguageFromString("pl", "{\"files\": \"plik|pliki|plikow\"}");
            system.LoadLanguageFromString("ru", "{\"files\": \"fayl|fayla|faylov\"}");
            system.LoadLanguageFromString("ar", "{\"files\": \"zero|one|two|few|many|other\"}");
            system.LoadLanguageFromString("ja", "{\"files\": \"fairu\"}");

            // English — unchanged from the pre-WP-15 behaviour.
            system.SetLanguage("en");
            LocEqual("en 0", "0 files", system.GetPlural("files", 0));
            LocEqual("en 1", "1 file", system.GetPlural("files", 1));
            LocEqual("en 2", "2 files", system.GetPlural("files", 2));

            // French — zero takes the singular.
            system.SetLanguage("fr");
            LocEqual("fr 0", "0 fichier", system.GetPlural("files", 0));
            LocEqual("fr 1", "1 fichier", system.GetPlural("files", 1));
            LocEqual("fr 2", "2 fichiers", system.GetPlural("files", 2));

            // Polish — one / few (x2-x4 outside the teens) / many.
            system.SetLanguage("pl");
            LocEqual("pl 1", "plik", system.GetPlural("files", 1));
            LocEqual("pl 2", "pliki", system.GetPlural("files", 2));
            LocEqual("pl 5", "plikow", system.GetPlural("files", 5));
            LocEqual("pl 12", "plikow", system.GetPlural("files", 12));
            LocEqual("pl 22", "pliki", system.GetPlural("files", 22));
            LocEqual("pl 112", "plikow", system.GetPlural("files", 112));
            LocEqual("pl 122", "pliki", system.GetPlural("files", 122));

            // Russian — one is x1 outside the teens, so 21 is singular and 11 is not.
            system.SetLanguage("ru");
            LocEqual("ru 1", "fayl", system.GetPlural("files", 1));
            LocEqual("ru 2", "fayla", system.GetPlural("files", 2));
            LocEqual("ru 5", "faylov", system.GetPlural("files", 5));
            LocEqual("ru 11", "faylov", system.GetPlural("files", 11));
            LocEqual("ru 21", "fayl", system.GetPlural("files", 21));

            // Arabic — all six categories.
            system.SetLanguage("ar");
            LocEqual("ar 0", "zero", system.GetPlural("files", 0));
            LocEqual("ar 1", "one", system.GetPlural("files", 1));
            LocEqual("ar 2", "two", system.GetPlural("files", 2));
            LocEqual("ar 3", "few", system.GetPlural("files", 3));
            LocEqual("ar 11", "many", system.GetPlural("files", 11));
            LocEqual("ar 100", "other", system.GetPlural("files", 100));

            // Japanese — a single form for every count.
            system.SetLanguage("ja");
            LocEqual("ja 0", "fairu", system.GetPlural("files", 0));
            LocEqual("ja 1", "fairu", system.GetPlural("files", 1));
            LocEqual("ja 7", "fairu", system.GetPlural("files", 7));

            // Locale variants fall back to the primary subtag.
            LocTrue("pt-BR resolves to a rule set",
                system.GetPluralRules("pt-BR").Name == PluralRuleSet.English.Name,
                $"got '{system.GetPluralRules("pt-BR").Name}'");

            // Rules are pluggable.
            system.RegisterPluralRule("xx", PluralRuleSet.Polish);
            LocTrue("custom rule registered",
                system.GetPluralCategory("xx", 5) == PluralCategory.Many,
                $"got {system.GetPluralCategory("xx", 5)}");
        }

        /// <summary>The validation pass must name every key missing from a loaded language.</summary>
        private static void CheckValidationPass()
        {
            var system = new LocalizationSystem();
            system.LoadLanguageFromString("en", "{\"a\": \"A\", \"b\": \"B\", \"c\": \"C\"}");

            var single = system.ValidateLanguages();
            LocTrue("single language validates", single.IsValid, single.ToString());

            system.LoadLanguageFromString("pl", "{\"a\": \"A\", \"d\": \"D\", \"n\": \"one|other\"}");

            var report = system.ValidateLanguages("en");
            LocTrue("report is invalid", !report.IsValid, "expected missing keys to be reported");
            LocTrue("expected key union", report.ExpectedKeyCount == 5, $"got {report.ExpectedKeyCount}");
            LocTrue("pl missing b and c",
                string.Join(",", report.GetMissingKeys("pl")) == "b,c",
                $"got '{string.Join(",", report.GetMissingKeys("pl"))}'");
            LocTrue("en missing d and n",
                string.Join(",", report.GetMissingKeys("en")) == "d,n",
                $"got '{string.Join(",", report.GetMissingKeys("en"))}'");
            LocTrue("plural form mismatch reported",
                report.PluralFormIssues.Length == 1 &&
                report.PluralFormIssues[0].Contains("'n'") &&
                report.PluralFormIssues[0].Contains("polish"),
                $"got [{string.Join(" | ", report.PluralFormIssues)}]");
            LocTrue("report renders", report.ToString().Contains("is missing"), report.ToString());
        }

        /// <summary>
        /// The shipped <c>en.json</c> must still resolve every key it did before WP-15, and
        /// the keys added to exercise the parser must decode correctly.
        /// </summary>
        private static void CheckShippedEnglishFile()
        {
            var system = new LocalizationSystem();

            if (!system.LoadLanguage("en"))
            {
                _localizationFailures.Add("en.json: LoadLanguage(\"en\") returned false.");
                return;
            }

            system.SetLanguage("en");

            // The nine keys that existed before WP-15.
            LocEqual("en.json menu.play", "Play", system.Get("menu.play"));
            LocEqual("en.json menu.settings", "Settings", system.Get("menu.settings"));
            LocEqual("en.json menu.quit", "Quit", system.Get("menu.quit"));
            LocEqual("en.json menu.resume", "Resume", system.Get("menu.resume"));
            LocEqual("en.json menu.back", "Back", system.Get("menu.back"));
            LocEqual("en.json menu.confirm", "Confirm", system.Get("menu.confirm"));
            LocEqual("en.json menu.cancel", "Cancel", system.Get("menu.cancel"));
            LocEqual("en.json loading.text", "Loading...", system.Get("loading.text"));
            LocEqual("en.json greeting", "Hello, Ada!", system.Get("greeting", ("name", "Ada")));

            // Keys added by WP-15 to exercise the parser.
            LocEqual("en.json unicode escape", "Working\u2026", system.Get("loading.hint"));
            LocEqual("en.json unicode escapes", "\u00a9 2026 Skylotus \u2014 all rights reserved",
                system.Get("credits.copyright"));
            LocEqual("en.json escaped quotes", "She said \"no\", then left.", system.Get("dialogue.quoted"));
            LocEqual("en.json nested hud.score", "Score: 40", system.Get("hud.score", ("score", 40)));
            LocEqual("en.json nested hud.lives", "Lives: 3", system.Get("hud.lives", ("lives", 3)));
            LocEqual("en.json plural 1", "1 item", system.GetPlural("items.count", 1));
            LocEqual("en.json plural 0", "0 items", system.GetPlural("items.count", 0));
            LocEqual("en.json plural 7", "7 items", system.GetPlural("items.count", 7));

            LocTrue("en.json validates", system.ValidateLanguages().IsValid,
                system.ValidateLanguages().ToString());
        }

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>Read a key from a parsed dictionary, recording a failure if it is absent.</summary>
        /// <param name="parsed">The parsed dictionary.</param>
        /// <param name="key">The key to read.</param>
        /// <returns>The value, or a marker string when the key is missing.</returns>
        private static string Lookup(Dictionary<string, string> parsed, string key)
        {
            if (parsed.TryGetValue(key, out var value)) return value;

            _localizationFailures.Add($"parser: key '{key}' was dropped.");
            return "<missing>";
        }

        /// <summary>Record a failure unless two strings match ordinally.</summary>
        /// <param name="label">Short name of the check.</param>
        /// <param name="expected">The expected value.</param>
        /// <param name="actual">The observed value.</param>
        private static void LocEqual(string label, string expected, string actual)
        {
            if (string.Equals(expected, actual, StringComparison.Ordinal)) return;

            _localizationFailures.Add($"{label}: expected '{Printable(expected)}', got '{Printable(actual)}'.");
        }

        /// <summary>Record a failure unless a condition holds.</summary>
        /// <param name="label">Short name of the check.</param>
        /// <param name="condition">The condition that must be true.</param>
        /// <param name="detail">What to report when it is not.</param>
        private static void LocTrue(string label, bool condition, string detail)
        {
            if (condition) return;

            _localizationFailures.Add($"{label}: {detail}");
        }

        /// <summary>
        /// Record a failure unless parsing the JSON throws a
        /// <see cref="LocalizationParseException"/> whose message contains the expected text.
        /// </summary>
        /// <param name="label">Short name of the check.</param>
        /// <param name="json">The malformed JSON.</param>
        /// <param name="expectedFragment">Text the error message must contain.</param>
        private static void LocRejects(string label, string json, string expectedFragment)
        {
            try
            {
                var parsed = LocalizationSystem.ParseLanguageJson(json, "broken.json");
                _localizationFailures.Add(
                    $"{label}: expected a parse error, but the file loaded with {parsed.Count} key(s).");
            }
            catch (LocalizationParseException e)
            {
                if (!e.Message.Contains(expectedFragment))
                    _localizationFailures.Add(
                        $"{label}: error did not mention '{expectedFragment}'. Message was: {e.Message}");
            }
        }

        /// <summary>Make control characters visible in a failure message.</summary>
        /// <param name="value">The value to render.</param>
        /// <returns>The value with newlines and tabs escaped.</returns>
        private static string Printable(string value) =>
            value == null ? "<null>" : value.Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r");
    }
}
