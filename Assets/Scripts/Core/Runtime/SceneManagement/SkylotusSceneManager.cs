using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Skylotus
{
    /// <summary>
    /// Async scene management with loading screens, transition callbacks,
    /// navigation history (back button support), and additive scene layering.
    ///
    /// Assign a loading screen CanvasGroup and progress bar in the inspector,
    /// or use without them for instant cuts.
    /// </summary>
    public class SkylotusSceneManager : MonoBehaviour
    {
        [Header("Loading Screen")]
        [Tooltip("CanvasGroup used for the loading screen fade overlay.")]
        [SerializeField] private CanvasGroup _loadingScreen;

        [Tooltip("Optional progress bar slider updated during async load.")]
        [SerializeField] private UnityEngine.UI.Slider _progressBar;

        [Tooltip("Duration in seconds for the loading screen fade in/out.")]
        [SerializeField] private float _fadeDuration = 0.4f;

        [Tooltip("Minimum time the loading screen is shown (prevents flicker on fast loads).")]
        [SerializeField] private float _minimumLoadTime = 0.5f;

        /// <summary>True while a scene load operation is in progress.</summary>
        private bool _isLoading;

        /// <summary>Stack of previously visited scene names for back navigation.</summary>
        private readonly Stack<string> _sceneHistory = new();

        /// <summary>Name of the currently active scene.</summary>
        private string _currentScene;

        /// <summary>Name of the currently active scene.</summary>
        public string CurrentScene => _currentScene;

        /// <summary>True if a scene load is in progress.</summary>
        public bool IsLoading => _isLoading;

        /// <summary>Fires each frame during a load with a 0–1 progress value.</summary>
        public event Action<float> OnProgress;

        /// <summary>Fires after a scene has fully loaded and activated.</summary>
        public event Action<string> OnSceneLoaded;

        /// <summary>Fires just before a scene begins unloading.</summary>
        public event Action<string> OnSceneUnloading;

        /// <summary>Unity Awake — capture the initial scene and hide the loading screen.</summary>
        private void Awake()
        {
            _currentScene = SceneManager.GetActiveScene().name;

            if (_loadingScreen != null)
            {
                _loadingScreen.alpha = 0f;
                _loadingScreen.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Load a scene in Single mode (replaces current scene).
        /// </summary>
        /// <param name="sceneName">Name of the scene to load (must be in Build Settings).</param>
        /// <param name="showLoadingScreen">Whether to show the loading screen overlay.</param>
        /// <param name="addToHistory">Whether to push the current scene onto the history stack.</param>
        public void LoadScene(string sceneName, bool showLoadingScreen = true, bool addToHistory = true)
        {
            if (_isLoading) return;
            StartCoroutine(LoadSceneRoutine(sceneName, LoadSceneMode.Single, showLoadingScreen, addToHistory));
        }

        /// <summary>
        /// Load a scene additively (e.g. UI overlays, sub-levels).
        /// </summary>
        /// <param name="sceneName">Name of the scene to add.</param>
        public void LoadSceneAdditive(string sceneName)
        {
            if (_isLoading) return;
            StartCoroutine(LoadSceneRoutine(sceneName, LoadSceneMode.Additive, false, false));
        }

        /// <summary>
        /// Unload an additively loaded scene.
        /// </summary>
        /// <param name="sceneName">Name of the scene to unload.</param>
        /// <param name="onComplete">Optional callback fired after unload finishes.</param>
        public void UnloadScene(string sceneName, Action onComplete = null)
        {
            StartCoroutine(UnloadSceneRoutine(sceneName, onComplete));
        }

        /// <summary>
        /// Navigate back to the previous scene in history. No-op if history is empty.
        /// </summary>
        public void GoBack()
        {
            if (_sceneHistory.Count > 0)
            {
                var prev = _sceneHistory.Pop();
                LoadScene(prev, true, false);
            }
            else
            {
                GameLogger.LogWarning("Scene", "No scene history to go back to");
            }
        }

        /// <summary>
        /// Reload the current scene (useful for retrying a level).
        /// </summary>
        public void ReloadCurrentScene()
        {
            LoadScene(_currentScene, true, false);
        }

        /// <summary>
        /// Core async load coroutine: fade in loading screen → async load → wait for
        /// minimum display time → activate scene → fade out loading screen.
        /// </summary>
        private IEnumerator LoadSceneRoutine(string sceneName, LoadSceneMode mode,
            bool showLoading, bool addToHistory)
        {
            _isLoading = true;

            // Push current scene to history for back navigation
            if (addToHistory && !string.IsNullOrEmpty(_currentScene))
                _sceneHistory.Push(_currentScene);

            OnSceneUnloading?.Invoke(_currentScene);

            // Fade in the loading screen overlay
            if (showLoading && _loadingScreen != null)
            {
                _loadingScreen.gameObject.SetActive(true);
                yield return FadeCanvasGroup(_loadingScreen, 0f, 1f, _fadeDuration);
            }

            float startTime = Time.unscaledTime;

            // Begin async load — hold activation until we're ready
            var op = SceneManager.LoadSceneAsync(sceneName, mode);
            op.allowSceneActivation = false;

            // Report progress (Unity caps at 0.9 until activation is allowed)
            while (op.progress < 0.9f)
            {
                float progress = Mathf.Clamp01(op.progress / 0.9f);
                OnProgress?.Invoke(progress);
                if (_progressBar != null) _progressBar.value = progress;
                yield return null;
            }

            // Enforce minimum load time so the loading screen doesn't flicker
            float elapsed = Time.unscaledTime - startTime;
            if (elapsed < _minimumLoadTime)
                yield return new WaitForSecondsRealtime(_minimumLoadTime - elapsed);

            OnProgress?.Invoke(1f);
            if (_progressBar != null) _progressBar.value = 1f;

            // Allow the scene to activate
            op.allowSceneActivation = true;
            yield return new WaitUntil(() => op.isDone);

            _currentScene = sceneName;
            _isLoading = false;

            OnSceneLoaded?.Invoke(sceneName);
            EventBus.Publish(new OnSceneLoadedEvent { SceneName = sceneName });

            // Fade out the loading screen
            if (showLoading && _loadingScreen != null)
            {
                yield return FadeCanvasGroup(_loadingScreen, 1f, 0f, _fadeDuration);
                _loadingScreen.gameObject.SetActive(false);
            }

            GameLogger.Log("Scene", $"Loaded scene: {sceneName}");
        }

        /// <summary>Async unload coroutine for additive scenes.</summary>
        private IEnumerator UnloadSceneRoutine(string sceneName, Action onComplete)
        {
            OnSceneUnloading?.Invoke(sceneName);
            var op = SceneManager.UnloadSceneAsync(sceneName);

            if (op == null)
            {
                GameLogger.LogError("Scene", $"Failed to unload scene: {sceneName}");
                yield break;
            }

            yield return op;
            onComplete?.Invoke();
            GameLogger.Log("Scene", $"Unloaded scene: {sceneName}");
        }

        /// <summary>Linearly interpolate a CanvasGroup's alpha over a duration (unscaled time).</summary>
        private IEnumerator FadeCanvasGroup(CanvasGroup cg, float from, float to, float duration)
        {
            float t = 0f;
            cg.alpha = from;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                cg.alpha = Mathf.Lerp(from, to, t / duration);
                yield return null;
            }
            cg.alpha = to;
        }
    }
}
