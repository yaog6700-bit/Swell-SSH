using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SwellSSH.Models;

namespace SwellSSH.Services
{
    /// <summary>
    /// Reads and writes connection profiles to %AppData%\SwellSSH\connections.json.
    /// Passwords are encrypted with Windows DPAPI before storage.
    /// </summary>
    public class ConnectionStorage
    {
        private static readonly string AppDataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwellSSH");

        private static readonly string ConnectionsFile =
            Path.Combine(AppDataDir, "connections.json");

        private static readonly string SettingsFile =
            Path.Combine(AppDataDir, "settings.json");

        private static readonly string SnippetsFile =
            Path.Combine(AppDataDir, "snippets.json");

        private static readonly string AISettingsFile =
            Path.Combine(AppDataDir, "ai_settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        // ── Connection Profiles ─────────────────────────────────────────────────

        public async Task<List<ConnectionProfile>> LoadConnectionsAsync()
        {
            try
            {
                if (!File.Exists(ConnectionsFile))
                    return new List<ConnectionProfile>();

                string json = await File.ReadAllTextAsync(ConnectionsFile);
                return JsonSerializer.Deserialize<List<ConnectionProfile>>(json, JsonOptions)
                       ?? new List<ConnectionProfile>();
            }
            catch
            {
                return new List<ConnectionProfile>();
            }
        }

        public async Task SaveConnectionsAsync(List<ConnectionProfile> profiles)
        {
            Directory.CreateDirectory(AppDataDir);
            string json = JsonSerializer.Serialize(profiles, JsonOptions);
            await File.WriteAllTextAsync(ConnectionsFile, json);
        }

        // ── Terminal Settings ───────────────────────────────────────────────────

        public async Task<TerminalSettings> LoadSettingsAsync()
        {
            try
            {
                if (!File.Exists(SettingsFile))
                    return new TerminalSettings();

                string json = await File.ReadAllTextAsync(SettingsFile);
                return JsonSerializer.Deserialize<TerminalSettings>(json, JsonOptions)
                       ?? new TerminalSettings();
            }
            catch
            {
                return new TerminalSettings();
            }
        }

        public async Task SaveSettingsAsync(TerminalSettings settings)
        {
            Directory.CreateDirectory(AppDataDir);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(SettingsFile, json);
        }

        // ── AI Settings ─────────────────────────────────────────────────────────

        public async Task<AISettings> LoadAISettingsAsync()
        {
            try
            {
                if (!File.Exists(AISettingsFile))
                    return new AISettings();

                string json = await File.ReadAllTextAsync(AISettingsFile);
                return JsonSerializer.Deserialize<AISettings>(json, JsonOptions)
                       ?? new AISettings();
            }
            catch
            {
                return new AISettings();
            }
        }

        public async Task SaveAISettingsAsync(AISettings settings)
        {
            Directory.CreateDirectory(AppDataDir);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(AISettingsFile, json);
        }

        // ── Snippets ────────────────────────────────────────────────────────────

        public async Task<List<SnippetViewModel>> LoadSnippetsAsync()
        {
            try
            {
                if (!File.Exists(SnippetsFile))
                    return new List<SnippetViewModel>();

                string json = await File.ReadAllTextAsync(SnippetsFile);
                return JsonSerializer.Deserialize<List<SnippetViewModel>>(json, JsonOptions)
                       ?? new List<SnippetViewModel>();
            }
            catch
            {
                return new List<SnippetViewModel>();
            }
        }

        public async Task SaveSnippetsAsync(List<SnippetViewModel> snippets)
        {
            Directory.CreateDirectory(AppDataDir);
            string json = JsonSerializer.Serialize(snippets, JsonOptions);
            await File.WriteAllTextAsync(SnippetsFile, json);
        }

        // ── DPAPI Encryption Helpers ────────────────────────────────────────────

        /// <summary>
        /// Encrypts a plaintext secret using Windows DPAPI (current user scope).
        /// Returns Base64-encoded ciphertext safe for JSON storage.
        /// </summary>
        public static string EncryptSecret(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return "";
            byte[] data = Encoding.UTF8.GetBytes(plaintext);
            byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        /// <summary>
        /// Decrypts a DPAPI-encrypted Base64 string back to plaintext.
        /// Returns empty string on failure (e.g., different user or machine).
        /// </summary>
        public static string DecryptSecret(string encryptedBase64)
        {
            try
            {
                if (string.IsNullOrEmpty(encryptedBase64)) return "";
                byte[] encrypted = Convert.FromBase64String(encryptedBase64);
                byte[] data = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                return "";
            }
        }
    }
}
