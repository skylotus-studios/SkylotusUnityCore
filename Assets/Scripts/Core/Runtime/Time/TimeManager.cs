using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// Centralized time management providing named timers, cooldowns, hit-stop,
    /// slow motion, and a game-level time scale multiplier.
    ///
    /// Access via ServiceLocator:
    /// <code>
    /// var time = ServiceLocator.Get&lt;TimeManager&gt;();
    /// time.CreateTimer("bomb", 5f, () => Explode());
    /// time.HitStop(0.05f);
    /// </code>
    /// </summary>
    public class TimeManager : MonoBehaviour
    {
        /// <summary>Internal representation of a named timer.</summary>
        private class Timer
        {
            public string Id;
            public float Duration;
            public float Elapsed;
            public bool Loop;
            public bool UseUnscaledTime;
            public bool IsPaused;
            public Action OnComplete;
            public Action<float> OnTick;
        }

        /// <summary>Internal representation of a named cooldown.</summary>
        private class Cooldown
        {
            public float Remaining;
            public bool UseUnscaledTime;
        }

        /// <summary>All active timers keyed by their string ID.</summary>
        private readonly Dictionary<string, Timer> _timers = new();

        /// <summary>All active cooldowns keyed by their string ID.</summary>
        private readonly Dictionary<string, Cooldown> _cooldowns = new();

        /// <summary>Reusable list of IDs to remove after iteration (avoids modifying during foreach).</summary>
        private readonly List<string> _toRemove = new();

        /// <summary>Game-level time scale multiplier applied on top of Unity's Time.timeScale.</summary>
        private float _gameTimeScale = 1f;

        /// <summary>Remaining duration of the current hit-stop freeze (0 = not active).</summary>
        private float _hitstopTimer;

        /// <summary>
        /// Outstanding pause requests, one entry per owner. The game is paused while this
        /// list is non-empty; see <see cref="PushPause"/>.
        /// </summary>
        private readonly List<object> _pauseTokens = new();

        /// <summary>Owner token used by the parameterless <see cref="Pause"/>/<see cref="Resume"/> pair.</summary>
        private readonly object _manualPauseOwner = new();

        /// <summary>
        /// Game-level time scale (0–∞). Multiplied into Unity's Time.timeScale.
        /// Set to 0.3f for slow motion, 1f for normal, 2f for fast-forward.
        /// </summary>
        public float GameTimeScale
        {
            get => _gameTimeScale;
            set
            {
                _gameTimeScale = Mathf.Max(0f, value);
                ApplyTimeScale();
            }
        }

        /// <summary>
        /// The time scale this manager wants Unity to run at, given the current state.
        /// Precedence is hit-stop, then pause requests, then <see cref="GameTimeScale"/> — so a
        /// hit-stop expiring during a pause leaves the game paused, and releasing the last pause
        /// returns to the game time scale rather than a hardcoded 1.
        /// </summary>
        public float EffectiveTimeScale
        {
            get
            {
                if (_hitstopTimer > 0f) return 0f;
                if (_pauseTokens.Count > 0) return 0f;
                return _gameTimeScale;
            }
        }

        /// <summary>True while at least one owner holds a pause request.</summary>
        public bool IsPaused => _pauseTokens.Count > 0;

        /// <summary>Number of distinct owners currently holding a pause request.</summary>
        public int PauseRequestCount => _pauseTokens.Count;

        /// <summary>True while a hit-stop freeze is still counting down.</summary>
        public bool IsHitStopped => _hitstopTimer > 0f;

        /// <summary>Shortcut for Time.unscaledTime.</summary>
        public float UnscaledTime => Time.unscaledTime;

        /// <summary>Shortcut for Time.deltaTime.</summary>
        public float DeltaTime => Time.deltaTime;

        // ─── Timers ─────────────────────────────────────────────────

        /// <summary>
        /// Create a named timer that fires a callback after <paramref name="duration"/> seconds.
        /// </summary>
        /// <param name="id">Unique string identifier for this timer.</param>
        /// <param name="duration">Time in seconds until the timer fires.</param>
        /// <param name="onComplete">Callback invoked when the timer finishes.</param>
        /// <param name="loop">If true, the timer resets and repeats indefinitely.</param>
        /// <param name="useUnscaledTime">If true, the timer ticks with real time (ignores pause).</param>
        /// <param name="onTick">Optional callback invoked each frame with normalized progress (0–1).</param>
        public void CreateTimer(string id, float duration, Action onComplete,
            bool loop = false, bool useUnscaledTime = false, Action<float> onTick = null)
        {
            _timers[id] = new Timer
            {
                Id = id,
                Duration = duration,
                Elapsed = 0f,
                Loop = loop,
                UseUnscaledTime = useUnscaledTime,
                OnComplete = onComplete,
                OnTick = onTick
            };
        }

        /// <summary>Cancel and remove a timer by ID.</summary>
        /// <param name="id">The timer ID to cancel.</param>
        public void CancelTimer(string id) => _timers.Remove(id);

        /// <summary>Pause a timer (it stops ticking but retains its progress).</summary>
        /// <param name="id">The timer ID to pause.</param>
        public void PauseTimer(string id)
        {
            if (_timers.TryGetValue(id, out var t)) t.IsPaused = true;
        }

        /// <summary>Resume a previously paused timer.</summary>
        /// <param name="id">The timer ID to resume.</param>
        public void ResumeTimer(string id)
        {
            if (_timers.TryGetValue(id, out var t)) t.IsPaused = false;
        }

        /// <summary>
        /// Get the remaining time on a timer in seconds.
        /// Returns 0 if the timer doesn't exist.
        /// </summary>
        /// <param name="id">The timer ID.</param>
        /// <returns>Remaining seconds, or 0.</returns>
        public float GetTimerRemaining(string id)
        {
            if (_timers.TryGetValue(id, out var t))
                return Mathf.Max(0f, t.Duration - t.Elapsed);
            return 0f;
        }

        /// <summary>Check whether a timer with the given ID is currently active.</summary>
        /// <param name="id">The timer ID.</param>
        /// <returns>True if the timer exists and is running.</returns>
        public bool IsTimerActive(string id) => _timers.ContainsKey(id);

        // ─── Cooldowns ──────────────────────────────────────────────

        /// <summary>
        /// Start a cooldown. Returns false if the cooldown is already active.
        /// </summary>
        /// <param name="id">Unique cooldown identifier (e.g. "fireball", "dash").</param>
        /// <param name="duration">Cooldown duration in seconds.</param>
        /// <param name="useUnscaledTime">If true, ticks with real time (ignores pause).</param>
        /// <returns>True if the cooldown was started; false if already on cooldown.</returns>
        public bool StartCooldown(string id, float duration, bool useUnscaledTime = false)
        {
            if (IsOnCooldown(id)) return false;

            _cooldowns[id] = new Cooldown
            {
                Remaining = duration,
                UseUnscaledTime = useUnscaledTime
            };
            return true;
        }

        /// <summary>Check if a cooldown is currently active.</summary>
        /// <param name="id">The cooldown ID.</param>
        /// <returns>True if the cooldown has remaining time.</returns>
        public bool IsOnCooldown(string id)
        {
            return _cooldowns.TryGetValue(id, out var cd) && cd.Remaining > 0f;
        }

        /// <summary>Get the remaining cooldown time in seconds.</summary>
        /// <param name="id">The cooldown ID.</param>
        /// <returns>Remaining seconds, or 0 if not on cooldown.</returns>
        public float GetCooldownRemaining(string id)
        {
            return _cooldowns.TryGetValue(id, out var cd) ? Mathf.Max(0f, cd.Remaining) : 0f;
        }

        /// <summary>Force-reset a cooldown, making the ability immediately available.</summary>
        /// <param name="id">The cooldown ID to reset.</param>
        public void ResetCooldown(string id) => _cooldowns.Remove(id);

        // ─── Time Effects ───────────────────────────────────────────

        /// <summary>
        /// Freeze time for a brief duration (hit impact / parry effect).
        /// Time resumes at the effective scale after <paramref name="duration"/> real seconds —
        /// which is still frozen if a pause request is outstanding when the freeze expires.
        /// Overlapping hit-stops extend the freeze rather than truncating it.
        /// </summary>
        /// <param name="duration">Freeze duration in real (unscaled) seconds. Values of 0 or less do nothing.</param>
        public void HitStop(float duration)
        {
            if (duration <= 0f) return;

            _hitstopTimer = Mathf.Max(_hitstopTimer, duration);
            ApplyTimeScale();
        }

        /// <summary>
        /// Enter slow motion for a duration, then automatically restore normal speed.
        /// </summary>
        /// <param name="timeScale">The slow-motion time scale (e.g. 0.3 for 30% speed).</param>
        /// <param name="duration">How long slow motion lasts in real seconds.</param>
        public void SlowMotion(float timeScale, float duration)
        {
            GameTimeScale = timeScale;
            CreateTimer("_slowmo_restore", duration, () => GameTimeScale = 1f,
                useUnscaledTime: true);
        }

        // ─── Pause Requests ─────────────────────────────────────────

        /// <summary>
        /// Request that the game be paused on behalf of <paramref name="owner"/>.
        /// Requests compose: the game stays paused until every owner has released, so two
        /// overlapping pausing modals cannot overwrite one another. The call is idempotent —
        /// pushing twice for the same owner still needs only one <see cref="ReleasePause"/>.
        /// </summary>
        /// <param name="owner">
        /// The object requesting the pause, typically the modal or system that opened it.
        /// Destroyed Unity objects are dropped automatically so a leaked request cannot freeze
        /// the game forever.
        /// </param>
        public void PushPause(object owner)
        {
            if (owner == null)
            {
                GameLogger.LogError("Time", "PushPause called with a null owner; ignoring");
                return;
            }

            if (_pauseTokens.Contains(owner)) return;

            _pauseTokens.Add(owner);
            ApplyTimeScale();
        }

        /// <summary>
        /// Release a pause request previously taken by <paramref name="owner"/>.
        /// Does nothing if that owner holds no request. When the last request is released the
        /// game returns to <see cref="GameTimeScale"/>, never a hardcoded 1.
        /// </summary>
        /// <param name="owner">The object whose pause request should be dropped.</param>
        public void ReleasePause(object owner)
        {
            if (owner == null) return;
            if (!_pauseTokens.Remove(owner)) return;

            ApplyTimeScale();
        }

        /// <summary>
        /// Drop every outstanding pause request. Intended for hard resets such as a scene
        /// transition that tears down the UI mid-modal; prefer <see cref="ReleasePause"/>.
        /// </summary>
        public void ReleaseAllPauses()
        {
            if (_pauseTokens.Count == 0) return;

            _pauseTokens.Clear();
            ApplyTimeScale();
        }

        /// <summary>Check whether <paramref name="owner"/> currently holds a pause request.</summary>
        /// <param name="owner">The owner to look up.</param>
        /// <returns>True if that owner has an outstanding request.</returns>
        public bool HasPauseRequest(object owner) => owner != null && _pauseTokens.Contains(owner);

        /// <summary>
        /// Pause the game on this manager's own behalf. Equivalent to <see cref="PushPause"/>
        /// with an internal owner; release it with <see cref="Resume"/>.
        /// </summary>
        public void Pause()
        {
            PushPause(_manualPauseOwner);
        }

        /// <summary>
        /// Release the pause taken by <see cref="Pause"/>. Requests held by other owners — an
        /// open modal, for instance — are left untouched, so the game only resumes once nothing
        /// else is asking for a pause.
        /// </summary>
        public void Resume()
        {
            ReleasePause(_manualPauseOwner);
        }

        // ─── Update Loop ────────────────────────────────────────────

        /// <summary>Unity Update — tick all timers and cooldowns, handle hit-stop expiry.</summary>
        private void Update()
        {
            // Drop pause requests whose owner was destroyed without releasing
            if (_pauseTokens.Count > 0 && PrunePauseTokens())
                ApplyTimeScale();

            // Handle hit-stop countdown (runs in unscaled time)
            if (_hitstopTimer > 0f)
            {
                AdvanceHitStop(Time.unscaledDeltaTime);
                return; // Skip timer/cooldown updates while frozen
            }

            // ── Tick Timers ─────────────────────────────────────────
            _toRemove.Clear();

            foreach (var kvp in _timers)
            {
                var timer = kvp.Value;
                if (timer.IsPaused) continue;

                float dt = timer.UseUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
                timer.Elapsed += dt;

                // Report normalized progress
                timer.OnTick?.Invoke(Mathf.Clamp01(timer.Elapsed / timer.Duration));

                if (timer.Elapsed >= timer.Duration)
                {
                    try { timer.OnComplete?.Invoke(); }
                    catch (Exception ex)
                    {
                        GameLogger.LogError("Time", $"Timer '{timer.Id}' callback error: {ex.Message}");
                    }

                    if (timer.Loop)
                        timer.Elapsed = 0f; // Reset for next cycle
                    else
                        _toRemove.Add(kvp.Key);
                }
            }

            foreach (var id in _toRemove) _timers.Remove(id);

            // ── Tick Cooldowns ──────────────────────────────────────
            _toRemove.Clear();

            foreach (var kvp in _cooldowns)
            {
                var cd = kvp.Value;
                float dt = cd.UseUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
                cd.Remaining -= dt;
                if (cd.Remaining <= 0f) _toRemove.Add(kvp.Key);
            }

            foreach (var id in _toRemove) _cooldowns.Remove(id);
        }

        /// <summary>
        /// Advance the hit-stop countdown by <paramref name="unscaledDelta"/> real seconds and
        /// restore the effective time scale once it expires.
        /// </summary>
        /// <param name="unscaledDelta">Elapsed real time in seconds.</param>
        private void AdvanceHitStop(float unscaledDelta)
        {
            _hitstopTimer -= unscaledDelta;
            if (_hitstopTimer > 0f) return;

            _hitstopTimer = 0f;
            ApplyTimeScale();
        }

        /// <summary>
        /// Remove pause requests whose owner is a destroyed Unity object — a modal torn down by
        /// a scene load, say — so the game cannot stay frozen with nothing left to release it.
        /// </summary>
        /// <returns>True if at least one request was dropped.</returns>
        private bool PrunePauseTokens()
        {
            bool removedAny = false;

            for (int i = _pauseTokens.Count - 1; i >= 0; i--)
            {
                object owner = _pauseTokens[i];
                bool isDead = owner == null ||
                              (owner is UnityEngine.Object unityOwner && unityOwner == null);

                if (!isDead) continue;

                _pauseTokens.RemoveAt(i);
                removedAny = true;
            }

            if (removedAny)
                GameLogger.LogWarning("Time", "Dropped pause request(s) whose owner was destroyed");

            return removedAny;
        }

        /// <summary>
        /// Write the single authoritative value of Unity's Time.timeScale. This is the only
        /// assignment to Time.timeScale among the core runtime systems — everything else
        /// requests a state change and lets this method resolve it.
        /// </summary>
        private void ApplyTimeScale()
        {
            Time.timeScale = EffectiveTimeScale;
        }
    }
}
