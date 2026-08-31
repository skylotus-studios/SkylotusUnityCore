using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor.Compilation;
using UnityEngine;

namespace Skylotus.Tests.EditMode
{
    /// <summary>
    /// WP-12's structural acceptance criterion, asserted rather than assumed:
    /// <c>Assets/Tests/**</c> compiles into its own assemblies and is excluded from player builds.
    ///
    /// The exclusion comes from the <c>UNITY_INCLUDE_TESTS</c> define constraint on both test
    /// asmdefs (and, for the EditMode one, from <c>includePlatforms: ["Editor"]</c>). Rather than
    /// take that on trust — or build a player, which mutates tracked project assets — this asks
    /// the compilation pipeline directly which assemblies each target actually gets.
    /// </summary>
    [TestFixture]
    public class TestAssemblyLayoutTests
    {
        /// <summary>Name of the EditMode test assembly.</summary>
        private const string EditModeAssembly = "Skylotus.Tests.EditMode";

        /// <summary>Name of the PlayMode test assembly.</summary>
        private const string PlayModeAssembly = "Skylotus.Tests.PlayMode";

        /// <summary>Name of the runtime assembly the tests exercise.</summary>
        private const string RuntimeAssembly = "Skylotus.Core.Runtime";

        /// <summary>Both test assemblies exist as separate assemblies, not folded into a predefined one.</summary>
        [Test]
        public void TestCode_CompilesIntoItsOwnAssemblies()
        {
            var editor = AssemblyNames(AssembliesType.Editor);

            CollectionAssert.Contains(editor, EditModeAssembly);
            CollectionAssert.Contains(editor, PlayModeAssembly);
            CollectionAssert.Contains(editor, RuntimeAssembly);

            CollectionAssert.DoesNotContain(editor, "Assembly-CSharp-Editor-testable",
                "Tests belong to their own asmdefs, not to a predefined testable assembly.");
        }

        /// <summary>
        /// Neither test assembly is part of a real player build, while the runtime assembly they
        /// test is. A regression here would ship the test code — and its NUnit dependency — inside
        /// the game.
        ///
        /// <c>AssembliesType.PlayerWithoutTestAssemblies</c> is the set an ordinary build compiles.
        /// <c>AssembliesType.Player</c> is <i>not</i> the right question to ask: inside the Editor
        /// that set has <c>UNITY_INCLUDE_TESTS</c> defined so PlayMode tests can be built into a
        /// test player, and it therefore lists <c>Skylotus.Tests.PlayMode</c> by design.
        /// </summary>
        [Test]
        public void TestAssemblies_AreExcludedFromPlayerBuilds()
        {
            var shipped = AssemblyNames(AssembliesType.PlayerWithoutTestAssemblies);

            CollectionAssert.Contains(shipped, RuntimeAssembly,
                "Sanity: the runtime assembly must be in a player build.");

            CollectionAssert.DoesNotContain(shipped, EditModeAssembly,
                "The EditMode test assembly must not reach a player build.");
            CollectionAssert.DoesNotContain(shipped, PlayModeAssembly,
                "The PlayMode test assembly must not reach a player build.");
        }

        /// <summary>
        /// The mechanism behind the exclusion, asserted so it cannot be dropped silently: both
        /// asmdefs carry the <c>UNITY_INCLUDE_TESTS</c> define constraint, and the EditMode one is
        /// additionally scoped to the Editor platform.
        /// </summary>
        [Test]
        public void TestAsmdefs_DeclareTheUnityIncludeTestsConstraint()
        {
            foreach (var assemblyName in new[] { EditModeAssembly, PlayModeAssembly })
            {
                string path = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assemblyName);
                Assert.IsNotNull(path, $"No asmdef found for {assemblyName}.");

                string json = File.ReadAllText(Path.Combine(
                    Path.GetDirectoryName(Application.dataPath), path));

                StringAssert.Contains("UNITY_INCLUDE_TESTS", json,
                    $"{assemblyName} must be gated behind UNITY_INCLUDE_TESTS to stay out of builds.");
            }

            string editModeJson = File.ReadAllText(Path.Combine(
                Path.GetDirectoryName(Application.dataPath),
                CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(EditModeAssembly)));

            StringAssert.Contains("\"Editor\"", editModeJson,
                "The EditMode assembly must also be restricted to the Editor platform.");
        }

        /// <summary>Collect the names of every assembly the pipeline builds for a target.</summary>
        /// <param name="type">Which set of assemblies to enumerate.</param>
        /// <returns>The assembly names, without extension.</returns>
        private static List<string> AssemblyNames(AssembliesType type)
        {
            var names = new List<string>();
            foreach (var assembly in CompilationPipeline.GetAssemblies(type))
                names.Add(assembly.name);

            return names;
        }
    }
}
