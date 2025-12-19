using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;

namespace TelegramTrayLauncher
{
    internal sealed class OverlayManager
    {
        private readonly List<WindowOverlay> _overlays = new List<WindowOverlay>();
        private readonly object _lock = new object();
        private readonly Action<string> _log;

        public OverlayManager(Action<string> log)
        {
            _log = log;
        }

        public void ShowForProcesses(List<Process> processes)
        {
            ShowInternal(processes, false);
        }

        public void ShowForProcesses(List<int> pids, bool fromPids)
        {
            if (!fromPids)
            {
                ShowInternal(new List<Process>(), false);
                return;
            }

            var processes = new List<Process>();
            foreach (var pid in pids)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    if (p != null && !p.HasExited)
                    {
                        processes.Add(p);
                    }
                }
                catch
                {
                    // ignore
                }
            }

            ShowInternal(processes, false);
        }

        private void ShowInternal(List<Process> processes, bool alreadyTracked)
        {
            HideOverlays();

            if (processes == null || processes.Count == 0)
            {
                return;
            }

            int displayIndex = 1;

            foreach (var process in processes)
            {
                try
                {
                    if (process == null || process.HasExited)
                    {
                        displayIndex++;
                        continue;
                    }

                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero || !NativeMethods.IsWindowVisible(handle))
                    {
                        displayIndex++;
                        continue;
                    }

                    if (!NativeMethods.GetWindowRect(handle, out var rect))
                    {
                        displayIndex++;
                        continue;
                    }

                    var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                    var overlay = WindowOverlay.Create(bounds, displayIndex.ToString());
                    lock (_lock)
                    {
                        _overlays.Add(overlay);
                    }
                }
                catch (Exception ex)
                {
                    _log("Ошибка показа оверлея: " + ex.Message);
                }
                finally
                {
                    displayIndex++;
                }
            }
        }

        public void HideOverlays()
        {
            lock (_lock)
            {
                foreach (var overlay in _overlays)
                {
                    try
                    {
                        overlay?.Close();
                        overlay?.Dispose();
                    }
                    catch
                    {
                        // ignore disposing errors
                    }
                }

                _overlays.Clear();
            }
        }
    }
}
