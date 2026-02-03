using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace TelegramTrayLauncher
{
    internal sealed class OverlayManager
    {
        private sealed class OverlayEntry
        {
            public WindowOverlay Overlay { get; }
            public IntPtr TargetHandle { get; }
            public Rectangle LastBounds { get; set; }
            public Action<IntPtr>? ClickAction { get; }

            public OverlayEntry(WindowOverlay overlay, IntPtr targetHandle, Rectangle bounds, Action<IntPtr>? clickAction)
            {
                Overlay = overlay;
                TargetHandle = targetHandle;
                LastBounds = bounds;
                ClickAction = clickAction;
            }
        }

        private readonly List<OverlayEntry> _overlays = new List<OverlayEntry>();
        private readonly object _lock = new object();
        private readonly Action<string> _log;
        private Timer? _updateTimer;
        private IntPtr _mouseHook = IntPtr.Zero;
        private NativeMethods.LowLevelMouseProc? _mouseProc;
        private bool _clickDebugEnabled;
        private long _lastClickTicks;

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

        public void ShowForEntries(List<AccountEntry> entries, Action<int> onOverlayClick)
        {
            HideOverlays();
            _clickDebugEnabled = true;

            if (entries == null || entries.Count == 0)
            {
                return;
            }

            foreach (var entry in entries)
            {
                try
                {
                    var pid = entry.Pid;
                    var label = entry.Number;
                    Process? process = null;

                    try
                    {
                        process = Process.GetProcessById(pid);
                    }
                    catch
                    {
                        // ignore
                    }

                    if (process == null || process.HasExited)
                    {
                        continue;
                    }

                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero || !NativeMethods.IsWindowVisible(handle))
                    {
                        continue;
                    }

                    if (!NativeMethods.GetWindowRect(handle, out var rect))
                    {
                        continue;
                    }

                    var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                    var overlay = WindowOverlay.Create(
                        bounds,
                        label,
                        handle,
                        null);
                    Action<IntPtr> clickAction = targetHandle =>
                    {
                        uint clickedPid = 0;
                        if (targetHandle != IntPtr.Zero)
                        {
                            NativeMethods.GetWindowThreadProcessId(targetHandle, out clickedPid);
                        }
                        var effectivePid = clickedPid != 0 ? (int)clickedPid : pid;
                        onOverlayClick(effectivePid);
                    };
                    lock (_lock)
                    {
                        _overlays.Add(new OverlayEntry(overlay, handle, bounds, clickAction));
                    }
                }
                catch (Exception ex)
                {
                    _log("Ошибка показа оверлея: " + ex.Message);
                }
            }

            StartTracking();
            StartMouseHook();
        }

        private void ShowInternal(List<Process> processes, bool alreadyTracked)
        {
            HideOverlays();
            _clickDebugEnabled = false;

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
                    var overlay = WindowOverlay.Create(bounds, displayIndex.ToString(), handle, null);
                    lock (_lock)
                    {
                        _overlays.Add(new OverlayEntry(overlay, handle, bounds, null));
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

            StartTracking();
            StopMouseHook();
        }

        public void HideOverlays()
        {
            StopTracking();
            StopMouseHook();

            lock (_lock)
            {
                foreach (var entry in _overlays)
                {
                    try
                    {
                        entry.Overlay?.Close();
                        entry.Overlay?.Dispose();
                    }
                    catch
                    {
                        // ignore disposing errors
                    }
                }

                _overlays.Clear();
            }
        }

        private void StartTracking()
        {
            if (_updateTimer != null)
            {
                return;
            }

            _updateTimer = new Timer
            {
                Interval = 200
            };
            _updateTimer.Tick += UpdateOverlays;
            _updateTimer.Start();
        }

        private void StopTracking()
        {
            if (_updateTimer == null)
            {
                return;
            }

            _updateTimer.Stop();
            _updateTimer.Tick -= UpdateOverlays;
            _updateTimer.Dispose();
            _updateTimer = null;
        }

        private void UpdateOverlays(object? sender, EventArgs e)
        {
            List<OverlayEntry>? toRemove = null;

            lock (_lock)
            {
                foreach (var entry in _overlays)
                {
                    try
                    {
                        if (entry.Overlay.IsDisposed || entry.TargetHandle == IntPtr.Zero)
                        {
                            toRemove ??= new List<OverlayEntry>();
                            toRemove.Add(entry);
                            continue;
                        }

                        if (!NativeMethods.IsWindowVisible(entry.TargetHandle))
                        {
                            toRemove ??= new List<OverlayEntry>();
                            toRemove.Add(entry);
                            continue;
                        }

                        if (!NativeMethods.GetWindowRect(entry.TargetHandle, out var rect))
                        {
                            continue;
                        }

                        var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                        if (bounds != entry.LastBounds)
                        {
                            entry.Overlay.UpdatePosition(bounds);
                            entry.LastBounds = bounds;
                        }
                    }
                    catch
                    {
                        toRemove ??= new List<OverlayEntry>();
                        toRemove.Add(entry);
                    }
                }

                if (toRemove != null)
                {
                    foreach (var entry in toRemove)
                    {
                        try
                        {
                            entry.Overlay?.Close();
                            entry.Overlay?.Dispose();
                        }
                        catch
                        {
                            // ignore
                        }

                        _overlays.Remove(entry);
                    }
                }
            }
        }

        private void StartMouseHook()
        {
            if (!_clickDebugEnabled || _mouseHook != IntPtr.Zero)
            {
                return;
            }

            _mouseProc = MouseHookCallback;
            _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
        }

        private void StopMouseHook()
        {
            if (_mouseHook == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _mouseProc = null;
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)NativeMethods.WM_LBUTTONDOWN)
            {
                try
                {
                    if (NativeMethods.GetCursorPos(out var point))
                    {
                        var hwnd = NativeMethods.WindowFromPoint(point);
                        var root = hwnd != IntPtr.Zero ? NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) : IntPtr.Zero;

                        OverlayEntry? hitEntry = null;
                        lock (_lock)
                        {
                            foreach (var entry in _overlays)
                            {
                                if (entry.Overlay.Handle == hwnd || entry.Overlay.Handle == root)
                                {
                                    hitEntry = entry;
                                    break;
                                }
                            }
                        }

                        if (hitEntry != null && hitEntry.ClickAction != null)
                        {
                            var nowTicks = DateTime.UtcNow.Ticks;
                            if (nowTicks - _lastClickTicks > TimeSpan.TicksPerMillisecond * 250)
                            {
                                _lastClickTicks = nowTicks;
                                try
                                {
                                    hitEntry.Overlay.BeginInvoke(hitEntry.ClickAction, hitEntry.TargetHandle);
                                }
                                catch
                                {
                                    // ignore
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }

            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }
    }
}
