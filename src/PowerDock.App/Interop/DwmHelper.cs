using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using PowerDock.Core.Diagnostics;

namespace PowerDock.App.Interop
{
    public static class DwmHelper
    {
        // DWM Window Attributes
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        // DWM Window Corner Preference Values
        public const int DWMWCP_DEFAULT = 0;
        public const int DWMWCP_DONOTROUND = 1;
        public const int DWMWCP_ROUND = 2; // 8 DIP (AGENTS.md D11 & §7.1)
        public const int DWMWCP_ROUNDSMALL = 3; // 4 DIP

        // DWM System Backdrop Types (Win11 22621+)
        public const int DWMSBT_AUTO = 0;
        public const int DWMSBT_NONE = 1;
        public const int DWMSBT_MAINWINDOW = 2; // Mica
        public const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
        public const int DWMSBT_TABBEDWINDOW = 4; // Mica Alt

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;

            public MARGINS(int left, int right, int top, int bottom)
            {
                cxLeftWidth = left;
                cxRightWidth = right;
                cyTopHeight = top;
                cyBottomHeight = bottom;
            }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_FRAMECHANGED = 0x0020;

        public static bool SupportsAcrylicBackdrop()
        {
            try
            {
                object buildVal = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", null);
                if (buildVal != null)
                {
                    int buildNum;
                    if (int.TryParse(buildVal.ToString(), out buildNum))
                    {
                        return buildNum >= 22621;
                    }
                }
            }
            catch
            {
            }
            return Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22621;
        }

        public static bool ApplyMainWindowStyle(Window window, bool isDark)
        {
            if (window == null) return false;

            var helper = new WindowInteropHelper(window);
            IntPtr hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero) return false;

            bool success = true;

            // 1. 设置圆角为 8 DIP (DWMWCP_ROUND)
            try
            {
                int cornerPref = DWMWCP_ROUND;
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
                if (hr != 0) success = false;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("DwmSetWindowAttribute DWMWA_WINDOW_CORNER_PREFERENCE failed: " + ex.Message);
                success = false;
            }

            // 2. 设置深浅主题暗色模式标志
            try
            {
                int darkMode = isDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            }
            catch
            {
            }

            // 3. 设置 DWM Acrylic 材质 (Build 22621+) 或静态降级
            if (SupportsAcrylicBackdrop())
            {
                try
                {
                    int backdropType = DWMSBT_TRANSIENTWINDOW; // Acrylic
                    int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));

                    // 扩展客户区帧 (AGENTS.md §7.3)
                    MARGINS margins = new MARGINS(-1, -1, -1, -1);
                    DwmExtendFrameIntoClientArea(hwnd, ref margins);

                    // 触发 SWP_FRAMECHANGED
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("Apply Acrylic backdrop failed: " + ex.Message);
                }
            }

            return success;
        }
    }
}
