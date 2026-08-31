using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Skylotus.Editor
{
    /// <summary>
    /// Regression check for WP-6: proves the debug console is compiled out of a non-development
    /// player. See the partial-class note on <see cref="SkylotusCI"/> — this file only adds
    /// methods, it never edits <c>SkylotusCI.cs</c>.
    ///
    /// A compile in the editor proves nothing here: <c>UNITY_EDITOR</c> is always defined while
    /// the editor compiles, so the guarded code is always present in the assemblies an editor
    /// compile produces. The only real evidence is a built player whose managed assemblies do
    /// not contain the console's code.
    /// </summary>
    public static partial class SkylotusCI
    {
        /// <summary>Folder (under the system temp directory) the strip-check players are built into.</summary>
        private const string StripCheckFolderName = "SkylotusStripCheck";

        /// <summary>
        /// Environment variable selecting the scripting backend used by the strip check.
        /// Defaults to <c>Mono2x</c>: the IL2CPP module is not installed on every machine, and
        /// the guard being tested is a C# preprocessor directive, so Mono proves it just as well.
        /// </summary>
        private const string StripCheckBackendVariable = "SKYLOTUS_SCRIPTING_BACKEND";

        /// <summary>
        /// String literals and metadata names that exist only inside the console's
        /// <c>#if UNITY_EDITOR || DEVELOPMENT_BUILD</c> regions. None of them may appear in any
        /// managed assembly of a release player.
        /// </summary>
        private static readonly string[] ConsoleOnlySymbols =
        {
            "RegisterBuiltInCommands",       // DebugConsole method name (metadata #Strings)
            "RegisterDebugCommands",         // Bootstrapper method name (metadata #Strings)
            "<color=cyan>=== Commands ===</color>",
            "Set game time scale (timescale <value>)",
            "Set log level (log_level <trace|debug|info|warning|error>)",
            "Force garbage collection",
            "Show/set game state (state [newState])",
            "List all save slots"
        };

        /// <summary>
        /// Literal that is present in every build, guarded or not. It is the control for the
        /// scan: if this is missing, the search is looking in the wrong place or with the wrong
        /// encoding, and an absence of console symbols would mean nothing.
        /// </summary>
        private const string AlwaysPresentSymbol = "=== Skylotus Core Systems Bootstrapping ===";

        /// <summary>
        /// The <see cref="DebugConsole"/> <b>type</b> must survive into a release player even
        /// though its behaviour does not: game code calls its no-op statics, and the core systems
        /// prefab carries the component, which would become a missing script if the type vanished.
        /// </summary>
        private const string ReleaseRequiredSymbol = "DebugConsole";

        /// <summary>
        /// Build a non-development and a development Windows x64 player and assert that the
        /// debug console's code is present in the development one and absent from the release
        /// one. Fails the run (exit 1) if either expectation is violated.
        ///
        /// Both players go to a temp folder outside the repository and are deleted afterwards.
        /// The scripting backend is temporarily forced (see
        /// <see cref="StripCheckBackendVariable"/>) and restored before the method returns, so
        /// <c>ProjectSettings/ProjectSettings.asset</c> ends up unchanged.
        ///
        /// Run as:
        /// <c>Tools\unity-verify.ps1 -Mode method -Graphics -Method
        /// Skylotus.Editor.SkylotusCI.VerifyReleaseConsoleStripped</c>
        /// </summary>
        public static void VerifyReleaseConsoleStripped()
        {
            var backendRestored = false;
            var originalBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
            var outputRoot = Path.Combine(Path.GetTempPath(), StripCheckFolderName);

            try
            {
                var backend = ResolveStripCheckBackend(originalBackend);
                if (backend != originalBackend)
                {
                    Debug.Log($"[{Category}] Temporarily switching Standalone scripting backend " +
                              $"{originalBackend} -> {backend} for the strip check.");
                    PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, backend);
                }

                if (Directory.Exists(outputRoot))
                    Directory.Delete(outputRoot, recursive: true);

                if (!BuildStripCheckPlayer(outputRoot, development: false, out var releaseDir))
                    return;

                if (!BuildStripCheckPlayer(outputRoot, development: true, out var developmentDir))
                    return;

                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, originalBackend);
                backendRestored = true;

                var problems = new List<string>();
                var releaseHits = ScanManagedAssemblies(
                    releaseDir, out var releaseControlFound, out var releaseTypeFound);
                var developmentHits = ScanManagedAssemblies(
                    developmentDir, out var developmentControlFound, out _);

                if (!releaseTypeFound)
                {
                    problems.Add($"The release player has no \"{ReleaseRequiredSymbol}\" type. " +
                                 "Stripping the type itself breaks the no-op API and turns the " +
                                 "core systems prefab's component into a missing script.");
                }

                if (!releaseControlFound)
                {
                    problems.Add($"Control literal \"{AlwaysPresentSymbol}\" not found in the " +
                                 "release player — the scan cannot be trusted.");
                }

                if (!developmentControlFound)
                {
                    problems.Add($"Control literal \"{AlwaysPresentSymbol}\" not found in the " +
                                 "development player — the scan cannot be trusted.");
                }

                foreach (var hit in releaseHits)
                    problems.Add($"Release player still contains console symbol: {hit}");

                if (developmentHits.Count == 0)
                {
                    problems.Add("Development player contains none of the console symbols either " +
                                 "— the symbols searched for are stale, so the release result " +
                                 "proves nothing.");
                }

                if (problems.Count > 0)
                {
                    foreach (var problem in problems)
                        Debug.LogError($"[{Category}] {problem}");

                    Fail($"Debug console strip check failed ({problems.Count} problem(s)). " +
                         $"Players left in {outputRoot} for inspection.");
                    return;
                }

                Directory.Delete(outputRoot, recursive: true);

                Succeed($"Debug console is stripped from the release player: 0 of " +
                        $"{ConsoleOnlySymbols.Length} console symbols present, while the " +
                        $"development player has {developmentHits.Count}. Control literal found " +
                        $"in both, and the {ReleaseRequiredSymbol} type survives in release.");
            }
            catch (Exception e)
            {
                Fail($"Debug console strip check threw: {e}");
            }
            finally
            {
                if (!backendRestored)
                    PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, originalBackend);
            }
        }

        /// <summary>
        /// Resolve the scripting backend the strip check builds with:
        /// <see cref="StripCheckBackendVariable"/> when set and parseable, otherwise
        /// <see cref="ScriptingImplementation.Mono2x"/>.
        /// </summary>
        /// <param name="current">The project's configured backend, used only for logging.</param>
        /// <returns>The backend to build the check's players with.</returns>
        private static ScriptingImplementation ResolveStripCheckBackend(ScriptingImplementation current)
        {
            var raw = Environment.GetEnvironmentVariable(StripCheckBackendVariable);

            if (!string.IsNullOrEmpty(raw) &&
                Enum.TryParse<ScriptingImplementation>(raw, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            if (!string.IsNullOrEmpty(raw))
            {
                Debug.LogWarning($"[{Category}] {StripCheckBackendVariable}='{raw}' is not a " +
                                 "ScriptingImplementation name; falling back to Mono2x.");
            }

            Debug.Log($"[{Category}] Strip check builds with Mono2x " +
                      $"(project setting is {current}; the guard under test is a preprocessor " +
                      "directive, so the backend does not affect the result).");

            return ScriptingImplementation.Mono2x;
        }

        /// <summary>
        /// Build one Windows x64 player for the strip check.
        /// </summary>
        /// <param name="outputRoot">Folder both players are built beneath.</param>
        /// <param name="development">True to pass <see cref="BuildOptions.Development"/>, which
        /// defines <c>DEVELOPMENT_BUILD</c>.</param>
        /// <param name="playerDirectory">Receives the folder the player was written to.</param>
        /// <returns>True on a successful build; on failure the method has already called
        /// <see cref="SkylotusCI.Fail"/>.</returns>
        private static bool BuildStripCheckPlayer(string outputRoot, bool development, out string playerDirectory)
        {
            var label = development ? "development" : "release";
            playerDirectory = Path.Combine(outputRoot, label);

            var scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                Fail("No enabled scenes in Build Settings; nothing to build.");
                return false;
            }

            Directory.CreateDirectory(playerDirectory);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = Path.Combine(playerDirectory, "Skylotus.exe"),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = development ? BuildOptions.Development : BuildOptions.None
            };

            Debug.Log($"[{Category}] Building {label} player -> {options.locationPathName}");

            var summary = BuildPipeline.BuildPlayer(options).summary;

            if (summary.result != BuildResult.Succeeded)
            {
                Fail($"The {label} strip-check build failed: {summary.result} " +
                     $"({summary.totalErrors} error(s)). See the build log above.");
                return false;
            }

            Debug.Log($"[{Category}] {label} player built in {summary.totalTime.TotalSeconds:F1}s.");
            return true;
        }

        /// <summary>
        /// Scan every managed assembly of a built player for the console's symbols.
        /// </summary>
        /// <param name="playerDirectory">Folder containing the built player.</param>
        /// <param name="controlFound">Receives whether <see cref="AlwaysPresentSymbol"/> was
        /// found, which is what makes a negative result meaningful.</param>
        /// <param name="typeFound">Receives whether <see cref="ReleaseRequiredSymbol"/> was
        /// found — the console type must survive even when its behaviour does not.</param>
        /// <returns>One <c>assembly: symbol</c> entry per console symbol found.</returns>
        private static List<string> ScanManagedAssemblies(
            string playerDirectory, out bool controlFound, out bool typeFound)
        {
            controlFound = false;
            typeFound = false;
            var hits = new List<string>();

            var managed = Directory
                .GetDirectories(playerDirectory, "Managed", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (managed == null)
            {
                hits.Add($"{playerDirectory}: no Managed folder found in the built player");
                return hits;
            }

            var assemblies = Directory.GetFiles(managed, "*.dll");
            Debug.Log($"[{Category}] Scanning {assemblies.Length} managed assembly/assemblies in {managed}");

            foreach (var assembly in assemblies)
            {
                var bytes = File.ReadAllBytes(assembly);
                var name = Path.GetFileName(assembly);

                if (ContainsText(bytes, AlwaysPresentSymbol))
                {
                    controlFound = true;
                    Debug.Log($"[{Category}] Control literal found in {name}.");
                }

                if (ContainsText(bytes, ReleaseRequiredSymbol))
                {
                    typeFound = true;
                    Debug.Log($"[{Category}] {ReleaseRequiredSymbol} type name found in {name}.");
                }

                foreach (var symbol in ConsoleOnlySymbols)
                {
                    if (ContainsText(bytes, symbol))
                        hits.Add($"{name}: \"{symbol}\"");
                }
            }

            return hits;
        }

        /// <summary>
        /// Search a file's raw bytes for a string, in both of the encodings .NET metadata uses:
        /// UTF-8 for member names (the <c>#Strings</c> heap) and UTF-16LE for string literals
        /// (the <c>#US</c> heap). Raw byte search avoids the alignment problem that decoding the
        /// whole file as UTF-16 would introduce.
        /// </summary>
        /// <param name="data">The file's bytes.</param>
        /// <param name="text">The text to look for.</param>
        /// <returns>True if the text appears in either encoding.</returns>
        private static bool ContainsText(byte[] data, string text)
        {
            return ContainsBytes(data, Encoding.UTF8.GetBytes(text)) ||
                   ContainsBytes(data, Encoding.Unicode.GetBytes(text));
        }

        /// <summary>Naive byte-sequence search; the inputs are small enough that it does not matter.</summary>
        /// <param name="haystack">Bytes to search in.</param>
        /// <param name="needle">Bytes to search for.</param>
        /// <returns>True if <paramref name="needle"/> occurs in <paramref name="haystack"/>.</returns>
        private static bool ContainsBytes(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length)
                return false;

            var last = haystack.Length - needle.Length;

            for (int i = 0; i <= last; i++)
            {
                if (haystack[i] != needle[0])
                    continue;

                var matched = true;
                for (int j = 1; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                    return true;
            }

            return false;
        }
    }
}
