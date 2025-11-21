using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;

namespace TelegramTrayLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool useConsole = args != null &&
                              Array.Exists(args, a => a.Equals("-console", StringComparison.OrdinalIgnoreCase));

            if (useConsole)
            {
                ConsoleHelper.EnsureConsole();
                ConsoleHelper.Log("=== TelegramTrayLauncher started in console mode ===");
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayAppContext(useConsole));
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

    internal class TrayAppContext : ApplicationContext
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly List<Process> _launchedProcesses = new List<Process>();
        // Папки, в которых найден Telegram.exe (наши “рабочие” экземпляры)
        private readonly HashSet<string> _telegramDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private readonly bool _useConsole;

        public TrayAppContext(bool useConsole)
        {
            _useConsole = useConsole;

            var menu = new ContextMenuStrip();

            var closeWindowsItem = new ToolStripMenuItem("Закрыть окна");
            closeWindowsItem.Click += (_, __) => CloseAllTelegram();

            var exitItem = new ToolStripMenuItem("Выход");
            exitItem.Click += (_, __) => ExitApplication();

            menu.Items.Add(closeWindowsItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = "Telegram launcher",
                ContextMenuStrip = menu
            };

            _notifyIcon.DoubleClick += (_, __) =>
            {
                _notifyIcon.ShowBalloonTip(
                    2000,
                    "Telegram launcher",
                    "Программа запущена и отслеживает Telegram.exe в подпапках.",
                    ToolTipIcon.Info);
            };

            Log("Tray icon created, starting background search...");
            Task.Run(SearchAndStartTelegram);
        }

        private void Log(string message)
        {
            if (_useConsole)
            {
                ConsoleHelper.Log(message);
            }
            else
            {
                Debug.WriteLine(message);
            }
        }

        /// <summary>
        /// Ищет Telegram.exe ТОЛЬКО в подпапках относительно директории приложения.
        /// Корневая папка, где лежит exe самой программы, игнорируется.
        /// </summary>
        private void SearchAndStartTelegram()
        {
            string baseDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName) ?? AppDomain.CurrentDomain.BaseDirectory;

            Log($"Base directory (exe location): {baseDir}");

            // Нас интересуют только подпапки первого уровня рядом с лаунчером
            // (папки вида 79xxxx_tdata). Глубокий обход tdata\user_data\cache нам не нужен.
            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(
                    baseDir,
                    "*",
                    SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                Log("Ошибка обхода подпапок: " + ex);
                return;
            }

            int dirCount = 0;
            int foundCount = 0;
            int startedCount = 0;

            foreach (var dir in subDirs)
            {
                dirCount++;
                Log($"Scanning directory: {dir}");

                IEnumerable<string> filesInDir;
                try
                {
                    filesInDir = Directory.EnumerateFiles(
                        dir,
                        "Telegram.exe",
                        SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex)
                {
                    Log("Ошибка чтения папки " + dir + ": " + ex.Message);
                    continue;
                }

                foreach (var file in filesInDir)
                {
                    foundCount++;
                    Log($"Found Telegram.exe: {file}");

                    // Запоминаем папку, в которой лежит найденный Telegram.exe
                    var exeDir = Path.GetDirectoryName(file);
                    if (!string.IsNullOrEmpty(exeDir))
                    {
                        lock (_lock)
                        {
                            _telegramDirs.Add(exeDir);
                        }
                    }

                    try
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = file,
                            WorkingDirectory = exeDir ?? baseDir,
                            UseShellExecute = true
                        };

                        var process = Process.Start(startInfo);
                        if (process != null)
                        {
                            startedCount++;
                            Log($"Started Telegram.exe, PID={process.Id}");
                            lock (_lock)
                            {
                                _launchedProcesses.Add(process);
                            }
                        }
                        else
                        {
                            Log("Process.Start вернул null для: " + file);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Ошибка запуска Telegram.exe (" + file + "): " + ex.Message);
                    }
                }
            }

            Log($"Search finished. Directories scanned: {dirCount}, found Telegram.exe: {foundCount}, started processes: {startedCount}");
        }

        /// <summary>
        /// Закрывает все процессы Telegram, которые запущены из известных нам папок Telegram.exe.
        /// Папки собираются в SearchAndStartTelegram.
        /// </summary>
        private void CloseAllTelegram()
        {
            // Папка, где лежит наш exe (как в SearchAndStartTelegram)
            string baseDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName)
                            ?? AppDomain.CurrentDomain.BaseDirectory;

            Log($"Closing Telegram processes from known folders under: {baseDir}");


            Process[] allTelegram;
            try
            {
                allTelegram = Process.GetProcessesByName("Telegram");
            }
            catch (Exception ex)
            {
                Log("Error enumerating Telegram processes: " + ex.Message);
                return;
            }

            int handled = 0;

            foreach (var process in allTelegram)
            {
                try
                {
                    if (process == null)
                    {
                        Log("Process is null, skipping.");
                        continue;
                    }

                    if (process.HasExited)
                    {
                        Log($"Process PID={process.Id} already exited.");
                        continue;
                    }

                    string fileName = null;
                    string exeDir = null;
                    try
                    {
                        fileName = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(fileName))
                        {
                            exeDir = Path.GetDirectoryName(fileName);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Может не дать прочитать путь (другой пользователь, антивирус и т.п.)
                        Log($"Cannot read MainModule for PID={process.Id}: {ex.Message}");
                    }

                    bool shouldHandle = false;
                    lock (_lock)
                    {
                        if (_telegramDirs.Count > 0 && !string.IsNullOrEmpty(exeDir))
                        {
                            // Предпочитаем точное совпадение по папке exe
                            shouldHandle = _telegramDirs.Contains(exeDir);
                        }
                        else if (!string.IsNullOrEmpty(fileName))
                        {
                            // Запасной вариант — старое поведение по baseDir
                            shouldHandle = fileName.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase);
                        }
                    }

                    if (!shouldHandle)
                    {
                        continue;
                    }

                    handled++;
                    Log($"Trying to close PID={process.Id}, path={fileName}");

                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        Log("Closing main window...");
                        process.CloseMainWindow();

                        // Даем Телеге шанс закрыться нормально
                        if (!process.WaitForExit(300))
                        {
                            Log("Process did not exit in 1s, killing...");
                            process.Kill(true);
                        }
                        else
                        {
                            Log("Process exited gracefully.");
                        }
                    }
                    else
                    {
                        Log("No main window handle, killing immediately...");
                        process.Kill(true);
                    }
                }
                catch (Exception ex)
                {
                    Log("Ошибка закрытия процесса Telegram: " + ex.Message);
                }
            }

            // На всякий случай подчистим внутренний список
            lock (_lock)
            {
                _launchedProcesses.RemoveAll(p => p == null || p.HasExited);
            }

            Log($"CloseAllTelegram finished. Processes handled: {handled}");
        }


        private void ExitApplication()
        {
            Log("ExitApplication called.");
            try
            {
                CloseAllTelegram();
            }
            catch (Exception ex)
            {
                Log("Ошибка при выходе: " + ex.Message);
            }
            finally
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                Log("Application exiting.");
                Application.Exit();
            }
        }
    }
}
