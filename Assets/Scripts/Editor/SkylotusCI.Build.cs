using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Skylotus.Editor
{
    /// <summary>
    /// Headless build entry points, invoked via <c>-executeMethod</c> from CI or from
    /// <c>Tools/unity-verify.ps1 -Mode method</c>. See the partial-class note on
    /// <see cref="SkylotusCI"/> — this file only adds methods, it never edits
    /// <c>SkylotusCI.cs</c>.
    /// </summary>
    public static partial class SkylotusCI
    {
        /// <summary>Root folder (relative to the project) that built players are written under.</summary>
        private const string BuildOutputRoot = "Builds";

        /// <summary>
        /// Build a Windows x64 standalone player from every enabled scene in
        /// <c>Build Settings</c>, using the currently configured scripting backend and
        /// stripping level (see <c>SkylotusCI.ConfigureProject</c>, WP-11).
        ///
        /// Output path can be overridden with the <c>SKYLOTUS_BUILD_OUTPUT</c> environment
        /// variable (useful for CI, which wants build output outside the workspace so it can be
        /// picked up as an artifact); otherwise it defaults to
        /// <c>Builds/Windows64/Skylotus.exe</c>.
        ///
        /// Run as:
        /// <c>Tools\unity-verify.ps1 -Mode method -Graphics -Method
        /// Skylotus.Editor.SkylotusCI.BuildWindows64</c>
        /// </summary>
        public static void BuildWindows64()
        {
            BuildStandalonePlayer(BuildTarget.StandaloneWindows64, "Windows64", "Skylotus.exe");
        }

        /// <summary>
        /// Build a Linux x64 standalone player from every enabled scene in
        /// <c>Build Settings</c>. See <see cref="BuildWindows64"/> for the output-path override
        /// and general behaviour; this target exists so CI can build on Linux runners without a
        /// cross-compile step.
        /// </summary>
        public static void BuildLinux64()
        {
            BuildStandalonePlayer(BuildTarget.StandaloneLinux64, "Linux64", "Skylotus");
        }

        /// <summary>
        /// Shared implementation behind every standalone build target: validates the scene list,
        /// resolves the output path, runs <see cref="BuildPipeline.BuildPlayer"/>, and reports
        /// the result through <see cref="SkylotusCI.Succeed"/> / <see cref="SkylotusCI.Fail"/>.
        /// </summary>
        /// <param name="target">Platform to build for.</param>
        /// <param name="subfolder">Sub-folder of <see cref="BuildOutputRoot"/> the player is
        /// written into.</param>
        /// <param name="executableName">File name of the built executable.</param>
        private static void BuildStandalonePlayer(BuildTarget target, string subfolder, string executableName)
        {
            try
            {
                var scenes = EditorBuildSettings.scenes
                    .Where(scene => scene.enabled)
                    .Select(scene => scene.path)
                    .ToArray();

                if (scenes.Length == 0)
                {
                    Fail("No enabled scenes in Build Settings; nothing to build.");
                    return;
                }

                var outputRoot = Environment.GetEnvironmentVariable("SKYLOTUS_BUILD_OUTPUT");
                if (string.IsNullOrEmpty(outputRoot))
                    outputRoot = Path.Combine(BuildOutputRoot, subfolder);

                Directory.CreateDirectory(outputRoot);
                var locationPathName = Path.Combine(outputRoot, executableName);

                var options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = locationPathName,
                    target = target,
                    targetGroup = BuildPipeline.GetBuildTargetGroup(target),
                    options = BuildOptions.None
                };

                Debug.Log($"[{Category}] Building {target} -> {locationPathName} " +
                          $"({scenes.Length} scene(s): {string.Join(", ", scenes)})");

                var report = BuildPipeline.BuildPlayer(options);
                var summary = report.summary;

                if (summary.result != BuildResult.Succeeded)
                {
                    Fail($"{target} build result: {summary.result} " +
                         $"({summary.totalErrors} error(s), {summary.totalWarnings} warning(s)). " +
                         $"See the build log above for details.");
                    return;
                }

                Succeed($"Built {target}: {summary.outputPath} " +
                        $"({summary.totalSize / (1024 * 1024)} MB, " +
                        $"{summary.totalTime.TotalSeconds:F1}s, {summary.totalWarnings} warning(s)).");
            }
            catch (Exception e)
            {
                Fail($"Build of {target} threw: {e}");
            }
        }
    }
}
