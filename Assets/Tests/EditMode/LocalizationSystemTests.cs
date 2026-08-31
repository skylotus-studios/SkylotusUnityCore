using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Skylotus.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="LocalizationSystem"/> (WP-15): the strict JSON parser,
    /// interpolation, CLDR pluralization per language, fallback and missing-key behaviour, and
    /// the cross-language validation pass — plus a regression pass over the shipped
    /// <c>en.json</c>.
    ///
    /// These cases are the WP-15 batchmode self-check, ported to NUnit and split so a failure
    /// names the specific behaviour that broke rather than a count.
    /// </summary>
    [TestFixture]
    public class LocalizationSystemTests
    {
        /// <summary>A well-formed file exercising unicode escapes, escaped quotes and nesting.</summary>
        private const string ValidJson = @"{
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

        /// <summary>Silence the localization channel: several cases deliberately trigger errors.</summary>
        [SetUp]
        public void SetUp()
        {
            EventBus.ClearAll();
            GameLogger.SetCategoryLevel("Localization", LogLevel.Off);
        }

        /// <summary>Restore logging.</summary>
        [TearDown]
        public void TearDown()
        {
            EventBus.ClearAll();
            GameLogger.SetCategoryLevel("Localization", LogLevel.Debug);
        }

        // ─── Parser: accepted input ─────────────────────────────────

        /// <summary>Every JSON escape decodes, and a surrogate pair survives as one code point.</summary>
        [Test]
        public void Parse_DecodesEveryEscapeSequence()
        {
            var parsed = LocalizationSystem.ParseLanguageJson(ValidJson, "valid.json");

            Assert.AreEqual("\u00e9t\u00e9", Lookup(parsed, "accented"), "\\u escape");
            Assert.AreEqual("She said \"no\", then left.", Lookup(parsed, "quoted"), "escaped quotes");
            Assert.AreEqual("line1\nline2\tend\\done", Lookup(parsed, "escapes"), "control escapes");
            Assert.AreEqual("a/b", Lookup(parsed, "slash"), "escaped solidus");
            Assert.AreEqual(char.ConvertFromUtf32(0x1F600), Lookup(parsed, "astral"), "surrogate pair");
            Assert.AreEqual(string.Empty, Lookup(parsed, "empty"), "empty value");
        }

        /// <summary>Nested objects flatten to dotted keys at any depth.</summary>
        [Test]
        public void Parse_FlattensNestedObjectsToDottedKeys()
        {
            var parsed = LocalizationSystem.ParseLanguageJson(ValidJson, "valid.json");

            Assert.AreEqual("Play", Lookup(parsed, "menu.play"));
            Assert.AreEqual("Deep", Lookup(parsed, "menu.nested.deep"));
            Assert.AreEqual(8, parsed.Count, "The file defines exactly eight leaf keys.");
        }

        // ─── Parser: rejected input ─────────────────────────────────

        /// <summary>
        /// Every malformed or unsupported construct throws
        /// <see cref="LocalizationParseException"/> with a message naming the problem, so a
        /// broken translation file can never load half-way.
        /// </summary>
        /// <param name="json">The malformed JSON.</param>
        /// <param name="expectedFragment">Text the error message must contain.</param>
        [TestCase("{\"a\": 1}", "double-quoted string", TestName = "Parse_Rejects_NumberValue")]
        [TestCase("{\"a\": true}", "double-quoted string", TestName = "Parse_Rejects_BooleanValue")]
        [TestCase("{\"a\": null}", "double-quoted string", TestName = "Parse_Rejects_NullValue")]
        [TestCase("{\"a\": [\"x\"]}", "array value", TestName = "Parse_Rejects_ArrayValue")]
        [TestCase("{\"a\": \"x\", \"a\": \"y\"}", "Duplicate key", TestName = "Parse_Rejects_DuplicateKey")]
        [TestCase("{\"a\": {\"b\": \"1\"}, \"a.b\": \"2\"}", "Duplicate key 'a.b'", TestName = "Parse_Rejects_NestedKeyCollision")]
        [TestCase("{\"a\": \"x\",}", "Trailing comma", TestName = "Parse_Rejects_TrailingComma")]
        [TestCase("{\"a\": \"c:\\qux\"}", "Unknown escape", TestName = "Parse_Rejects_UnknownEscape")]
        [TestCase("{\"a\": \"\\u12\"}", "hexadecimal", TestName = "Parse_Rejects_TruncatedUnicodeEscape")]
        [TestCase("{\"a\": \"no end", "Unterminated string", TestName = "Parse_Rejects_UnterminatedString")]
        [TestCase("{\"a\" \"b\"}", "Expected ':'", TestName = "Parse_Rejects_MissingColon")]
        [TestCase("{a: \"b\"}", "quoted key", TestName = "Parse_Rejects_UnquotedKey")]
        [TestCase("", "empty", TestName = "Parse_Rejects_EmptyFile")]
        [TestCase("[\"a\"]", "Expected '{'", TestName = "Parse_Rejects_RootArray")]
        [TestCase("{\"a\": \"b\"} junk", "after the closing", TestName = "Parse_Rejects_TrailingContent")]
        [TestCase("{\"a\": \"one\ntwo\"}", "control character", TestName = "Parse_Rejects_RawNewlineInString")]
        public void Parse_RejectsMalformedInput(string json, string expectedFragment)
        {
            var ex = Assert.Throws<LocalizationParseException>(
                () => LocalizationSystem.ParseLanguageJson(json, "broken.json"),
                $"Expected '{expectedFragment}' to be rejected.");

            StringAssert.Contains(expectedFragment, ex.Message);
        }

        /// <summary>Parse errors point at the offending line and column, not at the file as a whole.</summary>
        [Test]
        public void Parse_ErrorCarriesLineAndColumn()
        {
            const string json = "{\n  \"ok\": \"fine\",\n  \"bad\": 42\n}";

            var ex = Assert.Throws<LocalizationParseException>(
                () => LocalizationSystem.ParseLanguageJson(json, "positions.json"));

            Assert.AreEqual(3, ex.Line);
            Assert.AreEqual(10, ex.Column);
            Assert.AreEqual("positions.json", ex.SourceName);
            StringAssert.StartsWith("positions.json(3,10):", ex.Message);
        }

        /// <summary>A rejected reload leaves the previously loaded copy of that language untouched.</summary>
        [Test]
        public void LoadLanguageFromString_RejectedFile_LeavesThePreviousCopyIntact()
        {
            var system = new LocalizationSystem();

            Assert.IsTrue(system.LoadLanguageFromString("xx", "{\"a\": \"first\"}"));
            Assert.IsFalse(system.LoadLanguageFromString("xx", "{\"a\": 2}"),
                "A malformed file must be refused.");

            system.SetLanguage("xx");
            Assert.AreEqual("first", system.Get("a"));
        }

        // ─── Interpolation ──────────────────────────────────────────

        /// <summary>Named placeholders are replaced with the supplied values.</summary>
        [Test]
        public void Get_InterpolatesNamedVariables()
        {
            var system = new LocalizationSystem();
            system.LoadLanguageFromString("en", "{\"hello\": \"Hello, {name}! You are {age}.\"}");
            system.SetLanguage("en");

            Assert.AreEqual("Hello, Ada! You are 36.",
                system.Get("hello", ("name", "Ada"), ("age", 36)));
        }

        /// <summary>A placeholder with no supplied value is left in place rather than blanked.</summary>
        [Test]
        public void Get_LeavesUnsuppliedPlaceholdersAlone()
        {
            var system = new LocalizationSystem();
            system.LoadLanguageFromString("en", "{\"hello\": \"Hello, {name} and {other}!\"}");
            system.SetLanguage("en");

            Assert.AreEqual("Hello, Ada and {other}!", system.Get("hello", ("name", "Ada")));
        }

        // ─── Fallback and missing keys ──────────────────────────────

        /// <summary>A key missing from the active language resolves through the fallback language.</summary>
        [Test]
        public void Get_MissingFromCurrentLanguage_FallsBackToTheFallbackLanguage()
        {
            var system = new LocalizationSystem();
            system.LoadLanguageFromString("en", "{\"only.en\": \"English only\", \"shared\": \"EN\"}");
            system.LoadLanguageFromString("fr", "{\"shared\": \"FR\"}");
            system.SetFallbackLanguage("en");
            system.SetLanguage("fr");

            Assert.AreEqual("FR", system.Get("shared"), "The active language wins where it has the key.");
            Assert.AreEqual("English only", system.Get("only.en"), "Otherwise the fallback answers.");
        }

        /// <summary>A key missing from both languages returns the bracketed key, not an empty string.</summary>
        [Test]
        public void Get_MissingEverywhere_ReturnsTheBracketedKey()
        {
            var system = new LocalizationSystem();
            system.LoadLanguageFromString("en", "{\"a\": \"A\"}");
            system.SetLanguage("en");

            Assert.AreEqual("[nope.missing]", system.Get("nope.missing"));
            Assert.IsFalse(system.HasKey("nope.missing"));
            Assert.IsTrue(system.HasKey("a"));
        }

        /// <summary>Switching language publishes <see cref="OnLanguageChangedEvent"/> and raises the C# event.</summary>
        [Test]
        public void SetLanguage_RaisesTheEventAndPublishesOnTheBus()
        {
            var system = new LocalizationSystem();
            system.LoadLanguageFromString("fr", "{\"a\": \"A\"}");

            string raised = null;
            string published = null;

            system.OnLanguageChanged += code => raised = code;
            EventBus.Subscribe<OnLanguageChangedEvent>(e => published = e.LanguageCode);

            system.SetLanguage("fr");

            Assert.AreEqual("fr", raised);
            Assert.AreEqual("fr", published);
            Assert.AreEqual("fr", system.CurrentLanguage);
        }

        // ─── Pluralization ──────────────────────────────────────────

        /// <summary>English: one form for 1, "other" for everything else including zero.</summary>
        [Test]
        public void GetPlural_English()
        {
            var system = PluralFixture();
            system.SetLanguage("en");

            Assert.AreEqual("0 files", system.GetPlural("files", 0));
            Assert.AreEqual("1 file", system.GetPlural("files", 1));
            Assert.AreEqual("2 files", system.GetPlural("files", 2));
        }

        /// <summary>French: zero takes the singular, unlike English.</summary>
        [Test]
        public void GetPlural_French_ZeroTakesTheSingular()
        {
            var system = PluralFixture();
            system.SetLanguage("fr");

            Assert.AreEqual("0 fichier", system.GetPlural("files", 0));
            Assert.AreEqual("1 fichier", system.GetPlural("files", 1));
            Assert.AreEqual("2 fichiers", system.GetPlural("files", 2));
        }

        /// <summary>Polish: one / few (x2-x4 outside the teens) / many.</summary>
        [Test]
        public void GetPlural_Polish_ThreeForms()
        {
            var system = PluralFixture();
            system.SetLanguage("pl");

            Assert.AreEqual("plik", system.GetPlural("files", 1));
            Assert.AreEqual("pliki", system.GetPlural("files", 2));
            Assert.AreEqual("plikow", system.GetPlural("files", 5));
            Assert.AreEqual("plikow", system.GetPlural("files", 12), "The teens are 'many'.");
            Assert.AreEqual("pliki", system.GetPlural("files", 22));
            Assert.AreEqual("plikow", system.GetPlural("files", 112));
            Assert.AreEqual("pliki", system.GetPlural("files", 122));
        }

        /// <summary>Russian: "one" is x1 outside the teens, so 21 is singular and 11 is not.</summary>
        [Test]
        public void GetPlural_Russian_TeensAreNotSingular()
        {
            var system = PluralFixture();
            system.SetLanguage("ru");

            Assert.AreEqual("fayl", system.GetPlural("files", 1));
            Assert.AreEqual("fayla", system.GetPlural("files", 2));
            Assert.AreEqual("faylov", system.GetPlural("files", 5));
            Assert.AreEqual("faylov", system.GetPlural("files", 11));
            Assert.AreEqual("fayl", system.GetPlural("files", 21));
        }

        /// <summary>Arabic exercises all six CLDR categories.</summary>
        [Test]
        public void GetPlural_Arabic_SixForms()
        {
            var system = PluralFixture();
            system.SetLanguage("ar");

            Assert.AreEqual("zero", system.GetPlural("files", 0));
            Assert.AreEqual("one", system.GetPlural("files", 1));
            Assert.AreEqual("two", system.GetPlural("files", 2));
            Assert.AreEqual("few", system.GetPlural("files", 3));
            Assert.AreEqual("many", system.GetPlural("files", 11));
            Assert.AreEqual("other", system.GetPlural("files", 100));
        }

        /// <summary>Japanese has a single form for every count.</summary>
        [Test]
        public void GetPlural_Japanese_SingleForm()
        {
            var system = PluralFixture();
            system.SetLanguage("ja");

            Assert.AreEqual("fairu", system.GetPlural("files", 0));
            Assert.AreEqual("fairu", system.GetPlural("files", 1));
            Assert.AreEqual("fairu", system.GetPlural("files", 7));
        }

        /// <summary>A locale variant with no exact rule falls back to its primary subtag.</summary>
        [Test]
        public void GetPluralRules_LocaleVariant_FallsBackToPrimarySubtag()
        {
            var system = new LocalizationSystem();

            Assert.AreEqual(PluralRuleSet.English.Name, system.GetPluralRules("pt-BR").Name);
        }

        /// <summary>Rule sets are pluggable per language code.</summary>
        [Test]
        public void RegisterPluralRule_OverridesTheRuleSetForThatLanguage()
        {
            var system = new LocalizationSystem();
            system.RegisterPluralRule("xx", PluralRuleSet.Polish);

            Assert.AreEqual(PluralCategory.Many, system.GetPluralCategory("xx", 5));
        }

        // ─── Validation pass ────────────────────────────────────────

        /// <summary>A project with one loaded language always validates.</summary>
        [Test]
        public void ValidateLanguages_SingleLanguage_IsValid()
        {
            var system = new LocalizationSystem();
            system.LoadLanguageFromString("en", "{\"a\": \"A\", \"b\": \"B\", \"c\": \"C\"}");

            Assert.IsTrue(system.ValidateLanguages().IsValid);
        }

        /// <summary>The pass names every key missing from each loaded language, in both directions.</summary>
        [Test]
        public void ValidateLanguages_ReportsMissingKeysPerLanguage()
        {
            var system = new LocalizationSystem();
            system.LoadLanguageFromString("en", "{\"a\": \"A\", \"b\": \"B\", \"c\": \"C\"}");
            system.LoadLanguageFromString("pl", "{\"a\": \"A\", \"d\": \"D\", \"n\": \"one|other\"}");

            var report = system.ValidateLanguages("en");

            Assert.IsFalse(report.IsValid);
            Assert.AreEqual(5, report.ExpectedKeyCount, "The union of both files is five keys.");
            Assert.AreEqual("b,c", string.Join(",", report.GetMissingKeys("pl")));
            Assert.AreEqual("d,n", string.Join(",", report.GetMissingKeys("en")));
            StringAssert.Contains("is missing", report.ToString());
        }

        /// <summary>A value whose pipe-separated form count disagrees with the language's rules is reported.</summary>
        [Test]
        public void ValidateLanguages_ReportsPluralFormCountMismatch()
        {
            var system = new LocalizationSystem();
            system.LoadLanguageFromString("en", "{\"a\": \"A\", \"b\": \"B\", \"c\": \"C\"}");
            system.LoadLanguageFromString("pl", "{\"a\": \"A\", \"d\": \"D\", \"n\": \"one|other\"}");

            var report = system.ValidateLanguages("en");

            Assert.AreEqual(1, report.PluralFormIssues.Length);
            StringAssert.Contains("'n'", report.PluralFormIssues[0]);
            StringAssert.Contains("polish", report.PluralFormIssues[0]);
        }

        // ─── The shipped file ───────────────────────────────────────

        /// <summary>
        /// Regression pass over the real <c>en.json</c>: every key that existed before WP-15 must
        /// still resolve, and the keys WP-15 added to exercise the parser must decode correctly.
        /// </summary>
        [Test]
        public void ShippedEnglishFile_ResolvesEveryKeyAndValidates()
        {
            var system = new LocalizationSystem();

            Assert.IsTrue(system.LoadLanguage("en"), "en.json should load from Resources/Localization.");
            system.SetLanguage("en");

            // The nine keys that existed before WP-15.
            Assert.AreEqual("Play", system.Get("menu.play"));
            Assert.AreEqual("Settings", system.Get("menu.settings"));
            Assert.AreEqual("Quit", system.Get("menu.quit"));
            Assert.AreEqual("Resume", system.Get("menu.resume"));
            Assert.AreEqual("Back", system.Get("menu.back"));
            Assert.AreEqual("Confirm", system.Get("menu.confirm"));
            Assert.AreEqual("Cancel", system.Get("menu.cancel"));
            Assert.AreEqual("Loading...", system.Get("loading.text"));
            Assert.AreEqual("Hello, Ada!", system.Get("greeting", ("name", "Ada")));

            // Keys added by WP-15 to exercise the parser.
            Assert.AreEqual("Working\u2026", system.Get("loading.hint"));
            Assert.AreEqual("\u00a9 2026 Skylotus \u2014 all rights reserved", system.Get("credits.copyright"));
            Assert.AreEqual("She said \"no\", then left.", system.Get("dialogue.quoted"));
            Assert.AreEqual("Score: 40", system.Get("hud.score", ("score", 40)));
            Assert.AreEqual("Lives: 3", system.Get("hud.lives", ("lives", 3)));
            Assert.AreEqual("1 item", system.GetPlural("items.count", 1));
            Assert.AreEqual("0 items", system.GetPlural("items.count", 0));
            Assert.AreEqual("7 items", system.GetPlural("items.count", 7));

            var report = system.ValidateLanguages();
            Assert.IsTrue(report.IsValid, report.ToString());
        }

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>Build a system loaded with one pluralized key in six languages.</summary>
        /// <returns>The prepared system.</returns>
        private static LocalizationSystem PluralFixture()
        {
            var system = new LocalizationSystem();

            system.LoadLanguageFromString("en", "{\"files\": \"{count} file|{count} files\"}");
            system.LoadLanguageFromString("fr", "{\"files\": \"{count} fichier|{count} fichiers\"}");
            system.LoadLanguageFromString("pl", "{\"files\": \"plik|pliki|plikow\"}");
            system.LoadLanguageFromString("ru", "{\"files\": \"fayl|fayla|faylov\"}");
            system.LoadLanguageFromString("ar", "{\"files\": \"zero|one|two|few|many|other\"}");
            system.LoadLanguageFromString("ja", "{\"files\": \"fairu\"}");

            return system;
        }

        /// <summary>Read a key from a parsed dictionary, failing the test if it was dropped.</summary>
        /// <param name="parsed">The parsed dictionary.</param>
        /// <param name="key">The key to read.</param>
        /// <returns>The value.</returns>
        private static string Lookup(Dictionary<string, string> parsed, string key)
        {
            Assert.IsTrue(parsed.TryGetValue(key, out var value), $"Parser dropped key '{key}'.");
            return value;
        }
    }
}
