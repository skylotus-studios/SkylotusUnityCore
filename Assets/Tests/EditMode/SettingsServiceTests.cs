using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Skylotus.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="SettingsService"/> (WP-2): that a value saved under the
    /// service's own keys is pushed onto Unity by <c>ApplyAll</c> with no settings screen
    /// involved, that a setter persists without flushing, that unset values report their typed
    /// default, and that the settings tabs hold no direct <c>PlayerPrefs</c> access.
    ///
    /// These cases are the WP-2 batchmode self-check, ported to NUnit.
    ///
    /// <b>What this cannot prove.</b> A real quit-and-relaunch needs a built player. This
    /// exercises the same <c>PlayerPrefs</c> → <c>ApplyAll</c> → Unity path the
    /// <see cref="Bootstrapper"/> invokes at boot, but not the boot sequence itself — that is
    /// covered by the PlayMode boot suite. Audio is out of reach here too: an
    /// <c>AudioManager</c> constructed in Edit Mode never gets its <c>Awake</c>.
    ///
    /// Every project setting and preference key touched below is captured before the fixture runs
    /// and written back afterwards, including <c>vSyncCount</c> <i>per quality level</i> — it is a
    /// property of the active level, not a global, so restoring it after a level switch would
    /// otherwise write onto the wrong level and dirty
    /// <c>ProjectSettings/QualitySettings.asset</c>.
    /// </summary>
    [TestFixture]
    public class SettingsServiceTests
    {
        /// <summary>Settings tab sources that must contain no direct PlayerPrefs access.</summary>
        private static readonly string[] _tabSources =
        {
            "Scripts/MainMenu/Settings/SettingsAudioTab.cs",
            "Scripts/MainMenu/Settings/SettingsVideoTab.cs",
            "Scripts/MainMenu/Settings/SettingsGameplayTab.cs"
        };

        /// <summary>The quality level that was active before the fixture ran.</summary>
        private int _originalQualityLevel;

        /// <summary>Each quality level's <c>vSyncCount</c> as it was before the fixture ran.</summary>
        private int[] _originalVSync;

        /// <summary>The preference keys this fixture writes, with their prior values (null = unset).</summary>
        private Dictionary<string, string> _originalPrefs;

        /// <summary>Capture every project setting and preference this fixture is allowed to change.</summary>
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _originalQualityLevel = QualitySettings.GetQualityLevel();
            _originalVSync = CaptureVSyncPerLevel();
            _originalPrefs = CaptureSettingsPrefs();
        }

        /// <summary>Write everything back exactly as it was.</summary>
        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            RestoreSettingsPrefs(_originalPrefs);
            RestoreVSyncPerLevel(_originalVSync);
            QualitySettings.SetQualityLevel(_originalQualityLevel, true);
        }

        /// <summary>Silence the settings channel — some cases exercise failure paths.</summary>
        [SetUp]
        public void SetUp()
        {
            GameLogger.SetCategoryLevel("Settings", LogLevel.Off);
        }

        /// <summary>Restore logging.</summary>
        [TearDown]
        public void TearDown()
        {
            GameLogger.SetCategoryLevel("Settings", LogLevel.Debug);
        }

        // ─── Apply at boot ──────────────────────────────────────────

        /// <summary>
        /// The bug WP-2 fixes: a saved value must reach Unity without any settings screen being
        /// opened. Writes a quality level and a vertical-sync state straight into
        /// <c>PlayerPrefs</c>, then constructs a fresh service and calls <c>ApplyAll</c> — exactly
        /// what the bootstrapper does at boot — and asserts Unity actually changed.
        /// </summary>
        [Test]
        public void ApplyAll_PushesSavedValuesOntoUnity()
        {
            Assert.GreaterOrEqual(QualitySettings.names.Length, 2,
                "This project needs at least two quality levels for a level change to be provable.");

            // Pick a level that is definitely not the one already active, so passing cannot be a
            // coincidence.
            int current = QualitySettings.GetQualityLevel();
            int target = current == 0 ? 1 : 0;
            bool targetVSync = QualitySettings.vSyncCount == 0;

            PlayerPrefs.SetInt(SettingsService.QualityKey, target);
            PlayerPrefs.SetInt(SettingsService.VSyncKey, targetVSync ? 1 : 0);

            new SettingsService().ApplyAll();

            Assert.AreEqual(target, QualitySettings.GetQualityLevel(), "ApplyAll should apply quality.");
            Assert.AreEqual(targetVSync, QualitySettings.vSyncCount > 0, "ApplyAll should apply vsync.");
        }

        /// <summary>With nothing saved, <c>ApplyAll</c> leaves the project's own settings alone.</summary>
        [Test]
        public void ApplyAll_WithNothingSaved_LeavesProjectSettingsAlone()
        {
            PlayerPrefs.DeleteKey(SettingsService.QualityKey);
            PlayerPrefs.DeleteKey(SettingsService.VSyncKey);

            int before = QualitySettings.GetQualityLevel();
            new SettingsService().ApplyAll();

            Assert.AreEqual(before, QualitySettings.GetQualityLevel());
        }

        // ─── Dirty flag ─────────────────────────────────────────────

        /// <summary>A newly constructed service has nothing pending.</summary>
        [Test]
        public void NewService_IsNotDirty()
        {
            Assert.IsFalse(new SettingsService().IsDirty);
        }

        /// <summary>
        /// A setter persists its value and marks the service dirty, but does not flush — that is
        /// what keeps a full slider drag to a single disk write.
        /// </summary>
        [Test]
        public void Setter_PersistsTheValueAndMarksDirtyWithoutFlushing()
        {
            var settings = new SettingsService();

            settings.SetBrightness(0.25f);

            Assert.IsTrue(settings.IsDirty, "A setter should mark the service dirty.");
            Assert.AreEqual(0.25f, PlayerPrefs.GetFloat(SettingsService.BrightnessKey, -1f), 0.0001f,
                "The value should be in PlayerPrefs immediately, flush or no flush.");
        }

        /// <summary>Many writes still leave exactly one pending flush, and flushing clears it.</summary>
        [Test]
        public void Flush_ClearsTheDirtyFlagAndIsANoOpWhenClean()
        {
            var settings = new SettingsService();

            for (int i = 0; i < 100; i++)
                settings.SetBrightness(i / 100f);

            Assert.IsTrue(settings.IsDirty);

            settings.Flush();
            Assert.IsFalse(settings.IsDirty, "Flush should clear the dirty flag.");

            settings.Flush();
            Assert.IsFalse(settings.IsDirty, "Flushing a clean service should do nothing.");
        }

        /// <summary>A brand-new service reads back what a previous one wrote.</summary>
        [Test]
        public void Value_RoundTripsThroughPlayerPrefsIntoAFreshService()
        {
            new SettingsService().SetBrightness(0.99f);

            Assert.AreEqual(0.99f, new SettingsService().Brightness, 0.0001f);
        }

        // ─── Defaults ───────────────────────────────────────────────

        /// <summary>With no saved value, a reader reports the typed default rather than zero.</summary>
        [Test]
        public void Readers_WithNothingSaved_ReportTheTypedDefault()
        {
            PlayerPrefs.DeleteKey(SettingsService.BrightnessKey);
            PlayerPrefs.DeleteKey(SettingsService.VibrationKey);
            PlayerPrefs.DeleteKey(SettingsService.ScreenshakeKey);

            var settings = new SettingsService();

            Assert.AreEqual(SettingsService.DefaultBrightness, settings.Brightness, 0.0001f);
            Assert.AreEqual(SettingsService.DefaultVibration, settings.Vibration);
            Assert.AreEqual(SettingsService.DefaultScreenshake, settings.Screenshake);
        }

        // ─── Ownership of the keys ──────────────────────────────────

        /// <summary>
        /// The settings tabs are view code. Any <c>PlayerPrefs</c> call in one of them is a key
        /// the service does not own — the exact duplication WP-2 removed. Doc comments in these
        /// files legitimately name the type to say they no longer use it, so comment lines are
        /// skipped.
        /// </summary>
        [Test]
        public void SettingsTabs_ContainNoDirectPlayerPrefsAccess()
        {
            foreach (var relative in _tabSources)
            {
                string path = Path.Combine(Application.dataPath, relative);
                Assert.IsTrue(File.Exists(path), $"Settings tab source not found: {path}");

                int line = FindPlayerPrefsUse(File.ReadAllLines(path));

                Assert.AreEqual(-1, line,
                    $"{Path.GetFileName(path)} line {line} reaches for PlayerPrefs directly; " +
                    "settings keys belong to SettingsService.");
            }
        }

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>Find the first line of code — not comment — naming <c>PlayerPrefs</c>.</summary>
        /// <param name="lines">The file's lines.</param>
        /// <returns>The 1-based line number of the first use, or -1 if there is none.</returns>
        private static int FindPlayerPrefsUse(string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//")) continue;
                if (lines[i].Contains("PlayerPrefs")) return i + 1;
            }

            return -1;
        }

        /// <summary>
        /// Read <c>vSyncCount</c> for every quality level. It is a property of the active level,
        /// not a global, so capturing all of them is the only restore that cannot put a value
        /// back on the wrong level.
        /// </summary>
        /// <returns>One entry per quality level, in level order.</returns>
        private static int[] CaptureVSyncPerLevel()
        {
            int active = QualitySettings.GetQualityLevel();
            var captured = new int[QualitySettings.names.Length];

            for (int i = 0; i < captured.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                captured[i] = QualitySettings.vSyncCount;
            }

            QualitySettings.SetQualityLevel(active, false);
            return captured;
        }

        /// <summary>Write back every quality level's <c>vSyncCount</c>.</summary>
        /// <param name="captured">The array returned by <see cref="CaptureVSyncPerLevel"/>.</param>
        private static void RestoreVSyncPerLevel(int[] captured)
        {
            int active = QualitySettings.GetQualityLevel();

            for (int i = 0; i < captured.Length && i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.vSyncCount = captured[i];
            }

            QualitySettings.SetQualityLevel(active, false);
        }

        /// <summary>
        /// Every preference key this fixture writes, so it can put them all back. Values are held
        /// as strings because the keys are a mix of ints and floats; the key says which is which
        /// on the way back in.
        /// </summary>
        /// <returns>The keys paired with their stored value, or null where the key is unset.</returns>
        private static Dictionary<string, string> CaptureSettingsPrefs()
        {
            return new Dictionary<string, string>
            {
                [SettingsService.QualityKey] = PlayerPrefs.HasKey(SettingsService.QualityKey)
                    ? PlayerPrefs.GetInt(SettingsService.QualityKey).ToString()
                    : null,
                [SettingsService.VSyncKey] = PlayerPrefs.HasKey(SettingsService.VSyncKey)
                    ? PlayerPrefs.GetInt(SettingsService.VSyncKey).ToString()
                    : null,
                [SettingsService.BrightnessKey] = PlayerPrefs.HasKey(SettingsService.BrightnessKey)
                    ? PlayerPrefs.GetFloat(SettingsService.BrightnessKey).ToString("R")
                    : null,
                [SettingsService.VibrationKey] = PlayerPrefs.HasKey(SettingsService.VibrationKey)
                    ? PlayerPrefs.GetInt(SettingsService.VibrationKey).ToString()
                    : null,
                [SettingsService.ScreenshakeKey] = PlayerPrefs.HasKey(SettingsService.ScreenshakeKey)
                    ? PlayerPrefs.GetInt(SettingsService.ScreenshakeKey).ToString()
                    : null
            };
        }

        /// <summary>Put the captured preferences back, deleting any this run created.</summary>
        /// <param name="captured">The map returned by <see cref="CaptureSettingsPrefs"/>.</param>
        private static void RestoreSettingsPrefs(Dictionary<string, string> captured)
        {
            foreach (var pair in captured)
            {
                if (pair.Value == null)
                {
                    PlayerPrefs.DeleteKey(pair.Key);
                }
                else if (pair.Key == SettingsService.BrightnessKey)
                {
                    PlayerPrefs.SetFloat(pair.Key, float.Parse(pair.Value));
                }
                else
                {
                    PlayerPrefs.SetInt(pair.Key, int.Parse(pair.Value));
                }
            }

            PlayerPrefs.Save();
        }
    }
}
