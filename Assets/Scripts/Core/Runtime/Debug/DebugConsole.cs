using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// In-game runtime debug console. Toggle visibility with the backtick (`) key.
    /// Register custom commands with <see cref="Register"/> to extend functionality.
    ///
    /// Built-in commands: help, clear, fps, timescale, log_level, scene, gc, quit
    /// </summary>
    public class DebugConsole : MonoBehaviour
    {
        /// <summary>Internal representation of a registered console command.</summary>
        private struct Command
        {
            public string Name;
            public string Description;
            public Action<string[]> Handler;
        }

        /// <summary>Singleton reference for static access.</summary>
        private static DebugConsole _instance;

        /// <summary>All registered commands keyed by lowercase name.</summary>
        private static readonly Dictionary<string, Command> _commands = new();

        /// <summary>Console output log lines (newest at the end).</summary>
        private static readonly List<string> _log = new();

        /// <summary>History of previously executed commands for up-arrow recall.</summary>
        private static readonly List<string> _commandHistory = new();

        /// <summary>Maximum number of log lines retained before oldest is discarded.</summary>
        private const int MaxLogLines = 200;

        /// <summary>
        /// Reset static state on domain reload (Editor Enter Play Mode settings).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
            _commands.Clear();
            _log.Clear();
            _commandHistory.Clear();
        }

        /// <summary>Whether the console overlay is currently visible.</summary>
        private bool _isOpen;

        /// <summary>Current text in the input field.</summary>
        private string _inputText = "";

        /// <summary>Scroll position of the log area.</summary>
        private Vector2 _scrollPos;

        /// <summary>Current position in command history (for up/down arrow navigation).</summary>
        private int _historyIndex = -1;

        // ─── GUI Styles ─────────────────────────────────────────────
        private GUIStyle _consoleStyle;
        private GUIStyle _inputStyle;
        private GUIStyle _logStyle;
        private bool _stylesInitialized;

        /// <summary>Whether the console is currently open. Useful for suppressing game input.</summary>
        public static bool IsOpen => _instance != null && _instance._isOpen;

        /// <summary>Unity Awake — enforce singleton and register built-in commands.</summary>
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            RegisterBuiltInCommands();
        }

        /// <summary>Unity Update — listen for the toggle key.</summary>
        private void Update()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (UnityEngine.Input.GetKeyDown(KeyCode.BackQuote))
                _isOpen = !_isOpen;
#elif ENABLE_INPUT_SYSTEM
            if (UnityEngine.InputSystem.Keyboard.current != null &&
                UnityEngine.InputSystem.Keyboard.current[UnityEngine.InputSystem.Key.Backquote].wasPressedThisFrame)
                _isOpen = !_isOpen;
