using System;
using System.Diagnostics;
using System.IO;

namespace TelegramTrayLauncher
{
    internal static class BaseDirectoryResolver
    {
        private static readonly string ConfigDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TelegramManager");
        private const string WorkdirFileName = "workdir.txt";

        public static string Resolve(string? explicitPath, Action<string>? log = null, Func<string?>? prompt = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                try
                {
                    var full = Path.GetFullPath(explicitPath);
                    if (Directory.Exists(full))
                    {
                        return full;
                    }

                    log?.Invoke($"Provided workdir does not exist: {full}");
                }
                catch (Exception ex)
                {
                    log?.Invoke("Cannot use provided workdir: " + ex.Message);
                }
            }

            var exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName)
                        ?? AppDomain.CurrentDomain.BaseDirectory;

            var saved = TryReadSavedWorkdir(log);
            if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
            {
                return saved;
            }

            try
            {
                var current = Environment.CurrentDirectory;
                if (!string.IsNullOrWhiteSpace(current))
                {
                    var fullCurrent = Path.GetFullPath(current);
                    if (Directory.Exists(fullCurrent) &&
                        !fullCurrent.Equals(Path.GetFullPath(exeDir), StringComparison.OrdinalIgnoreCase))
                    {
                        Persist(fullCurrent, log);
                        return fullCurrent;
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("Cannot read Environment.CurrentDirectory: " + ex.Message);
            }

            if (prompt != null)
            {
                try
                {
                    var chosen = prompt();
                    if (!string.IsNullOrWhiteSpace(chosen) && Directory.Exists(chosen))
                    {
                        Persist(chosen, log);
                        return chosen;
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke("Prompt for workdir failed: " + ex.Message);
                }
            }

            return exeDir;
        }

        private static string? TryReadSavedWorkdir(Action<string>? log)
        {
            try
            {
                var file = Path.Combine(ConfigDir, WorkdirFileName);
                if (File.Exists(file))
                {
                    var content = File.ReadAllText(file).Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        return Path.GetFullPath(content);
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("Cannot read saved workdir: " + ex.Message);
            }

            return null;
        }

        private static void Persist(string path, Action<string>? log)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                File.WriteAllText(Path.Combine(ConfigDir, WorkdirFileName), path);
            }
            catch (Exception ex)
            {
                log?.Invoke("Cannot persist workdir: " + ex.Message);
            }
        }
    }
}
