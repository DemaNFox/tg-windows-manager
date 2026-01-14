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
                    _log("App update skipped: app_update.json is missing repo settings.");
                    return;
                }

                var release = await FetchLatestReleaseAsync(config.RepoOwner, config.RepoName);
                if (release == null || string.IsNullOrWhiteSpace(release.Tag))
                {
                    _log("App update skipped: latest release not found.");
                    return;
                }

                string? assetUrl = ResolveAssetUrl(release, config.AssetName);
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

                bool shouldUpdate = await PromptYesNoAsync(
                    $"Доступна новая версия приложения ({release.Tag}). Обновить сейчас?",
                    "Telegram Manager");
                if (!shouldUpdate)
                {
                    return;
                }

                await DownloadAndUpdateAsync(assetUrl);
            }
            catch (Exception ex)
            {
                LogAppUpdateFailure("App update failed.", ex);
                TryShowUpdateError("Не удалось обновить программу. Подробности в app_update.log.");
            }
        }

        private async Task DownloadAndUpdateAsync(string assetUrl)
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
            ZipFile.ExtractToDirectory(zipPath, extractPath);
            }
            finally
            {
                CloseProgressForm(progressForm);
            }

            ZipFile.ExtractToDirectory(zipPath, extractPath);

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

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -Pid {Process.GetCurrentProcess().Id} -Source \"{extractPath}\" -Target \"{targetDir}\" -Exe \"{exeName}\" -LogPath \"{logPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start update script.");
            }
            _uiContext.Post(_ => _exitForUpdate(), null);
        }

        private async Task DownloadFileWithProgressAsync(string url, string destinationPath, UpdateProgressForm progressForm)
        {
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
    [int]$Pid,
    [string]$Source,
    [string]$Target,
    [string]$Exe,
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'
function Write-Log([string]$Message) {
    $line = ""[{0}] {1}"" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    try { Add-Content -Path $LogPath -Value $line } catch {}
}

try {
    Write-Log ""Waiting for process $Pid""
    Wait-Process -Id $Pid -ErrorAction SilentlyContinue
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

                return new ReleaseInfo { Tag = tag ?? string.Empty, Assets = assets };
            }
            catch (Exception ex)
            {
                _log("Failed to fetch app release info: " + ex.Message);
                return null;
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
                    _log("Failed to show update prompt: " + ex.Message);
                    tcs.TrySetResult(false);
                }
            }, null);

            return tcs.Task;
        }

        private void LogAppUpdateFailure(string message, Exception ex)
        {
            _log(message + " " + ex.Message);
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
        public List<ReleaseAsset> Assets { get; set; } = new List<ReleaseAsset>();
    }

    internal sealed class ReleaseAsset
    {
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }
}
