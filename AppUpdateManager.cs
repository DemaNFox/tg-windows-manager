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
                _log("App update failed: " + ex.Message);
            }
        }

        private async Task DownloadAndUpdateAsync(string assetUrl)
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "tg-app-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            string zipPath = Path.Combine(tempRoot, "update.zip");
            string extractPath = Path.Combine(tempRoot, "extract");

            _log("Downloading app update from: " + assetUrl);
            using (var client = new HttpClient())
            using (var response = await client.GetAsync(assetUrl))
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(file);
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
            File.WriteAllText(scriptPath, BuildUpdateScript(), Encoding.UTF8);

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -Pid {Process.GetCurrentProcess().Id} -Source \"{extractPath}\" -Target \"{targetDir}\" -Exe \"{exeName}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            Process.Start(startInfo);
            _uiContext.Post(_ => _exitForUpdate(), null);
        }

        private static string BuildUpdateScript()
        {
            return @"
param(
    [int]$Pid,
    [string]$Source,
    [string]$Target,
    [string]$Exe
)

try {
    Wait-Process -Id $Pid -ErrorAction SilentlyContinue
} catch {
}

try {
    Copy-Item -Path (Join-Path $Source '*') -Destination $Target -Recurse -Force
} catch {
    Start-Sleep -Seconds 1
    Copy-Item -Path (Join-Path $Source '*') -Destination $Target -Recurse -Force
}

Start-Process -FilePath (Join-Path $Target $Exe)
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
    }

    internal sealed class AppUpdateConfig
    {
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
