using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace Skylotus.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="GameLogger"/>'s thread safety.
    ///
    /// The logger used a single shared <see cref="StringBuilder"/> and a plain
    /// <c>Dictionary</c>, and appended to its log file with no lock. Concurrent callers could
    /// therefore interleave into one buffer and emit torn lines, corrupt the category map, or
    /// lose file writes to an <see cref="IOException"/> that the surrounding catch swallowed.
    ///
    /// Nothing in the project logged off the main thread when that was found — <c>SaveSystem</c>
    /// deliberately kept its worker-thread helpers log-free to avoid it — so these tests exist to
    /// stop the constraint being reintroduced silently.
    ///
    /// <see cref="GameLogger"/> is static and session-wide, so every test restores the state it
    /// touched.
    /// </summary>
    [TestFixture]
    public class GameLoggerTests
    {
        /// <summary>How many threads contend in the concurrency tests.</summary>
        private const int ThreadCount = 8;

        /// <summary>How many messages each thread emits.</summary>
        private const int MessagesPerThread = 40;

        /// <summary>Category used by these tests, kept distinct from any real one.</summary>
        private const string TestCategory = "GameLoggerTests";

        /// <summary>Restore the logger to its default configuration after each case.</summary>
        [TearDown]
        public void TearDown()
        {
            GameLogger.Initialize(writeToFile: false);
            GameLogger.GlobalLevel = LogLevel.Debug;
        }

        // ─── Structure ──────────────────────────────────────────────

        /// <summary>
        /// The message buffer must be per-thread. A shared instance is the defect itself: two
        /// threads running Clear/Append/ToString against one builder produce torn output.
        /// </summary>
        [Test]
        public void MessageBuffer_IsThreadStatic()
        {
            var field = typeof(GameLogger).GetField("_buffer",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(field, Is.Not.Null, "GameLogger._buffer no longer exists under that name.");
            Assert.That(field.IsDefined(typeof(ThreadStaticAttribute), inherit: false), Is.True,
                "GameLogger._buffer must be [ThreadStatic]; a shared builder tears under concurrency.");
        }

        /// <summary>
        /// The category-level map is read on every log call and written by
        /// <see cref="GameLogger.SetCategoryLevel"/>, so it must tolerate concurrent access.
        /// </summary>
        [Test]
        public void CategoryLevelMap_IsConcurrent()
        {
            var field = typeof(GameLogger).GetField("_categoryLevels",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(field, Is.Not.Null, "GameLogger._categoryLevels no longer exists under that name.");
            Assert.That(field.FieldType.IsGenericType, Is.True);
            Assert.That(field.FieldType.GetGenericTypeDefinition(),
                Is.EqualTo(typeof(ConcurrentDictionary<,>)),
                "A plain Dictionary can corrupt or throw when a read races SetCategoryLevel.");
        }

        // ─── Behaviour ──────────────────────────────────────────────

        /// <summary>
        /// Logging from many threads while the category map is being rewritten must not throw.
        /// This is the path that previously risked a corrupted dictionary.
        /// </summary>
        [Test]
        public void ConcurrentLoggingAndCategoryChanges_DoNotThrow()
        {
            // Filtered out before any formatting work, so the console stays quiet while the
            // dictionary read on every call still happens — which is the part under test.
            GameLogger.GlobalLevel = LogLevel.Off;

            var exceptions = new ConcurrentQueue<Exception>();
            using var start = new ManualResetEventSlim(false);

            var workers = Enumerable.Range(0, ThreadCount).Select(i => Task.Run(() =>
            {
                start.Wait();
                try
                {
                    for (var n = 0; n < MessagesPerThread; n++)
                    {
                        GameLogger.Log($"{TestCategory}_{i}", $"thread {i} message {n}");
                        GameLogger.SetCategoryLevel($"{TestCategory}_{n % 4}", LogLevel.Warning);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            })).ToArray();

            start.Set();
            Assert.That(Task.WaitAll(workers, TimeSpan.FromSeconds(30)), Is.True,
                "Concurrent logging deadlocked.");

            Assert.That(exceptions, Is.Empty,
                $"Concurrent logging threw: {string.Join(" | ", exceptions.Select(e => e.Message))}");
        }

        /// <summary>
        /// Every line written from every thread must arrive complete and well-formed.
        ///
        /// This is the assertion that actually catches a shared buffer: a torn line loses its
        /// prefix or splices two messages together, and neither survives the format check. It
        /// also catches a missing file lock, which drops lines outright.
        /// </summary>
        [Test]
        public void ConcurrentFileLogging_WritesEveryLineIntact()
        {
            GameLogger.GlobalLevel = LogLevel.Debug;
            GameLogger.Initialize(writeToFile: true);

            var path = (string)typeof(GameLogger)
                .GetField("_logFilePath", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);

            Assert.That(path, Is.Not.Null.And.Not.Empty, "File logging did not produce a path.");

            try
            {
                using var start = new ManualResetEventSlim(false);

                var workers = Enumerable.Range(0, ThreadCount).Select(i => Task.Run(() =>
                {
                    start.Wait();
                    for (var n = 0; n < MessagesPerThread; n++)
                        GameLogger.Log(TestCategory, $"t{i:D2}-m{n:D3}");
                })).ToArray();

                start.Set();
                Assert.That(Task.WaitAll(workers, TimeSpan.FromSeconds(30)), Is.True,
                    "Concurrent file logging deadlocked.");

                // Release the handle before reading it back.
                GameLogger.Initialize(writeToFile: false);

                var lines = File.ReadAllLines(path)
                    .Where(l => l.Contains(TestCategory))
                    .ToArray();

                var expected = ThreadCount * MessagesPerThread;
                Assert.That(lines.Length, Is.EqualTo(expected),
                    "Lines were lost or duplicated — concurrent appends were not serialized.");

                // "[HH:mm:ss.fff] [Info] [GameLoggerTests] tNN-mNNN" and nothing else. A torn
                // buffer splices two messages into one line, which fails this outright.
                var wellFormed = new Regex(
                    @"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] \[Info\] \[" + TestCategory + @"\] t\d{2}-m\d{3}$");

                var malformed = lines.Where(l => !wellFormed.IsMatch(l)).Take(3).ToArray();
                Assert.That(malformed, Is.Empty,
                    $"Torn or interleaved lines found, e.g.: {string.Join(" || ", malformed)}");

                // Every message exactly once, so nothing was silently overwritten.
                Assert.That(lines.Distinct().Count(), Is.EqualTo(expected),
                    "Duplicate lines — two threads shared a buffer.");
            }
            finally
            {
                GameLogger.Initialize(writeToFile: false);
                try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
            }
        }
    }
}
