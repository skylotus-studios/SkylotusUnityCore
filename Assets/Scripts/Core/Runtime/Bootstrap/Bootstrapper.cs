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
    /// 5. SettingsService
    /// 6. AudioManager
    /// 7. ObjectPool
    /// 8. SkylotusSceneManager
    /// 9. GameStateMachine
    /// 10. TimeManager
    /// 11. InputManager (requires InputActionAsset)
    /// 12. UIManager
    /// 13. DialogueSystem
    /// 14. NotificationSystem
    /// 15. DebugConsole (optional; editor and development builds only)
    ///
    /// <see cref="SettingsService"/> is constructed early but only <b>applied</b> once the
    /// MonoBehaviour systems are registered — it pushes saved volumes into
    /// <see cref="AudioManager"/> — and always before <see cref="Start"/> loads the first scene,
    /// so a relaunched game comes up at the volume, quality and resolution the player chose.
    ///
    /// The MonoBehaviour-based systems (6–15) live on the <b>core systems prefab</b>
    /// assigned to <c>_coreSystemsPrefab</c>, which is instantiated once at boot. That prefab
    /// is the only place their <c>[SerializeField]</c> values can be authored: a component added
    /// at runtime with <c>AddComponent</c> gets compile-time defaults for every serialized field,
    /// permanently and unreachably. Regenerate the prefab with
    /// <c>Skylotus.Editor.SkylotusCI.GenerateCoreSystemsPrefab</c>.
    ///
    /// If no prefab is assigned the bootstrapper falls back to building the systems in code so an
    /// un-migrated boot scene still runs — with every inspector value stuck at its default, which
    /// means no loading screen, no UI containers, and no audio/notification tuning.
    ///
    /// The <see cref="DebugConsole"/> is the one system that never reaches a release player:
    /// every path that activates it, creates it, or registers commands on it is compiled behind
    /// <c>#if UNITY_EDITOR || DEVELOPMENT_BUILD</c>, matching the guard inside
    /// <see cref="DebugConsole"/> itself. In a release build the prefab's console object is
    /// deactivated unconditionally, and the code-construction fallback never adds the component.
    /// </summary>
    public class Bootstrapper : MonoBehaviour
    {
        [Header("Systems Configuration")]
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [Tooltip("Enable the in-game debug console (toggle with ` key). Editor and development " +
                 "builds only — the console is compiled out of release players, where this " +
                 "field does not exist and the setting has no effect.")]
        [SerializeField] private bool _enableDebugConsole = true;
#endif

        [Tooltip("Write log output to a timestamped file in Application.persistentDataPath/Logs.")]
        [SerializeField] private bool _enableFileLogging = false;

        [Tooltip("Default language code loaded on startup.")]
        [SerializeField] private string _defaultLanguage = "en";

        [Tooltip("AES encryption key for save files. Leave empty for plaintext saves.")]
        [SerializeField] private string _saveEncryptionKey = "";

        [Header("Scene Flow")]
        [Tooltip("Scene loaded automatically after all systems initialize. Leave empty to stay in the boot scene.")]
        [SerializeField] private string _firstScene = "MainMenu";

        [Header("Core Systems Prefab")]
        [Tooltip("Prefab carrying every MonoBehaviour core system with its inspector values " +
                 "pre-wired (loading screen, UI containers, audio and notification tuning). " +
                 "Generate or refresh it with Skylotus.Editor.SkylotusCI.GenerateCoreSystemsPrefab. " +
                 "If null, the systems are built in code with default values and the loading " +
                 "screen and UI containers will be unavailable.")]
        [SerializeField] private GameObject _coreSystemsPrefab;

        [Header("References (Optional)")]
        [Tooltip("The project's Input Action Asset. Only used by the code-construction fallback — " +
                 "when a core systems prefab is assigned, the prefab's InputManager carries the " +
                 "reference instead. If both are empty, InputManager is not registered.")]
        [SerializeField] private UnityEngine.InputSystem.InputActionAsset _inputActions;

        [Tooltip("Color palette ScriptableObject. If assigned, registered with ServiceLocator for global access.")]
        [SerializeField] private Skylotus.Core.UI.ColorPalette _colorPalette;

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
            gameObject.name = "[Skylotus Bootstrapper]";

            InitializeSystems();
        }

        /// <summary>
        /// Unity Start — load the first scene (e.g. MainMenu) after all systems are ready.
        /// Runs one frame after Awake so every MonoBehaviour's Awake has completed.
        /// </summary>
        private void Start()
        {
            if (!string.IsNullOrEmpty(_firstScene))
            {
                var sceneManager = ServiceLocator.Get<SkylotusSceneManager>();
                if (sceneManager != null)
                {
                    GameLogger.Log("Core", $"Loading first scene: {_firstScene}");
                    sceneManager.LoadScene(_firstScene, showLoadingScreen: false, addToHistory: false);
                }
            }
        }

        /// <summary>
        /// Create and register all core systems in dependency order.
        ///
        /// The pure-C# systems (SaveSystem, LocalizationSystem, SettingsService) are constructed
        /// here because they have no serialized state. Everything MonoBehaviour-shaped comes from
        /// <c>_coreSystemsPrefab</c> when one is assigned, and from
        /// <see cref="RegisterSystemsFromCode"/> when it is not.
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

            // ─── Settings ───────────────────────────────────────────
            // Registered now so anything below can read a saved value; applied after the
            // MonoBehaviour systems exist, because ApplyAll writes into AudioManager.
            var settings = new SettingsService();
            ServiceLocator.Register(settings);

            // ─── MonoBehaviour systems (audio → console) ────────────
            if (_coreSystemsPrefab != null)
                RegisterSystemsFromPrefab();
            else
                RegisterSystemsFromCode();

            // ─── Saved Settings ─────────────────────────────────────
            // Both registration paths have AudioManager up by here, and Start() has not yet run,
            // so the first scene renders with the player's saved settings already in effect.
            settings.ApplyAll();

            // ─── Event Queue Processor ──────────────────────────────
            gameObject.AddComponent<EventQueueProcessor>();

            GameLogger.Log("Core", "=== All Skylotus Core Systems Initialized ===");
        }

        /// <summary>
        /// Instantiate the core systems prefab and register each system it carries.
        ///
        /// The prefab is instantiated beneath a dormant (inactive) holder so that no component's
        /// <c>Awake</c> runs until every boot-time decision has been applied — Unity defers
        /// <c>Awake</c> for objects that are not active in the hierarchy. Re-parenting the
        /// instance to the scene root activates it and fires every <c>Awake</c> in one pass.
        ///
        /// Registration order matches <see cref="InitializeSystems"/>'s documented sequence:
        /// audio → pool → scene → state → time → input → palette → UI → dialogue →
        /// notification → console.
        /// </summary>
        private void RegisterSystemsFromPrefab()
        {
            var staging = new GameObject("[Skylotus Core Staging]");
            staging.SetActive(false);

            var root = Instantiate(_coreSystemsPrefab, staging.transform);
            root.name = "[Skylotus Core]";

            // The console is opt-in, and only exists at all in the editor and development
            // builds. Deactivating it while the hierarchy is still dormant means its Awake never
            // runs, so it never claims the static singleton slot. A release player takes the
            // second branch: the prefab still carries the component (the type is not stripped,
            // only its behaviour is), so the object is deactivated unconditionally.
            var console = root.GetComponentInChildren<DebugConsole>(includeInactive: true);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (console != null && !_enableDebugConsole)
                console.gameObject.SetActive(false);
#else
            if (console != null)
                console.gameObject.SetActive(false);
#endif

            // Leaving the dormant holder activates the hierarchy and runs every Awake.
            root.transform.SetParent(null, worldPositionStays: false);
            Destroy(staging);
            DontDestroyOnLoad(root);

            // ─── Audio Manager ──────────────────────────────────────
            RegisterFromPrefab<AudioManager>(root);

            // ─── Object Pool ────────────────────────────────────────
            RegisterFromPrefab<ObjectPool>(root);

            // ─── Scene Manager ──────────────────────────────────────
            RegisterFromPrefab<SkylotusSceneManager>(root);

            // ─── Game State ─────────────────────────────────────────
            RegisterFromPrefab<GameStateMachine>(root);

            // ─── Time Manager ───────────────────────────────────────
            RegisterFromPrefab<TimeManager>(root);

            // ─── Input Manager (only if the prefab carries an InputActionAsset) ─
            var inputManager = root.GetComponentInChildren<InputManager>(includeInactive: true);
            if (inputManager == null)
            {
                GameLogger.LogWarning("Core",
                    "Core systems prefab has no InputManager — input will be unavailable.");
            }
            else if (inputManager.Actions == null)
            {
                GameLogger.LogError("Core",
                    "The core systems prefab's InputManager has no InputActionAsset assigned. " +
                    "Assign one on the prefab (not on the Bootstrapper) and re-enter play mode.");
            }
            else
            {
                // Initialize explicitly — InputManager deliberately does no work in Awake so the
                // bootstrapper controls when the asset is cloned and rebinds are loaded.
                inputManager.Initialize();
                ServiceLocator.Register(inputManager);
            }

            // ─── Color Palette ──────────────────────────────────────
            if (_colorPalette != null)
                ServiceLocator.Register(_colorPalette);

            // ─── UI Manager ────────────────────────────────────────
            RegisterFromPrefab<UIManager>(root);

            // ─── Dialogue System ────────────────────────────────────
            RegisterFromPrefab<DialogueSystem>(root);

            // ─── Notification System ────────────────────────────────
            RegisterFromPrefab<NotificationSystem>(root);

            // ─── Debug Console (last, so it can reference other systems) ─
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_enableDebugConsole)
            {
                if (console == null)
                {
                    GameLogger.LogWarning("Core",
                        "Debug console is enabled but the core systems prefab has no DebugConsole.");
                }

                RegisterDebugCommands();
            }
