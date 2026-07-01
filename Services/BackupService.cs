using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using SwellSSH.Models;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SwellSSH.Services
{
    /// <summary>
    /// Handles export and import of all SwellSSH data as a single .swellbak JSON package.
    /// Passwords remain DPAPI-encrypted in the backup; cross-machine restore will yield empty passwords.
    /// </summary>
    public class BackupService
    {
        // ── Internal package model ──────────────────────────────────────────────

        private class BackupPackage
        {
            public int Version { get; set; } = 1;
            public string ExportedAt { get; set; } = DateTime.UtcNow.ToString("O");
            public string AppVersion { get; set; } = "";
            public List<ConnectionProfile> Connections { get; set; } = new();
            public TerminalSettings? Settings { get; set; }
            public List<SnippetViewModel> Snippets { get; set; } = new();
        }

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly ConnectionStorage _storage = new();

        // ── Export ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Shows a save-file picker and writes a .swellbak backup to the chosen path.
        /// Returns true on success, false if the user cancelled.
        /// </summary>
        public async Task<BackupResult> ExportAsync(Window ownerWindow)
        {
            try
            {
                var picker = new FileSavePicker();
                InitializePicker(picker, ownerWindow);
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.SuggestedFileName = $"SwellSSH_Backup_{DateTime.Now:yyyyMMdd_HHmmss}";
                picker.FileTypeChoices.Add("SwellSSH 备份", new[] { ".swellbak" });

                StorageFile? file = await picker.PickSaveFileAsync();
                if (file == null)
                    return BackupResult.Cancelled();

                // Collect data
                var connections = await _storage.LoadConnectionsAsync();
                var settings    = await _storage.LoadSettingsAsync();
                var snippets    = await _storage.LoadSnippetsAsync();

                var package = new BackupPackage
                {
                    Connections = connections,
                    Settings    = settings,
                    Snippets    = snippets,
                    AppVersion  = GetAppVersion()
                };

                string json = JsonSerializer.Serialize(package, JsonOptions);
                await FileIO.WriteTextAsync(file, json);

                return BackupResult.Success($"备份已导出到：\n{file.Path}");
            }
            catch (Exception ex)
            {
                return BackupResult.Failure($"导出失败：{ex.Message}");
            }
        }

        // ── Import ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Shows an open-file picker and restores data from the chosen .swellbak file.
        /// Returns a result describing what happened.
        /// </summary>
        public async Task<BackupResult> ImportAsync(Window ownerWindow)
        {
            try
            {
                var picker = new FileOpenPicker();
                InitializePicker(picker, ownerWindow);
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.FileTypeFilter.Add(".swellbak");

                StorageFile? file = await picker.PickSingleFileAsync();
                if (file == null)
                    return BackupResult.Cancelled();

                string json = await FileIO.ReadTextAsync(file);
                var package = JsonSerializer.Deserialize<BackupPackage>(json, JsonOptions);

                if (package == null || package.Version < 1)
                    return BackupResult.Failure("备份文件格式不正确或版本不兼容。");

                // Write back to disk
                if (package.Connections != null)
                    await _storage.SaveConnectionsAsync(package.Connections);

                if (package.Settings != null)
                    await _storage.SaveSettingsAsync(package.Settings);

                if (package.Snippets != null)
                    await _storage.SaveSnippetsAsync(package.Snippets);

                bool hasDpapiRisk = package.Connections?.Exists(
                    c => !string.IsNullOrEmpty(c.EncryptedPassword) ||
                         !string.IsNullOrEmpty(c.EncryptedPassphrase)) ?? false;

                string note = hasDpapiRisk
                    ? "\n\n⚠️ 备份中含有加密密码。如果备份来自其他机器，密码将无法自动解密，请手动重新输入各连接的密码。"
                    : "";

                int connCount = package.Connections?.Count ?? 0;
                int snippetCount = package.Snippets?.Count ?? 0;
                string detail = $"已恢复 {connCount} 个连接、{snippetCount} 条指令片段及终端设置。" + note;

                return BackupResult.Success(detail, needsReload: true, package.Settings);
            }
            catch (JsonException)
            {
                return BackupResult.Failure("备份文件解析失败，请确认文件完整且未被修改。");
            }
            catch (Exception ex)
            {
                return BackupResult.Failure($"恢复失败：{ex.Message}");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static void InitializePicker(object picker, Window ownerWindow)
        {
            // WinUI 3 requires initializing file pickers with the window HWND
            var hwnd = WindowNative.GetWindowHandle(ownerWindow);
            if (picker is FileSavePicker savePicker)
                InitializeWithWindow.Initialize(savePicker, hwnd);
            else if (picker is FileOpenPicker openPicker)
                InitializeWithWindow.Initialize(openPicker, hwnd);
        }

        private static string GetAppVersion()
        {
            var ver = AppUpdateService.CurrentVersion;
            return ver.Major == 0 && ver.Minor == 0
                ? "dev"
                : $"{ver.Major}.{ver.Minor}.{ver.Build}";
        }
    }

    // ── Result type ─────────────────────────────────────────────────────────────

    public class BackupResult
    {
        public enum ResultKind { Success, Failure, Cancelled }

        public ResultKind Kind      { get; private init; }
        public string     Message   { get; private init; } = "";
        public bool       NeedsReload { get; private init; }
        public TerminalSettings? RestoredSettings { get; private init; }

        public bool IsSuccess   => Kind == ResultKind.Success;
        public bool IsCancelled => Kind == ResultKind.Cancelled;

        public static BackupResult Success(string message, bool needsReload = false, TerminalSettings? settings = null)
            => new() { Kind = ResultKind.Success, Message = message, NeedsReload = needsReload, RestoredSettings = settings };

        public static BackupResult Failure(string message)
            => new() { Kind = ResultKind.Failure, Message = message };

        public static BackupResult Cancelled()
            => new() { Kind = ResultKind.Cancelled, Message = "" };
    }
}
