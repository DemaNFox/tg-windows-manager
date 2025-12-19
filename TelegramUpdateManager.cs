using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal sealed class TelegramUpdateManager
    {
        private const string BaseTelegramFileName = "Telegram.exe";
        private const string TempFileSuffix = ".download";
        private const string DefaultDownloadUrl = "https://telegram.org/dl/desktop/win64_portable";
        private const string DefaultVersionUrl = "https://api.github.com/repos/telegramdesktop/tdesktop/releases";

        private readonly string _baseDir;
        private readonly Action<string> _log;
        private readonly SynchronizationContext _uiContext;

        public TelegramUpdateManager(string baseDir, Action<string> log, SynchronizationContext uiContext)
        {
            _baseDir = baseDir ?? string.Empty;
            _log = log;
            _uiContext = uiContext;
        }

        public void Start()
        {
            _ = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            try
            {
                var config = TelegramUpdateConfig.LoadOptional(AppContext.BaseDirectory, _log);
                string downloadUrl = !string.IsNullOrWhiteSpace(config?.DownloadUrl) ? config.DownloadUrl : DefaultDownloadUrl;
                string versionUrl = !string.IsNullOrWhiteSpace(config?.VersionUrl) ? config.VersionUrl : DefaultVersionUrl;

                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    _log("Telegram update skipped: download URL is not configured.");
                    return;
                }

                string baseExePath = Path.Combine(AppContext.BaseDirectory, BaseTelegramFileName);
                bool baseExists = File.Exists(baseExePath);
                bool updated = false;

                UpdateInfo? updateInfo = null;
                if (!string.IsNullOrWhiteSpace(versionUrl))
                {
                    updateInfo = await FetchUpdateInfoAsync(versionUrl);
                }

                if (!baseExists)
                {
                    _log("Base Telegram.exe is missing. Downloading...");
                    await DownloadAndReplaceAsync(baseExePath, updateInfo?.DownloadUrl ?? downloadUrl, updateInfo?.Sha256);
                    await PromptCreateMissingTargetsAsync(baseExePath);
                    return;
                }

                if (updateInfo != null && IsUpdateAvailable(baseExePath, updateInfo.Version))
                {
                    bool shouldUpdate = await PromptYesNoAsync(
                        "\u0414\u043e\u0441\u0442\u0443\u043f\u043d\u0430 \u043d\u043e\u0432\u0430\u044f \u0432\u0435\u0440\u0441\u0438\u044f Telegram. \u041e\u0431\u043d\u043e\u0432\u0438\u0442\u044c \u0441\u0435\u0439\u0447\u0430\u0441?",
                        "Telegram Manager");
                    if (shouldUpdate)
                    {
                        await DownloadAndReplaceAsync(baseExePath, updateInfo.DownloadUrl ?? downloadUrl, updateInfo.Sha256);
                        updated = true;
                    }
                }

                if (updated)
                {
                    await PropagateToTargetsAsync(baseExePath, replaceExisting: true);
                }
            }
            catch (Exception ex)
            {
                _log("Telegram update failed: " + ex.Message);
            }
        }

        private async Task PromptCreateMissingTargetsAsync(string baseExePath)
        {
            var targets = GetTargetDirectories();
            var missing = new List<string>();
            foreach (var dir in targets)
            {
                string targetExe = Path.Combine(dir, BaseTelegramFileName);
                if (!File.Exists(targetExe))
                {
                    missing.Add(dir);
                }
            }

            if (missing.Count == 0)
            {
                return;
            }

            bool shouldCreate = await PromptYesNoAsync(
                $"\u041d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d {BaseTelegramFileName} \u0432 {missing.Count} \u043f\u0430\u043f\u043a\u0430\u0445. \u0421\u043e\u0437\u0434\u0430\u0442\u044c?",
                "Telegram Manager");
            if (!shouldCreate)
            {
                return;
            }

            foreach (var dir in missing)
            {
                TryCopy(baseExePath, Path.Combine(dir, BaseTelegramFileName), overwrite: false);
            }
        }

        private Task PropagateToTargetsAsync(string baseExePath, bool replaceExisting)
        {
            var targets = GetTargetDirectories();
            foreach (var dir in targets)
            {
                string targetExe = Path.Combine(dir, BaseTelegramFileName);
                TryCopy(baseExePath, targetExe, overwrite: replaceExisting);
            }

            return Task.CompletedTask;
        }

        private List<string> GetTargetDirectories()
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(_baseDir) || !Directory.Exists(_baseDir))
            {
                return result;
            }

            try
            {
                foreach (var dir in Directory.GetDirectories(_baseDir))
                {
                    result.Add(dir);
                }
            }
            catch (Exception ex)
            {
                _log("Failed to enumerate target directories: " + ex.Message);
            }

            return result;
        }

        private async Task<UpdateInfo?> FetchUpdateInfoAsync(string versionUrl)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("tg-manager");
                string payload = await client.GetStringAsync(versionUrl);
                payload = payload.Trim();

                if (payload.StartsWith("{", StringComparison.Ordinal) || payload.StartsWith("[", StringComparison.Ordinal))
                {
                    using var doc = JsonDocument.Parse(payload);
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                    {
                        root = root[0];
                    }

                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        string? version = null;
                        if (root.TryGetProperty("version", out var versionProp))
                        {
                            version = versionProp.GetString();
                        }
                        else if (root.TryGetProperty("tag_name", out var tagProp))
                        {
                            version = tagProp.GetString();
                            if (!string.IsNullOrWhiteSpace(version) && version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                            {
                                version = version.Substring(1);
                            }
                        }

                        return new UpdateInfo { Version = version };
                    }
                }

                return new UpdateInfo { Version = payload };
            }
            catch (Exception ex)
            {
                _log("Failed to fetch update info: " + ex.Message);
                return null;
            }
        }

        private bool IsUpdateAvailable(string baseExePath, string? remoteVersion)
        {
            if (string.IsNullOrWhiteSpace(remoteVersion))
            {
                return false;
            }

            string? localVersion = null;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(baseExePath);
                localVersion = info.FileVersion ?? info.ProductVersion;
            }
            catch
            {
                // ignore
            }

            if (string.IsNullOrWhiteSpace(localVersion))
            {
                return true;
            }

            if (Version.TryParse(localVersion, out var local) &&
                Version.TryParse(remoteVersion, out var remote))
            {
                return remote > local;
            }

            return !string.Equals(localVersion, remoteVersion, StringComparison.OrdinalIgnoreCase);
        }

        private async Task DownloadAndReplaceAsync(string targetPath, string downloadUrl, string? sha256)
        {
            string tempPath = targetPath + TempFileSuffix;
            string tempZipPath = tempPath + ".zip";
            _log("Downloading Telegram archive from: " + downloadUrl);

            bool isZip = downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                         downloadUrl.Contains("win64_portable", StringComparison.OrdinalIgnoreCase);

            using (var client = new HttpClient())
            using (var response = await client.GetAsync(downloadUrl))
            {
                response.EnsureSuccessStatusCode();
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (!string.IsNullOrWhiteSpace(mediaType) && mediaType.IndexOf("zip", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    isZip = true;
                }

                string savePath = isZip ? tempZipPath : tempPath;
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var file = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(file);
            }

            if (!string.IsNullOrWhiteSpace(sha256) && !isZip)
            {
                string actual = ComputeSha256(tempPath);
                if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempPath);
                    throw new InvalidOperationException("SHA256 mismatch for downloaded Telegram.exe.");
                }
            }

            if (isZip)
            {
                string extractRoot = Path.Combine(Path.GetTempPath(), "tg-update-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(extractRoot);
                try
                {
                    ZipFile.ExtractToDirectory(tempZipPath, extractRoot);
                    string? exePath = FindTelegramExe(extractRoot);
                    if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                    {
                        throw new FileNotFoundException("Telegram.exe not found in downloaded archive.");
                    }

                    ReplaceFile(exePath, targetPath);
                }
                finally
                {
                    TryDeleteFile(tempZipPath);
                    TryDeleteDirectory(extractRoot);
                }
            }
            else
            {
                ReplaceFile(tempPath, targetPath);
            }
        }

        private static void ReplaceFile(string sourcePath, string targetPath)
        {
            string backupPath = targetPath + ".bak";
            if (File.Exists(targetPath))
            {
                File.Replace(sourcePath, targetPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(sourcePath, targetPath);
            }
        }

        private static string? FindTelegramExe(string rootDir)
        {
            var matches = Directory.GetFiles(rootDir, BaseTelegramFileName, SearchOption.AllDirectories);
            if (matches.Length == 0)
            {
                return null;
            }

            foreach (var match in matches)
            {
                var parent = Path.GetDirectoryName(match) ?? string.Empty;
                if (parent.EndsWith(Path.DirectorySeparatorChar + "Telegram", StringComparison.OrdinalIgnoreCase))
                {
                    return match;
                }
            }

            return matches[0];
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore cleanup errors
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // ignore cleanup errors
            }
        }

        private void TryCopy(string sourcePath, string targetPath, bool overwrite)
        {
            try
            {
                File.Copy(sourcePath, targetPath, overwrite);
            }
            catch (Exception ex)
            {
                _log("Failed to copy Telegram.exe to " + targetPath + ": " + ex.Message);
            }
        }

        private Task<bool> PromptYesNoAsync(string text, string caption)
        {
            var tcs = new TaskCompletionSource<bool>();
            _uiContext.Post(_ =>
            {
                try
                {
                    var result = MessageBox.Show(text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    tcs.TrySetResult(result == DialogResult.Yes);
                }
                catch (Exception ex)
                {
                    _log("Failed to show prompt: " + ex.Message);
                    tcs.TrySetResult(false);
                }
            }, null);

            return tcs.Task;
        }

        private static string ComputeSha256(string filePath)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha.ComputeHash(stream);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }
    }

    internal sealed class TelegramUpdateConfig
    {
        private const string ConfigFileName = "telegram_update.json";

        public string? VersionUrl { get; set; }
        public string? DownloadUrl { get; set; }
        public string? Sha256Url { get; set; }

        public static TelegramUpdateConfig? LoadOptional(string baseDir, Action<string> log)
        {
            string path = Path.Combine(baseDir, ConfigFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                string json = File.ReadAllText(path);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<TelegramUpdateConfig>(json, options);
            }
            catch (Exception ex)
            {
                log("Failed to read " + ConfigFileName + ": " + ex.Message);
                return null;
            }
        }
    }

    internal sealed class UpdateInfo
    {
        public string? Version { get; set; }
        public string? DownloadUrl { get; set; }
        public string? Sha256 { get; set; }
    }
}