#endif
        }

        /// <summary>
        /// Register a custom console command.
        /// </summary>
        /// <param name="name">The command keyword (case-insensitive).</param>
        /// <param name="description">A short description shown in the help listing.</param>
        /// <param name="handler">Callback receiving an array of space-separated argument strings.</param>
        public static void Register(string name, string description, Action<string[]> handler)
        {
            _commands[name.ToLower()] = new Command
            {
                Name = name.ToLower(),
                Description = description,
                Handler = handler
            };
        }

        /// <summary>
        /// Write a line to the console log. Supports Unity rich text tags for color.
        /// </summary>
        /// <param name="message">The message to display.</param>
        public static void Print(string message)
        {
            _log.Add(message);
            if (_log.Count > MaxLogLines)
                _log.RemoveAt(0);
        }

        /// <summary>
        /// Parse and execute a command string. The first word is the command name,
        /// subsequent words are passed as arguments.
        /// </summary>
        /// <param name="input">The raw command string (e.g. "timescale 0.5").</param>
        public static void Execute(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return;

            Print($"> {input}");
            _commandHistory.Add(input);

            var parts = input.Trim().Split(' ');
            var cmdName = parts[0].ToLower();
            var args = parts.Length > 1 ? parts.Skip(1).ToArray() : Array.Empty<string>();

            if (_commands.TryGetValue(cmdName, out var cmd))
            {
                try { cmd.Handler.Invoke(args); }
                catch (Exception ex) { Print($"<color=red>Error: {ex.Message}</color>"); }
            }
            else
            {
                Print($"<color=yellow>Unknown command: {cmdName}. Type 'help' for list.</color>");
            }
        }

        /// <summary>Register the default set of built-in console commands.</summary>
        private void RegisterBuiltInCommands()
        {
            Register("help", "List all commands", _ =>
            {
                Print("<color=cyan>=== Commands ===</color>");
                foreach (var cmd in _commands.Values.OrderBy(c => c.Name))
                    Print($"  <color=white>{cmd.Name}</color> - {cmd.Description}");
            });

            Register("clear", "Clear console output", _ => _log.Clear());

            Register("fps", "Show current FPS", _ =>
            {
                Print($"FPS: {1f / Time.unscaledDeltaTime:F1}");
            });

            Register("timescale", "Set time scale (timescale <value>)", args =>
            {
                if (args.Length > 0 && float.TryParse(args[0], out var scale))
                {
                    Time.timeScale = scale;
                    Print($"Time scale set to {scale}");
                }
                else Print($"Current time scale: {Time.timeScale}");
            });

            Register("log_level", "Set log level (log_level <trace|debug|info|warning|error>)", args =>
            {
                if (args.Length > 0 && Enum.TryParse<LogLevel>(args[0], true, out var level))
                {
                    GameLogger.GlobalLevel = level;
                    Print($"Log level set to {level}");
                }
                else Print($"Current log level: {GameLogger.GlobalLevel}");
            });

            Register("scene", "Load scene by name (scene <name>)", args =>
            {
                if (args.Length > 0)
                {
                    var mgr = ServiceLocator.Get<SkylotusSceneManager>();
                    mgr.LoadScene(args[0]);
                    Print($"Loading scene: {args[0]}");
                }
            });

            Register("gc", "Force garbage collection", _ =>
            {
                GC.Collect();
                Print("GC.Collect() called.");
            });

            Register("quit", "Quit the application", _ => Application.Quit());
        }

        // ─── IMGUI Rendering ────────────────────────────────────────

        /// <summary>Lazy-initialize GUI styles on first use.</summary>
        private void InitStyles()
        {
            if (_stylesInitialized) return;

            _consoleStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeTex(1, 1, new Color(0.05f, 0.05f, 0.1f, 0.92f)) }
            };

            _inputStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 16,
                normal = { textColor = Color.white }
            };

            _logStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                richText = true,
                wordWrap = true
            };

            _stylesInitialized = true;
        }

        /// <summary>Draw the console overlay when open.</summary>
        private void OnGUI()
        {
            if (!_isOpen) return;
            InitStyles();

            float height = Screen.height * 0.4f;
            GUILayout.BeginArea(new Rect(0, 0, Screen.width, height), _consoleStyle);

            // Scrollable log area
            _scrollPos = GUILayout.BeginScrollView(_scrollPos);
            foreach (var line in _log)
                GUILayout.Label(line, _logStyle);
            GUILayout.EndScrollView();

            // Input field
            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("ConsoleInput");
            _inputText = GUILayout.TextField(_inputText, _inputStyle);
            GUI.FocusControl("ConsoleInput");

            // Keyboard handling: Enter to execute, Up/Down for history
            if (Event.current.isKey && GUI.GetNameOfFocusedControl() == "ConsoleInput")
            {
                if (Event.current.keyCode == KeyCode.Return && !string.IsNullOrEmpty(_inputText))
                {
                    Execute(_inputText);
                    _inputText = "";
                    _historyIndex = -1;
                    _scrollPos.y = float.MaxValue; // auto-scroll to bottom
                    Event.current.Use();
                }
                else if (Event.current.keyCode == KeyCode.UpArrow && _commandHistory.Count > 0)
                {
                    _historyIndex = Mathf.Min(_historyIndex + 1, _commandHistory.Count - 1);
                    _inputText = _commandHistory[_commandHistory.Count - 1 - _historyIndex];
                    Event.current.Use();
                }
                else if (Event.current.keyCode == KeyCode.DownArrow)
                {
                    _historyIndex = Mathf.Max(_historyIndex - 1, -1);
                    _inputText = _historyIndex >= 0
                        ? _commandHistory[_commandHistory.Count - 1 - _historyIndex]
                        : "";
                    Event.current.Use();
                }
            }

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        /// <summary>Create a single-pixel texture filled with a solid color (for GUI backgrounds).</summary>
        private static Texture2D MakeTex(int w, int h, Color col)
        {
            var tex = new Texture2D(w, h);
            var pixels = new Color[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = col;
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}