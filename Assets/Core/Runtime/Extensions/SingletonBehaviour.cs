using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// Generic singleton base class for MonoBehaviours that persist across scenes.
    /// Subclass and access via <c>Instance</c>. Automatically creates itself if
    /// accessed before being placed in a scene.
    ///
    /// Usage:
    /// <code>
    /// public class GameManager : SingletonBehaviour&lt;GameManager&gt; { }
    /// // Then: GameManager.Instance.DoThing();
    /// </code>
    /// </summary>
    /// <typeparam name="T">The concrete MonoBehaviour subclass.</typeparam>
    public abstract class SingletonBehaviour<T> : MonoBehaviour where T : MonoBehaviour
    {
        /// <summary>Cached singleton instance.</summary>
        private static T _instance;

        /// <summary>Thread-safety lock for instance creation.</summary>
        private static readonly object _lock = new();

        /// <summary>Guard flag to prevent access after application quit (avoids ghost objects).</summary>
        private static bool _applicationQuitting;

        /// <summary>
        /// Access the singleton instance. If none exists, one is found in the scene
        /// or auto-created on a new DontDestroyOnLoad GameObject.
        /// Returns null if the application is quitting.
        /// </summary>
        public static T Instance
        {
            get
            {
                // Prevent creating new instances during shutdown
                if (_applicationQuitting)
                {
                    GameLogger.LogWarning("Singleton",
                        $"Instance of {typeof(T).Name} requested after application quit.");
                    return null;
                }

                lock (_lock)
                {
                    if (_instance == null)
                    {
                        // Try to find an existing instance in the scene
                        _instance = FindAnyObjectByType<T>();

                        // Auto-create if none exists
                        if (_instance == null)
                        {
                            var go = new GameObject($"[{typeof(T).Name}]");
                            _instance = go.AddComponent<T>();
                            DontDestroyOnLoad(go);
                        }
                    }

                    return _instance;
                }
            }
        }

        /// <summary>
        /// Unity Awake — enforce singleton. If another instance already exists,
        /// this duplicate destroys itself. Otherwise, mark as DontDestroyOnLoad.
        /// </summary>
        protected virtual void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this as T;
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>
        /// Unity OnApplicationQuit — set the quitting flag to prevent
        /// late-access from creating ghost objects.
        /// </summary>
        protected virtual void OnApplicationQuit()
        {
            _applicationQuitting = true;
        }

        /// <summary>
        /// Unity OnDestroy — clear the instance reference if this was the singleton.
        /// </summary>
        protected virtual void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
