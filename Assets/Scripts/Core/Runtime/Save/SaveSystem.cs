using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// Slot-based save system with JSON serialization, optional AES-256 obfuscation,
    /// format versioning with registerable migrations, crash-safe atomic writes, and
    /// metadata (timestamps).
    ///
    /// Saves live in <c>Application.persistentDataPath/Saves</c>. Each slot occupies up to
    /// three files: <c>slot.sav</c> (current), <c>slot.bak</c> (the previous good save, kept
    /// automatically), and <c>slot.tmp</c> (a scratch file that only exists mid-write).
    ///
    /// Usage:
    /// <code>
    /// var save = ServiceLocator.Get&lt;SaveSystem&gt;();
    /// save.Save("slot1", playerData);
    /// var data = save.Load&lt;PlayerData&gt;("slot1");
    /// </code>
    ///
    /// <b>Crash safety.</b> A write goes to <c>slot.tmp</c>, is flushed to the physical device,
    /// and is then swapped onto <c>slot.sav</c> with <see cref="File.Replace(string, string, string)"/>,
    /// which demotes the outgoing file to <c>slot.bak</c>. Losing power at any point leaves either
    /// the previous save or the new one readable — never a half-written one. If the primary file
    /// cannot be read or parsed, <see cref="Load{T}"/> falls back to <c>slot.bak</c> and says so.
    ///
    /// <b>Versioning and migration.</b> Files carry the schema version they were written with
    /// (<see cref="CurrentSaveVersion"/>). When a file's version differs, the system walks
    /// migrations registered through <see cref="RegisterMigration"/> to bring the payload up to
    /// date. If no path exists, the load is <i>refused</i> with an error — it never deserializes a
    /// foreign-version payload, because doing so silently produces wrong data rather than a failure.
    /// Register migrations at boot, before any load.
    ///
    /// <b>JsonUtility constraints (important when designing save models).</b> Serialization uses
    /// <see cref="JsonUtility"/>, which is Unity's serializer, not a general JSON library. It cannot
    /// round-trip:
    /// <list type="bullet">
    /// <item><description><c>Dictionary&lt;K,V&gt;</c> or any other associative container — use
    /// parallel <c>List&lt;T&gt;</c> fields, or a <c>List</c> of key/value structs, and rebuild the
    /// dictionary after load.</description></item>
    /// <item><description>Polymorphism — a field typed as a base class is written and read as that
    /// base class; derived fields are lost. Store a discriminator and reconstruct manually.</description></item>
    /// <item><description><c>Nullable&lt;T&gt;</c> (<c>int?</c>, <c>float?</c>) — model "absent" with
    /// a sentinel value or a companion <c>bool</c>.</description></item>
    /// <item><description>Top-level arrays or bare primitives — the root object passed to
    /// <see cref="Save{T}"/> must be a class or struct with fields; wrap collections in a container
    /// type.</description></item>
    /// <item><description>Properties, <c>static</c> fields, <c>const</c> fields, and
    /// <c>readonly</c> fields — only public instance fields, or private ones marked
    /// <c>[SerializeField]</c>, on a <c>[Serializable]</c> type are persisted.</description></item>
    /// </list>
    /// A <c>null</c> string or list is written as empty rather than null, so a loaded object never
    /// distinguishes "was null" from "was empty".
    ///
    /// <b>Encryption threat model — read before relying on it.</b> The key comes from
    /// <c>_saveEncryptionKey</c>, a <c>[SerializeField]</c> on the <c>Bootstrapper</c> scene object,
    /// so it ships inside the build and is recoverable in minutes with any asset ripper or a strings
    /// dump. The cipher is also unauthenticated (AES-CBC, no MAC), so tampering shows up as a parse
    /// failure at best. Treat this as <i>anti-tamper friction</i> that stops a player editing a save
    /// in Notepad. It is not security, it does not protect anything valuable, and it must never be
    /// used to guard server-authoritative state or anything a cheater profits from.
    ///
    /// <b>Threading.</b> <see cref="SaveAsync{T}"/> and <see cref="LoadAsync{T}"/> move file I/O and
    /// cryptography onto a worker thread while keeping <see cref="JsonUtility"/> calls, migrations,
    /// event publishing, and logging on the caller's thread. Await them from the main thread —
    /// Unity's synchronization context then resumes the continuation there. Never block on them
    /// (<c>.Result</c>, <c>.Wait()</c>, <c>.GetAwaiter().GetResult()</c>) from the main thread: the
    /// continuation needs that thread, and blocking it deadlocks the process. The synchronous
    /// <see cref="Save{T}"/> / <see cref="Load{T}"/> pair still blocks the caller, and a large save
    /// will hitch the frame.
    /// </summary>
    public class SaveSystem
    {
        /// <summary>Subdirectory under persistentDataPath where save files live.</summary>
        private const string SaveDir = "Saves";

        /// <summary>File extension for save files.</summary>
        private const string Extension = ".sav";

        /// <summary>File extension for the scratch file written before an atomic swap.</summary>
        private const string TempExtension = ".tmp";

        /// <summary>File extension for the previous save, retained automatically on every write.</summary>
        private const string BackupExtension = ".bak";

        /// <summary>
        /// Schema version stamped into every file this build writes.
        /// Increment it when the save data schema changes, and register a
        /// <see cref="RegisterMigration"/> step from the old version to the new one.
        /// </summary>
        public const int CurrentSaveVersion = 1;

        /// <summary>Upper bound on migration steps for one load, to break accidental cycles.</summary>
        private const int MaxMigrationSteps = 32;

        /// <summary>Whether AES obfuscation is active for read/write.</summary>
        private readonly bool _useEncryption;

        /// <summary>The 32-byte AES key derived from the constructor argument, or null when disabled.</summary>
        private readonly byte[] _keyBytes;

        /// <summary>
        /// Save directory resolved once at construction. <c>Application.persistentDataPath</c> is a
        /// main-thread-only property, so it is cached here to let the async paths build file paths
        /// on a worker thread.
        /// </summary>
        private readonly string _saveDirectory;

        /// <summary>Migration steps keyed by the version they upgrade <i>from</i>.</summary>
        private readonly Dictionary<int, MigrationStep> _migrations = new Dictionary<int, MigrationStep>();

        /// <summary>
        /// Internal wrapper serialized to disk. Contains version, timestamp, and the
        /// JSON-serialized user data as a nested string.
        /// </summary>
        [Serializable]
        private class SaveWrapper
        {
            public int Version;
            public string Timestamp;
            public string Data;
        }

        /// <summary>One registered upgrade hop: the version it produces, and the transform to run.</summary>
        private readonly struct MigrationStep
        {
            /// <summary>Version the payload carries after <see cref="Migrate"/> runs.</summary>
            public readonly int To;

            /// <summary>Transform from the old payload JSON to the new payload JSON.</summary>
            public readonly Func<string, string> Migrate;

            public MigrationStep(int to, Func<string, string> migrate)
            {
                To = to;
                Migrate = migrate;
            }
        }

        /// <summary>
        /// Result of reading one file from disk: the decrypted wrapper JSON, or a reason it is
        /// unusable. Produced off the main thread, so it carries no Unity API calls and no logging.
        /// </summary>
        private readonly struct RawFile
        {
            /// <summary>Whether a file was present at the path (regardless of whether it read cleanly).</summary>
            public readonly bool Exists;

            /// <summary>Decrypted wrapper JSON, valid only when <see cref="Error"/> is null.</summary>
            public readonly string WrapperJson;

            /// <summary>Why the file is unusable, or null when it read cleanly.</summary>
            public readonly string Error;

            private RawFile(bool exists, string wrapperJson, string error)
            {
                Exists = exists;
                WrapperJson = wrapperJson;
                Error = error;
            }

            /// <summary>A file that read and decrypted cleanly.</summary>
            public static RawFile Ok(string wrapperJson) => new RawFile(true, wrapperJson, null);

            /// <summary>A file that exists but could not be read or decrypted.</summary>
            public static RawFile Failed(string error) => new RawFile(true, null, error);

            /// <summary>No file at the path.</summary>
            public static RawFile Missing() => new RawFile(false, null, "file does not exist");
        }

        /// <summary>
        /// Create the save system. Pass an encryption key to enable AES-256 obfuscation,
        /// or null/empty to store saves as plaintext JSON. Read the encryption threat model
        /// in the class summary before treating the key as protection.
        /// </summary>
        /// <param name="encryptionKey">Optional AES key. Must be at least 1 character; padded with spaces or trimmed to 32 bytes internally.</param>
        public SaveSystem(string encryptionKey = null)
        {
            _useEncryption = !string.IsNullOrEmpty(encryptionKey);
            _keyBytes = _useEncryption ? DeriveKey(encryptionKey) : null;
            _saveDirectory = Path.Combine(Application.persistentDataPath, SaveDir);
            Directory.CreateDirectory(_saveDirectory);
        }

        // ─── Core API ───────────────────────────────────────────────

        /// <summary>
        /// Serialize and write data to a named save slot, atomically. The previous contents of the
        /// slot are retained as <c>slot.bak</c>, so an interrupted write can never destroy both.
        /// Publishes <see cref="OnSaveCompletedEvent"/> on success or failure.
        /// This call blocks; prefer <see cref="SaveAsync{T}"/> during gameplay.
        /// </summary>
        /// <typeparam name="T">Any serializable type (fields must be public or [SerializeField]).</typeparam>
        /// <param name="slotName">The save slot name (becomes the file name).</param>
        /// <param name="data">The data object to serialize.</param>
        /// <returns>True if the save succeeded.</returns>
        public bool Save<T>(string slotName, T data)
        {
            try
            {
                var slot = SanitizeSlotName(slotName);
                var content = BuildFileContent(data);

                var fallbackReason = WriteSlotAtomic(slot, content);
                if (fallbackReason != null)
                    GameLogger.LogWarning("Save",
                        $"Atomic replace unavailable ({fallbackReason}); used copy-and-move fallback");

                EventBus.Publish(new OnSaveCompletedEvent
                {
                    SlotIndex = slotName.GetHashCode(),
                    Success = true
                });

                GameLogger.Log("Save", $"Saved to slot '{slotName}'");
                return true;
            }
            catch (Exception ex)
            {
                GameLogger.LogError("Save", $"Save failed: {ex.Message}");
                EventBus.Publish(new OnSaveCompletedEvent { Success = false });
                return false;
            }
        }

        /// <summary>
        /// Asynchronous <see cref="Save{T}"/>. Serialization happens on the calling thread;
        /// encryption and file I/O run on a worker thread. Await from the main thread so the
        /// completion event and logging are published there — and never block on the returned task
        /// from the main thread, which deadlocks (see the threading note on the class).
        /// </summary>
        /// <typeparam name="T">Any serializable type (fields must be public or [SerializeField]).</typeparam>
        /// <param name="slotName">The save slot name (becomes the file name).</param>
        /// <param name="data">The data object to serialize.</param>
        /// <returns>A task producing true if the save succeeded.</returns>
        public async Task<bool> SaveAsync<T>(string slotName, T data)
        {
            string slot;
            string content;

            try
            {
                slot = SanitizeSlotName(slotName);
                content = BuildFileContent(data);
            }
            catch (Exception ex)
            {
                GameLogger.LogError("Save", $"Save failed: {ex.Message}");
                EventBus.Publish(new OnSaveCompletedEvent { Success = false });
                return false;
            }

            try
            {
                var fallbackReason = await Task.Run(() => WriteSlotAtomic(slot, content));
                if (fallbackReason != null)
                    GameLogger.LogWarning("Save",
                        $"Atomic replace unavailable ({fallbackReason}); used copy-and-move fallback");

                EventBus.Publish(new OnSaveCompletedEvent
                {
                    SlotIndex = slotName.GetHashCode(),
                    Success = true
                });

                GameLogger.Log("Save", $"Saved to slot '{slotName}' (async)");
                return true;
            }
            catch (Exception ex)
            {
                GameLogger.LogError("Save", $"Save failed: {ex.Message}");
                EventBus.Publish(new OnSaveCompletedEvent { Success = false });
                return false;
            }
        }

        /// <summary>
        /// Load and deserialize data from a named save slot, falling back to the slot's backup file
        /// when the primary is unreadable. Returns a default-constructed instance when the slot does
        /// not exist, both files are unusable, or the file's version has no registered migration
        /// path — the failure is always logged. Use <see cref="TryLoad{T}"/> when the caller needs to
        /// tell "loaded" apart from "defaulted".
        /// </summary>
        /// <typeparam name="T">The data type to deserialize into.</typeparam>
        /// <param name="slotName">The save slot name.</param>
        /// <returns>The deserialized data, or a new default instance on failure.</returns>
        public T Load<T>(string slotName) where T : new()
        {
            TryLoad<T>(slotName, out var data);
            return data;
        }

        /// <summary>
        /// Attempt to load a slot, reporting whether the data is genuine or a default fallback.
        /// Reads <c>slot.sav</c> first and <c>slot.bak</c> second; refuses, loudly, to deserialize a
        /// payload whose version cannot be migrated to <see cref="CurrentSaveVersion"/>.
        /// </summary>
        /// <typeparam name="T">The data type to deserialize into.</typeparam>
        /// <param name="slotName">The save slot name.</param>
        /// <param name="data">Receives the loaded data, or a new default instance on failure.</param>
        /// <returns>True only when real save data was loaded.</returns>
        public bool TryLoad<T>(string slotName, out T data) where T : new()
        {
            data = new T();

            string slot;
            try
            {
                slot = SanitizeSlotName(slotName);
            }
            catch (Exception ex)
            {
                GameLogger.LogError("Save", $"Load failed: {ex.Message}");
                return false;
            }

            var primary = ReadSlotFile(GetPathFor(slot, Extension));
            if (TryDeserialize(primary, out data, out var primaryError))
                return true;

            var backup = ReadSlotFile(GetPathFor(slot, BackupExtension));
            if (TryDeserialize(backup, out data, out var backupError))
            {
                GameLogger.LogWarning("Save",
                    $"Slot '{slotName}' main file unusable ({primaryError}) — recovered from backup");
                return true;
            }

            data = new T();
            return ReportLoadFailure(slotName, primary, backup, primaryError, backupError);
        }

        /// <summary>
        /// Asynchronous <see cref="Load{T}"/>. File I/O and decryption run on a worker thread;
        /// JSON parsing, migration, and logging run on the caller's thread after the await.
        /// The backup file is only touched if the primary fails. Never block on the returned task
        /// from the main thread, which deadlocks (see the threading note on the class).
        /// </summary>
        /// <typeparam name="T">The data type to deserialize into.</typeparam>
        /// <param name="slotName">The save slot name.</param>
        /// <returns>A task producing the deserialized data, or a new default instance on failure.</returns>
        public async Task<T> LoadAsync<T>(string slotName) where T : new()
        {
            string slot;
            try
            {
                slot = SanitizeSlotName(slotName);
            }
            catch (Exception ex)
            {
                GameLogger.LogError("Save", $"Load failed: {ex.Message}");
                return new T();
            }

            var primaryPath = GetPathFor(slot, Extension);
            var primary = await Task.Run(() => ReadSlotFile(primaryPath));
            if (TryDeserialize<T>(primary, out var data, out var primaryError))
                return data;

            var backupPath = GetPathFor(slot, BackupExtension);
            var backup = await Task.Run(() => ReadSlotFile(backupPath));
            if (TryDeserialize<T>(backup, out data, out var backupError))
            {
                GameLogger.LogWarning("Save",
                    $"Slot '{slotName}' main file unusable ({primaryError}) — recovered from backup");
                return data;
            }

            ReportLoadFailure(slotName, primary, backup, primaryError, backupError);
            return new T();
        }

        /// <summary>
        /// Register an upgrade step for save payloads written by an older build. The function
        /// receives the payload JSON as written at <paramref name="fromVersion"/> and returns the
        /// equivalent JSON at <paramref name="toVersion"/>. Steps chain, so 1→2 and 2→3 together
        /// load a version-1 file into a version-3 build. Register everything at boot, before the
        /// first load; a version with no registered step is refused rather than misread.
        /// </summary>
        /// <param name="fromVersion">Version of the payload this step accepts.</param>
        /// <param name="toVersion">Version the payload carries afterwards. Must be greater than <paramref name="fromVersion"/> and no greater than <see cref="CurrentSaveVersion"/>.</param>
        /// <param name="migrate">Transform from old payload JSON to new payload JSON. Must not return null.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="migrate"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the version pair cannot form part of a chain ending at <see cref="CurrentSaveVersion"/>.</exception>
        public void RegisterMigration(int fromVersion, int toVersion, Func<string, string> migrate)
        {
            if (migrate == null)
                throw new ArgumentNullException(nameof(migrate));

            if (toVersion <= fromVersion)
                throw new ArgumentOutOfRangeException(nameof(toVersion),
                    $"Migration target ({toVersion}) must be greater than its source ({fromVersion}).");

            if (toVersion > CurrentSaveVersion)
                throw new ArgumentOutOfRangeException(nameof(toVersion),
                    $"Migration target ({toVersion}) is beyond this build's save version ({CurrentSaveVersion}).");

            if (_migrations.ContainsKey(fromVersion))
                GameLogger.LogWarning("Save",
                    $"Replacing the migration already registered from save version {fromVersion}");

            _migrations[fromVersion] = new MigrationStep(toVersion, migrate);
            GameLogger.Log("Save", $"Registered save migration {fromVersion} -> {toVersion}");
        }

        /// <summary>
        /// Check whether a save exists for the given slot name. True when either the primary file or
        /// its backup is present, so a slot left with only a backup by an interrupted write still
        /// counts as existing.
        /// </summary>
        /// <param name="slotName">The slot name to check.</param>
        /// <returns>True if a save file or its backup exists on disk.</returns>
        public bool SlotExists(string slotName)
        {
            var slot = SanitizeSlotName(slotName);
            return File.Exists(GetPathFor(slot, Extension)) ||
                   File.Exists(GetPathFor(slot, BackupExtension));
        }

        /// <summary>
        /// Delete a save slot from disk, including its backup and any leftover scratch file.
        /// </summary>
        /// <param name="slotName">The slot name to delete.</param>
        /// <returns>True if a save file or backup was found and deleted.</returns>
        public bool DeleteSlot(string slotName)
        {
            var slot = SanitizeSlotName(slotName);

            var deleted = DeleteIfExists(GetPathFor(slot, Extension));
            deleted |= DeleteIfExists(GetPathFor(slot, BackupExtension));
            DeleteIfExists(GetPathFor(slot, TempExtension));

            if (deleted)
                GameLogger.Log("Save", $"Deleted slot '{slotName}'");

            return deleted;
        }

        /// <summary>
        /// Get the names of all existing save slots, including slots that currently only have a
        /// backup file. Names are returned in ordinal sort order.
        /// </summary>
        /// <returns>Array of slot names (file names without extension).</returns>
        public string[] GetAllSlots()
        {
            if (!Directory.Exists(_saveDirectory))
                return Array.Empty<string>();

            var slots = new HashSet<string>(StringComparer.Ordinal);
            CollectSlotNames(slots, Extension);
            CollectSlotNames(slots, BackupExtension);

            var result = new string[slots.Count];
            slots.CopyTo(result);
            Array.Sort(result, StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// Read metadata for a slot without deserializing the full data payload. Falls back to the
        /// backup file when the primary is unreadable. The version reported is the version stored in
        /// the file, before any migration.
        /// </summary>
        /// <param name="slotName">The slot name to inspect.</param>
        /// <returns>Nullable tuple of (UTC timestamp, stored version), or null if no readable file exists.</returns>
        public (DateTime timestamp, int version)? GetSlotInfo(string slotName)
        {
            try
            {
                var slot = SanitizeSlotName(slotName);

                return ReadSlotInfo(GetPathFor(slot, Extension)) ??
                       ReadSlotInfo(GetPathFor(slot, BackupExtension));
            }
            catch { return null; }
        }

        /// <summary>
        /// Convenience method for storing small values (settings, last-played level)
        /// via PlayerPrefs instead of the file system.
        /// </summary>
        /// <param name="key">The PlayerPrefs key (automatically prefixed with "qs_").</param>
        /// <param name="value">The string value to store.</param>
        public static void QuickSave(string key, string value) =>
            PlayerPrefs.SetString($"qs_{key}", value);

        /// <summary>
        /// Load a value previously stored with <see cref="QuickSave"/>.
        /// </summary>
        /// <param name="key">The PlayerPrefs key.</param>
        /// <param name="defaultValue">Value returned if the key doesn't exist.</param>
        /// <returns>The stored string, or <paramref name="defaultValue"/>.</returns>
        public static string QuickLoad(string key, string defaultValue = "") =>
            PlayerPrefs.GetString($"qs_{key}", defaultValue);

        // ─── Serialization ──────────────────────────────────────────

        /// <summary>
        /// Wrap and serialize user data into the exact bytes destined for disk.
        /// Calls <see cref="JsonUtility"/>, so it must run on the main thread.
        /// </summary>
        private string BuildFileContent<T>(T data)
        {
            var json = JsonUtility.ToJson(data, true);

            var wrapper = new SaveWrapper
            {
                Version = CurrentSaveVersion,
                Timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Data = json
            };

            var wrapperJson = JsonUtility.ToJson(wrapper);
            return _useEncryption ? Encrypt(wrapperJson) : wrapperJson;
        }

        /// <summary>
        /// Parse one candidate file into user data: wrapper JSON, then migration, then payload.
        /// Calls <see cref="JsonUtility"/> and runs migration hooks, so it must run on the main thread.
        /// </summary>
        /// <returns>True on success; otherwise false with <paramref name="error"/> describing why.</returns>
        private bool TryDeserialize<T>(RawFile file, out T data, out string error) where T : new()
        {
            data = new T();

            if (file.Error != null)
            {
                error = file.Error;
                return false;
            }

            SaveWrapper wrapper;
            try
            {
                wrapper = JsonUtility.FromJson<SaveWrapper>(file.WrapperJson);
            }
            catch (Exception ex)
            {
                error = $"wrapper JSON is unreadable ({ex.Message})";
                return false;
            }

            if (wrapper == null || wrapper.Data == null)
            {
                error = "content is not a save file";
                return false;
            }

            if (!TryMigrate(wrapper, out error))
                return false;

            try
            {
                data = JsonUtility.FromJson<T>(wrapper.Data);
            }
            catch (Exception ex)
            {
                data = new T();
                error = $"payload JSON is unreadable ({ex.Message})";
                return false;
            }

            if (ReferenceEquals(data, null))
            {
                data = new T();
                error = "payload deserialized to null";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Walk registered migrations until the wrapper reaches <see cref="CurrentSaveVersion"/>.
        /// Refuses rather than guessing when a hop is missing, the file is from a newer build, a
        /// hook throws, or the chain fails to terminate.
        /// </summary>
        /// <returns>True when the wrapper is at the current version; otherwise false with a reason.</returns>
        private bool TryMigrate(SaveWrapper wrapper, out string error)
        {
            var steps = 0;

            while (wrapper.Version != CurrentSaveVersion)
            {
                if (wrapper.Version > CurrentSaveVersion)
                {
                    error = $"file was written by a newer build (version {wrapper.Version}, " +
                            $"this build reads {CurrentSaveVersion}) and cannot be downgraded";
                    return false;
                }

                if (!_migrations.TryGetValue(wrapper.Version, out var step))
                {
                    error = $"no migration registered from save version {wrapper.Version} " +
                            $"(this build reads {CurrentSaveVersion}) — " +
                            $"call RegisterMigration({wrapper.Version}, ..., ...) at boot";
                    return false;
                }

                string migrated;
                try
                {
                    migrated = step.Migrate(wrapper.Data);
                }
                catch (Exception ex)
                {
                    error = $"migration {wrapper.Version} -> {step.To} threw: {ex.Message}";
                    return false;
                }

                if (migrated == null)
                {
                    error = $"migration {wrapper.Version} -> {step.To} returned null";
                    return false;
                }

                GameLogger.Log("Save", $"Migrated save data {wrapper.Version} -> {step.To}");
                wrapper.Data = migrated;
                wrapper.Version = step.To;

                if (++steps > MaxMigrationSteps)
                {
                    error = $"migration chain exceeded {MaxMigrationSteps} steps — check for a cycle";
                    return false;
                }
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Log why a load produced no data: a plain warning when the slot simply does not exist,
        /// an error naming both failures when files were present but unusable.
        /// </summary>
        /// <returns>Always false, so callers can <c>return</c> it directly.</returns>
        private static bool ReportLoadFailure(
            string slotName, RawFile primary, RawFile backup, string primaryError, string backupError)
        {
            if (!primary.Exists && !backup.Exists)
            {
                GameLogger.LogWarning("Save", $"Slot '{slotName}' not found");
                return false;
            }

            GameLogger.LogError("Save",
                $"Refusing to load slot '{slotName}': main file — {primaryError}; backup — {backupError}");
            return false;
        }

        // ─── File I/O ───────────────────────────────────────────────

        /// <summary>
        /// Write one slot atomically: full write to <c>slot.tmp</c>, flushed to the device, then a
        /// swap onto <c>slot.sav</c> that demotes the outgoing file to <c>slot.bak</c>. An
        /// interruption at any point leaves at least one complete file behind.
        ///
        /// Thread-safe by construction: no Unity API calls and no logging, so it can run on a worker
        /// thread. The caller logs the returned fallback reason on the main thread.
        /// </summary>
        /// <param name="slot">Already-sanitized slot name.</param>
        /// <param name="content">Exact file content to write.</param>
        /// <returns>Null when the atomic swap was used, or the reason it was unavailable.</returns>
        private string WriteSlotAtomic(string slot, string content)
        {
            var path = GetPathFor(slot, Extension);
            var tempPath = GetPathFor(slot, TempExtension);
            var backupPath = GetPathFor(slot, BackupExtension);

            Directory.CreateDirectory(_saveDirectory);
            WriteAllTextDurable(tempPath, content);

            if (!File.Exists(path))
            {
                // First write for this slot — nothing to demote to backup.
                File.Move(tempPath, path);
                return null;
            }

            try
            {
                File.Replace(tempPath, path, backupPath, true);
                return null;
            }
            catch (Exception ex)
            {
                // File.Replace is unavailable on some platforms and filesystems. The fallback is
                // ordered so every intermediate state still has one complete, loadable file:
                // backup is refreshed first, the primary is only removed once it exists.
                File.Copy(path, backupPath, true);
                File.Delete(path);
                File.Move(tempPath, path);
                return ex.Message;
            }
        }

        /// <summary>
        /// Write text and force it to the physical device before returning, so a power loss after
        /// this call cannot leave the file half-written in an OS cache.
        /// </summary>
        private static void WriteAllTextDurable(string path, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);

            using var stream = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);

            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        /// <summary>
        /// Read and decrypt one file into wrapper JSON, converting every failure into a
        /// <see cref="RawFile"/> reason rather than an exception.
        ///
        /// Thread-safe by construction: no Unity API calls and no logging, so it can run on a worker
        /// thread.
        /// </summary>
        private RawFile ReadSlotFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return RawFile.Missing();

                var content = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(content))
                    return RawFile.Failed("file is empty");

                return RawFile.Ok(_useEncryption ? Decrypt(content) : content);
            }
            catch (Exception ex)
            {
                return RawFile.Failed($"unreadable ({ex.Message})");
            }
        }

        /// <summary>Read timestamp and stored version from one file, or null if it is unusable.</summary>
        private (DateTime timestamp, int version)? ReadSlotInfo(string path)
        {
            var file = ReadSlotFile(path);
            if (file.Error != null)
                return null;

            try
            {
                var wrapper = JsonUtility.FromJson<SaveWrapper>(file.WrapperJson);
                if (wrapper == null || string.IsNullOrEmpty(wrapper.Timestamp))
                    return null;

                var timestamp = DateTime.Parse(
                    wrapper.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

                return (timestamp, wrapper.Version);
            }
            catch { return null; }
        }

        /// <summary>Delete a file if it is there, swallowing races and permission errors.</summary>
        /// <returns>True if a file was deleted.</returns>
        private static bool DeleteIfExists(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return false;

                File.Delete(path);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Add every slot name carrying the given extension to the set.</summary>
        private void CollectSlotNames(HashSet<string> slots, string extension)
        {
            foreach (var file in Directory.GetFiles(_saveDirectory, $"*{extension}"))
            {
                // Guard against the Windows short-name quirk where "*.sav" can also match ".savX".
                if (!string.Equals(Path.GetExtension(file), extension, StringComparison.OrdinalIgnoreCase))
                    continue;

                slots.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        // ─── Internals ──────────────────────────────────────────────

        /// <summary>Build the full file path for a sanitized slot name and one of its extensions.</summary>
        private string GetPathFor(string sanitizedSlot, string extension) =>
            Path.Combine(_saveDirectory, sanitizedSlot + extension);

        /// <summary>
        /// Sanitize a slot name to prevent path traversal attacks.
        /// Strips directory separators, parent-directory references, and invalid filename characters.
        /// </summary>
        private static string SanitizeSlotName(string slotName)
        {
            if (string.IsNullOrWhiteSpace(slotName))
                throw new ArgumentException("Slot name cannot be null or empty.", nameof(slotName));

            // Strip path separators and parent directory references
            slotName = slotName.Replace("/", "").Replace("\\", "").Replace("..", "");

            // Remove any remaining invalid filename characters
            foreach (char c in Path.GetInvalidFileNameChars())
                slotName = slotName.Replace(c.ToString(), "");

            if (string.IsNullOrWhiteSpace(slotName))
                throw new ArgumentException("Slot name is invalid after sanitization.", nameof(slotName));

            return slotName;
        }

        /// <summary>
        /// Derive the 32-byte AES key: UTF-8 bytes of the supplied key, space-padded or truncated.
        /// This is deliberately the same derivation earlier builds used, so existing save files stay
        /// readable. It is not a KDF and offers no protection beyond obfuscation.
        /// </summary>
        private static byte[] DeriveKey(string encryptionKey)
        {
            var raw = Encoding.UTF8.GetBytes(encryptionKey);
            var key = new byte[32];

            for (int i = 0; i < key.Length; i++)
                key[i] = i < raw.Length ? raw[i] : (byte)' ';

            return key;
        }

        /// <summary>Encrypt plaintext using AES-256-CBC with a random IV prepended to the output.</summary>
        private string Encrypt(string plainText)
        {
            using var aes = Aes.Create();
            aes.Key = _keyBytes;
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            // Prepend IV to cipher so it can be extracted during decryption
            var result = new byte[aes.IV.Length + cipherBytes.Length];
            Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
            Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

            return Convert.ToBase64String(result);
        }

        /// <summary>Decrypt AES-256-CBC ciphertext. Expects a 16-byte IV prepended to the data.</summary>
        private string Decrypt(string cipherText)
        {
            var fullBytes = Convert.FromBase64String(cipherText);
            if (fullBytes.Length <= 16)
                throw new CryptographicException("Encrypted save is too short to contain an IV and a payload.");

            using var aes = Aes.Create();
            aes.Key = _keyBytes;

            // Extract the IV from the first 16 bytes
            var iv = new byte[16];
            Buffer.BlockCopy(fullBytes, 0, iv, 0, 16);
            aes.IV = iv;

            var cipherBytes = new byte[fullBytes.Length - 16];
            Buffer.BlockCopy(fullBytes, 16, cipherBytes, 0, cipherBytes.Length);

            using var decryptor = aes.CreateDecryptor();
            var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            return Encoding.UTF8.GetString(plainBytes);
        }
    }
}
