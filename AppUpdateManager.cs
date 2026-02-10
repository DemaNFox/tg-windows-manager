using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal sealed class AppUpdateManager
    {
        private const string ConfigFileName = "app_update.json";
        private const string DefaultAssetSuffix = "portable-win-x64.zip";

        private readonly Action<string> _log;
        private readonly SynchronizationContext _uiContext;
        private readonly Action _exitForUpdate;

        public AppUpdateManager(Action<string> log, SynchronizationContext uiContext, Action exitForUpdate)
        {
            _log = log;
            _uiContext = uiContext;
            _exitForUpdate = exitForUpdate;
        }

        public void Start()
        {
            _ = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            try
            {
                var config = AppUpdateConfig.LoadOptional(AppContext.BaseDirectory, _log);
                if (config == null || string.IsNullOrWhiteSpace(config.RepoOwner) || string.IsNullOrWhiteSpace(config.RepoName))
                {
                    if (config == null || string.IsNullOrWhiteSpace(config.LocalPath))
                    {
                        _log("App update skipped: app_update.json is missing repo settings.");
                        return;
                    }
                }

                string? assetUrl = null;
                string? tagLabel = null;
                ReleaseInfo? releaseInfo = null;

                if (!string.IsNullOrWhiteSpace(config?.LocalPath))
                {
                    string localPath = config.LocalPath;
                    if (!Path.IsPathRooted(localPath))
                    {
                        localPath = Path.Combine(AppContext.BaseDirectory, localPath);
                    }

                    assetUrl = localPath;
                    tagLabel = "local";
                }
                else
                {
                    var release = await FetchLatestReleaseAsync(config!.RepoOwner!, config.RepoName!);
                    if (release == null || string.IsNullOrWhiteSpace(release.Tag))
                    {
                        _log("App update skipped: latest release not found.");
                        return;
                    }

                    releaseInfo = release;
                    assetUrl = ResolveAssetUrl(release, config.AssetName);
                    if (string.IsNullOrWhiteSpace(assetUrl))
                    {
                        _log("App update skipped: release asset not found.");
                        return;
                    }

                    if (!IsUpdateAvailable(release.Tag))
                    {
                        _log("App update skipped: already on latest version.");
                        return;
                    }

                    tagLabel = release.Tag;
                }

                bool shouldUpdate = await PromptYesNoAsync(
                    BuildUpdatePromptText(tagLabel ?? "unknown", releaseInfo),
                    "Telegram Manager");
                if (!shouldUpdate)
                {
                    return;
                }

                await DownloadAndUpdateAsync(assetUrl!, config?.TestMode == true);
            }
            catch (Exception ex)
            {
                LogAppUpdateFailure("App update failed.", ex);
                TryShowUpdateError("\u041d\u0435 \u0443\u0434\u0430\u043b\u043e\u0441\u044c \u043e\u0431\u043d\u043e\u0432\u0438\u0442\u044c \u043f\u0440\u043e\u0433\u0440\u0430\u043c\u043c\u0443. \u041f\u043e\u0434\u0440\u043e\u0431\u043d\u043e\u0441\u0442\u0438 \u0432 app_update.log.");
            }
        }

        private async Task DownloadAndUpdateAsync(string assetUrl, bool preserveAppUpdateJson)
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "tg-app-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            string zipPath = Path.Combine(tempRoot, "update.zip");
            string extractPath = Path.Combine(tempRoot, "extract");

            var progressForm = await ShowProgressFormAsync();
            try
            {
                _log("Downloading app update from: " + assetUrl);
                await DownloadFileWithProgressAsync(assetUrl, zipPath, progressForm);

                UpdateProgress(progressForm, "Preparing update...", null, marquee: true);
                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, recursive: true);
                }
                ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true);
            }
            finally
            {
                CloseProgressForm(progressForm);
            }

            string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                throw new InvalidOperationException("Cannot determine executable path.");
            }

            string targetDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
            string exeName = Path.GetFileName(exePath);

            string scriptPath = Path.Combine(tempRoot, "apply-update.ps1");
            string logPath = Path.Combine(tempRoot, "apply-update.log");
            File.WriteAllText(scriptPath, BuildUpdateScript(), Encoding.UTF8);

            string powershellPath = GetPowerShellPath();
            string? preservePath = null;
            if (preserveAppUpdateJson)
            {
                preservePath = Path.Combine(tempRoot, "app_update.json");
                string sourcePath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
                if (File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, preservePath, overwrite: true);
                }
            }

            string arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -ProcessId {Process.GetCurrentProcess().Id} -Source \"{extractPath}\" -Target \"{targetDir}\" -Exe \"{exeName}\" -LogPath \"{logPath}\"";
            if (!string.IsNullOrWhiteSpace(preservePath))
            {
                arguments += $" -PreserveAppUpdateJson \"{preservePath}\"";
            }
            _log($"Starting update script: {powershellPath} {arguments}");

            var startInfo = new ProcessStartInfo
            {
                FileName = powershellPath,
                Arguments = arguments,
                WorkingDirectory = tempRoot,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start update script.");
            }

            if (!WaitForUpdateScriptStart(process, logPath))
            {
                var stderr = SafeReadStream(process.StandardError);
                var stdout = SafeReadStream(process.StandardOutput);
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    WriteAppUpdateLog("Update script stdout: " + stdout.Trim());
                }
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    WriteAppUpdateLog("Update script stderr: " + stderr.Trim());
                }

                // If the script process is still running, assume it started but was slow to create a log file.
                // Proceed with exit so the script can complete the update.
                if (process.HasExited)
                {
                    throw new InvalidOperationException("Update script failed to start.");
                }

                WriteAppUpdateLog("Update script did not create a log file in time, but the process is running. Exiting to apply update.");
            }

            _uiContext.Post(_ => _exitForUpdate(), null);
        }

        private bool WaitForUpdateScriptStart(Process process, string logPath)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (File.Exists(logPath))
                {
                    return true;
                }

                if (process.HasExited)
                {
                    WriteAppUpdateLog("Update script exited with code: " + process.ExitCode);
                    return false;
                }

                Thread.Sleep(100);
            }

            WriteAppUpdateLog("Update script did not create a log file.");
            return false;
        }

        private static string SafeReadStream(StreamReader reader)
        {
            try
            {
                return reader.ReadToEnd();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetPowerShellPath()
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string candidate = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            return "powershell";
        }

        private async Task DownloadFileWithProgressAsync(string url, string destinationPath, UpdateProgressForm progressForm)
        {
            if (File.Exists(url))
            {
                UpdateProgress(progressForm, "Copying update...", null, marquee: true);
                File.Copy(url, destinationPath, overwrite: true);
                return;
            }

            using var client = new HttpClient();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long? totalLength = response.Content.Headers.ContentLength;
            if (totalLength.HasValue && totalLength.Value > 0)
            {
                UpdateProgress(progressForm, "Downloading update...", 0, marquee: false);
            }
            else
            {
                UpdateProgress(progressForm, "Downloading update...", null, marquee: true);
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await file.WriteAsync(buffer, 0, read);
                if (totalLength.HasValue && totalLength.Value > 0)
                {
                    totalRead += read;
                    int percent = (int)Math.Min(100, totalRead * 100 / totalLength.Value);
                    UpdateProgress(progressForm, "Downloading update...", percent, marquee: false);
                }
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

        private static string BuildUpdateScript()
        {
            return @"
param(
    [int]$ProcessId,
    [string]$Source,
    [string]$Target,
    [string]$Exe,
    [string]$LogPath,
    [string]$PreserveAppUpdateJson
)

$ErrorActionPreference = 'Stop'
function Write-Log([string]$Message) {
    $line = ""[{0}] {1}"" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    try { Add-Content -Path $LogPath -Value $line } catch {}
}

try {
    Write-Log ""Waiting for process $ProcessId""
    Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue
} catch {
}

try {
    Write-Log ""Copying files from $Source to $Target""
    Copy-Item -Path (Join-Path $Source '*') -Destination $Target -Recurse -Force
} catch {
    Start-Sleep -Seconds 1
    Copy-Item -Path (Join-Path $Source '*') -Destination $Target -Recurse -Force
}

try {
    if ($PreserveAppUpdateJson -and (Test-Path $PreserveAppUpdateJson)) {
        Write-Log ""Restoring app_update.json""
        Copy-Item -Path $PreserveAppUpdateJson -Destination (Join-Path $Target 'app_update.json') -Force
    }
} catch {
    Write-Log ""Failed to restore app_update.json: $($_.Exception.Message)""
}

try {
    $exePath = Join-Path $Target $Exe
    Write-Log ""Starting updated app: $exePath""
    Start-Process -FilePath $exePath -WorkingDirectory $Target
} catch {
    Write-Log ""Failed to start updated app: $($_.Exception.Message)""
}
";
        }

        private static string? ResolveAssetUrl(ReleaseInfo release, string? assetName)
        {
            if (!string.IsNullOrWhiteSpace(assetName))
            {
                foreach (var asset in release.Assets)
                {
                    if (string.Equals(asset.Name, assetName, StringComparison.OrdinalIgnoreCase))
                    {
                        return asset.Url;
                    }
                }
            }

            foreach (var asset in release.Assets)
            {
                if (!string.IsNullOrWhiteSpace(asset.Name) &&
                    asset.Name.EndsWith(DefaultAssetSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    return asset.Url;
                }
            }

            return null;
        }

        private bool IsUpdateAvailable(string remoteTag)
        {
            if (AllowSameVersion())
            {
                return true;
            }

            string remoteVersionText = NormalizeVersion(remoteTag);
            string localVersionText = NormalizeVersion(GetLocalVersion());

            if (Version.TryParse(remoteVersionText, out var remote) &&
                Version.TryParse(localVersionText, out var local))
            {
                return remote > local;
            }

            if (string.IsNullOrWhiteSpace(localVersionText))
            {
                return true;
            }

            return !string.Equals(localVersionText, remoteVersionText, StringComparison.OrdinalIgnoreCase);
        }

        private static bool AllowSameVersion()
        {
            string? value = Environment.GetEnvironmentVariable("TG_UPDATE_ALLOW_SAME_VERSION");
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetLocalVersion()
        {
            var entry = Assembly.GetEntryAssembly();
            var infoVersion = entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVersion))
            {
                return infoVersion;
            }

            string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(exePath);
                    if (!string.IsNullOrWhiteSpace(info.FileVersion))
                    {
                        return info.FileVersion;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            return string.Empty;
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

        private async Task<ReleaseInfo?> FetchLatestReleaseAsync(string owner, string repo)
        {
            try
            {
                string url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("tg-manager");
                string payload = await client.GetStringAsync(url);

                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                string? tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
                string? body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;
                var assets = new List<ReleaseAsset>();

                if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsProp.EnumerateArray())
                    {
                        string? name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                        string? urlValue = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(urlValue))
                        {
                            assets.Add(new ReleaseAsset { Name = name ?? string.Empty, Url = urlValue });
                        }
                    }
                }

                return new ReleaseInfo { Tag = tag ?? string.Empty, Body = body ?? string.Empty, Assets = assets };
            }
            catch (Exception ex)
            {
                _log("Failed to fetch app release info: " + ex.Message);
                return null;
            }
        }

        private Task<bool> PromptYesNoAsync(string text, string caption)
        {
            if (IsAutoAcceptEnabled())
            {
                WriteAppUpdateLog("Auto-accepting app update prompt.");
                return Task.FromResult(true);
            }

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
                    _log("Failed to show update prompt: " + ex.Message);
                    tcs.TrySetResult(false);
                }
            }, null);

            return tcs.Task;
        }

        private static bool IsAutoAcceptEnabled()
        {
            string? value = Environment.GetEnvironmentVariable("TG_UPDATE_AUTO_ACCEPT");
            string? test = Environment.GetEnvironmentVariable("TG_UPDATE_TEST");
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(test))
            {
                return false;
            }

            bool accept = value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                          value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                          value.Equals("yes", StringComparison.OrdinalIgnoreCase);
            bool isTest = test.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                          test.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                          test.Equals("yes", StringComparison.OrdinalIgnoreCase);
            return accept && isTest;
        }

        private static string BuildUpdatePromptText(string tagLabel, ReleaseInfo? release)
        {
            var text = new StringBuilder();
            text.Append("Доступна новая версия приложения (");
            text.Append(tagLabel);
            text.Append(").");

            string notes = BuildReleaseNotesPreview(release?.Body);
            if (!string.IsNullOrWhiteSpace(notes))
            {
                text.Append(Environment.NewLine);
                text.Append(Environment.NewLine);
                text.Append("Что изменилось:");
                text.Append(Environment.NewLine);
                text.Append(notes);
            }

            text.Append(Environment.NewLine);
            text.Append(Environment.NewLine);
            text.Append("Обновить сейчас?");
            return text.ToString();
        }

        private static string BuildReleaseNotesPreview(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            string normalized = body.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n');
            var cleaned = new List<string>();

            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                line = line.TrimStart('#', '-', '*', '>', ' ');
                line = line.Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.Length > 140)
                {
                    line = line.Substring(0, 137).TrimEnd() + "...";
                }

                cleaned.Add("• " + line);
                if (cleaned.Count >= 6)
                {
                    break;
                }
            }

            return string.Join(Environment.NewLine, cleaned);
        }

        private void LogAppUpdateFailure(string message, Exception ex)
        {
            WriteAppUpdateLog(message + " " + ex.Message);
            try
            {
                string logPath = Path.Combine(AppContext.BaseDirectory, "app_update.log");
                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}{ex}{Environment.NewLine}";
                File.AppendAllText(logPath, entry);
            }
            catch
            {
                // ignore logging failures
            }
        }

        private void WriteAppUpdateLog(string message)
        {
            _log(message);
            try
            {
                string logPath = Path.Combine(AppContext.BaseDirectory, "app_update.log");
                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                File.AppendAllText(logPath, entry);
            }
            catch
            {
                // ignore logging failures
            }
        }

        private void TryShowUpdateError(string message)
        {
            _uiContext.Post(_ =>
            {
                try
                {
                    MessageBox.Show(message, "Telegram Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch
                {
                    // ignore UI failures
                }
            }, null);
        }

        private sealed class UpdateProgressForm : Form
        {
            private readonly Label _label;
            private readonly ProgressBar _progress;

            public UpdateProgressForm()
            {
                Width = 420;
                Height = 140;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterScreen;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = true;
                Text = "Telegram Manager";
                TopMost = true;

                _label = new Label
                {
                    Left = 12,
                    Top = 12,
                    Width = 380,
                    Text = "Downloading update..."
                };

                _progress = new ProgressBar
                {
                    Left = 12,
                    Top = 40,
                    Width = 380,
                    Height = 20,
                    Style = ProgressBarStyle.Continuous,
                    Minimum = 0,
                    Maximum = 100
                };

                Controls.Add(_label);
                Controls.Add(_progress);
            }

            public void SetProgress(string text, int? percent, bool marquee)
            {
                _label.Text = text;
                _progress.Style = marquee ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
                if (!marquee && percent.HasValue)
                {
                    _progress.Value = Math.Max(_progress.Minimum, Math.Min(_progress.Maximum, percent.Value));
                }
            }
        }
    }

    internal sealed class AppUpdateConfig
    {
        private const string ConfigFileName = "app_update.json";
        public string? RepoOwner { get; set; }
        public string? RepoName { get; set; }
        public string? AssetName { get; set; }
        public string? LocalPath { get; set; }
        public bool TestMode { get; set; }

        public static AppUpdateConfig? LoadOptional(string baseDir, Action<string> log)
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
                return JsonSerializer.Deserialize<AppUpdateConfig>(json, options);
            }
            catch (Exception ex)
            {
                log("Failed to read " + ConfigFileName + ": " + ex.Message);
                return null;
            }
        }
    }

    internal sealed class ReleaseInfo
    {
        public string Tag { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public List<ReleaseAsset> Assets { get; set; } = new List<ReleaseAsset>();
    }

    internal sealed class ReleaseAsset
    {
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }
}


