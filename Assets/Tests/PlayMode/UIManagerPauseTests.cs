using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Skylotus.Tests.PlayMode
{
    /// <summary>
    /// PlayMode coverage for WP-4's acceptance criteria <b>at the <see cref="UIManager"/> layer</b>.
    ///
    /// WP-4 proved the pause-token stack against <see cref="TimeManager"/> directly; the modal →
    /// pause-token path was only inferred from reading <c>UIManager</c>. These cases drive the real
    /// <c>ShowModal</c> / <c>CloseModal</c> API and assert on the global <c>Time.timeScale</c>, so
    /// the criterion is observed end to end.
    ///
    /// <b>Isolation.</b> The <see cref="TimeManager"/> used here is the registered one WP-5's editor
    /// auto-bootstrap brought up — that is the point: <c>UIManager.RequestPause</c> resolves it
    /// through <see cref="ServiceLocator"/>, and swapping in a private instance would test the test
    /// rather than the wiring. The <see cref="UIManager"/> and the modals are this fixture's own,
    /// and every case releases what it took.
    /// </summary>
    [TestFixture]
    public class UIManagerPauseTests
    {
        /// <summary>Tolerance for time-scale comparisons.</summary>
        private const float Tolerance = 0.0001f;

        /// <summary>How long to wait for a close transition before giving up.</summary>
        private const float CloseTimeoutSeconds = 5f;

        /// <summary>A concrete <see cref="UIScreen"/> so modals can be constructed at runtime.</summary>
        private class TestModal : UIScreen { }

        /// <summary>The UI manager under test.</summary>
        private UIManager _ui;

        /// <summary>GameObject hosting <see cref="_ui"/>.</summary>
        private GameObject _uiHost;

        /// <summary>The registered time manager the UI pauses through.</summary>
        private TimeManager _time;

        /// <summary>Modals created by the current case, torn down afterwards.</summary>
        private readonly List<TestModal> _modals = new List<TestModal>();

        /// <summary>The global time scale as it was before this fixture ran.</summary>
        private float _originalTimeScale;

        /// <summary>Capture the global time scale once for the whole fixture.</summary>
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _originalTimeScale = Time.timeScale;
        }

        /// <summary>Put the global time scale back.</summary>
        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            Time.timeScale = _originalTimeScale;
        }

        /// <summary>Resolve the registered time manager and build a fresh UI manager.</summary>
        [SetUp]
        public void SetUp()
        {
            GameLogger.SetCategoryLevel("UI", LogLevel.Off);
            GameLogger.SetCategoryLevel("Time", LogLevel.Off);

            Assert.IsTrue(ServiceLocator.TryGet(out _time),
                "No TimeManager is registered. These tests rely on the core systems being up — " +
                "in the Editor that is WP-5's auto-bootstrap, which runs before any play-mode test.");

            _time.ReleaseAllPauses();
            _time.GameTimeScale = 1f;

            _uiHost = new GameObject("UIManagerPauseTests") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_uiHost);
            _ui = _uiHost.AddComponent<UIManager>();
        }

        /// <summary>Stop the UI manager's coroutines, destroy the modals, and unwind the pause state.</summary>
        [TearDown]
        public void TearDown()
        {
            // Destroy the manager first: it owns the fade coroutines, which would otherwise keep
            // touching a CanvasGroup that is about to go away.
            if (_uiHost != null) UnityEngine.Object.DestroyImmediate(_uiHost);

            foreach (var modal in _modals)
                if (modal != null) UnityEngine.Object.DestroyImmediate(modal.gameObject);
            _modals.Clear();

            if (_time != null)
            {
                _time.CancelTimer("_slowmo_restore");
                _time.ReleaseAllPauses();
                _time.GameTimeScale = 1f;
            }

            Time.timeScale = 1f;
            GameLogger.SetCategoryLevel("UI", LogLevel.Debug);
            GameLogger.SetCategoryLevel("Time", LogLevel.Debug);
        }

        // ─── WP-4 criterion 1, through the UI ───────────────────────

        /// <summary>
        /// WP-4 criterion 1 at the UI layer: open a pausing modal during
        /// <c>SlowMotion(0.3f, ...)</c> and close it — time must return to <b>0.3</b>, not 1.0.
        /// This is the exact line <c>UIManager.CloseModalRoutine</c> used to get wrong.
        /// </summary>
        [UnityTest]
        public IEnumerator CloseModal_DuringSlowMotion_ReturnsToTheSlowMotionScale()
        {
            var modal = NewModal("PausingModal", pausesGame: true);

            _time.SlowMotion(0.3f, 10f);
            Assert.AreEqual(0.3f, Time.timeScale, Tolerance, "Setup: slow motion should be in flight.");

            _ui.ShowModal(modal);
            Assert.AreEqual(0f, Time.timeScale, Tolerance, "A pausing modal should freeze the game.");
            Assert.IsTrue(_time.HasPauseRequest(modal), "The modal itself should hold the request.");

            yield return WaitForShow();

            _ui.CloseModal(modal);
            yield return WaitForClose(() => !_time.HasPauseRequest(modal));

            Assert.AreEqual(0.3f, Time.timeScale, Tolerance,
                "Closing the modal must return to the in-flight slow motion, not to 1.");

            _time.CancelTimer("_slowmo_restore");
        }

        // ─── WP-4 criterion 2, through the UI ───────────────────────

        /// <summary>
        /// WP-4 criterion 2 at the UI layer: with two pausing modals open, closing the first must
        /// leave the game paused.
        /// </summary>
        [UnityTest]
        public IEnumerator CloseModal_WithASecondPausingModalOpen_LeavesTheGamePaused()
        {
            var first = NewModal("FirstModal", pausesGame: true);
            var second = NewModal("SecondModal", pausesGame: true);

            _ui.ShowModal(first);
            _ui.ShowModal(second);

            Assert.AreEqual(2, _time.PauseRequestCount, "Each modal should hold its own request.");
            Assert.AreEqual(0f, Time.timeScale, Tolerance);

            yield return WaitForShow();

            _ui.CloseModal(first);
            yield return WaitForClose(() => !_time.HasPauseRequest(first));

            Assert.AreEqual(0f, Time.timeScale, Tolerance,
                "The second modal still wants the game paused.");
            Assert.IsTrue(_time.HasPauseRequest(second));

            _ui.CloseModal(second);
            yield return WaitForClose(() => !_time.HasPauseRequest(second));

            Assert.AreEqual(1f, Time.timeScale, Tolerance, "The last close resumes the game.");
        }

        // ─── The sharp edge WP-4 removed ────────────────────────────

        /// <summary>
        /// The specific defect WP-4 fixed: a modal that never asked for a pause must not touch the
        /// time scale when it closes. The old <c>CloseModalRoutine</c> wrote <c>1f</c>
        /// unconditionally, destroying any slow motion in flight.
        /// </summary>
        [UnityTest]
        public IEnumerator CloseModal_NonPausingModal_DoesNotTouchTheTimeScale()
        {
            var modal = NewModal("NonPausingModal", pausesGame: false);

            _time.GameTimeScale = 0.25f;
            Assert.AreEqual(0.25f, Time.timeScale, Tolerance, "Setup: slow motion should be applied.");

            _ui.ShowModal(modal);
            Assert.AreEqual(0.25f, Time.timeScale, Tolerance,
                "A non-pausing modal must not freeze the game.");
            Assert.AreEqual(0, _time.PauseRequestCount);

            yield return WaitForShow();

            _ui.CloseModal(modal);
            yield return WaitForClose(() => !ModalIsOpen(modal));

            Assert.AreEqual(0.25f, Time.timeScale, Tolerance,
                "Closing a modal that never paused must leave the time scale alone.");
        }

        /// <summary>
        /// A modal closing must not cancel a pause the game itself is holding — the composition
        /// rule, seen from the UI side.
        /// </summary>
        [UnityTest]
        public IEnumerator CloseModal_DoesNotCancelAGameplayPause()
        {
            var modal = NewModal("PausingModal", pausesGame: true);

            _time.Pause();
            _ui.ShowModal(modal);

            Assert.AreEqual(2, _time.PauseRequestCount);

            yield return WaitForShow();

            _ui.CloseModal(modal);
            yield return WaitForClose(() => !_time.HasPauseRequest(modal));

            Assert.AreEqual(0f, Time.timeScale, Tolerance,
                "The gameplay pause outlives the modal.");
            Assert.AreEqual(1, _time.PauseRequestCount);

            _time.Resume();
            Assert.AreEqual(1f, Time.timeScale, Tolerance);
        }

        /// <summary>
        /// A pausing modal destroyed without closing — a scene load tearing the UI down mid-modal —
        /// must not leave the game frozen with nothing to release it.
        /// </summary>
        [UnityTest]
        public IEnumerator DestroyedPausingModal_IsPrunedSoTheGameResumes()
        {
            var modal = NewModal("DoomedModal", pausesGame: true);

            _ui.ShowModal(modal);
            Assert.AreEqual(0f, Time.timeScale, Tolerance, "Setup: the modal should freeze the game.");

            // Let the show fade finish before the CanvasGroup it is animating goes away.
            yield return WaitForShow();

            UnityEngine.Object.DestroyImmediate(modal.gameObject);
            _modals.Clear();

            // TimeManager.Update sweeps destroyed owners; give it a couple of frames.
            yield return null;
            yield return null;

            Assert.AreEqual(0, _time.PauseRequestCount);
            Assert.AreEqual(1f, Time.timeScale, Tolerance);
        }

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>
        /// Build a modal: a root GameObject with a <see cref="CanvasGroup"/> (so the real fade path
        /// runs) and a <see cref="TestModal"/> whose <c>_pauseGameWhenOpen</c> flag is set the way a
        /// designer would set it in the inspector.
        /// </summary>
        /// <param name="name">Name for the modal GameObject.</param>
        /// <param name="pausesGame">Value for the serialized <c>_pauseGameWhenOpen</c> field.</param>
        /// <returns>The modal, deactivated and ready to be shown.</returns>
        private TestModal NewModal(string name, bool pausesGame)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<CanvasGroup>();

            var modal = go.AddComponent<TestModal>();
            SetPrivateField(modal, "_pauseGameWhenOpen", pausesGame);
            go.SetActive(false);

            _modals.Add(modal);
            return modal;
        }

        /// <summary>Whether the UI manager still lists a modal as open.</summary>
        /// <param name="modal">The modal to look for.</param>
        /// <returns>True while the modal is in the active-modal list.</returns>
        private bool ModalIsOpen(UIScreen modal)
        {
            var field = typeof(UIManager).GetField(
                "_activeModals", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(field, "UIManager._activeModals not found — was it renamed?");
            return ((List<UIScreen>)field.GetValue(_ui)).Contains(modal);
        }

        /// <summary>
        /// Wait, in real time, until a close transition has finished. The fade runs on unscaled
        /// time, so this works while the game is frozen at <c>timeScale</c> 0.
        /// </summary>
        /// <param name="finished">Predicate that becomes true once the close has completed.</param>
        /// <returns>An enumerator to yield on.</returns>
        private static IEnumerator WaitForClose(Func<bool> finished)
        {
            float deadline = Time.realtimeSinceStartup + CloseTimeoutSeconds;

            while (!finished() && Time.realtimeSinceStartup < deadline)
                yield return null;

            Assert.IsTrue(finished(),
                $"The modal close did not complete within {CloseTimeoutSeconds}s.");
        }

        /// <summary>Assign a <c>[SerializeField] private</c> field on a component created at runtime.</summary>
        /// <param name="target">Instance whose private field is being set.</param>
        /// <param name="fieldName">Name of the private instance field.</param>
        /// <param name="value">Value to assign.</param>
        private static void SetPrivateField(object target, string fieldName, object value)
        {
            // Walk the hierarchy: a private field declared on the base UIScreen is not visible
            // through the derived type, which is where the flag this needs actually lives.
            FieldInfo field = null;
            for (var type = target.GetType(); type != null && field == null; type = type.BaseType)
                field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.IsNotNull(field, $"{target.GetType().Name} has no field '{fieldName}'.");
            field.SetValue(target, value);
        }

        /// <summary>
        /// Wait out a show transition in real time, so the fade coroutine has finished before the
        /// case closes or destroys the modal it is animating.
        /// </summary>
        /// <returns>An enumerator to yield on.</returns>
        private static IEnumerator WaitForShow()
        {
            yield return new WaitForSecondsRealtime(0.4f);
        }
    }
}
