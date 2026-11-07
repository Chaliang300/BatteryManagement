using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerDock.App.Interop
{
    public static class NativeMethods
    {
        public const int ASFW_ANY = -1;

        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;
        public const int SW_SHOWMINIMIZED = 2;
        public const int SW_MAXIMIZE = 3;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_SHOW = 5;
        public const int SW_MINIMIZE = 6;
        public const int SW_SHOWMINNOACTIVE = 7;
        public const int SW_SHOWNA = 8;
        public const int SW_RESTORE = 9;

        public const uint FLASHW_STOP = 0;
        public const uint FLASHW_CAPTION = 1;
        public const uint FLASHW_TRAY = 2;
        public const uint FLASHW_ALL = 3;
        public const uint FLASHW_TIMER = 4;
        public const uint FLASHW_TIMERNOFG = 12;

        [StructLayout(LayoutKind.Sequential)]
        public struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FlashWindowEx(ref FLASHWINFO pfwi);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        // --- tray registration verification (PR-S0-04) ---

        [StructLayout(LayoutKind.Sequential)]
        public struct NOTIFYICONIDENTIFIER
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public Guid guidItem;
        }

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        /// <summary>
        /// Finds this process's WinForms tray icon window and asks the shell where the icon is.
        /// Returns true only when Shell_NotifyIconGetRect succeeds with a non-empty rectangle.
        /// WinForms NotifyIcon uses uID=1; uID 0..3 is probed for robustness.
        /// </summary>
        public static bool TryGetTrayIconRect(out Rectangle rect, out int uid, out string windowClass, out string error)
        {
            rect = Rectangle.Empty;
            uid = -1;
            windowClass = null;
            error = null;
            uint currentPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var windows = new System.Collections.Generic.List<IntPtr>();
            try
            {
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid == currentPid) windows.Add(hWnd);
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                error = "EnumWindows failed: " + ex.Message;
                return false;
            }

            foreach (IntPtr hWnd in windows)
            {
                var cls = new StringBuilder(256);
                GetClassName(hWnd, cls, 256);
                string clsName = cls.ToString();
                if (string.IsNullOrEmpty(clsName) || clsName.IndexOf("WindowsForms10", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                for (uint candidate = 0; candidate <= 3; candidate++)
                {
                    NOTIFYICONIDENTIFIER identifier = new NOTIFYICONIDENTIFIER();
                    identifier.cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER));
                    identifier.hWnd = hWnd;
                    identifier.uID = candidate;
                    identifier.guidItem = Guid.Empty;
                    RECT location;
                    int hr;
                    try
                    {
                        hr = Shell_NotifyIconGetRect(ref identifier, out location);
                    }
                    catch (Exception ex)
                    {
                        error = "Shell_NotifyIconGetRect exception: " + ex.Message;
                        continue;
                    }
                    int width = location.Right - location.Left;
                    int height = location.Bottom - location.Top;
                    if (hr == 0 && width > 0 && height > 0)
                    {
                        rect = new Rectangle(location.Left, location.Top, width, height);
                        uid = (int)candidate;
                        windowClass = clsName;
                        return true;
                    }
                    if (hr != 0 && string.IsNullOrEmpty(error))
                    {
                        int win32 = Marshal.GetLastWin32Error();
                        error = "Shell_NotifyIconGetRect hr=0x" + hr.ToString("X8") + (win32 != 0 ? ", win32=" + win32 : "");
                    }
                }
            }

            if (string.IsNullOrEmpty(error))
            {
                error = "no WindowsForms10 tray window / registered icon found for pid " + currentPid;
            }
            return false;
        }
}
}


