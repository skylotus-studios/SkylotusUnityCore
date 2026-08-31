using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Skylotus.Tests.PlayMode
{
    /// <summary>
    /// PlayMode coverage for <see cref="TimeManager"/> (WP-4): named timers and cooldowns, the
    /// composing pause-token stack, hit-stop, and the precedence rule that makes
    /// <c>TimeManager</c> the sole writer of <c>Time.timeScale</c>.
    ///
    /// The pause and hit-stop cases are the WP-4 batchmode self-check ported to NUnit; the timer
    /// and cooldown cases are new, and need play mode because they depend on frames actually
    /// elapsing.
    ///
    /// <b>Isolation.</b> WP-5's editor auto-bootstrap has already registered a real
    /// <see cref="TimeManager"/> by the time these run. Each case builds its own on a throwaway
    /// GameObject instead of touching that one — but <c>Time.timeScale</c> is global, so the
    /// fixture captures and restores it, and every case releases what it took.
    /// </summary>
    [TestFixture]
    public class TimeManagerTests
    {
        /// <summary>Tolerance for time-scale comparisons.</summary>
        private const float Tolerance = 0.0001f;

        /// <summary>The manager under test.</summary>
        private TimeManager _time;

        /// <summary>GameObject hosting <see cref="_time"/>.</summary>
        private GameObject _host;

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

        /// <summary>Build a fresh manager for each case.</summary>
        [SetUp]
        public void SetUp()
        {
            GameLogger.SetCategoryLevel("Time", LogLevel.Off);

            // Start every case from a known global scale: the timer cases tick on Time.deltaTime
            // and would never fire if a previous case left the game frozen.
            Time.timeScale = 1f;

            _host = new GameObject("TimeManagerTests") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(_host);
            _time = _host.AddComponent<TimeManager>();
        }

        /// <summary>Release anything the case took and destroy the manager.</summary>
        [TearDown]
        public void TearDown()
        {
            if (_time != null) _time.ReleaseAllPauses();
            if (_host != null) Object.DestroyImmediate(_host);

            Time.timeScale = 1f;
            GameLogger.SetCategoryLevel("Time", LogLevel.Debug);
        }

        // ─── Timers ─────────────────────────────────────────────────

        /// <summary>A named timer fires its completion callback once, then removes itself.</summary>
        [UnityTest]
        public IEnumerator CreateTimer_FiresOnceThenRemovesItself()
        {
            int fired = 0;
            _time.CreateTimer("probe", 0.1f, () => fired++);

            Assert.IsTrue(_time.IsTimerActive("probe"));

            yield return new WaitForSeconds(0.35f);

            Assert.AreEqual(1, fired, "A non-looping timer fires exactly once.");
            Assert.IsFalse(_time.IsTimerActive("probe"), "It should remove itself after firing.");
        }

        /// <summary>A looping timer keeps firing and stays registered.</summary>
        [UnityTest]
        public IEnumerator CreateTimer_Looping_FiresRepeatedly()
        {
            int fired = 0;
            _time.CreateTimer("loop", 0.05f, () => fired++, loop: true);

            yield return new WaitForSeconds(0.4f);

            Assert.Greater(fired, 2, $"A looping timer should have fired several times, got {fired}.");
            Assert.IsTrue(_time.IsTimerActive("loop"), "A looping timer stays registered.");

            _time.CancelTimer("loop");
        }

        /// <summary>Cancelling a timer stops it before it can fire.</summary>
        [UnityTest]
        public IEnumerator CancelTimer_PreventsTheCallback()
        {
            int fired = 0;
            _time.CreateTimer("doomed", 0.1f, () => fired++);
            _time.CancelTimer("doomed");

            yield return new WaitForSeconds(0.3f);

            Assert.AreEqual(0, fired);
            Assert.IsFalse(_time.IsTimerActive("doomed"));
        }

        /// <summary>A paused timer holds its progress and resumes from it.</summary>
        [UnityTest]
        public IEnumerator PauseTimer_HoldsProgressUntilResumed()
        {
            int fired = 0;
            _time.CreateTimer("held", 0.2f, () => fired++);
            _time.PauseTimer("held");

            yield return new WaitForSeconds(0.5f);

            Assert.AreEqual(0, fired, "A paused timer must not fire.");
            Assert.IsTrue(_time.IsTimerActive("held"));

            _time.ResumeTimer("held");
            yield return new WaitForSeconds(0.45f);

            Assert.AreEqual(1, fired, "Resuming should let it complete.");
        }

        /// <summary>The tick callback reports normalized progress and ends at 1.</summary>
        [UnityTest]
        public IEnumerator CreateTimer_TickCallbackReportsNormalizedProgress()
        {
            float lastProgress = -1f;
            float maxProgress = -1f;
            bool sawIntermediate = false;

            _time.CreateTimer("ticking", 0.25f, null, onTick: p =>
            {
                lastProgress = p;
                maxProgress = Mathf.Max(maxProgress, p);
                if (p > 0.01f && p < 0.99f) sawIntermediate = true;
            });

            yield return new WaitForSeconds(0.5f);

            Assert.IsTrue(sawIntermediate, "Progress should have been reported mid-flight, not only at the end.");
            Assert.AreEqual(1f, maxProgress, 0.001f, "Progress should reach 1.");
            Assert.AreEqual(1f, lastProgress, 0.001f);
        }

        /// <summary><c>GetTimerRemaining</c> counts down and reports 0 for an unknown timer.</summary>
        [UnityTest]
        public IEnumerator GetTimerRemaining_CountsDown()
        {
            _time.CreateTimer("counting", 1f, null);

            Assert.AreEqual(0f, _time.GetTimerRemaining("no-such-timer"), Tolerance);

            float first = _time.GetTimerRemaining("counting");
            yield return new WaitForSeconds(0.2f);
            float second = _time.GetTimerRemaining("counting");

            Assert.Less(second, first, "Remaining time should decrease as frames elapse.");
            Assert.Greater(second, 0f, "It should not have expired yet.");

            _time.CancelTimer("counting");
        }

        // ─── Cooldowns ──────────────────────────────────────────────

        /// <summary>A cooldown blocks a restart while it runs, and clears when it expires.</summary>
        [UnityTest]
        public IEnumerator StartCooldown_BlocksRestartsUntilItExpires()
        {
            Assert.IsTrue(_time.StartCooldown("dash", 0.2f), "The first start should succeed.");
            Assert.IsFalse(_time.StartCooldown("dash", 0.2f), "A second start while running must be refused.");
            Assert.IsTrue(_time.IsOnCooldown("dash"));
            Assert.Greater(_time.GetCooldownRemaining("dash"), 0f);

            yield return new WaitForSeconds(0.45f);

            Assert.IsFalse(_time.IsOnCooldown("dash"));
            Assert.AreEqual(0f, _time.GetCooldownRemaining("dash"), Tolerance);
            Assert.IsTrue(_time.StartCooldown("dash", 0.05f), "It should be startable again once expired.");
        }

        /// <summary><c>ResetCooldown</c> makes the ability immediately available again.</summary>
        [Test]
        public void ResetCooldown_ClearsItImmediately()
        {
            _time.StartCooldown("fireball", 10f);
            Assert.IsTrue(_time.IsOnCooldown("fireball"));

            _time.ResetCooldown("fireball");

            Assert.IsFalse(_time.IsOnCooldown("fireball"));
            Assert.IsTrue(_time.StartCooldown("fireball", 0.05f));
        }

        /// <summary>An unscaled cooldown keeps ticking while the game is paused.</summary>
        [UnityTest]
        public IEnumerator StartCooldown_Unscaled_TicksWhilePaused()
        {
            var owner = new object();

            _time.StartCooldown("scaled", 0.25f);
            _time.StartCooldown("real", 0.25f, useUnscaledTime: true);
            _time.PushPause(owner);

            Assert.AreEqual(0f, Time.timeScale, Tolerance, "Setup: the game should be paused.");

            yield return new WaitForSecondsRealtime(0.5f);

            Assert.IsFalse(_time.IsOnCooldown("real"), "An unscaled cooldown ignores the pause.");
            Assert.IsTrue(_time.IsOnCooldown("scaled"), "A scaled cooldown is frozen by the pause.");

            _time.ReleasePause(owner);
        }

        // ─── Pause tokens (WP-4) ────────────────────────────────────

        /// <summary>
        /// WP-4 criterion 1: a pause taken during <c>SlowMotion(0.3f, ...)</c> must return the
        /// game to 0.3 when released, not to a hardcoded 1.
        /// </summary>
        [Test]
        public void ReleasePause_RestoresGameTimeScaleNotOne()
        {
            var owner = new object();

            _time.GameTimeScale = 0.3f;
            Assert.AreEqual(0.3f, Time.timeScale, Tolerance, "Slow motion should be applied.");

            _time.PushPause(owner);
            Assert.AreEqual(0f, Time.timeScale, Tolerance);

            _time.ReleasePause(owner);

            Assert.AreEqual(0.3f, Time.timeScale, Tolerance,
                "Releasing the pause must return to the game time scale, not 1.");
            Assert.AreEqual(0, _time.PauseRequestCount);
        }

        /// <summary>WP-4 criterion 2: with two overlapping pauses, releasing the first leaves the game paused.</summary>
        [Test]
        public void PushPause_Overlapping_ComposesRatherThanOverwrites()
        {
            var first = new object();
            var second = new object();

            _time.GameTimeScale = 1f;
            _time.PushPause(first);
            _time.PushPause(second);
            Assert.AreEqual(0f, Time.timeScale, Tolerance);

            _time.ReleasePause(first);

            Assert.AreEqual(0f, Time.timeScale, Tolerance, "The second request still holds the pause.");
            Assert.AreEqual(1, _time.PauseRequestCount);

            _time.ReleasePause(second);
            Assert.AreEqual(1f, Time.timeScale, Tolerance);
        }

        /// <summary>Pushing twice for one owner still needs only one release.</summary>
        [Test]
        public void PushPause_IsIdempotentPerOwner()
        {
            var owner = new object();
            var stranger = new object();

            _time.GameTimeScale = 1f;
            _time.PushPause(owner);
            _time.PushPause(owner);

            Assert.AreEqual(1, _time.PauseRequestCount, "A duplicate push should be ignored.");
            Assert.IsTrue(_time.HasPauseRequest(owner));

            _time.ReleasePause(stranger);
            Assert.AreEqual(0f, Time.timeScale, Tolerance, "Releasing a non-owner must not resume.");

            _time.ReleasePause(owner);
            Assert.AreEqual(1f, Time.timeScale, Tolerance, "One release is enough.");
        }

        /// <summary><c>Resume</c> releases only the request <c>Pause</c> took, leaving other owners alone.</summary>
        [Test]
        public void Resume_DoesNotCancelAnotherOwnersPause()
        {
            var modal = new object();

            _time.GameTimeScale = 1f;
            _time.PushPause(modal);
            _time.Pause();

            Assert.AreEqual(2, _time.PauseRequestCount);

            _time.Resume();
            Assert.AreEqual(0f, Time.timeScale, Tolerance, "The modal's request survives Resume.");

            _time.ReleasePause(modal);
            Assert.AreEqual(1f, Time.timeScale, Tolerance);
        }

        /// <summary>A pause request whose owner was destroyed is dropped on the next frame's sweep.</summary>
        [UnityTest]
        public IEnumerator PushPause_DestroyedOwner_IsPrunedAutomatically()
        {
            var owner = new GameObject("PauseOwner") { hideFlags = HideFlags.HideAndDontSave };

            _time.GameTimeScale = 1f;
            _time.PushPause(owner);
            Assert.AreEqual(0f, Time.timeScale, Tolerance, "Setup: the game should be paused.");

            Object.DestroyImmediate(owner);

            // TimeManager.Update sweeps dead owners; give it a frame.
            yield return null;
            yield return null;

            Assert.AreEqual(0, _time.PauseRequestCount,
                "A destroyed owner's request must be dropped so the game cannot freeze forever.");
            Assert.AreEqual(1f, Time.timeScale, Tolerance);
        }

        /// <summary><c>ReleaseAllPauses</c> drops every outstanding request at once.</summary>
        [Test]
        public void ReleaseAllPauses_DropsEveryRequest()
        {
            _time.GameTimeScale = 1f;
            _time.PushPause(new object());
            _time.PushPause(new object());
            _time.PushPause(new object());

            _time.ReleaseAllPauses();

            Assert.AreEqual(0, _time.PauseRequestCount);
            Assert.AreEqual(1f, Time.timeScale, Tolerance);
        }

        // ─── Hit-stop ───────────────────────────────────────────────

        /// <summary>
        /// WP-4 criterion 3: a hit-stop that expires while a pause is outstanding must not resume
        /// the game. The countdown is stepped directly so the result does not depend on a frame
        /// rate; every assertion is against the public API.
        /// </summary>
        [Test]
        public void HitStop_ExpiringDuringAPause_LeavesTheGamePaused()
        {
            var modal = new object();

            _time.GameTimeScale = 0.5f;
            _time.PushPause(modal);
            _time.HitStop(0.05f);
            Assert.AreEqual(0f, Time.timeScale, Tolerance);

            AdvanceHitStop(0.06f);

            Assert.IsFalse(_time.IsHitStopped, "The hit-stop should have expired.");
            Assert.AreEqual(0f, Time.timeScale, Tolerance, "The pause must survive the hit-stop.");

            _time.ReleasePause(modal);
            Assert.AreEqual(0.5f, Time.timeScale, Tolerance);
        }

        /// <summary>An unpaused hit-stop restores <c>GameTimeScale</c>, never a hardcoded 1.</summary>
        [Test]
        public void HitStop_Expiring_RestoresGameTimeScale()
        {
            _time.GameTimeScale = 0.25f;
            _time.HitStop(0.05f);
            Assert.AreEqual(0f, Time.timeScale, Tolerance);

            AdvanceHitStop(0.05f);

            Assert.AreEqual(0.25f, Time.timeScale, Tolerance);
        }

        /// <summary>A zero-length hit-stop is ignored rather than freezing the game.</summary>
        [Test]
        public void HitStop_ZeroDuration_IsIgnored()
        {
            _time.GameTimeScale = 0.25f;
            _time.HitStop(0f);

            Assert.IsFalse(_time.IsHitStopped);
            Assert.AreEqual(0.25f, Time.timeScale, Tolerance);
        }

        /// <summary>Overlapping hit-stops extend the freeze rather than truncating it.</summary>
        [Test]
        public void HitStop_Overlapping_TakesTheLongerDuration()
        {
            _time.GameTimeScale = 1f;
            _time.HitStop(0.2f);
            _time.HitStop(0.05f);

            AdvanceHitStop(0.1f);
            Assert.IsTrue(_time.IsHitStopped, "The longer freeze must still be running.");

            AdvanceHitStop(0.15f);
            Assert.IsFalse(_time.IsHitStopped);
            Assert.AreEqual(1f, Time.timeScale, Tolerance);
        }

        /// <summary><c>EffectiveTimeScale</c> ranks hit-stop above pauses above the game scale.</summary>
        [Test]
        public void EffectiveTimeScale_FollowsTheDocumentedPrecedence()
        {
            var owner = new object();

            _time.GameTimeScale = 0.4f;
            Assert.AreEqual(0.4f, _time.EffectiveTimeScale, Tolerance);

            _time.PushPause(owner);
            Assert.AreEqual(0f, _time.EffectiveTimeScale, Tolerance);

            _time.HitStop(0.05f);
            Assert.AreEqual(0f, _time.EffectiveTimeScale, Tolerance);

            AdvanceHitStop(0.05f);
            Assert.AreEqual(0f, _time.EffectiveTimeScale, Tolerance, "Still paused.");

            _time.ReleasePause(owner);
            Assert.AreEqual(0.4f, _time.EffectiveTimeScale, Tolerance);
        }

        /// <summary><c>SlowMotion</c> applies the scale and restores 1 when its timer completes.</summary>
        [UnityTest]
        public IEnumerator SlowMotion_RestoresNormalSpeedWhenItExpires()
        {
            _time.SlowMotion(0.3f, 0.2f);

            Assert.AreEqual(0.3f, Time.timeScale, Tolerance);

            yield return new WaitForSecondsRealtime(0.5f);

            Assert.AreEqual(1f, _time.GameTimeScale, Tolerance);
            Assert.AreEqual(1f, Time.timeScale, Tolerance);
        }

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>
        /// Step the hit-stop countdown deterministically. <c>Update</c> feeds this the real frame
        /// delta, which would make the assertions above depend on frame timing.
        /// </summary>
        /// <param name="unscaledDelta">Elapsed real seconds to apply.</param>
        private void AdvanceHitStop(float unscaledDelta)
        {
            var method = typeof(TimeManager).GetMethod(
                "AdvanceHitStop", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(method, "TimeManager.AdvanceHitStop not found — was it renamed?");
            method.Invoke(_time, new object[] { unscaledDelta });
        }
    }
}
