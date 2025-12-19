using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace TelegramTrayLauncher
{
    internal class TelegramProcessManager
    {
        private readonly List<Process> _launchedProcesses = new List<Process>();
        private readonly HashSet<string> _telegramDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new object();
        private readonly Action<string> _log;

        public TelegramProcessManager(Action<string> log)
        {
            _log = log;
        }

        internal record TelegramExecutable(string Name, string ExePath, string Directory);

        /// <summary>
        /// Ищет Telegram.exe ТОЛЬКО в подпапках относительно baseDir.
        /// Корневая папка, где лежит exe самой программы, игнорируется.
        /// </summary>
        public List<TelegramExecutable> DiscoverExecutables(string baseDir)
        {
            _log($"Scanning for Telegram.exe under: {baseDir}");

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
                _log("Ошибка обхода подпапок: " + ex);
                return new List<TelegramExecutable>();
            }

            int dirCount = 0;
            int foundCount = 0;
            var executables = new List<TelegramExecutable>();
            foreach (var dir in subDirs)
            {
                dirCount++;
                _log($"Scanning directory: {dir}");

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
                    _log("Ошибка чтения папки " + dir + ": " + ex.Message);
                    continue;
                }

                foreach (var file in filesInDir)
                {
                    foundCount++;
                    _log($"Found Telegram.exe: {file}");

                    var exeDir = Path.GetDirectoryName(file);
                    if (!string.IsNullOrEmpty(exeDir))
                    {
                        lock (_lock)
                        {
                            _telegramDirs.Add(exeDir);
                        }
                    }

                    var name = Path.GetFileName(Path.GetDirectoryName(file.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                    executables.Add(new TelegramExecutable(name ?? "Telegram", file, exeDir ?? baseDir));
                }
            }

            _log($"Discovery finished. Directories scanned: {dirCount}, found Telegram.exe: {foundCount}");
            return executables;
        }

        public void StartAllAvailable(string baseDir, string? scaleArg)
        {
            var executables = DiscoverExecutables(baseDir);
            StartExecutables(executables, scaleArg);
        }

        public void StartExecutables(IEnumerable<TelegramExecutable> executables, string? scaleArg)
        {
            int started = 0;
            var list = new List<TelegramExecutable>();
            foreach (var exe in executables)
            {
                list.Add(exe);
                if (TryStartProcess(exe.ExePath, exe.Directory, scaleArg))
                {
                    started++;
                }
            }

            _log($"Started {started} Telegram instance(s) out of {list.Count} available.");
        }

        public void StartSingle(string exePath, string workingDir, string? scaleArg)
        {
            TryStartProcess(exePath, workingDir, scaleArg);
        }

        private bool TryStartProcess(string file, string workingDir, string? scaleArg)
        {
            try
            {
                var args = string.IsNullOrWhiteSpace(scaleArg) ? string.Empty : "-scale " + scaleArg;

                var startInfo = new ProcessStartInfo
                {
                    FileName = file,
                    WorkingDirectory = workingDir,
                    UseShellExecute = true,
                    Arguments = args
                };

                var process = Process.Start(startInfo);
                if (process != null)
                {
                    _log($"Started Telegram.exe, PID={process.Id}");
                    lock (_lock)
                    {
                        _launchedProcesses.Add(process);
                        if (!string.IsNullOrEmpty(workingDir))
                        {
                            _telegramDirs.Add(workingDir);
                        }
                    }
                    return true;
                }
                else
                {
                    _log("Process.Start вернул null для: " + file);
                }
            }
            catch (Exception ex)
            {
                _log("Ошибка запуска Telegram.exe (" + file + "): " + ex.Message);
            }
            return false;
        }

        public List<Process> GetTrackedTelegramProcesses(string baseDir)
        {
            Process[] allTelegram;
            try
            {
                allTelegram = Process.GetProcessesByName("Telegram");
            }
            catch (Exception ex)
            {
                _log("Error enumerating Telegram processes: " + ex.Message);
                return new List<Process>();
            }

            var result = new List<Process>();

            foreach (var process in allTelegram)
            {
                try
                {
                    if (process == null || process.HasExited)
                    {
                        continue;
                    }

                    string? fileName = null;
                    string? exeDir = null;
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
                        _log($"Cannot read MainModule for PID={process.Id}: {ex.Message}");
                    }

                    if (!ShouldHandleProcess(baseDir, exeDir, fileName))
                    {
                        continue;
                    }

                    result.Add(process);
                }
                catch (Exception ex)
                {
                    _log("Ошибка обработки процесса Telegram: " + ex.Message);
                }
            }

            result.Sort((a, b) => a.Id.CompareTo(b.Id));
            return result;
        }

        private bool ShouldHandleProcess(string baseDir, string? exeDir, string? fileName)
        {
            lock (_lock)
            {
                if (_telegramDirs.Count > 0 && !string.IsNullOrEmpty(exeDir))
                {
                    // Предпочитаем точное совпадение по папке exe
                    return _telegramDirs.Contains(exeDir);
                }

                if (!string.IsNullOrEmpty(fileName))
                {
                    // Запасной вариант — старое поведение по baseDir
                    return fileName.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase);
                }

                return false;
            }
        }

        public void CloseSingleTelegram(int pid, string baseDir)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                if (process == null || process.HasExited)
                {
                    _log($"Process PID={pid} already exited.");
                    return;
                }

                string? fileName = null;
                string? exeDir = null;
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
                    _log($"Cannot read MainModule for PID={pid}: {ex.Message}");
                }

                if (!ShouldHandleProcess(baseDir, exeDir, fileName))
                {
                    _log($"Skipping PID={pid}, process is not from known folders.");
                    return;
                }

                _log($"Trying to close PID={pid}, path={fileName}");

                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    process.CloseMainWindow();

                    if (!process.WaitForExit(300))
                    {
                        _log("Process did not exit in 0.3s, killing...");
                        process.Kill(true);
                    }
                    else
                    {
                        _log("Process exited gracefully.");
                    }
                }
                else
                {
                    _log("No main window handle, killing immediately...");
                    process.Kill(true);
                }
            }
            catch (Exception ex)
            {
                _log("Ошибка закрытия выбранного процесса Telegram: " + ex.Message);
            }
            finally
            {
                lock (_lock)
                {
                    _launchedProcesses.RemoveAll(p => p == null || p.HasExited);
                }
            }
        }

        /// <summary>
        /// Закрывает все процессы Telegram, которые запущены из известных папок Telegram.exe.
        /// Папки собираются в SearchAndStartTelegram.
        /// </summary>
        public void CloseAllTelegram(string baseDir)
        {
            _log($"Closing Telegram processes from known folders under: {baseDir}");

            Process[] allTelegram;
            try
            {
                allTelegram = Process.GetProcessesByName("Telegram");
            }
            catch (Exception ex)
            {
                _log("Error enumerating Telegram processes: " + ex.Message);
                return;
            }

            int handled = 0;

            foreach (var process in allTelegram)
            {
                try
                {
                    if (process == null || process.HasExited)
                    {
                        continue;
                    }

                    string? fileName = null;
                    string? exeDir = null;
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
                        _log($"Cannot read MainModule for PID={process.Id}: {ex.Message}");
                    }

                    bool shouldHandle = ShouldHandleProcess(baseDir, exeDir, fileName);

                    if (!shouldHandle)
                    {
                        continue;
                    }

                    handled++;
                    _log($"Trying to close PID={process.Id}, path={fileName}");

                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        process.CloseMainWindow();

                        // Даем Телеге шанс закрыться нормально
                        if (!process.WaitForExit(300))
                        {
                            _log("Process did not exit in 0.3s, killing...");
                            process.Kill(true);
                        }
                        else
                        {
                            _log("Process exited gracefully.");
                        }
                    }
                    else
                    {
                        _log("No main window handle, killing immediately...");
                        process.Kill(true);
                    }
                }
                catch (Exception ex)
                {
                    _log("Ошибка закрытия процесса Telegram: " + ex.Message);
                }
            }

            // На всякий случай подчистим внутренний список
            lock (_lock)
            {
                _launchedProcesses.RemoveAll(p => p == null || p.HasExited);
            }

            _log($"CloseAllTelegram finished. Processes handled: {handled}");
        }

        public void CloseTelegramForDirectories(IEnumerable<string> directories, string baseDir)
        {
            var directorySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in directories)
            {
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    directorySet.Add(dir);
                }
            }

            if (directorySet.Count == 0)
            {
                return;
            }

            var processes = GetTrackedTelegramProcesses(baseDir);
            foreach (var process in processes)
            {
                try
                {
                    if (process == null || process.HasExited)
                    {
                        continue;
                    }

                    string? exeDir = null;
                    try
                    {
                        exeDir = Path.GetDirectoryName(process.MainModule?.FileName ?? string.Empty);
                    }
                    catch
                    {
                        // ignore
                    }

                    if (!string.IsNullOrEmpty(exeDir) && directorySet.Contains(exeDir))
                    {
                        CloseSingleTelegram(process.Id, baseDir);
                    }
                }
                catch (Exception ex)
                {
                    _log("Error closing Telegram for directory group: " + ex.Message);
                }
            }
        }
    }
}
