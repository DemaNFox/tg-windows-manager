using System;
using System.Runtime.InteropServices;

namespace TelegramTrayLauncher
{
    internal static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal const int SW_SHOWNOACTIVATE = 4;
        internal const int GWL_HWNDPARENT = -8;
        internal const int WH_MOUSE_LL = 14;
        internal const int WM_LBUTTONDOWN = 0x0201;
        internal const uint GA_ROOT = 2;

        internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        private static IVirtualDesktopManager _virtualDesktopManager;

        internal static bool TryIsWindowOnCurrentVirtualDesktop(IntPtr hWnd, out bool isOnCurrentDesktop)
        {
            isOnCurrentDesktop = true;

            try
            {
                _virtualDesktopManager ??= (IVirtualDesktopManager)new VirtualDesktopManager();
                var hr = _virtualDesktopManager.IsWindowOnCurrentVirtualDesktop(hWnd, out var isOnCurrent);
                if (hr == 0)
                {
                    isOnCurrentDesktop = isOnCurrent;
                    return true;
                }
            }
            catch
            {
                // ignore and treat as current desktop
            }

            return false;
        }

        internal static bool TryGetWindowDesktopId(IntPtr hWnd, out Guid desktopId)
        {
            desktopId = Guid.Empty;

            try
            {
                _virtualDesktopManager ??= (IVirtualDesktopManager)new VirtualDesktopManager();
                var hr = _virtualDesktopManager.GetWindowDesktopId(hWnd, out desktopId);
                return hr == 0;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryMoveWindowToDesktop(IntPtr hWnd, Guid desktopId)
        {
            try
            {
                _virtualDesktopManager ??= (IVirtualDesktopManager)new VirtualDesktopManager();
                var hr = _virtualDesktopManager.MoveWindowToDesktop(hWnd, desktopId);
                return hr == 0;
            }
            catch
            {
                return false;
            }
        }

        internal static bool TrySetWindowOwner(IntPtr hWnd, IntPtr owner)
        {
            if (hWnd == IntPtr.Zero || owner == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (IntPtr.Size == 8)
                {
                    return SetWindowLongPtr64(hWnd, GWL_HWNDPARENT, owner) != IntPtr.Zero;
                }

                return SetWindowLong32(hWnd, GWL_HWNDPARENT, owner.ToInt32()) != 0;
            }
            catch
            {
                return false;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [ComImport]
    [Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVirtualDesktopManager
    {
        int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);
        int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        int MoveWindowToDesktop(IntPtr topLevelWindow, [MarshalAs(UnmanagedType.LPStruct)] Guid desktopId);
    }

    [ComImport]
    [Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    internal class VirtualDesktopManager
    {
    }
}

