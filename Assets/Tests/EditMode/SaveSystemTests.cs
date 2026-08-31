using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace Skylotus.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="SaveSystem"/> (WP-14): plaintext and encrypted round
    /// trips, atomic write and backup rotation, recovery from a truncated primary, migration
    /// refusal and success, the async API, deletion, and slot-name sanitization including
    /// path-traversal attempts.
    ///
    /// These cases are the WP-14 batchmode self-check, ported to NUnit. They write into the real
    /// <c>Application.persistentDataPath/Saves</c> directory — the same one the game uses — under
    /// slot names prefixed <c>wp12_</c>, and delete every file they create on both sides of each
    /// test.
    /// </summary>
    [TestFixture]
    public class SaveSystemTests
    {
        /// <summary>Slot name used by every test in this fixture.</summary>
        private const string Slot = "wp12_savetests";

        /// <summary>Slot name used by the path-traversal case, before sanitization.</summary>
        private const string TraversalSlot = "../wp12_escaped";

        /// <summary>Sanitized form the traversal slot is expected to collapse to.</summary>
        private const string TraversalSanitized = "wp12_escaped";

        /// <summary>A trivially serializable payload for the round-trip checks.</summary>
        [Serializable]
        public class SaveProbe
        {
            /// <summary>An integer field, so a wrong load is visible as a wrong number.</summary>
            public int Score;

            /// <summary>A string field, so encryption can be checked for plaintext leakage.</summary>
            public string Name;
        }

        /// <summary>Directory the save system writes into.</summary>
        private string _saveDirectory;

        /// <summary>Resolve the save directory and start from a clean slate.</summary>
        [SetUp]
        public void SetUp()
        {
            _saveDirectory = Path.Combine(Application.persistentDataPath, "Saves");
            GameLogger.SetCategoryLevel("Save", LogLevel.Off);
            Cleanup();
        }

        /// <summary>Remove every file this fixture may have written.</summary>
        [TearDown]
        public void TearDown()
        {
            Cleanup();
            GameLogger.SetCategoryLevel("Save", LogLevel.Debug);
        }

        // ─── Round trips ────────────────────────────────────────────

        /// <summary>A plaintext save round-trips and leaves no temp file or premature backup.</summary>
        [Test]
        public void Save_ThenLoad_RoundTripsPlaintext()
        {
            var system = new SaveSystem();

            Assert.IsTrue(system.Save(Slot, new SaveProbe { Score = 42, Name = "alpha" }),
                "Save should report success.");

            var loaded = system.Load<SaveProbe>(Slot);

            Assert.AreEqual(42, loaded.Score);
            Assert.AreEqual("alpha", loaded.Name);
            Assert.IsFalse(File.Exists(PathFor(".tmp")), "No temp file may be left behind.");
            Assert.IsFalse(File.Exists(PathFor(".bak")), "The first write has nothing to back up.");
        }

        /// <summary>An encrypted save round-trips and its file does not contain the plaintext payload.</summary>
        [Test]
        public void Save_ThenLoad_RoundTripsEncrypted()
        {
            var system = new SaveSystem("wp12-test-key");

            Assert.IsTrue(system.Save(Slot, new SaveProbe { Score = 7, Name = "bravo" }));

            string raw = File.ReadAllText(PathFor(".sav"));
            StringAssert.DoesNotContain("bravo", raw,
                "An encrypted save must not carry its payload in the clear.");

            var loaded = system.Load<SaveProbe>(Slot);
            Assert.AreEqual(7, loaded.Score);
            Assert.AreEqual("bravo", loaded.Name);
        }

        /// <summary>A file written with one key cannot be read with another.</summary>
        [Test]
        public void Load_WithTheWrongKey_DoesNotReturnTheData()
        {
            new SaveSystem("key-one").Save(Slot, new SaveProbe { Score = 11, Name = "charlie" });

            Assert.IsFalse(new SaveSystem("key-two").TryLoad<SaveProbe>(Slot, out var loaded),
                "Decrypting with the wrong key must fail rather than return garbage.");
            Assert.AreEqual(0, loaded.Score);
        }

        // ─── Atomic write and backup ────────────────────────────────

        /// <summary>The second write rotates the previous save into <c>.bak</c>.</summary>
        [Test]
        public void Save_Twice_RotatesThePreviousFileIntoBackup()
        {
            var system = new SaveSystem();

            system.Save(Slot, new SaveProbe { Score = 1, Name = "first" });
            system.Save(Slot, new SaveProbe { Score = 2, Name = "second" });

            Assert.IsTrue(File.Exists(PathFor(".bak")), "A backup must exist after the second write.");
            Assert.AreEqual(2, system.Load<SaveProbe>(Slot).Score, "The primary holds the newest save.");
        }

        /// <summary>
        /// WP-14's headline criterion: a save interrupted mid-write leaves either the previous
        /// save or the new one intact, never a corrupt file. Simulated by truncating the primary
        /// after a successful rotation — the load falls back to the backup.
        /// </summary>
        [Test]
        public void Load_TruncatedPrimary_RecoversFromBackup()
        {
            var system = new SaveSystem();
            system.Save(Slot, new SaveProbe { Score = 1, Name = "first" });
            system.Save(Slot, new SaveProbe { Score = 2, Name = "second" });

            File.WriteAllText(PathFor(".sav"), "{\"Version\":1,\"Timestamp");

            Assert.IsTrue(system.TryLoad<SaveProbe>(Slot, out var recovered),
                "A truncated primary must still load from the backup.");
            Assert.AreEqual(1, recovered.Score);
            Assert.AreEqual("first", recovered.Name);
        }

        /// <summary>An interrupted swap that left only a backup still counts as an existing slot.</summary>
        [Test]
        public void BackupOnlySlot_ExistsIsListedAndLoads()
        {
            var system = new SaveSystem();
            system.Save(Slot, new SaveProbe { Score = 1, Name = "first" });
            system.Save(Slot, new SaveProbe { Score = 2, Name = "second" });

            File.Delete(PathFor(".sav"));

            Assert.IsTrue(system.SlotExists(Slot));
            Assert.IsTrue(system.TryLoad<SaveProbe>(Slot, out var loaded));
            Assert.AreEqual(1, loaded.Score);
            CollectionAssert.Contains(system.GetAllSlots(), Slot);
        }

        // ─── Missing slot ───────────────────────────────────────────

        /// <summary>An absent slot reports as absent and yields a default instance, not an exception.</summary>
        [Test]
        public void Load_MissingSlot_ReturnsDefaultAndReportsFailure()
        {
            var system = new SaveSystem();

            Assert.IsFalse(system.SlotExists(Slot));
            Assert.IsFalse(system.TryLoad<SaveProbe>(Slot, out var loaded));
            Assert.IsNotNull(loaded);
            Assert.AreEqual(0, loaded.Score);
            Assert.IsNull(system.GetSlotInfo(Slot));
        }

        // ─── Version mismatch and migration ─────────────────────────

        /// <summary>A file from an older schema with no registered migration is refused, not misread.</summary>
        [Test]
        public void Load_OlderVersionWithNoMigration_IsRefused()
        {
            WriteVersionedFile(0, "{\"OldScore\":9,\"Name\":\"legacy\"}");

            var system = new SaveSystem();

            Assert.IsFalse(system.TryLoad<SaveProbe>(Slot, out var refused),
                "A version with no migration path must fail loudly.");
            Assert.AreEqual(0, refused.Score, "A refused load returns a default instance, not garbage.");
        }

        /// <summary>The same file loads once a matching migration is registered.</summary>
        [Test]
        public void Load_OlderVersionWithMigration_Succeeds()
        {
            WriteVersionedFile(0, "{\"OldScore\":9,\"Name\":\"legacy\"}");

            var system = new SaveSystem();
            system.RegisterMigration(0, 1, json => json.Replace("OldScore", "Score"));

            Assert.IsTrue(system.TryLoad<SaveProbe>(Slot, out var migrated));
            Assert.AreEqual(9, migrated.Score);
            Assert.AreEqual("legacy", migrated.Name);
        }

        /// <summary>A file written by a newer build is refused — there is no forward migration.</summary>
        [Test]
        public void Load_NewerVersion_IsRefused()
        {
            WriteVersionedFile(99, "{\"Score\":5,\"Name\":\"future\"}");

            var system = new SaveSystem();
            system.RegisterMigration(0, 1, json => json);

            Assert.IsFalse(system.TryLoad<SaveProbe>(Slot, out _));
        }

        // ─── Async ──────────────────────────────────────────────────

        /// <summary>
        /// The async API round-trips. Driven from a worker thread on purpose: awaiting these on
        /// Unity's main thread needs that thread to be pumping continuations, which a synchronous
        /// NUnit test body never does — blocking on them from the main thread deadlocks, which is
        /// exactly what the API's own threading note warns about.
        /// </summary>
        [Test]
        public void SaveAsync_ThenLoadAsync_RoundTrips()
        {
            var system = new SaveSystem();

            var task = Task.Run(async () =>
            {
                if (!await system.SaveAsync(Slot, new SaveProbe { Score = 3, Name = "async" }))
                    return false;

                var loaded = await system.LoadAsync<SaveProbe>(Slot);
                return loaded.Score == 3 && loaded.Name == "async";
            });

            Assert.IsTrue(task.Wait(TimeSpan.FromSeconds(30)), "Async round trip did not finish in 30s.");
            Assert.AreEqual(TaskStatus.RanToCompletion, task.Status);
            Assert.IsTrue(task.Result);
        }

        // ─── Deletion ───────────────────────────────────────────────

        /// <summary>Deleting a slot removes both the save and its backup.</summary>
        [Test]
        public void DeleteSlot_RemovesSaveAndBackup()
        {
            var system = new SaveSystem();
            system.Save(Slot, new SaveProbe { Score = 1, Name = "first" });
            system.Save(Slot, new SaveProbe { Score = 4, Name = "doomed" });

            Assert.IsTrue(File.Exists(PathFor(".bak")), "Setup: a backup should exist.");
            Assert.IsTrue(system.DeleteSlot(Slot));

            Assert.IsFalse(File.Exists(PathFor(".sav")));
            Assert.IsFalse(File.Exists(PathFor(".bak")));
            Assert.IsFalse(system.SlotExists(Slot));
        }

        // ─── Slot-name sanitization ─────────────────────────────────

        /// <summary>
        /// A slot name attempting to escape the save directory is sanitized, and the file lands
        /// inside it. This is the security-relevant case: <c>"../escaped"</c> must not write to
        /// the parent directory.
        /// </summary>
        [Test]
        public void Save_PathTraversalSlotName_StaysInsideTheSaveDirectory()
        {
            var system = new SaveSystem();
            system.Save(TraversalSlot, new SaveProbe { Score = 1, Name = "x" });

            string escaped = Path.Combine(_saveDirectory, TraversalSanitized + ".sav");
            string outside = Path.GetFullPath(
                Path.Combine(_saveDirectory, "..", TraversalSanitized + ".sav"));

            Assert.IsTrue(File.Exists(escaped),
                "The sanitized slot should have been written inside the save directory.");
            Assert.IsFalse(File.Exists(outside),
                "Nothing may be written outside the save directory.");
        }

        /// <summary>Directory separators and other invalid characters are stripped from a slot name.</summary>
        [Test]
        public void Save_SlotNameWithSeparators_IsSanitizedAndStillRoundTrips()
        {
            var system = new SaveSystem();
            string dirty = "wp12" + Path.DirectorySeparatorChar + "sub:slot*name";

            Assert.IsTrue(system.Save(dirty, new SaveProbe { Score = 5, Name = "sanitized" }));

            var loaded = system.Load<SaveProbe>(dirty);
            Assert.AreEqual(5, loaded.Score);
            Assert.AreEqual("sanitized", loaded.Name);

            // Whatever it collapsed to, it must be a direct child of the save directory.
            foreach (var file in Directory.GetFiles(_saveDirectory, "wp12*"))
            {
                Assert.AreEqual(Path.GetFullPath(_saveDirectory).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetDirectoryName(Path.GetFullPath(file)),
                    $"{file} escaped the save directory.");
            }

            system.DeleteSlot(dirty);
        }

        // ─── Slot metadata ──────────────────────────────────────────

        /// <summary>A saved slot reports the current schema version and a recent timestamp.</summary>
        [Test]
        public void GetSlotInfo_ReportsVersionAndTimestamp()
        {
            var system = new SaveSystem();
            system.Save(Slot, new SaveProbe { Score = 1, Name = "meta" });

            var info = system.GetSlotInfo(Slot);

            Assert.IsNotNull(info);
            Assert.AreEqual(SaveSystem.CurrentSaveVersion, info.Value.version);
            Assert.Less((DateTime.UtcNow - info.Value.timestamp).Duration(), TimeSpan.FromMinutes(5),
                "The recorded timestamp should be from this test run.");
        }

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>Full path of one of this fixture's slot files.</summary>
        /// <param name="extension">File extension including the dot.</param>
        /// <returns>The absolute path.</returns>
        private string PathFor(string extension) => Path.Combine(_saveDirectory, Slot + extension);

        /// <summary>
        /// Hand-write a plaintext save file stamped with an arbitrary schema version, so the
        /// migration paths can be exercised without a build that actually wrote that version.
        /// </summary>
        /// <param name="version">Schema version to stamp into the wrapper.</param>
        /// <param name="payload">The payload JSON as that version would have written it.</param>
        private void WriteVersionedFile(int version, string payload)
        {
            Directory.CreateDirectory(_saveDirectory);

            string escaped = payload.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string wrapper =
                $"{{\"Version\":{version},\"Timestamp\":\"{DateTime.UtcNow:o}\",\"Data\":\"{escaped}\"}}";

            File.WriteAllText(PathFor(".sav"), wrapper);
        }

        /// <summary>Delete every file this fixture is allowed to create.</summary>
        private void Cleanup()
        {
            if (!Directory.Exists(_saveDirectory)) return;

            foreach (var file in Directory.GetFiles(_saveDirectory, "wp12*"))
            {
                try { File.Delete(file); }
                catch (IOException) { /* another process holds it; the next run will clear it */ }
            }
        }
    }
}
