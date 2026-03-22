using LitMotion;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// Auto-bootstraps all Skylotus core systems before any scene loads.
    /// Attach to a GameObject in your boot scene. All systems are registered
    /// with <see cref="ServiceLocator"/> and persist across scenes via DontDestroyOnLoad.
    ///
    /// Systems initialized (in order):
    /// 1. GameLogger — first, so all other systems can log
    /// 2. LitMotion (no initialization required)
    /// 3. SaveSystem
    /// 4. LocalizationSystem
    /// 5. AudioManager
    /// 6. ObjectPool
    /// 7. SkylotusSceneManager
    /// 8. GameStateMachine
    /// 9. TimeManager
    /// 10. InputManager (requires InputActionAsset)
    /// 11. UIManager
    /// 12. DialogueSystem
    /// 13. NotificationSystem
    /// 14. DebugConsole (optional)
    /// </summary>
    public class SkylotusBootstrapper : MonoBehaviour
    {
        [Header("Systems Configuration")]
        [Tooltip("Enable the in-game debug console (toggle with ` key).")]
        [SerializeField] private bool _enableDebugConsole = true;

        [Tooltip("Write log output to a timestamped file in Application.persistentDataPath/Logs.")]
        [SerializeField] private bool _enableFileLogging = false;

        [Tooltip("Default language code loaded on startup.")]
        [SerializeField] private string _defaultLanguage = "en";

        [Tooltip("AES encryption key for save files. Leave empty for plaintext saves.")]
        [SerializeField] private string _saveEncryptionKey = "";

        [Header("References (Optional)")]
        [Tooltip("The project's Input Action Asset. If null, InputManager is not created.")]
        [SerializeField] private UnityEngine.InputSystem.InputActionAsset _inputActions;

        /// <summary>Ensures only one bootstrapper runs across scene reloads.</summary>
        private static bool _initialized;

        /// <summary>
        /// Reset static state on domain reload (Editor Enter Play Mode settings).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _initialized = false;
        }

        /// <summary>
        /// Unity Awake — enforce singleton, mark as persistent, and initialize all systems.
        /// </summary>
        private void Awake()
        {
            // Prevent duplicate initialization on scene reload
            if (_initialized)
            {
                Destroy(gameObject);
                return;
            }

            _initialized = true;
            DontDestroyOnLoad(gameObject);
            gameObject.name = "[Skylotus]";

            InitializeSystems();
        }

        /// <summary>
        /// Create and register all core systems in dependency order.
        /// </summary>
        private void InitializeSystems()
        {
            // ─── Logger (first, so other systems can log) ───────────
            GameLogger.Initialize(_enableFileLogging, includeStackTrace: true);
            GameLogger.Log("Core", "=== Skylotus Core Systems Bootstrapping ===");

            // ─── LitMotion ─────────────────────────────────────────
            // LitMotion requires no manual initialization — it's ready to use.

            // ─── Save System ────────────────────────────────────────
            var saveSystem = new SaveSystem(
                string.IsNullOrEmpty(_saveEncryptionKey) ? null : _saveEncryptionKey);
            ServiceLocator.Register(saveSystem);

            // ─── Localization ───────────────────────────────────────
            var localization = new LocalizationSystem();
            localization.LoadLanguage(_defaultLanguage);
            localization.SetLanguage(_defaultLanguage);
            ServiceLocator.Register(localization);

            // ─── Audio Manager ──────────────────────────────────────
            var audioGo = CreateChild("AudioManager");
            var audioManager = audioGo.AddComponent<AudioManager>();
            ServiceLocator.Register(audioManager);

            // ─── Object Pool ────────────────────────────────────────
            var poolGo = CreateChild("ObjectPool");
            var objectPool = poolGo.AddComponent<ObjectPool>();
            ServiceLocator.Register(objectPool);

            // ─── Scene Manager ──────────────────────────────────────
            var sceneGo = CreateChild("SceneManager");
            var sceneManager = sceneGo.AddComponent<SkylotusSceneManager>();
            ServiceLocator.Register(sceneManager);

            // ─── Game State ─────────────────────────────────────────
            var stateGo = CreateChild("GameState");
            var gameState = stateGo.AddComponent<GameStateMachine>();
            ServiceLocator.Register(gameState);

            // ─── Time Manager ───────────────────────────────────────
            var timeGo = CreateChild("TimeManager");
            var timeManager = timeGo.AddComponent<TimeManager>();
            ServiceLocator.Register(timeManager);

            // ─── Input Manager (only if an InputActionAsset is assigned) ─
            if (_inputActions != null)
            {
                var inputGo = CreateChild("InputManager");
                var inputManager = inputGo.AddComponent<InputManager>();

                // Inject the asset via reflection since the field is serialized/private
                var field = typeof(InputManager).GetField("_inputActions",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                field?.SetValue(inputManager, _inputActions);

                // Initialize after the asset is injected (not in Awake, which fires before injection)
                inputManager.Initialize();

                ServiceLocator.Register(inputManager);
            }

            // ─── UI Manager ────────────────────────────────────────
            var uiGo = CreateChild("UIManager");
            var uiManager = uiGo.AddComponent<UIManager>();
            ServiceLocator.Register(uiManager);

            // ─── Dialogue System ────────────────────────────────────
            var dialogueGo = CreateChild("DialogueSystem");
            var dialogueSystem = dialogueGo.AddComponent<DialogueSystem>();
            ServiceLocator.Register(dialogueSystem);

            // ─── Notification System ────────────────────────────────
            var notifGo = CreateChild("NotificationSystem");
            var notificationSystem = notifGo.AddComponent<NotificationSystem>();
            ServiceLocator.Register(notificationSystem);

            // ─── Debug Console (last, so it can reference other systems) ─
            if (_enableDebugConsole)
            {
                var consoleGo = CreateChild("DebugConsole");
                consoleGo.AddComponent<DebugConsole>();
                RegisterDebugCommands();
            }

            // ─── Event Queue Processor ──────────────────────────────
            gameObject.AddComponent<EventQueueProcessor>();

            GameLogger.Log("Core", "=== All Skylotus Core Systems Initialized ===");
        }

        /// <summary>Create a child GameObject under the bootstrapper root.</summary>
        /// <param name="name">Name for the child GameObject.</param>
        /// <returns>The created child GameObject.</returns>
        private GameObject CreateChild(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform);
            return go;
        }

        /// <summary>Register console commands that interact with core systems.</summary>
        private void RegisterDebugCommands()
        {
            DebugConsole.Register("state", "Show/set game state (state [newState])", args =>
            {
                var gsm = ServiceLocator.Get<GameStateMachine>();
                if (args.Length > 0 && System.Enum.TryParse<GameStateType>(args[0], true, out var st))
                {
                    gsm.TransitionTo(st);
                    DebugConsole.Print($"State -> {st}");
                }
                else
                {
                    DebugConsole.Print($"Current state: {gsm.CurrentState}");
                }
            });

            DebugConsole.Register("volume", "Set volume (volume <channel> <0-1>)", args =>
            {
                var audio = ServiceLocator.Get<AudioManager>();
                if (args.Length >= 2 &&
                    System.Enum.TryParse<AudioChannel>(args[0], true, out var ch) &&
                    float.TryParse(args[1], out var vol))
                {
                    audio.SetVolume(ch, vol);
                    DebugConsole.Print($"{ch} volume -> {vol}");
                }
            });

            DebugConsole.Register("lang", "Switch language (lang <code>)", args =>
            {
                if (args.Length > 0)
                {
                    var loc = ServiceLocator.Get<LocalizationSystem>();
                    loc.SetLanguage(args[0]);
                    DebugConsole.Print($"Language -> {args[0]}");
                }
            });

            DebugConsole.Register("saves", "List all save slots", _ =>
            {
                var save = ServiceLocator.Get<SaveSystem>();
                var slots = save.GetAllSlots();
                if (slots.Length == 0)
                    DebugConsole.Print("No save slots found.");
                else
                    foreach (var s in slots) DebugConsole.Print($"  {s}");
            });

            DebugConsole.Register("notify", "Show test notification (notify <msg>)", args =>
            {
                if (args.Length > 0)
                {
                    var notif = ServiceLocator.Get<NotificationSystem>();
                    notif.Notify(string.Join(" ", args));
                }
            });

            DebugConsole.Register("tween_count", "Show active LitMotion count", _ =>
            {
                DebugConsole.Print($"Active motions: {MotionDebugger.Items.Count}");
            });
        }

        /// <summary>
        /// Unity OnApplicationQuit — clean up all static state to prevent
        /// leaks between play-mode sessions in the editor.
        /// </summary>
        private void OnApplicationQuit()
        {
            EventBus.ClearAll();
            ServiceLocator.Reset();
            MotionDispatcher.Clear();
            _initialized = false;
        }
    }

    /// <summary>
    /// Internal MonoBehaviour that calls <see cref="EventBus.ProcessQueue"/>
    /// every frame to deliver deferred events.
    /// </summary>
    internal class EventQueueProcessor : MonoBehaviour
    {
        /// <summary>Unity Update — process all queued EventBus events.</summary>
        private void Update()
        {
            EventBus.ProcessQueue();
        }
    }
}