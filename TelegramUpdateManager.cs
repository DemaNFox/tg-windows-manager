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

        private string _baseDir;
        private readonly Action<string> _log;
        private readonly SynchronizationContext _uiContext;
        private readonly Func<HttpClient> _httpClientFactory;

        public TelegramUpdateManager(string baseDir, Action<string> log, SynchronizationContext uiContext, Func<HttpClient>? httpClientFactory = null)
        {
            _baseDir = baseDir ?? string.Empty;
            _log = log;
            _uiContext = uiContext;
            _httpClientFactory = httpClientFactory ?? (() => new HttpClient());
        }

        public void UpdateBaseDir(string baseDir)
        {
            _baseDir = baseDir ?? string.Empty;
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

                string baseExePath = Path.Combine(AppContext.BaseDirectory, "assets", BaseTelegramFileName);
                bool baseExists = File.Exists(baseExePath);
                var missingTargets = GetMissingTargets();

                UpdateInfo? updateInfo = null;
                if (!string.IsNullOrWhiteSpace(versionUrl))
                {
                    updateInfo = await FetchUpdateInfoAsync(versionUrl);
                }

                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    _log("Telegram update skipped: download URL is not configured.");
                    return;
                }

                if (!baseExists)
                {
                    _log("Base Telegram.exe is missing. Downloading...");
                    await RunWithProgressFormAsync(form =>
                        DownloadAndReplaceAsync(baseExePath, updateInfo?.DownloadUrl ?? downloadUrl, updateInfo?.Sha256, form));

                    if (missingTargets.Count > 0)
                    {
                        bool shouldInsert = await PromptYesNoAsync(
                            $"\u041d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d {BaseTelegramFileName} \u0432 {missingTargets.Count} \u043f\u0430\u043f\u043a\u0430\u0445. \u0421\u043a\u0430\u0447\u0430\u0442\u044c \u0430\u043a\u0442\u0443\u0430\u043b\u044c\u043d\u044b\u0439 \u0444\u0430\u0439\u043b \u0438 \u043f\u043e\u0434\u0441\u0442\u0430\u0432\u0438\u0442\u044c \u0435\u0433\u043e? \u0418\u043b\u0438 \u0432\u044b \u043c\u043e\u0436\u0435\u0442\u0435 \u0441\u0430\u043c\u0438 \u0432\u0441\u0442\u0430\u0432\u0438\u0442\u044c {BaseTelegramFileName} \u0434\u043b\u044f \u0437\u0430\u043f\u0443\u0441\u043a\u0430 \u0430\u043a\u043a\u0430\u0443\u043d\u0442\u043e\u0432.",
                            "Telegram Manager");
                        if (shouldInsert)
                        {
                            await RunWithProgressFormAsync(form =>
                                CopyToTargetsAsync(baseExePath, missingTargets, form));
                        }
                    }

                    return;
                }

                if (updateInfo != null && IsUpdateAvailable(baseExePath, updateInfo.Version))
                {
                    bool shouldUpdate = await PromptYesNoAsync(
                        "\u0414\u043e\u0441\u0442\u0443\u043f\u043d\u0430 \u043d\u043e\u0432\u0430\u044f \u0432\u0435\u0440\u0441\u0438\u044f Telegram. \u041e\u0431\u043d\u043e\u0432\u0438\u0442\u044c \u0441\u0435\u0439\u0447\u0430\u0441?",
                        "Telegram Manager");
                    if (shouldUpdate)
                    {
                        await RunWithProgressFormAsync(async form =>
                        {
                            await DownloadAndReplaceAsync(baseExePath, updateInfo.DownloadUrl ?? downloadUrl, updateInfo.Sha256, form);
                            await PropagateToTargetsAsync(baseExePath, replaceExisting: true, form);
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                LogUpdateFailure("Telegram update failed.", ex);
            }
        }

        private List<string> GetMissingTargets()
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

            return missing;
        }

        private Task CopyToTargetsAsync(string baseExePath, List<string> targets, UpdateProgressForm progressForm)
        {
            return CopyToTargetsCoreAsync(
                baseExePath,
                targets,
                overwrite: false,
                progressForm,
                "Copying Telegram.exe to account folders...");
        }

        private Task PropagateToTargetsAsync(string baseExePath, bool replaceExisting, UpdateProgressForm progressForm)
        {
            var targets = GetTargetDirectories();
            return CopyToTargetsCoreAsync(
                baseExePath,
                targets,
                overwrite: replaceExisting,
                progressForm,
                "Applying Telegram update to account folders...");
        }

        private Task CopyToTargetsCoreAsync(
            string baseExePath,
            List<string> targets,
            bool overwrite,
            UpdateProgressForm progressForm,
            string progressText)
        {
            if (targets.Count == 0)
            {
                UpdateProgress(progressForm, progressText, 100, marquee: false);
                return Task.CompletedTask;
            }

            UpdateProgress(progressForm, progressText, 0, marquee: false);
            int total = targets.Count;
            for (int i = 0; i < total; i++)
            {
                string targetExe = Path.Combine(targets[i], BaseTelegramFileName);
                TryCopy(baseExePath, targetExe, overwrite);
                int percent = (int)Math.Min(100, ((i + 1L) * 100) / total);
                UpdateProgress(progressForm, progressText, percent, marquee: false);
            }

            return Task.CompletedTask;
        }

        private async Task RunWithProgressFormAsync(Func<UpdateProgressForm, Task> action)
        {
            var progressForm = await ShowProgressFormAsync();
            try
            {
                await action(progressForm);
            }
            finally
            {
                CloseProgressForm(progressForm);
            }
        }

        private Task<UpdateProgressForm> ShowProgressFormAsync()
        {
            var tcs = new TaskCompletionSource<UpdateProgressForm>();
            _uiContext.Post(_ =>
            {
                try
                {
                    var form = new UpdateProgressForm();
                    form.Show();
                    tcs.TrySetResult(form);
                }
                catch (Exception ex)
                {
                    _log("Failed to show update progress: " + ex.Message);
                    tcs.TrySetResult(new UpdateProgressForm());
                }
            }, null);

            return tcs.Task;
        }

        private void UpdateProgress(UpdateProgressForm form, string text, int? percent, bool marquee)
        {
            _uiContext.Post(_ =>
            {
                if (form.IsDisposed)
                {
                    return;
                }

                form.SetProgress(text, percent, marquee);
            }, null);
        }

        private void CloseProgressForm(UpdateProgressForm form)
        {
            _uiContext.Post(_ =>
            {
                if (!form.IsDisposed)
                {
                    form.Close();
                }
            }, null);
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
                    var tdataPath = Path.Combine(dir, "tdata");
                    if (Directory.Exists(tdataPath))
                    {
                        result.Add(dir);
                    }
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
                using var client = _httpClientFactory();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("tg-manager");
                string payload = await client.GetStringAsync(versionUrl);
                return ParseUpdateInfoPayload(payload);
            }
            catch (Exception ex)
            {
                _log("Failed to fetch update info: " + ex.Message);
                return null;
            }
        }

        internal static UpdateInfo? ParseUpdateInfoPayload(string payload)
        {
            payload = (payload ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            if (payload.StartsWith("{", StringComparison.Ordinal) || payload.StartsWith("[", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(payload);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    return ParseReleaseArray(root);
                }

                if (root.ValueKind == JsonValueKind.Object)
                {
                    return ParseReleaseObject(root);
                }

                return null;
            }

            return new UpdateInfo { Version = payload };
        }

        private static UpdateInfo? ParseReleaseArray(JsonElement releases)
        {
            JsonElement? fallback = null;
            foreach (var item in releases.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                fallback ??= item;
                bool isPrerelease = TryGetBooleanProperty(item, "prerelease");
                bool isDraft = TryGetBooleanProperty(item, "draft");
                if (!isPrerelease && !isDraft)
                {
                    return ParseReleaseObject(item);
                }
            }

            return fallback.HasValue ? ParseReleaseObject(fallback.Value) : null;
        }

        private static UpdateInfo? ParseReleaseObject(JsonElement root)
        {
            string? version = null;
            if (root.TryGetProperty("version", out var versionProp))
            {
                version = versionProp.GetString();
            }
            else if (root.TryGetProperty("tag_name", out var tagProp))
            {
                version = tagProp.GetString();
            }

            version = NormalizeVersion(version);
            return new UpdateInfo { Version = version };
        }

        private static bool TryGetBooleanProperty(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var prop))
            {
                return false;
            }

            if (prop.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (prop.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (prop.ValueKind == JsonValueKind.String &&
                bool.TryParse(prop.GetString(), out bool parsed))
            {
                return parsed;
            }

            return false;
        }

        private bool IsUpdateAvailable(string baseExePath, string? remoteVersion)
        {
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

            string remoteText = NormalizeVersion(remoteVersion);
            if (string.IsNullOrWhiteSpace(remoteText))
            {
                return false;
            }
            string localText = NormalizeVersion(localVersion);

            if (string.IsNullOrWhiteSpace(localText))
            {
                return true;
            }

            if (Version.TryParse(localText, out var local) &&
                Version.TryParse(remoteText, out var remote))
            {
                return remote > local;
            }

            return !string.Equals(localText, remoteText, StringComparison.OrdinalIgnoreCase);
        }

        internal async Task DownloadAndReplaceAsync(string targetPath, string downloadUrl, string? sha256, UpdateProgressForm? progressForm = null)
        {
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            string tempPath = targetPath + TempFileSuffix;
            string tempZipPath = tempPath + ".zip";
            _log("Downloading Telegram archive from: " + downloadUrl);

            bool isZip = downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                         downloadUrl.Contains("win64_portable", StringComparison.OrdinalIgnoreCase);

            using (var client = _httpClientFactory())
            using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (!string.IsNullOrWhiteSpace(mediaType) && mediaType.IndexOf("zip", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    isZip = true;
                }

                long? contentLength = response.Content.Headers.ContentLength;
                if (progressForm != null)
                {
                    if (contentLength.HasValue && contentLength.Value > 0)
                    {
                        UpdateProgress(progressForm, "Downloading Telegram update...", 0, marquee: false);
                    }
                    else
                    {
                        UpdateProgress(progressForm, "Downloading Telegram update...", null, marquee: true);
                    }
                }

                string savePath = isZip ? tempZipPath : tempPath;
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var file = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                int read;
                long totalRead = 0;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await file.WriteAsync(buffer, 0, read);
                    if (progressForm != null && contentLength.HasValue && contentLength.Value > 0)
                    {
                        totalRead += read;
                        int percent = (int)Math.Min(100, totalRead * 100 / contentLength.Value);
                        UpdateProgress(progressForm, "Downloading Telegram update...", percent, marquee: false);
                    }
                }
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
                    if (progressForm != null)
                    {
                        UpdateProgress(progressForm, "Extracting Telegram update...", null, marquee: true);
                    }

                    ZipFile.ExtractToDirectory(tempZipPath, extractRoot);
                    string? exePath = FindTelegramExe(extractRoot);
                    if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                    {
                        throw new FileNotFoundException("Telegram.exe not found in downloaded archive.");
                    }

                    if (progressForm != null)
                    {
                        UpdateProgress(progressForm, "Applying Telegram update...", null, marquee: true);
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
                if (progressForm != null)
                {
                    UpdateProgress(progressForm, "Applying Telegram update...", null, marquee: true);
                }
                ReplaceFile(tempPath, targetPath);
            }

            if (progressForm != null)
            {
                UpdateProgress(progressForm, "Telegram update completed.", 100, marquee: false);
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

        private static string NormalizeVersion(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string text = value.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(1);
            }

            int plusIndex = text.IndexOf('+');
            if (plusIndex >= 0)
            {
                text = text.Substring(0, plusIndex);
            }

            int dashIndex = text.IndexOf('-');
            if (dashIndex >= 0)
            {
                text = text.Substring(0, dashIndex);
            }

            return text.Trim();
        }

        private void LogUpdateFailure(string message, Exception ex)
        {
            _log(message + " " + ex.Message);
            try
            {
                string logPath = Path.Combine(AppContext.BaseDirectory, "telegram_update.log");
                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}{ex}{Environment.NewLine}";
                File.AppendAllText(logPath, entry);
            }
            catch
            {
                // ignore logging failures
            }
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

        internal sealed class UpdateProgressForm : Form
        {
            private readonly Label _label;
            private readonly ProgressBar _progressBar;

            public UpdateProgressForm()
            {
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterScreen;
                ShowInTaskbar = false;
                MaximizeBox = false;
                MinimizeBox = false;
                TopMost = true;
                Width = 420;
                Height = 140;
                Text = "Telegram Manager";

                _label = new Label
                {
                    Left = 16,
                    Top = 16,
                    Width = 372,
                    Height = 36,
                    Text = "Preparing update..."
                };

                _progressBar = new ProgressBar
                {
                    Left = 16,
                    Top = 64,
                    Width = 372,
                    Height = 22,
                    Minimum = 0,
                    Maximum = 100,
                    Style = ProgressBarStyle.Marquee
                };

                Controls.Add(_label);
                Controls.Add(_progressBar);
            }

            public void SetProgress(string text, int? percent, bool marquee)
            {
                _label.Text = text;
                if (marquee || !percent.HasValue)
                {
                    _progressBar.Style = ProgressBarStyle.Marquee;
                    return;
                }

                if (_progressBar.Style != ProgressBarStyle.Continuous)
                {
                    _progressBar.Style = ProgressBarStyle.Continuous;
                }

                int value = Math.Max(_progressBar.Minimum, Math.Min(_progressBar.Maximum, percent.Value));
                _progressBar.Value = value;
            }
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
