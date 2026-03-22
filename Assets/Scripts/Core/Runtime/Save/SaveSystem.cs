using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// Slot-based save system with JSON serialization, AES-256 encryption,
    /// format versioning, and metadata (timestamps).
    ///
    /// Saves are stored as individual files in Application.persistentDataPath/Saves.
    /// Encryption is optional — pass a key to the constructor to enable it.
    ///
    /// Usage:
    /// <code>
    /// var save = ServiceLocator.Get&lt;SaveSystem&gt;();
    /// save.Save("slot1", playerData);
    /// var data = save.Load&lt;PlayerData&gt;("slot1");
    /// </code>
    /// </summary>
    public class SaveSystem
    {
        /// <summary>Subdirectory under persistentDataPath where save files live.</summary>
        private const string SaveDir = "Saves";

        /// <summary>File extension for save files.</summary>
        private const string Extension = ".sav";

        /// <summary>
        /// Increment this when the save data schema changes.
        /// A mismatch between file version and code version triggers a warning on load.
        /// </summary>
        private const int CurrentVersion = 1;

        /// <summary>AES encryption key (null/empty = encryption disabled).</summary>
        private string _encryptionKey;

        /// <summary>Whether AES encryption is active for read/write.</summary>
        private bool _useEncryption;

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

        /// <summary>
        /// Create the save system. Pass an encryption key to enable AES-256 encryption,
        /// or null/empty to store saves as plaintext JSON.
        /// </summary>
        /// <param name="encryptionKey">Optional AES key. Must be at least 1 character; padded/trimmed to 32 bytes internally.</param>
        public SaveSystem(string encryptionKey = null)
        {
            _useEncryption = !string.IsNullOrEmpty(encryptionKey);
            _encryptionKey = encryptionKey;
            Directory.CreateDirectory(GetSaveDirectory());
        }

        // ─── Core API ───────────────────────────────────────────────

        /// <summary>
        /// Serialize and write data to a named save slot.
        /// Publishes <see cref="OnSaveCompletedEvent"/> on success or failure.
        /// </summary>
        /// <typeparam name="T">Any serializable type (fields must be public or [SerializeField]).</typeparam>
        /// <param name="slotName">The save slot name (becomes the file name).</param>
        /// <param name="data">The data object to serialize.</param>
        /// <returns>True if the save succeeded.</returns>
        public bool Save<T>(string slotName, T data)
        {
            try
            {
                var json = JsonUtility.ToJson(data, true);

                var wrapper = new SaveWrapper
                {
                    Version = CurrentVersion,
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    Data = json
                };

                var wrapperJson = JsonUtility.ToJson(wrapper);
                var content = _useEncryption ? Encrypt(wrapperJson) : wrapperJson;

                File.WriteAllText(GetSlotPath(slotName), content);

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
        /// Load and deserialize data from a named save slot.
        /// Returns a default-constructed instance if the slot does not exist or loading fails.
        /// </summary>
        /// <typeparam name="T">The data type to deserialize into.</typeparam>
        /// <param name="slotName">The save slot name.</param>
        /// <returns>The deserialized data, or a new default instance on failure.</returns>
        public T Load<T>(string slotName) where T : new()
        {
            try
            {
                var path = GetSlotPath(slotName);

                if (!File.Exists(path))
                {
                    GameLogger.LogWarning("Save", $"Slot '{slotName}' not found");
                    return new T();
                }

                var content = File.ReadAllText(path);
                var wrapperJson = _useEncryption ? Decrypt(content) : content;
                var wrapper = JsonUtility.FromJson<SaveWrapper>(wrapperJson);

                // Warn on version mismatch — future migration hooks can go here
                if (wrapper.Version != CurrentVersion)
                    GameLogger.LogWarning("Save",
                        $"Save version mismatch: file={wrapper.Version}, current={CurrentVersion}");

                return JsonUtility.FromJson<T>(wrapper.Data);
            }
            catch (Exception ex)
            {
                GameLogger.LogError("Save", $"Load failed: {ex.Message}");
                return new T();
            }
        }

        /// <summary>Check whether a save file exists for the given slot name.</summary>
        /// <param name="slotName">The slot name to check.</param>
        /// <returns>True if the file exists on disk.</returns>
        public bool SlotExists(string slotName) => File.Exists(GetSlotPath(slotName));

        /// <summary>
        /// Delete a save slot's file from disk.
        /// </summary>
        /// <param name="slotName">The slot name to delete.</param>
        /// <returns>True if the file was found and deleted.</returns>
        public bool DeleteSlot(string slotName)
        {
            var path = GetSlotPath(slotName);
            if (!File.Exists(path)) return false;

            File.Delete(path);
            GameLogger.Log("Save", $"Deleted slot '{slotName}'");
            return true;
        }

        /// <summary>
        /// Get the names of all existing save slots.
        /// </summary>
        /// <returns>Array of slot names (file names without extension).</returns>
        public string[] GetAllSlots()
        {
            var files = Directory.GetFiles(GetSaveDirectory(), $"*{Extension}");
            var slots = new string[files.Length];
            for (int i = 0; i < files.Length; i++)
                slots[i] = Path.GetFileNameWithoutExtension(files[i]);
            return slots;
        }

        /// <summary>
        /// Read metadata for a slot without deserializing the full data payload.
        /// </summary>
        /// <param name="slotName">The slot name to inspect.</param>
        /// <returns>Nullable tuple of (timestamp, version), or null if the slot doesn't exist.</returns>
        public (DateTime timestamp, int version)? GetSlotInfo(string slotName)
        {
            try
            {
                var path = GetSlotPath(slotName);
                if (!File.Exists(path)) return null;

                var content = File.ReadAllText(path);
                var wrapperJson = _useEncryption ? Decrypt(content) : content;
                var wrapper = JsonUtility.FromJson<SaveWrapper>(wrapperJson);

                return (DateTime.Parse(wrapper.Timestamp), wrapper.Version);
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

        // ─── Internals ──────────────────────────────────────────────

        /// <summary>Build the full directory path for save files.</summary>
        private string GetSaveDirectory() =>
            Path.Combine(Application.persistentDataPath, SaveDir);

        /// <summary>Build the full file path for a named slot.</summary>
        private string GetSlotPath(string slotName) =>
            Path.Combine(GetSaveDirectory(), SanitizeSlotName(slotName) + Extension);

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

        /// <summary>Encrypt plaintext using AES-256-CBC with a random IV prepended to the output.</summary>
        private string Encrypt(string plainText)
        {
            using var aes = Aes.Create();
            var key = Encoding.UTF8.GetBytes(_encryptionKey.PadRight(32).Substring(0, 32));
            aes.Key = key;
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
            using var aes = Aes.Create();
            var key = Encoding.UTF8.GetBytes(_encryptionKey.PadRight(32).Substring(0, 32));
            aes.Key = key;

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