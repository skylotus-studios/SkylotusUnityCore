using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Skylotus.Editor
{
    /// <summary>
    /// Batchmode checks for <see cref="SettingsService"/> (WP-2): that a saved setting is
    /// actually pushed onto Unity by <c>ApplyAll</c> without any settings screen being opened,
    /// that a setter persists without flushing, and that no settings tab reaches for
    /// <c>PlayerPrefs</c> behind the service's back.
    ///
    /// These live here rather than in <c>Assets/Tests/</c> because that folder is owned by
    /// WP-12 and does not exist yet. Once an EditMode test assembly lands, move the bodies of
    /// the <c>CheckXxx</c> methods into NUnit cases and delete this file.
    ///
    /// <b>What this cannot prove.</b> A real quit-and-relaunch needs a PlayMode run or a built
    /// player. This runs in the Editor, so it exercises the same code path
    /// (<c>PlayerPrefs</c> → <c>ApplyAll</c> → Unity) that <see cref="Bootstrapper"/> invokes at
    /// boot, but not the boot sequence itself. Audio is likewise out of reach: an
    /// <c>AudioManager</c> added in Edit Mode never gets its <c>Awake</c>, so its mixer is
    /// unresolved and its volumes prove nothing.
    /// </summary>
    /// <remarks>
    /// Run with:
    /// <c>Tools\unity-verify.ps1 -Mode method -Method Skylotus.Editor.SkylotusCI.ValidateSettings</c>
    /// </remarks>
    public static partial class SkylotusCI
    {
        /// <summary>Collected failure descriptions for the current settings run.</summary>
        private static readonly List<string> _settingsFailures = new List<string>();

        /// <summary>
        /// Settings tab sources that must contain no direct PlayerPrefs access, relative to
        /// <c>Application.dataPath</c> — batchmode's working directory is the caller's, not the
        /// project's, so a project-relative literal would not resolve.
        /// </summary>
        private static readonly string[] _settingsTabSources =
        {
            "Scripts/MainMenu/Settings/SettingsAudioTab.cs",
            "Scripts/MainMenu/Settings/SettingsVideoTab.cs",
            "Scripts/MainMenu/Settings/SettingsGameplayTab.cs"
        };

        // ─── Entry Point ────────────────────────────────────────────

        /// <summary>
        /// Verify WP-2's acceptance criteria that are reachable from Edit Mode: a value saved
        /// under the service's own keys is applied by <c>ApplyAll</c> with no settings screen
        /// involved, a setter persists the value without touching the disk, and the three tab
        /// classes hold no <c>PlayerPrefs</c> calls.
        ///
        /// Every pref this touches is captured and restored before the method returns, so
        /// running it does not leave the developer's editor prefs altered.
        /// </summary>
        public static void ValidateSettings()
        {
            _settingsFailures.Clear();

            int originalQuality = QualitySettings.GetQualityLevel();
            var originalVSync = CaptureVSyncPerLevel();
            var originalPrefs = CaptureSettingsPrefs();

            try
            {
                CheckApplyAllPushesSavedValues();
                CheckSettersPersistWithoutFlushing();
                CheckDefaultsWhenNothingSaved();
                CheckTabsHoldNoPlayerPrefs();
            }
            catch (Exception e)
            {
                Fail($"ValidateSettings threw: {e}");
                return;
            }
            finally
            {
                RestoreSettingsPrefs(originalPrefs);
                RestoreVSyncPerLevel(originalVSync);
                QualitySettings.SetQualityLevel(originalQuality, true);
            }

            if (_settingsFailures.Count > 0)
            {
                foreach (var failure in _settingsFailures)
                    Debug.LogError($"[{Category}] {failure}");

                Fail($"{_settingsFailures.Count} settings check(s) failed.");
                return;
            }

            Succeed("Settings: ApplyAll pushes saved values, setters defer the disk write, " +
                    "and no tab touches PlayerPrefs.");
        }

        // ─── Checks ─────────────────────────────────────────────────

        /// <summary>
        /// The bug WP-2 fixes: a saved value must reach Unity without any settings screen being
        /// opened. Writes a quality level and a vertical-sync state straight into PlayerPrefs,
        /// then constructs a fresh service and calls <c>ApplyAll</c> — exactly what
        /// <see cref="Bootstrapper"/> does at boot — and asserts Unity actually changed.
        /// </summary>
        private static void CheckApplyAllPushesSavedValues()
        {
            if (QualitySettings.names.Length < 2)
            {
                SettingsFail("apply quality",
                    "project has fewer than two quality levels; cannot prove a level change");
                return;
            }

            // Pick a level that is definitely not the one already active, so passing cannot be
            // a coincidence.
            int current = QualitySettings.GetQualityLevel();
            int target = current == 0 ? 1 : 0;
            bool targetVSync = QualitySettings.vSyncCount == 0;

            PlayerPrefs.SetInt(SettingsService.QualityKey, target);
            PlayerPrefs.SetInt(SettingsService.VSyncKey, targetVSync ? 1 : 0);

            new SettingsService().ApplyAll();

            SettingsTrue("ApplyAll applies quality",
                QualitySettings.GetQualityLevel() == target,
                $"expected level {target}, got {QualitySettings.GetQualityLevel()}");

            SettingsTrue("ApplyAll applies vsync",
                (QualitySettings.vSyncCount > 0) == targetVSync,
                $"expected vSync {targetVSync}, got {QualitySettings.vSyncCount}");
        }

        /// <summary>
        /// A setter must persist its value and mark the service dirty, but must not flush —
        /// that is what keeps a full slider drag to a single disk write. <c>Flush</c> then
        /// clears the flag, and flushing a clean service is a no-op.
        /// </summary>
        private static void CheckSettersPersistWithoutFlushing()
        {
            var settings = new SettingsService();

            SettingsTrue("fresh service is clean", !settings.IsDirty, "IsDirty was true on construction");

            settings.SetBrightness(0.25f);
            SettingsTrue("setter marks dirty", settings.IsDirty, "IsDirty was false after SetBrightness");

            SettingsTrue("setter persists immediately",
                Mathf.Approximately(PlayerPrefs.GetFloat(SettingsService.BrightnessKey, -1f), 0.25f),
                $"pref held {PlayerPrefs.GetFloat(SettingsService.BrightnessKey, -1f)}");

            // Many more writes, still one pending flush — the slider-drag case.
            for (int i = 0; i < 100; i++)
                settings.SetBrightness(i / 100f);

            settings.Flush();
            SettingsTrue("flush clears dirty", !settings.IsDirty, "IsDirty was true after Flush");

            settings.Flush();
            SettingsTrue("flushing a clean service is a no-op", !settings.IsDirty, "IsDirty flipped");

            // A brand-new service must read back what the previous one wrote.
            SettingsTrue("value round-trips through PlayerPrefs",
                Mathf.Approximately(new SettingsService().Brightness, 0.99f),
                $"read back {new SettingsService().Brightness}");
        }

        /// <summary>
        /// With no saved value, a reader must report the typed default rather than zero, and
        /// <c>ApplyAll</c> must leave the project's own settings alone.
        /// </summary>
        private static void CheckDefaultsWhenNothingSaved()
        {
            PlayerPrefs.DeleteKey(SettingsService.BrightnessKey);
            PlayerPrefs.DeleteKey(SettingsService.QualityKey);
            PlayerPrefs.DeleteKey(SettingsService.VSyncKey);

            var settings = new SettingsService();

            SettingsTrue("brightness default",
                Mathf.Approximately(settings.Brightness, SettingsService.DefaultBrightness),
                $"expected {SettingsService.DefaultBrightness}, got {settings.Brightness}");

            SettingsTrue("vibration default",
                settings.Vibration == SettingsService.DefaultVibration,
                $"expected {SettingsService.DefaultVibration}, got {settings.Vibration}");

            SettingsTrue("screenshake default",
                settings.Screenshake == SettingsService.DefaultScreenshake,
                $"expected {SettingsService.DefaultScreenshake}, got {settings.Screenshake}");

            int before = QualitySettings.GetQualityLevel();
            settings.ApplyAll();

            SettingsTrue("ApplyAll leaves unset values alone",
                QualitySettings.GetQualityLevel() == before,
                $"quality moved from {before} to {QualitySettings.GetQualityLevel()}");
        }

        /// <summary>
        /// The tabs are view code. Any <c>PlayerPrefs</c> call in one of them is a key the
        /// service does not own — the exact duplication WP-2 removed.
        /// </summary>
        private static void CheckTabsHoldNoPlayerPrefs()
        {
            foreach (var relative in _settingsTabSources)
            {
                string path = Path.Combine(Application.dataPath, relative);

                if (!File.Exists(path))
                {
                    SettingsFail("tab source", $"{path} not found");
                    continue;
                }

                int line = FindPlayerPrefsUse(File.ReadAllLines(path));

                SettingsTrue($"no PlayerPrefs in {Path.GetFileName(path)}",
                    line < 0,
                    $"line {line} contains a direct PlayerPrefs reference");
            }
        }

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>
        /// Find the first line of code — not comment — naming <c>PlayerPrefs</c>. Doc comments
        /// in these files legitimately mention the type to say they no longer use it, so a plain
        /// substring search over the whole file would report those. None of the settings tabs
        /// contain block comments, so skipping <c>//</c> and <c>///</c> lines is sufficient.
        /// </summary>
        /// <param name="lines">The file's lines.</param>
        /// <returns>The 1-based line number of the first use, or -1 if there is none.</returns>
        private static int FindPlayerPrefsUse(string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//")) continue;

                if (lines[i].Contains("PlayerPrefs"))
                    return i + 1;
            }

            return -1;
        }

        /// <summary>
        /// Read <c>vSyncCount</c> for every quality level.
        ///
        /// <c>QualitySettings.vSyncCount</c> is a property of the <i>active</i> quality level,
        /// not a global — writing it while a probe has switched levels edits that level and
        /// persists into <c>ProjectSettings/QualitySettings.asset</c>. Capturing all of them is
        /// the only restore that cannot put a value back on the wrong level.
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
        /// Every pref key this validation writes, so it can put them all back. Values are held
        /// as strings because the keys are a mix of ints and floats; the key itself says which
        /// is which on the way back in.
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
                    : null
            };
        }

        /// <summary>Put the captured prefs back exactly as they were, deleting any this run created.</summary>
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

        /// <summary>Record a failure unless the condition holds.</summary>
        /// <param name="what">Short name of the behaviour being checked.</param>
        /// <param name="condition">The condition that must be true.</param>
        /// <param name="detail">What went wrong, shown when the condition is false.</param>
        private static void SettingsTrue(string what, bool condition, string detail)
        {
            if (!condition)
                _settingsFailures.Add($"{what}: {detail}");
        }

        /// <summary>Record an unconditional failure.</summary>
        /// <param name="what">Short name of the behaviour being checked.</param>
        /// <param name="detail">What went wrong.</param>
        private static void SettingsFail(string what, string detail)
        {
            _settingsFailures.Add($"{what}: {detail}");
        }
    }
}
