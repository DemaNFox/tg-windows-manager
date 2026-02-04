using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (ExplorerGroupCommandHandler.TryHandle(args))
            {
                return;
            }

            bool useConsole = args != null &&
                              Array.Exists(args, a => a.Equals("-console", StringComparison.OrdinalIgnoreCase));

            string? workDirArg = GetWorkDirArg(args);
            string baseDir = BaseDirectoryResolver.Resolve(
                workDirArg,
                useConsole ? ConsoleHelper.Log : null,
                PromptForWorkdir);

            if (useConsole)
            {
                ConsoleHelper.EnsureConsole();
                ConsoleHelper.Log("=== TelegramTrayLauncher started in console mode ===");
            }

            EnvLoader.Load(baseDir, useConsole ? ConsoleHelper.Log : null);
            Application.Run(new TrayAppContext(useConsole, baseDir));
        }

        private static string? GetWorkDirArg(string[]? args)
        {
            if (args == null || args.Length == 0)
            {
                return null;
            }

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.StartsWith("--workdir=", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("-workdir=", StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Substring(arg.IndexOf('=') + 1).Trim('"');
                }

                if (arg.Equals("--workdir", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-workdir", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                    {
                        return args[i + 1].Trim('"');
                    }
                }
            }

            return null;
        }

        private static string? PromptForWorkdir()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Выберите рабочую папку с подпапками Telegram.exe (рабочая папка ярлыка)",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };

            var result = dialog.ShowDialog();
            if (result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath) && Directory.Exists(dialog.SelectedPath))
            {
                return dialog.SelectedPath;
            }

            return null;
        }
    }

    internal static class ConsoleHelper
    {
        private const int ATTACH_PARENT_PROCESS = -1;

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        public static void EnsureConsole()
        {
            try
            {
                if (!AttachConsole(ATTACH_PARENT_PROCESS))
                {
                    AllocConsole();
                }
            }
            catch
            {
                // если не получилось — просто игнорируем
            }
        }

        public static void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Debug.WriteLine(line);
            try
            {
                Console.WriteLine(line);
            }
            catch
            {
                // если консоли нет — ок
            }
        }
    }

    internal static class EnvLoader
    {
        public static void Load(string baseDir, Action<string>? log)
        {
            try
            {
                var candidates = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, ".env"),
                    Path.Combine(Directory.GetCurrentDirectory(), ".env"),
                    Path.Combine(baseDir ?? string.Empty, ".env")
                };

                foreach (var path in candidates)
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        continue;
                    }

                    foreach (var line in File.ReadAllLines(path))
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                        {
                            continue;
                        }

                        int idx = trimmed.IndexOf('=');
                        if (idx <= 0)
                        {
                            continue;
                        }

                        var key = trimmed.Substring(0, idx).Trim();
                        var value = trimmed.Substring(idx + 1).Trim().Trim('"');
                        if (string.IsNullOrEmpty(key))
                        {
                            continue;
                        }

                        Environment.SetEnvironmentVariable(key, value);
                    }

                    log?.Invoke($"Loaded environment variables from {path}");
                    return;
                }

                log?.Invoke("No .env file found.");
            }
            catch (Exception ex)
            {
                log?.Invoke("Failed to load .env: " + ex.Message);
            }
        }
    }
}
