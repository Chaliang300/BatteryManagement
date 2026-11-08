using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using PowerDock.Core.Diagnostics;

namespace PowerDock.App.Interop
{
    /// <summary>
    /// Thin, checked DWM wrapper for the main-window backdrop. Every Set call returns the HRESULT
    /// and every applied attribute can be read back, so "material requested" is never confused with
    /// "material presented".
    /// </summary>
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
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int attrValue, int attrSize);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool compositionEnabled);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_FRAMECHANGED = 0x0020;

        public static string HResultText(int hr)
        {
            return "0x" + hr.ToString("X8");
        }

        /// <summary>Windows 11 22621 is the floor for DWMWA_SYSTEMBACKDROP_TYPE.</summary>
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

        public static bool IsCompositionEnabled()
        {
            try
            {
                bool enabled;
                if (DwmIsCompositionEnabled(out enabled) == 0)
                {
                    return enabled;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("DwmIsCompositionEnabled failed: " + ex.Message);
            }
            return false;
        }

        /// <summary>HKCU\...\Themes\Personalize\EnableTransparency = 0 disables system transparency.</summary>
        public static bool IsSystemTransparencyEnabled()
        {
            try
            {
                object val = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "EnableTransparency", null);
                if (val != null)
                {
                    return Convert.ToInt32(val) != 0;
                }
            }
            catch
            {
            }
            return true;
        }

        public static bool IsHighContrast
        {
            get
            {
                try { return SystemParameters.HighContrast; }
                catch { return false; }
            }
        }

        public static int SetCornerPreference(IntPtr hwnd, int preference, out int readBack)
        {
            readBack = -1;
            int value = preference;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
            if (hr == 0)
            {
                DwmGetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, out readBack, sizeof(int));
            }
            return hr;
        }

        public static int SetDarkMode(IntPtr hwnd, bool isDark, out int readBack)
        {
            readBack = -1;
            int value = isDark ? 1 : 0;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
            if (hr == 0)
            {
                DwmGetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, out readBack, sizeof(int));
            }
            return hr;
        }

        public static int SetBackdropType(IntPtr hwnd, int type, out int readBack)
        {
            readBack = -1;
            int value = type;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int));
            if (hr == 0)
            {
                DwmGetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, out readBack, sizeof(int));
            }
            return hr;
        }

        public static int GetBackdropType(IntPtr hwnd, out int readBack)
        {
            return DwmGetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, out readBack, sizeof(int));
        }

        /// <summary>Extends the DWM frame into the whole client area; must return 0 to be effective.</summary>
        public static int ExtendFrame(IntPtr hwnd)
        {
            MARGINS margins = new MARGINS(-1, -1, -1, -1);
            return DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }

        public static bool ForceFrameChanged(IntPtr hwnd)
        {
            return SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }
    }
}