#endif
        }

        /// <summary>
        /// Find a system component on the instantiated core systems prefab and register it.
        /// Logs an error rather than throwing when the prefab is missing the component, so a
        /// stale prefab degrades one system instead of aborting the whole boot.
        /// </summary>
        /// <typeparam name="T">The system component type to locate and register.</typeparam>
        /// <param name="root">Root of the instantiated core systems prefab.</param>
        /// <returns>The registered component, or null if the prefab does not carry one.</returns>
        private T RegisterFromPrefab<T>(GameObject root) where T : MonoBehaviour
        {
            var system = root.GetComponentInChildren<T>(includeInactive: true);

            if (system == null)
            {
                GameLogger.LogError("Core",
                    $"Core systems prefab has no {typeof(T).Name} component. " +
                    "Regenerate it with SkylotusCI.GenerateCoreSystemsPrefab.");
                return null;
            }

            ServiceLocator.Register(system);
            return system;
        }

        /// <summary>
        /// Legacy fallback used when no core systems prefab is assigned: build every system as a
        /// child GameObject with <c>AddComponent</c>.
        ///
        /// Every <c>[SerializeField]</c> on these components keeps its compile-time default and
        /// cannot be authored — no loading screen, no UI containers, no audio or notification
        /// tuning. This path exists only so an un-migrated boot scene still boots; assign
        /// <c>_coreSystemsPrefab</c> to get configurable systems.
        /// </summary>
        private void RegisterSystemsFromCode()
        {
            GameLogger.LogWarning("Core",
                "No core systems prefab assigned — falling back to code-constructed systems. " +
                "Inspector values (loading screen, UI containers, tuning) are unavailable. " +
                "Run SkylotusCI.GenerateCoreSystemsPrefab and assign the result.");

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

                // Reflection is unavoidable on this path: the component is created at runtime, so
                // there is no serialized data to carry the reference and InputManager exposes no
                // setter. The prefab path assigns the asset in the inspector instead.
                var field = typeof(InputManager).GetField("_inputActions",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                field?.SetValue(inputManager, _inputActions);

                // Initialize after the asset is injected (not in Awake, which fires before injection)
                inputManager.Initialize();

                ServiceLocator.Register(inputManager);
            }

            // ─── Color Palette ──────────────────────────────────────
            if (_colorPalette != null)
                ServiceLocator.Register(_colorPalette);

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
            // Editor and development builds only; a release player never creates the object.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_enableDebugConsole)
            {
                var consoleGo = CreateChild("DebugConsole");
                consoleGo.AddComponent<DebugConsole>();
                RegisterDebugCommands();
            }
#endif
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

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>
        /// Register console commands that interact with core systems. Compiled only into the
        /// editor and development builds, matching <see cref="DebugConsole"/>'s own guard —
        /// a release player has no console to register against, and these handlers (and the
        /// systems they reach) never enter the shipped assembly.
        /// </summary>
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
#endif

        /// <summary>
        /// Unity OnApplicationPause — commit any unsaved settings. On mobile this is the last
        /// callback guaranteed to run before the OS may kill a backgrounded process, so it is
        /// the only reliable place to flush; settings setters never write to disk themselves.
        /// </summary>
        /// <param name="pauseStatus">True when the application is going into the background.</param>
        private void OnApplicationPause(bool pauseStatus)
        {
            if (pauseStatus && ServiceLocator.TryGet<SettingsService>(out var settings))
                settings.Flush();
        }

        /// <summary>
        /// Unity OnApplicationQuit — flush pending settings, then clean up all static state to
        /// prevent leaks between play-mode sessions in the editor.
        /// </summary>
        private void OnApplicationQuit()
        {
            if (ServiceLocator.TryGet<SettingsService>(out var settings))
                settings.Flush();

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