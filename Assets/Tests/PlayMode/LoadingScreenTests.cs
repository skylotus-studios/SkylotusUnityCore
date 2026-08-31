using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Skylotus.Tests.PlayMode
{
    /// <summary>
    /// PlayMode coverage for WP-1's carried-over criterion: <c>LoadScene(name,
    /// showLoadingScreen: true)</c> fades an actual overlay in, drives the progress bar to 1, and
    /// fades it out.
    ///
    /// <b>Nothing in the project exercises this path.</b> <c>Bootstrapper.Start</c> loads the first
    /// scene with <c>showLoadingScreen: false</c>, so before this fixture existed the overlay had
    /// never run once. These tests drive it directly.
    ///
    /// <b>Isolation.</b> The <see cref="SkylotusSceneManager"/> used here is the registered one — the whole
    /// point of the criterion is that its serialized loading-screen references, which only exist
    /// because WP-1 moved the systems onto a prefab, are real. A privately constructed
    /// <c>SkylotusSceneManager</c> would have the null references WP-1 removed and would prove nothing.
    /// The fade duration is lengthened for the run and restored afterwards so the fade can be
    /// sampled across frames rather than caught in one.
    /// </summary>
    [TestFixture]
    public class LoadingScreenTests
    {
        /// <summary>
        /// Scene the loading screen is exercised against. Must be in Build Settings.
        ///
        /// <c>BootScene</c> rather than <c>Gameplay</c> deliberately: <c>Gameplay</c> carries a
        /// <c>CursorCanvas</c> whose <c>CustomCursor.UpdateMouseCursor</c> falls back to the legacy
        /// <c>UnityEngine.Input.mousePosition</c> when no mouse device is present, which throws
        /// every frame under this project's Input-System-only handling — a real defect, but one
        /// that lives outside this package's files and would otherwise mask every assertion here.
        /// <c>BootScene</c> holds only a camera and a <c>Bootstrapper</c>, and that bootstrapper
        /// destroys itself on sight because the core systems are already up.
        /// </summary>
        private const string TargetScene = "BootScene";

        /// <summary>Fade duration used during the test, long enough to sample across frames.</summary>
        private const float TestFadeDuration = 0.6f;

        /// <summary>A value nothing in the load path would produce, so a written bar is unmistakable.</summary>
        private const float BarSentinel = 0.37f;

        /// <summary>Give up on a load after this many real seconds.</summary>
        private const float LoadTimeoutSeconds = 60f;

        /// <summary>The registered scene manager.</summary>
        private SkylotusSceneManager _scenes;

        /// <summary>The overlay's CanvasGroup, read off the manager's serialized field.</summary>
        private CanvasGroup _overlay;

        /// <summary>The progress bar, read off the manager's serialized field.</summary>
        private Slider _progressBar;

        /// <summary>The manager's authored fade duration, restored in teardown.</summary>
        private float _originalFadeDuration;

        /// <summary>The progress bar's authored value, restored in teardown.</summary>
        private float _originalBarValue;

        /// <summary>Resolve the registered scene manager and its overlay references.</summary>
        [SetUp]
        public void SetUp()
        {
            GameLogger.SetCategoryLevel("Scene", LogLevel.Off);

            Assert.IsTrue(ServiceLocator.TryGet(out _scenes),
                "No SkylotusSceneManager is registered. These tests rely on the core systems being up — " +
                "in the Editor that is WP-5's auto-bootstrap, which runs before any play-mode test.");

            _overlay = GetPrivate<CanvasGroup>(_scenes, "_loadingScreen");
            _progressBar = GetPrivate<Slider>(_scenes, "_progressBar");

            _originalFadeDuration = GetPrivate<float>(_scenes, "_fadeDuration");
            SetPrivate(_scenes, "_fadeDuration", TestFadeDuration);

            if (_progressBar != null) _originalBarValue = _progressBar.value;
        }

        /// <summary>Put the manager's authored values back.</summary>
        [TearDown]
        public void TearDown()
        {
            if (_scenes != null) SetPrivate(_scenes, "_fadeDuration", _originalFadeDuration);
            if (_progressBar != null) _progressBar.value = _originalBarValue;

            GameLogger.SetCategoryLevel("Scene", LogLevel.Debug);
        }

        // ─── The wiring WP-1 made possible ──────────────────────────

        /// <summary>
        /// The overlay and progress bar are assigned on the running <see cref="SkylotusSceneManager"/>.
        /// Before WP-1 these were permanently null — the systems were built with
        /// <c>AddComponent</c>, so no serialized value could ever reach them, and every
        /// <c>showLoadingScreen: true</c> call was a silent no-op.
        /// </summary>
        [Test]
        public void RegisteredSceneManager_HasItsLoadingScreenReferencesAssigned()
        {
            Assert.IsNotNull(_overlay,
                "SkylotusSceneManager._loadingScreen is null at runtime — the core systems prefab is not " +
                "in use, or its loading-screen canvas is unwired.");
            Assert.IsNotNull(_progressBar, "SkylotusSceneManager._progressBar is null at runtime.");
        }

        /// <summary>The overlay starts hidden and deactivated, so it never blocks the first frame.</summary>
        [Test]
        public void LoadingOverlay_StartsHiddenAndInactive()
        {
            Assert.IsNotNull(_overlay);
            Assert.AreEqual(0f, _overlay.alpha, 0.001f);
            Assert.IsFalse(_overlay.gameObject.activeInHierarchy);
        }

        // ─── The criterion itself ───────────────────────────────────

        /// <summary>
        /// WP-1's carried-over criterion, observed frame by frame: the overlay is activated and
        /// faded from 0 to 1, the progress bar is driven to 1, the scene actually changes, and the
        /// overlay is faded back out and deactivated.
        ///
        /// The progress bar is pre-set to a sentinel value first, so "the bar reached 1" cannot be
        /// satisfied by a bar that was already there. Unity's async loader reports 0.9 almost
        /// immediately for a scene this small, so the honest claim is that the bar was below 1
        /// while the overlay was up and was written to 1 by the end — not that a smooth ramp of
        /// intermediate values was observed.
        /// </summary>
        [UnityTest]
        public IEnumerator LoadScene_WithLoadingScreen_FadesInDrivesTheBarAndFadesOut()
        {
            Assert.IsNotNull(_overlay, "The overlay must be wired for this criterion to mean anything.");
            Assert.IsNotNull(_progressBar);
            Assert.IsFalse(_scenes.IsLoading, "Setup: no other load may be in flight.");

            _progressBar.value = BarSentinel;

            var reportedProgress = new List<float>();
            var alphaSamples = new List<float>();
            bool overlayWasActivated = false;
            bool sawBarBelowOneWhileVisible = false;
            float maxAlpha = 0f;
            float maxBar = 0f;

            string loadedScene = null;
            OnSceneLoadedEvent busEvent = default;
            int busPublishes = 0;

            void OnProgress(float p) => reportedProgress.Add(p);
            void OnLoaded(string name) => loadedScene = name;
            void OnBus(OnSceneLoadedEvent e)
            {
                busEvent = e;
                busPublishes++;
            }

            _scenes.OnProgress += OnProgress;
            _scenes.OnSceneLoaded += OnLoaded;
            EventBus.Subscribe<OnSceneLoadedEvent>(OnBus);

            try
            {
                _scenes.LoadScene(TargetScene, showLoadingScreen: true, addToHistory: false);

                float deadline = Time.realtimeSinceStartup + LoadTimeoutSeconds;

                // Sample every frame from the moment the load starts until the overlay has faded
                // back out and been deactivated again.
                while (Time.realtimeSinceStartup < deadline)
                {
                    bool active = _overlay.gameObject.activeInHierarchy;
                    if (active) overlayWasActivated = true;

                    float alpha = _overlay.alpha;
                    alphaSamples.Add(alpha);
                    maxAlpha = Mathf.Max(maxAlpha, alpha);
                    maxBar = Mathf.Max(maxBar, _progressBar.value);

                    if (active && alpha > 0.01f && _progressBar.value < 0.999f)
                        sawBarBelowOneWhileVisible = true;

                    // The overlay is already active by the time LoadScene returns, so this only
                    // trips once the load has finished and the fade-out has deactivated it again.
                    if (!_scenes.IsLoading && !active)
                        break;

                    yield return null;
                }

                Assert.Less(Time.realtimeSinceStartup, deadline,
                    "The load never completed and faded out within the timeout.");
            }
            finally
            {
                _scenes.OnProgress -= OnProgress;
                _scenes.OnSceneLoaded -= OnLoaded;
                EventBus.Unsubscribe<OnSceneLoadedEvent>(OnBus);
            }

            // ── The overlay actually appeared ───────────────────────
            Assert.IsTrue(overlayWasActivated,
                "The loading overlay was never activated, so showLoadingScreen:true was a no-op.");
            Assert.GreaterOrEqual(maxAlpha, 0.99f,
                $"The overlay never reached full opacity (peak alpha {maxAlpha}).");

            // ── It faded rather than snapped ────────────────────────
            int intermediateSamples = 0;
            foreach (var alpha in alphaSamples)
                if (alpha > 0.05f && alpha < 0.95f) intermediateSamples++;

            Assert.Greater(intermediateSamples, 1,
                $"Only {intermediateSamples} sample(s) landed between 0 and 1 across " +
                $"{alphaSamples.Count} frames — the overlay snapped on rather than fading.");

            // ── The progress bar was driven ─────────────────────────
            Assert.IsTrue(sawBarBelowOneWhileVisible,
                "The progress bar was never observed below 1 while the overlay was visible, so " +
                "reaching 1 proves nothing.");
            Assert.AreEqual(1f, maxBar, 0.001f, "The progress bar should have been driven to 1.");
            Assert.AreEqual(1f, _progressBar.value, 0.001f,
                $"The bar ended at {_progressBar.value}; it was pre-set to {BarSentinel} and must " +
                "have been written by the loader.");

            Assert.Greater(reportedProgress.Count, 0, "OnProgress never fired.");
            Assert.AreEqual(1f, reportedProgress[reportedProgress.Count - 1], 0.001f,
                "The last reported progress should be 1.");

            // ── The scene actually changed ──────────────────────────
            Assert.AreEqual(TargetScene, SceneManager.GetActiveScene().name);
            Assert.AreEqual(TargetScene, _scenes.CurrentScene);
            Assert.AreEqual(TargetScene, loadedScene, "OnSceneLoaded should have fired with the scene name.");
            Assert.AreEqual(1, busPublishes, "OnSceneLoadedEvent should have been published once.");
            Assert.AreEqual(TargetScene, busEvent.SceneName);

            // ── And it faded back out ───────────────────────────────
            Assert.IsFalse(_scenes.IsLoading);
            Assert.AreEqual(0f, _overlay.alpha, 0.01f, "The overlay should have faded back to zero.");
            Assert.IsFalse(_overlay.gameObject.activeInHierarchy,
                "The overlay should have been deactivated after fading out.");
        }

        /// <summary>
        /// A second <c>LoadScene</c> issued while one is in flight is refused rather than
        /// interleaving two loads and two fades over the same overlay.
        /// </summary>
        [UnityTest]
        public IEnumerator LoadScene_WhileAlreadyLoading_IsRefused()
        {
            Assert.IsFalse(_scenes.IsLoading, "Setup: no other load may be in flight.");

            int loadedCount = 0;
            void OnLoaded(string name) => loadedCount++;
            _scenes.OnSceneLoaded += OnLoaded;

            try
            {
                _scenes.LoadScene(TargetScene, showLoadingScreen: true, addToHistory: false);
                Assert.IsTrue(_scenes.IsLoading);

                // Ignored: the first load owns the overlay until it finishes.
                _scenes.LoadScene(TargetScene, showLoadingScreen: true, addToHistory: false);

                float deadline = Time.realtimeSinceStartup + LoadTimeoutSeconds;
                while (_scenes.IsLoading && Time.realtimeSinceStartup < deadline)
                    yield return null;

                Assert.IsFalse(_scenes.IsLoading, "The load never completed within the timeout.");

                // Let the fade-out finish before the next case inspects the overlay.
                while (_overlay != null && _overlay.gameObject.activeInHierarchy &&
                       Time.realtimeSinceStartup < deadline)
                    yield return null;

                Assert.AreEqual(1, loadedCount, "Exactly one load should have completed.");
            }
            finally
            {
                _scenes.OnSceneLoaded -= OnLoaded;
            }
        }

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>Read a private instance field.</summary>
        /// <typeparam name="T">The field's type.</typeparam>
        /// <param name="target">The instance to read from.</param>
        /// <param name="fieldName">Name of the private field, including its underscore prefix.</param>
        /// <returns>The field's current value.</returns>
        private static T GetPrivate<T>(object target, string fieldName)
        {
            return (T)FieldOf(target, fieldName).GetValue(target);
        }

        /// <summary>Write a private instance field.</summary>
        /// <param name="target">The instance to write to.</param>
        /// <param name="fieldName">Name of the private field, including its underscore prefix.</param>
        /// <param name="value">The value to assign.</param>
        private static void SetPrivate(object target, string fieldName, object value)
        {
            FieldOf(target, fieldName).SetValue(target, value);
        }

        /// <summary>Locate a private instance field, failing the test with a clear message if it moved.</summary>
        /// <param name="target">The instance whose type is searched.</param>
        /// <param name="fieldName">Name of the private field.</param>
        /// <returns>The field.</returns>
        private static FieldInfo FieldOf(object target, string fieldName)
        {
            var field = target.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(field,
                $"{target.GetType().Name} has no field '{fieldName}' — was it renamed?");
            return field;
        }
    }
}
