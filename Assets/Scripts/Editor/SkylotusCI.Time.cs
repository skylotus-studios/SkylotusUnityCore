using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Skylotus.Editor
{
    /// <summary>
    /// Batchmode checks for <see cref="TimeManager"/> (WP-4): the pause-token stack, the
    /// hit-stop / pause / <c>GameTimeScale</c> precedence rule, and the guarantee that
    /// restoring never lands on a hardcoded 1.
    ///
    /// These live here rather than in <c>Assets/Tests/</c> because that folder is owned by
    /// WP-12 and does not exist yet. Once an EditMode test assembly lands, move the bodies
    /// of the <c>CheckXxx</c> methods into NUnit cases and delete this file.
    /// </summary>
    /// <remarks>
    /// Run with:
    /// <c>Tools\unity-verify.ps1 -Mode method -Method Skylotus.Editor.SkylotusCI.ValidateTimeScale</c>
    ///
    /// The checks drive <see cref="TimeManager"/> directly rather than through the UI: they
    /// call the same public request/release API <c>UIManager</c> now calls, and step the
    /// hit-stop countdown through the private <c>AdvanceHitStop</c> so the result does not
    /// depend on a play-mode frame rate.
    /// </remarks>
    public static partial class SkylotusCI
    {
        /// <summary>Collected failure descriptions for the current time-scale run.</summary>
        private static readonly List<string> _timeFailures = new List<string>();

        // ─── Entry Point ────────────────────────────────────────────

        /// <summary>
        /// Verify WP-4's acceptance criteria: closing a pausing modal during slow motion
        /// returns to the slow-motion scale rather than 1; two overlapping pause requests
        /// compose so releasing the first leaves the game paused; and a hit-stop expiring
        /// during a pause does not resume the game.
        /// </summary>
        public static void ValidateTimeScale()
        {
            _timeFailures.Clear();

            float originalTimeScale = UnityEngine.Time.timeScale;

            try
            {
                CheckReleaseRestoresGameTimeScale();
                CheckOverlappingPausesCompose();
                CheckHitStopDuringPause();
                CheckHitStopRestoresGameTimeScale();
                CheckPushIsIdempotent();
                CheckManualPauseComposesWithModals();
                CheckDestroyedOwnerIsPruned();
            }
            catch (Exception e)
            {
                Fail($"ValidateTimeScale threw: {e}");
                return;
            }
            finally
            {
                UnityEngine.Time.timeScale = originalTimeScale;
            }

            if (_timeFailures.Count > 0)
            {
                foreach (var failure in _timeFailures)
                    Debug.LogError($"[{Category}] {failure}");

                Fail($"{_timeFailures.Count} time-scale check(s) failed.");
                return;
            }

            Succeed("TimeManager: pause tokens compose, hit-stop yields to pause, restore honours GameTimeScale.");
        }

        // ─── Checks ─────────────────────────────────────────────────

        /// <summary>
        /// WP-4 criterion 1: a pausing modal opened during <c>SlowMotion(0.3f, ...)</c> must
        /// return the game to 0.3 when it closes, not to 1.
        /// </summary>
        private static void CheckReleaseRestoresGameTimeScale()
        {
            var manager = NewTimeManager();

            try
            {
                var modal = new object();

                manager.GameTimeScale = 0.3f;
                TimeEqual("slow motion applied", 0.3f, UnityEngine.Time.timeScale);

                manager.PushPause(modal);
                TimeEqual("modal pauses", 0f, UnityEngine.Time.timeScale);

                manager.ReleasePause(modal);
                TimeEqual("modal close restores slow motion", 0.3f, UnityEngine.Time.timeScale);
                TimeTrue("no pause requests remain", manager.PauseRequestCount == 0,
                    $"{manager.PauseRequestCount} still held");
            }
            finally
            {
                DestroyTimeManager(manager);
            }
        }

        /// <summary>
        /// WP-4 criterion 2: with two overlapping pausing modals, closing the first must
        /// leave the game paused.
        /// </summary>
        private static void CheckOverlappingPausesCompose()
        {
            var manager = NewTimeManager();

            try
            {
                var first = new object();
                var second = new object();

                manager.GameTimeScale = 1f;
                manager.PushPause(first);
                manager.PushPause(second);
                TimeEqual("two modals pause", 0f, UnityEngine.Time.timeScale);

                manager.ReleasePause(first);
                TimeEqual("closing the first leaves the game paused", 0f, UnityEngine.Time.timeScale);
                TimeTrue("second request survives", manager.PauseRequestCount == 1,
                    $"{manager.PauseRequestCount} held");

                manager.ReleasePause(second);
                TimeEqual("closing the second resumes", 1f, UnityEngine.Time.timeScale);
            }
            finally
            {
                DestroyTimeManager(manager);
            }
        }

        /// <summary>
        /// WP-4 criterion 3: a hit-stop that expires while a pause request is outstanding
        /// must not resume the game.
        /// </summary>
        private static void CheckHitStopDuringPause()
        {
            var manager = NewTimeManager();

            try
            {
                var modal = new object();

                manager.GameTimeScale = 0.5f;
                manager.PushPause(modal);
                manager.HitStop(0.05f);
                TimeEqual("hit-stop during pause", 0f, UnityEngine.Time.timeScale);

                AdvanceHitStop(manager, 0.06f);
                TimeTrue("hit-stop expired", !manager.IsHitStopped, "still counting down");
                TimeEqual("expiry leaves the game paused", 0f, UnityEngine.Time.timeScale);

                manager.ReleasePause(modal);
                TimeEqual("release restores the game scale", 0.5f, UnityEngine.Time.timeScale);
            }
            finally
            {
                DestroyTimeManager(manager);
            }
        }

        /// <summary>
        /// An unpaused hit-stop must restore <c>GameTimeScale</c> when it expires, never a
        /// hardcoded 1.
        /// </summary>
        private static void CheckHitStopRestoresGameTimeScale()
        {
            var manager = NewTimeManager();

            try
            {
                manager.GameTimeScale = 0.25f;
                manager.HitStop(0.05f);
                TimeEqual("hit-stop freezes", 0f, UnityEngine.Time.timeScale);

                AdvanceHitStop(manager, 0.05f);
                TimeEqual("hit-stop restores the game scale", 0.25f, UnityEngine.Time.timeScale);

                manager.HitStop(0f);
                TimeTrue("zero-length hit-stop is ignored", !manager.IsHitStopped, "a freeze was started");
                TimeEqual("zero-length hit-stop does not freeze", 0.25f, UnityEngine.Time.timeScale);
            }
            finally
            {
                DestroyTimeManager(manager);
            }
        }

        /// <summary>
        /// Pushing twice for one owner must still need only one release, and releasing an
        /// owner that holds nothing must be a no-op rather than an unpause.
        /// </summary>
        private static void CheckPushIsIdempotent()
        {
            var manager = NewTimeManager();

            try
            {
                var modal = new object();
                var stranger = new object();

                manager.GameTimeScale = 1f;
                manager.PushPause(modal);
                manager.PushPause(modal);
                TimeTrue("duplicate push is ignored", manager.PauseRequestCount == 1,
                    $"{manager.PauseRequestCount} held");

                manager.ReleasePause(stranger);
                TimeEqual("releasing a non-owner does not resume", 0f, UnityEngine.Time.timeScale);

                manager.ReleasePause(modal);
                TimeEqual("one release is enough", 1f, UnityEngine.Time.timeScale);
            }
            finally
            {
                DestroyTimeManager(manager);
            }
        }

        /// <summary>
        /// <c>Resume</c> must release only the request <c>Pause</c> took, leaving a modal's
        /// request — and therefore the pause — in place.
        /// </summary>
        private static void CheckManualPauseComposesWithModals()
        {
            var manager = NewTimeManager();

            try
            {
                var modal = new object();

                manager.GameTimeScale = 1f;
                manager.PushPause(modal);
                manager.Pause();
                TimeTrue("two owners hold requests", manager.PauseRequestCount == 2,
                    $"{manager.PauseRequestCount} held");

                manager.Resume();
                TimeEqual("Resume does not cancel the modal's pause", 0f, UnityEngine.Time.timeScale);

                manager.ReleasePause(modal);
                TimeEqual("last release resumes", 1f, UnityEngine.Time.timeScale);
            }
            finally
            {
                DestroyTimeManager(manager);
            }
        }

        /// <summary>
        /// A modal destroyed without releasing must not freeze the game forever: the next
        /// prune drops its request.
        /// </summary>
        private static void CheckDestroyedOwnerIsPruned()
        {
            var manager = NewTimeManager();
            var owner = new GameObject("WP4_PauseOwner") { hideFlags = HideFlags.HideAndDontSave };

            try
            {
                manager.GameTimeScale = 1f;
                manager.PushPause(owner);
                TimeEqual("destroyed-owner setup pauses", 0f, UnityEngine.Time.timeScale);

                UnityEngine.Object.DestroyImmediate(owner);

                bool pruned = PrunePauseTokens(manager);
                TimeTrue("prune reports the drop", pruned, "prune returned false");
                TimeTrue("request was dropped", manager.PauseRequestCount == 0,
                    $"{manager.PauseRequestCount} held");
            }
            finally
            {
                if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
                DestroyTimeManager(manager);
            }
        }

        // ─── Harness ────────────────────────────────────────────────

        /// <summary>Create a throwaway <see cref="TimeManager"/> on a hidden GameObject.</summary>
        /// <returns>The new manager.</returns>
        private static TimeManager NewTimeManager()
        {
            var go = new GameObject("WP4_TimeManager") { hideFlags = HideFlags.HideAndDontSave };
            return go.AddComponent<TimeManager>();
        }

        /// <summary>Destroy a manager created by <see cref="NewTimeManager"/>.</summary>
        /// <param name="manager">The manager to tear down.</param>
        private static void DestroyTimeManager(TimeManager manager)
        {
            if (manager != null)
                UnityEngine.Object.DestroyImmediate(manager.gameObject);
        }

        /// <summary>
        /// Step the hit-stop countdown deterministically. <c>Update</c> feeds this the real
        /// frame delta, which an edit-mode batch run does not have.
        /// </summary>
        /// <param name="manager">The manager to step.</param>
        /// <param name="unscaledDelta">Elapsed real seconds to apply.</param>
        private static void AdvanceHitStop(TimeManager manager, float unscaledDelta)
        {
            InvokePrivate(manager, "AdvanceHitStop", unscaledDelta);
        }

        /// <summary>Run the destroyed-owner sweep that <c>Update</c> performs each frame.</summary>
        /// <param name="manager">The manager to sweep.</param>
        /// <returns>True if at least one request was dropped.</returns>
        private static bool PrunePauseTokens(TimeManager manager)
        {
            return (bool)InvokePrivate(manager, "PrunePauseTokens");
        }

        /// <summary>
        /// Call a private instance method on <see cref="TimeManager"/>. Used only to step the
        /// per-frame work that edit mode never runs; every assertion above goes through the
        /// public API.
        /// </summary>
        /// <param name="manager">The target instance.</param>
        /// <param name="name">The method name.</param>
        /// <param name="args">Arguments to pass.</param>
        /// <returns>The method's return value, or null for a void method.</returns>
        private static object InvokePrivate(TimeManager manager, string name, params object[] args)
        {
            var method = typeof(TimeManager).GetMethod(
                name, BindingFlags.Instance | BindingFlags.NonPublic);

            if (method == null)
                throw new MissingMethodException($"TimeManager.{name} not found — was it renamed?");

            return method.Invoke(manager, args);
        }

        /// <summary>Record a failure unless two time scales match within float tolerance.</summary>
        /// <param name="label">Short name of the check.</param>
        /// <param name="expected">The expected time scale.</param>
        /// <param name="actual">The observed time scale.</param>
        private static void TimeEqual(string label, float expected, float actual)
        {
            if (Mathf.Abs(expected - actual) < 0.0001f) return;

            _timeFailures.Add($"{label}: expected timeScale {expected}, got {actual}");
        }

        /// <summary>Record a failure unless <paramref name="condition"/> holds.</summary>
        /// <param name="label">Short name of the check.</param>
        /// <param name="condition">The condition that must be true.</param>
        /// <param name="detail">What to report when it is not.</param>
        private static void TimeTrue(string label, bool condition, string detail)
        {
            if (condition) return;

            _timeFailures.Add($"{label}: {detail}");
        }
    }
}
