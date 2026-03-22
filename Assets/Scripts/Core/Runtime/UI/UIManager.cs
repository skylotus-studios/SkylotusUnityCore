using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// Base class for all UI screens managed by <see cref="UIManager"/>.
    /// Attach to the root GameObject of each screen and implement lifecycle callbacks.
    /// </summary>
    public abstract class UIScreen : MonoBehaviour
    {
        [SerializeField] private CanvasGroup _canvasGroup;

        [Tooltip("If true, Time.timeScale is set to 0 while this screen is open.")]
        [SerializeField] private bool _pauseGameWhenOpen;

        /// <summary>The CanvasGroup used for fade transitions. Auto-fetched if not assigned.</summary>
        public CanvasGroup CanvasGroup =>
            _canvasGroup ? _canvasGroup : (_canvasGroup = GetComponent<CanvasGroup>());

        /// <summary>Whether the game should pause while this screen is the active modal.</summary>
        public bool PauseGameWhenOpen => _pauseGameWhenOpen;

        /// <summary>Called when the screen is shown (after fade-in completes).</summary>
        public virtual void OnShow() { }

        /// <summary>Called when the screen is hidden (before being deactivated).</summary>
        public virtual void OnHide() { }

        /// <summary>Called when the screen becomes the top-most screen again (e.g. after a pop).</summary>
        public virtual void OnFocus() { }

        /// <summary>Called when the screen loses focus because another was pushed on top.</summary>
        public virtual void OnFocusLost() { }

        /// <summary>Called when back is pressed. Return false to prevent the back action.</summary>
        /// <returns>True to allow back/close, false to block it.</returns>
        public virtual bool OnBackPressed() => true;
    }

    /// <summary>
    /// UI navigation manager with a screen stack, animated fade transitions,
    /// and modal popup support. Register screens by name, then show/hide them
    /// with automatic history tracking.
    ///
    /// Usage:
    /// <code>
    /// var ui = ServiceLocator.Get&lt;UIManager&gt;();
    /// ui.RegisterScreen("MainMenu", mainMenuScreen);
    /// ui.ShowScreen("MainMenu");
    /// ui.ShowScreen("Settings");  // pushes MainMenu to stack
    /// ui.GoBack();                // pops back to MainMenu
    /// </code>
    /// </summary>
    public class UIManager : MonoBehaviour
    {
        [Header("Configuration")]
        [Tooltip("Duration in seconds for screen transition fades.")]
        [SerializeField] private float _transitionDuration = 0.25f;

        [Tooltip("Parent transform for regular screens (optional).")]
        [SerializeField] private Transform _screenContainer;

        [Tooltip("Parent transform for modal popups (optional, renders above screens).")]
        [SerializeField] private Transform _modalContainer;

        /// <summary>All registered screens keyed by name.</summary>
        private readonly Dictionary<string, UIScreen> _registeredScreens = new();

        /// <summary>Stack of previously shown screens for back navigation.</summary>
        private readonly Stack<UIScreen> _screenStack = new();

        /// <summary>Currently open modal popups.</summary>
        private readonly List<UIScreen> _activeModals = new();

        /// <summary>The currently visible screen (top of the navigation).</summary>
        private UIScreen _currentScreen;

        /// <summary>True while a screen transition animation is playing.</summary>
        private bool _isTransitioning;

        /// <summary>The currently visible screen.</summary>
        public UIScreen CurrentScreen => _currentScreen;

        /// <summary>True if a transition is in progress (prevents double-navigation).</summary>
        public bool IsTransitioning => _isTransitioning;

        /// <summary>Number of screens on the back stack.</summary>
        public int StackDepth => _screenStack.Count;

        /// <summary>
        /// Register a screen by name. The screen's GameObject is deactivated immediately.
        /// </summary>
        /// <param name="name">Unique name to reference this screen.</param>
        /// <param name="screen">The UIScreen component.</param>
        public void RegisterScreen(string name, UIScreen screen)
        {
            _registeredScreens[name] = screen;
            screen.gameObject.SetActive(false);
        }

        /// <summary>
        /// Show a registered screen by name. The current screen is pushed to the stack
        /// if <paramref name="pushCurrent"/> is true.
        /// </summary>
        /// <param name="name">The registered screen name.</param>
        /// <param name="pushCurrent">Whether to push the current screen for back navigation.</param>
        public void ShowScreen(string name, bool pushCurrent = true)
        {
            if (!_registeredScreens.TryGetValue(name, out var screen))
            {
                GameLogger.LogError("UI", $"Screen '{name}' not registered");
                return;
            }
            ShowScreen(screen, pushCurrent);
        }

        /// <summary>
        /// Show a screen directly by reference.
        /// </summary>
        /// <param name="screen">The UIScreen to show.</param>
        /// <param name="pushCurrent">Whether to push the current screen for back navigation.</param>
        public void ShowScreen(UIScreen screen, bool pushCurrent = true)
        {
            if (_isTransitioning || screen == _currentScreen) return;
            StartCoroutine(TransitionScreen(screen, pushCurrent));
        }

        /// <summary>
        /// Navigate back to the previous screen on the stack.
        /// Respects <see cref="UIScreen.OnBackPressed"/> — if it returns false, navigation is blocked.
        /// </summary>
        public void GoBack()
        {
            if (_isTransitioning) return;

            // Let the current screen veto the back action
            if (_currentScreen != null && !_currentScreen.OnBackPressed())
                return;

            if (_screenStack.Count > 0)
            {
                var prev = _screenStack.Pop();
                StartCoroutine(TransitionScreen(prev, false));
            }
        }

        /// <summary>
        /// Clear the entire screen stack and show a specific screen.
        /// Useful for hard navigation resets (e.g. return to main menu).
        /// </summary>
        /// <param name="name">The registered screen name to show.</param>
        public void ClearStackAndShow(string name)
        {
            // Hide all stacked screens
            while (_screenStack.Count > 0)
            {
                var s = _screenStack.Pop();
                s.OnHide();
                s.gameObject.SetActive(false);
            }

            if (_registeredScreens.TryGetValue(name, out var screen))
                ShowScreen(screen, false);
        }

        // ─── Modals ─────────────────────────────────────────────────

        /// <summary>
        /// Show a modal popup on top of the current screen.
        /// Optionally pauses the game (based on the screen's PauseGameWhenOpen flag).
        /// </summary>
        /// <param name="modal">The UIScreen to show as a modal.</param>
        public void ShowModal(UIScreen modal)
        {
            if (_activeModals.Contains(modal)) return;

            modal.gameObject.SetActive(true);

            if (modal.CanvasGroup != null)
            {
                modal.CanvasGroup.alpha = 0f;
                StartCoroutine(FadeCanvasGroup(modal.CanvasGroup, 0f, 1f, _transitionDuration));
            }

            modal.OnShow();
            _activeModals.Add(modal);

            if (modal.PauseGameWhenOpen)
                Time.timeScale = 0f;
        }

        /// <summary>
        /// Close a specific modal popup.
        /// </summary>
        /// <param name="modal">The modal to close.</param>
        public void CloseModal(UIScreen modal)
        {
            if (!_activeModals.Contains(modal)) return;
            StartCoroutine(CloseModalRoutine(modal));
        }

        /// <summary>Close all open modal popups.</summary>
        public void CloseAllModals()
        {
            var modals = new List<UIScreen>(_activeModals);
            foreach (var m in modals) CloseModal(m);
        }

        // ─── Transition Coroutines ──────────────────────────────────

        /// <summary>Fade out the current screen, fade in the new screen.</summary>
        private IEnumerator TransitionScreen(UIScreen newScreen, bool pushCurrent)
        {
            _isTransitioning = true;

            // Fade out current screen
            if (_currentScreen != null)
            {
                _currentScreen.OnFocusLost();

                if (_currentScreen.CanvasGroup != null)
                    yield return FadeCanvasGroup(_currentScreen.CanvasGroup, 1f, 0f, _transitionDuration);

                if (pushCurrent)
                    _screenStack.Push(_currentScreen);
                else
                {
                    _currentScreen.OnHide();
                    _currentScreen.gameObject.SetActive(false);
                }
            }

            // Fade in new screen
            newScreen.gameObject.SetActive(true);
            if (newScreen.CanvasGroup != null)
            {
                newScreen.CanvasGroup.alpha = 0f;
                yield return FadeCanvasGroup(newScreen.CanvasGroup, 0f, 1f, _transitionDuration);
            }

            _currentScreen = newScreen;
            newScreen.OnShow();
            newScreen.OnFocus();

            _isTransitioning = false;
        }

        /// <summary>Fade out a modal, deactivate it, and restore time scale if needed.</summary>
        private IEnumerator CloseModalRoutine(UIScreen modal)
        {
            if (modal.CanvasGroup != null)
                yield return FadeCanvasGroup(modal.CanvasGroup, 1f, 0f, _transitionDuration);

            modal.OnHide();
            modal.gameObject.SetActive(false);
            _activeModals.Remove(modal);

            // Restore time only if no remaining modals request pause
            bool anyPausing = false;
            foreach (var m in _activeModals)
                if (m.PauseGameWhenOpen) { anyPausing = true; break; }

            if (!anyPausing)
                Time.timeScale = 1f;
        }

        /// <summary>Linearly fade a CanvasGroup's alpha, handling interactable/blocksRaycasts.</summary>
        private IEnumerator FadeCanvasGroup(CanvasGroup cg, float from, float to, float dur)
        {
            float t = 0f;
            cg.alpha = from;
            cg.interactable = false;
            cg.blocksRaycasts = false;

            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                cg.alpha = Mathf.Lerp(from, to, t / dur);
                yield return null;
            }

            cg.alpha = to;
            bool visible = to > 0.01f;
            cg.interactable = visible;
            cg.blocksRaycasts = visible;
        }
    }
}
