using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Skylotus
{
    /// <summary>Severity levels for log output filtering.</summary>
    public enum LogLevel { Trace, Debug, Info, Warning, Error, Fatal, Off }

    /// <summary>
    /// Categorized logging system with runtime level control, per-category filtering,
    /// and optional file output. Use category strings to tag log lines
    /// (e.g. "Audio", "Save", "Input") so they can be filtered independently.
    ///
    /// Initialize early via <see cref="Initialize"/> — before any other system logs.
    /// </summary>
    public static class GameLogger
    {
        /// <summary>Global minimum log level. Messages below this level are discarded.</summary>
        private static LogLevel _globalLevel = LogLevel.Debug;

        /// <summary>Per-category overrides. If a category is present here, it uses its own level.</summary>
        private static readonly Dictionary<string, LogLevel> _categoryLevels = new();

        /// <summary>Reusable string builder to avoid allocation in hot paths.</summary>
        private static readonly StringBuilder _buffer = new();

        /// <summary>Full path to the current session's log file (null if file logging is off).</summary>
        private static string _logFilePath;

        /// <summary>Whether to write log lines to disk.</summary>
        private static bool _writeToFile;

        /// <summary>Whether to prepend HH:mm:ss.fff timestamps to each line.</summary>
        private static bool _includeTimestamp = true;

        /// <summary>Whether to append a stack trace to Error and Fatal messages in the log file.</summary>
        private static bool _includeStackTrace;

        /// <summary>
        /// Reset static state on domain reload (Editor Enter Play Mode settings).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _globalLevel = LogLevel.Debug;
            _categoryLevels.Clear();
            _buffer.Clear();
            _logFilePath = null;
            _writeToFile = false;
            _includeTimestamp = true;
            _includeStackTrace = false;
        }

        /// <summary>
        /// Gets or sets the global minimum log level. Any message below this severity is discarded
        /// unless the category has its own override set via <see cref="SetCategoryLevel"/>.
        /// </summary>
        public static LogLevel GlobalLevel
        {
            get => _globalLevel;
            set => _globalLevel = value;
        }

        /// <summary>
        /// Initialize the logger. Call once at startup before other systems.
        /// </summary>
        /// <param name="writeToFile">If true, log lines are also appended to a timestamped file in Application.persistentDataPath/Logs.</param>
        /// <param name="includeStackTrace">If true, Error and Fatal messages include a stack trace in the file output.</param>
        public static void Initialize(bool writeToFile = false, bool includeStackTrace = false)
        {
            _writeToFile = writeToFile;
            _includeStackTrace = includeStackTrace;

            if (_writeToFile)
            {
                var logDir = Path.Combine(Application.persistentDataPath, "Logs");
                Directory.CreateDirectory(logDir);
                _logFilePath = Path.Combine(logDir, $"game_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            }
        }

        /// <summary>
        /// Set the minimum log level for a specific category.
        /// This overrides the global level for all messages tagged with <paramref name="category"/>.
        /// </summary>
        /// <param name="category">The category tag (e.g. "Audio", "Save").</param>
        /// <param name="level">The minimum level for this category.</param>
        public static void SetCategoryLevel(string category, LogLevel level)
        {
            _categoryLevels[category] = level;
        }

        /// <summary>Log an informational message.</summary>
        /// <param name="category">The subsystem tag.</param>
        /// <param name="message">The message to log.</param>
        public static void Log(string category, string message) =>
            WriteLog(LogLevel.Info, category, message);

        /// <summary>Log a debug-level message (only shown when level ≤ Debug).</summary>
        public static void LogDebug(string category, string message) =>
            WriteLog(LogLevel.Debug, category, message);

        /// <summary>Log a warning-level message.</summary>
        public static void LogWarning(string category, string message) =>
            WriteLog(LogLevel.Warning, category, message);

        /// <summary>Log an error-level message.</summary>
        public static void LogError(string category, string message) =>
            WriteLog(LogLevel.Error, category, message);

        /// <summary>Log a fatal-level message (highest severity).</summary>
        public static void LogFatal(string category, string message) =>
            WriteLog(LogLevel.Fatal, category, message);

        /// <summary>Log a trace-level message (lowest severity, very verbose).</summary>
        public static void LogTrace(string category, string message) =>
            WriteLog(LogLevel.Trace, category, message);

        /// <summary>
        /// Core write method. Checks category and global level filters, formats the
        /// message, routes to Unity's Debug.Log / LogWarning / LogError, and optionally
        /// appends to the log file.
        /// </summary>
        private static void WriteLog(LogLevel level, string category, string message)
        {
            // Apply category-specific filter first, then fall back to global
            if (_categoryLevels.TryGetValue(category, out var catLevel))
            {
                if (level < catLevel) return;
            }
            else if (level < _globalLevel)
            {
                return;
            }

            _buffer.Clear();

            if (_includeTimestamp)
                _buffer.Append($"[{DateTime.Now:HH:mm:ss.fff}] ");

            _buffer.Append($"[{level}] [{category}] {message}");

            var formatted = _buffer.ToString();

            // Route to the appropriate Unity log channel
            switch (level)
            {
                case LogLevel.Trace:
                case LogLevel.Debug:
                case LogLevel.Info:
                    UnityEngine.Debug.Log(formatted);
                    break;
                case LogLevel.Warning:
                    UnityEngine.Debug.LogWarning(formatted);
                    break;
                case LogLevel.Error:
                case LogLevel.Fatal:
                    UnityEngine.Debug.LogError(formatted);
                    break;
            }

            // Optional file output
            if (_writeToFile)
            {
                try
                {
                    if (_includeStackTrace && level >= LogLevel.Error)
                        formatted += $"\n{Environment.StackTrace}";

                    File.AppendAllText(_logFilePath, formatted + "\n");
                }
                catch { /* Silently fail file writes to avoid cascading errors */ }
            }
        }
    }
}