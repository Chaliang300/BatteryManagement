using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;
using Microsoft.Win32;
using PowerDock.App.Interop;
using PowerDock.Core.Diagnostics;

namespace PowerDock.App.Shell
{
    /// <summary>
    /// Per-size drawing geometry for the three-cell tray icon. The 16px variant is explicit on
    /// purpose: a naive 24/16 scale leaves a sub-pixel cell gap (0.47px) that anti-aliases into one
    /// continuous bright block. AGENTS.md §6.8 carries the same table.
    /// </summary>
    public struct TrayIconGeometry
    {
        public int Size;
        public float ShellX, ShellY, ShellW, ShellH, ShellRadius, Stroke;
        public float CapX, CapY, CapW, CapH, CapRadius;
        public float BarX0, BarX1, BarX2, BarY, BarW, BarH, BarRadius;
        public float InnerLeft, InnerRight;
        public float Gap
        {
            get { return BarX1 - BarX0 - BarW; }
        }
    }

    public static class BatteryTrayIconRenderer
    {
        public static readonly Color ColorNormalDark = Color.White;
        public static readonly Color ColorNormalLight = Color.FromArgb(0x1F, 0x23, 0x29); // 墨色
        public static readonly Color ColorChargingDark = ColorTranslator.FromHtml("#34D399");
        public static readonly Color ColorChargingLight = ColorTranslator.FromHtml("#12A870");
        public static readonly Color ColorLowDark = ColorTranslator.FromHtml("#FF9F0A");
        public static readonly Color ColorLowLight = ColorTranslator.FromHtml("#C25E00");
        public static readonly Color ColorOfflineDark = ColorTranslator.FromHtml("#8A8F99");
        public static readonly Color ColorOfflineLight = ColorTranslator.FromHtml("#7C838F");

        public const float UnlitFillOpacity = 0.14f;

        public static bool DetectIsDarkTaskbar()
        {
            try
            {
                object val = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", null);
                if (val != null && Convert.ToInt32(val) == 1)
                {
                    return false;
                }
            }
            catch
            {
            }
            return true;
        }

        public static Color GetSemanticColor(int? percent, bool isCharging, bool isOffline, bool isDarkTaskbar)
        {
            if (isOffline)
            {
                return isDarkTaskbar ? ColorOfflineDark : ColorOfflineLight;
            }
            if (isCharging)
            {
                return isDarkTaskbar ? ColorChargingDark : ColorChargingLight;
            }
            if (percent.HasValue && percent.Value < 20)
            {
                return isDarkTaskbar ? ColorLowDark : ColorLowLight;
            }
            return isDarkTaskbar ? ColorNormalDark : ColorNormalLight;
        }

        public static int CalculateLitBars(int? percent, bool isCharging, bool isOffline)
        {
            if (isOffline || !percent.HasValue)
            {
                return 0;
            }

            int p = percent.Value;
            if (p > 66) return 3;
            if (p > 33) return 2;
            if (p > 0) return 1;

            if (isCharging) return 1;
            return 0;
        }

        /// <summary>Explicit per-size geometry (AGENTS.md §6.8). Do not replace with a single scale.</summary>
        public static TrayIconGeometry GetGeometry(int size)
        {
            if (size <= 0) size = 24;
            float s = size / 24f;
            TrayIconGeometry g = new TrayIconGeometry();
            g.Size = size;
            g.Stroke = 1.6f * s;
            g.ShellX = 2.5f * s;
            g.ShellY = 6.5f * s;
            g.ShellW = 17f * s;
            g.ShellH = 11f * s;
            g.ShellRadius = 2.2f * s;
            g.CapX = 19.5f * s;
            g.CapY = 9.75f * s;
            g.CapW = 1.5f * s;
            g.CapH = 4.5f * s;
            g.CapRadius = 0.8f * s;
            g.InnerLeft = g.ShellX + g.Stroke;
            g.InnerRight = g.ShellX + g.ShellW - g.Stroke;
            g.BarY = 8.9f * s;
            g.BarH = 6.2f * s;
            g.BarRadius = Math.Max(0.5f, 1.1f * s);

            float cellW;
            float gap;
            switch (size)
            {
                case 16:
                    cellW = 2.10f; gap = 1.45f; break;
                case 20:
                    cellW = 2.80f; gap = 1.40f; break;
                case 24:
                    cellW = 3.60f; gap = 1.50f; break;
                case 28:
                    cellW = 4.20f; gap = 1.75f; break;
                case 32:
                    cellW = 4.80f; gap = 2.00f; break;
                default:
                    cellW = 3.60f * s; gap = 1.50f * s; break;
            }

            g.BarW = cellW;
            g.BarX0 = g.InnerLeft;
            g.BarX1 = g.BarX0 + cellW + gap;
            g.BarX2 = g.BarX1 + cellW + gap;
            return g;
        }

        public static GraphicsPath CreateRoundedRectangle(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 0.01f)
            {
                path.AddRectangle(rect);
                return path;
            }

            float diameter = radius * 2f;
            if (diameter > rect.Width) diameter = rect.Width;
            if (diameter > rect.Height) diameter = rect.Height;

            RectangleF arc = new RectangleF(rect.X, rect.Y, diameter, diameter);
            path.AddArc(arc, 180, 90);

            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);

            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);

            arc.X = rect.Left;
            path.AddArc(arc, 90, 90);

            path.CloseFigure();
            return path;
        }

        public static Bitmap RenderBitmap(int size, int? percent, bool isCharging, bool isOffline)
        {
            return RenderBitmap(size, percent, isCharging, isOffline, DetectIsDarkTaskbar());
        }

        public static Bitmap RenderBitmap(int size, int? percent, bool isCharging, bool isOffline, bool isDarkTaskbar)
        {
            if (size <= 0) size = 24;
            TrayIconGeometry geo = GetGeometry(size);

            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                Color mainColor = GetSemanticColor(percent, isCharging, isOffline, isDarkTaskbar);
                int litBars = CalculateLitBars(percent, isCharging, isOffline);

                // Battery shell (17x11, stroke 1.6 at 24px).
                RectangleF shellPathRect = new RectangleF(
                    geo.ShellX + geo.Stroke / 2f,
                    geo.ShellY + geo.Stroke / 2f,
                    geo.ShellW - geo.Stroke,
                    geo.ShellH - geo.Stroke);
                using (GraphicsPath shellPath = CreateRoundedRectangle(shellPathRect, geo.ShellRadius))
                using (Pen shellPen = new Pen(mainColor, geo.Stroke))
                {
                    g.DrawPath(shellPen, shellPath);
                }

                // Positive terminal cap.
                RectangleF capRect = new RectangleF(geo.CapX, geo.CapY, geo.CapW, geo.CapH);
                using (GraphicsPath capPath = CreateRoundedRectangle(capRect, geo.CapRadius))
                using (Brush capBrush = new SolidBrush(mainColor))
                {
                    g.FillPath(capBrush, capPath);
                }

                // Three cells with explicit per-size gaps; unlit cells at 0.14 opacity.
                float[] barX = new float[] { geo.BarX0, geo.BarX1, geo.BarX2 };
                Color unlitColor = Color.FromArgb((int)(255 * UnlitFillOpacity), mainColor);

                using (Brush litBrush = new SolidBrush(mainColor))
                using (Brush unlitBrush = new SolidBrush(unlitColor))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        RectangleF barRect = new RectangleF(barX[i], geo.BarY, geo.BarW, geo.BarH);
                        using (GraphicsPath barPath = CreateRoundedRectangle(barRect, geo.BarRadius))
                        {
                            Brush brush = (i < litBars) ? litBrush : unlitBrush;
                            g.FillPath(brush, barPath);
                        }
                    }
                }
            }

            return bmp;
        }

        public static Icon RenderIcon(int size, int? percent, bool isCharging, bool isOffline, bool isDarkTaskbar, out IntPtr hIcon)
        {
            using (Bitmap bmp = RenderBitmap(size, percent, isCharging, isOffline, isDarkTaskbar))
            {
                hIcon = bmp.GetHicon();
                return Icon.FromHandle(hIcon);
            }
        }

        public static Icon RenderIcon(int size, int? percent, bool isCharging, bool isOffline, out IntPtr hIcon)
        {
            return RenderIcon(size, percent, isCharging, isOffline, DetectIsDarkTaskbar(), out hIcon);
        }
    }

    public sealed class TrayIconService : IDisposable
    {
        private Forms.NotifyIcon _notifyIcon;
        private IntPtr _currentHicon;
        private Icon _currentIcon;
        private volatile bool _isTrayAvailable;
        private bool _disposed;
        private bool _isDarkTaskbar;
        private int _iconSize;
        private readonly bool _forceFailureForTest;
        private readonly bool _failureForDiagnostics;
        private bool _registered;
        private string _lastRegistrationDetail;
        private int _registrationUid = -1;
        private string _registrationWindowClass;
        private Rectangle _registrationRect;

        private Action _onShowMainWindow;
        private Action _onRefreshDevices;
        private Action _onExit;

        /// <summary>
        /// True only when Shell_NotifyIcon really registered the icon (verified with
        /// Shell_NotifyIconGetRect, not inferred from Visible=true).
        /// </summary>
        public bool IsTrayAvailable
        {
            get { return _isTrayAvailable; }
        }

        public bool IsDarkTaskbar
        {
            get { return _isDarkTaskbar; }
            set { _isDarkTaskbar = value; }
        }

        public int IconSize
        {
            get { return _iconSize; }
            set { _iconSize = value; }
        }

        public bool ShouldCancelCloseToTray
        {
            get { return _isTrayAvailable; }
        }

        public Forms.NotifyIcon NotifyIcon
        {
            get { return _notifyIcon; }
        }

        /// <summary>Human-readable registration outcome (last Initialize call).</summary>
        public string LastRegistrationDetail
        {
            get { return _lastRegistrationDetail; }
        }

        public bool IsRegistered
        {
            get { return _registered; }
        }

        public int RegistrationUid
        {
            get { return _registrationUid; }
        }

        public string RegistrationWindowClass
        {
            get { return _registrationWindowClass; }
        }

        public Rectangle RegistrationRect
        {
            get { return _registrationRect; }
        }

        public Action OnShowMainWindow
        {
            get { return _onShowMainWindow; }
            set { _onShowMainWindow = value; }
        }

        public Action OnRefreshDevices
        {
            get { return _onRefreshDevices; }
            set { _onRefreshDevices = value; }
        }

        public Action OnExit
        {
            get { return _onExit; }
            set { _onExit = value; }
        }

        public TrayIconService()
            : this(null, null, null, false)
        {
        }

        public TrayIconService(bool forceFailureForTest)
            : this(null, null, null, forceFailureForTest, false)
        {
        }

        /// <summary>Diagnostic-only: simulate a tray registration failure to exercise the
        /// "tray unavailable => close exits the process" branch (S0-TRAY-03). Not used by product flow.</summary>
        public TrayIconService(bool forceFailureForTest, bool failureForDiagnostics)
            : this(null, null, null, forceFailureForTest, failureForDiagnostics)
        {
        }

        public TrayIconService(Action onShowMainWindow, Action onRefreshDevices, Action onExit)
            : this(onShowMainWindow, onRefreshDevices, onExit, false)
        {
        }

        public TrayIconService(Action onShowMainWindow, Action onRefreshDevices, Action onExit, bool forceFailureForTest)
            : this(onShowMainWindow, onRefreshDevices, onExit, forceFailureForTest, false)
        {
        }

        private TrayIconService(Action onShowMainWindow, Action onRefreshDevices, Action onExit, bool forceFailureForTest, bool failureForDiagnostics)
        {
            _currentHicon = IntPtr.Zero;
            _onShowMainWindow = onShowMainWindow;
            _onRefreshDevices = onRefreshDevices;
            _onExit = onExit;
            _forceFailureForTest = forceFailureForTest;
            _failureForDiagnostics = failureForDiagnostics;
            _iconSize = GetDefaultTraySize();
            _isDarkTaskbar = BatteryTrayIconRenderer.DetectIsDarkTaskbar();
        }

        public static int GetDefaultTraySize()
        {
            try
            {
                int sz = Forms.SystemInformation.SmallIconSize.Width;
                if (sz >= 32) return 32;
                if (sz >= 24) return 24;
                if (sz >= 20) return 20;
                return 16;
            }
            catch
            {
                return 24;
            }
        }

        public bool Initialize()
        {
            if (_forceFailureForTest)
            {
                _isTrayAvailable = false;
                _registered = false;
                _lastRegistrationDetail = "forced failure for test";
                AppLogger.Warn("TrayIconService: 托盘初始化失败 (测试模拟降级)，标明 IsTrayAvailable = false");
                return false;
            }

            if (_failureForDiagnostics)
            {
                _isTrayAvailable = false;
                _registered = false;
                _lastRegistrationDetail = "diagnostic injection: simulated tray registration failure (S0-TRAY-03 branch)";
                AppLogger.Warn("TrayIconService: 诊断注入托盘注册失败（用于验证关窗即退出分支），IsTrayAvailable=false");
                return false;
            }

            try
            {
                _notifyIcon = new Forms.NotifyIcon();
                BuildContextMenu();
                _notifyIcon.Text = "PowerDock";

                // Root-cause fix: the icon must be assigned BEFORE Visible=true. The previous
                // implementation called UpdateState while _isTrayAvailable was still false, the
                // guard returned early, Icon stayed null and NIM_ADD silently failed.
                UpdateState(100, false, false, "PowerDock");
                if (_notifyIcon.Icon == null)
                {
                    _isTrayAvailable = false;
                    _registered = false;
                    _lastRegistrationDetail = "icon bitmap was not assigned before Visible=true";
                    AppLogger.Warn("TrayIconService: 初始化失败：" + _lastRegistrationDetail);
                    return false;
                }

                _notifyIcon.Visible = true;

                string detail;
                if (!VerifyRegistration(1500, out detail))
                {
                    _isTrayAvailable = false;
                    _registered = false;
                    _lastRegistrationDetail = detail;
                    AppLogger.Warn("TrayIconService: 托盘注册失败，IsTrayAvailable=false；原因：" + detail);
                    try { _notifyIcon.Visible = false; } catch { }
                    return false;
                }

                _registered = true;
                _isTrayAvailable = true;
                _lastRegistrationDetail = detail;
                AppLogger.Info("TrayIconService: 托盘图标注册成功，IsTrayAvailable=true；" + detail);
                return true;
            }
            catch (Exception ex)
            {
                _isTrayAvailable = false;
                _registered = false;
                _lastRegistrationDetail = ex.GetType().Name + ": " + ex.Message;
                AppLogger.Warn("TrayIconService: 系统托盘服务初始化失败: " + ex.Message + "，标明 IsTrayAvailable = false");
                if (_notifyIcon != null)
                {
                    try { _notifyIcon.Dispose(); } catch { }
                    _notifyIcon = null;
                }
                return false;
            }
        }

        private void BuildContextMenu()
        {
            // 右键菜单: "显示 PowerDock", "刷新设备", "-", "退出 PowerDock"
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();

            Forms.ToolStripMenuItem itemShow = new Forms.ToolStripMenuItem("显示 PowerDock");
            itemShow.Click += delegate(object sender, EventArgs e) { TriggerShowMainWindow(); };

            Forms.ToolStripMenuItem itemRefresh = new Forms.ToolStripMenuItem("刷新设备");
            itemRefresh.Click += delegate(object sender, EventArgs e) { TriggerRefreshDevices(); };

            Forms.ToolStripSeparator itemSep = new Forms.ToolStripSeparator();

            Forms.ToolStripMenuItem itemExit = new Forms.ToolStripMenuItem("退出 PowerDock");
            itemExit.Click += delegate(object sender, EventArgs e) { TriggerExit(); };

            menu.Items.Add(itemShow);
            menu.Items.Add(itemRefresh);
            menu.Items.Add(itemSep);
            menu.Items.Add(itemExit);

            _notifyIcon.ContextMenuStrip = menu;

            _notifyIcon.MouseClick += delegate(object sender, Forms.MouseEventArgs e)
            {
                if (e.Button == Forms.MouseButtons.Left)
                {
                    TriggerShowMainWindow();
                }
            };

            _notifyIcon.DoubleClick += delegate(object sender, EventArgs e) { TriggerShowMainWindow(); };
        }

        /// <summary>
        /// Verifies that this process's icon exists in the notification area by calling
        /// Shell_NotifyIconGetRect on our own NotifyIcon window (WinForms uses uID=1).
        /// Never infers success from NotifyIcon.Visible.
        /// </summary>
        private bool VerifyRegistration(int timeoutMs, out string detail)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            string lastError = null;
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                Forms.Application.DoEvents();
                Rectangle rect;
                int uid;
                string windowClass;
                string error;
                if (NativeMethods.TryGetTrayIconRect(out rect, out uid, out windowClass, out error))
                {
                    _registrationRect = rect;
                    _registrationUid = uid;
                    _registrationWindowClass = windowClass;
                    detail = string.Format("GetRect=({0},{1})-({2},{3}) {4}x{5}, uid={6}, class='{7}'",
                        rect.Left, rect.Top, rect.Right, rect.Bottom, rect.Width, rect.Height, uid, windowClass);
                    return true;
                }
                lastError = error;
                Thread.Sleep(100);
            }

            detail = "Shell_NotifyIconGetRect found no registered icon for this process within " + timeoutMs + "ms";
            if (!string.IsNullOrEmpty(lastError))
            {
                detail += "; last error: " + lastError;
            }
            return false;
        }

        public void UpdateState(int? percent, bool isCharging, bool isOffline)
        {
            string tip = FormatTooltip(percent, isCharging, isOffline);
            UpdateState(percent, isCharging, isOffline, tip);
        }

        public void UpdateState(int? percent, bool isCharging, bool isOffline, string tooltip)
        {
            // Allowed before the icon is visible/registered so the initial render can assign Icon
            // before Visible=true; availability is tracked separately by IsTrayAvailable.
            if (_disposed || _notifyIcon == null)
            {
                return;
            }

            try
            {
                IntPtr newHicon = IntPtr.Zero;
                Icon newIcon = BatteryTrayIconRenderer.RenderIcon(_iconSize, percent, isCharging, isOffline, _isDarkTaskbar, out newHicon);

                _notifyIcon.Icon = newIcon;

                if (!string.IsNullOrEmpty(tooltip))
                {
                    string safeTip = tooltip.Length > 63 ? tooltip.Substring(0, 60) + "..." : tooltip;
                    _notifyIcon.Text = safeTip;
                }

                // 释放上一轮的 GDI 资源
                if (_currentIcon != null)
                {
                    try { _currentIcon.Dispose(); } catch { }
                }
                if (_currentHicon != IntPtr.Zero)
                {
                    NativeMethods.DestroyIcon(_currentHicon);
                }

                _currentHicon = newHicon;
                _currentIcon = newIcon;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TrayIconService: UpdateState failed: " + ex.Message);
            }
        }

        public static string FormatTooltip(int? percent, bool isCharging, bool isOffline)
        {
            if (isOffline)
            {
                return "PowerDock - 离线";
            }
            if (isCharging)
            {
                return percent.HasValue
                    ? string.Format("PowerDock - {0}% (充电中)", percent.Value)
                    : "PowerDock - 充电中";
            }
            if (percent.HasValue)
            {
                return string.Format("PowerDock - {0}%", percent.Value);
            }
            return "PowerDock";
        }

        public void HandleMainWindowClosing(Window window, System.ComponentModel.CancelEventArgs e)
        {
            if (_isTrayAvailable)
            {
                if (e != null)
                {
                    e.Cancel = true;
                }
                if (window != null)
                {
                    window.Hide();
                }
                AppLogger.Info("MainWindow hidden to tray.");
            }
            else
            {
                // 托盘不可用时，主窗口关闭直接退出应用，不留隐形进程 (AGENTS.md §7.6)
                if (e != null)
                {
                    e.Cancel = false;
                }
                AppLogger.Warn("Tray unavailable. Closing main window directly shuts down application.");
                var app = Application.Current;
                if (app != null)
                {
                    app.Shutdown();
                }
            }
        }

        public static void DefaultWakeupMainWindow()
        {
            var app = Application.Current;
            if (app == null) return;

            if (!app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(DefaultWakeupMainWindow));
                return;
            }

            Window target = app.MainWindow;
            if (target == null && app.Windows.Count > 0)
            {
                target = app.Windows[0];
            }

            if (target != null)
            {
                if (target.WindowState == WindowState.Minimized)
                {
                    target.WindowState = WindowState.Normal;
                }
                target.Show();
                target.Activate();

                var helper = new System.Windows.Interop.WindowInteropHelper(target);
                IntPtr hwnd = helper.Handle;
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.SetForegroundWindow(hwnd);
                }
            }
        }

        private void TriggerShowMainWindow()
        {
            try
            {
                if (_onShowMainWindow != null)
                {
                    _onShowMainWindow();
                }
                else
                {
                    DefaultWakeupMainWindow();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Error triggering show main window", ex);
            }
        }

        private void TriggerRefreshDevices()
        {
            try
            {
                if (_onRefreshDevices != null)
                {
                    _onRefreshDevices();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Error triggering refresh devices", ex);
            }
        }

        private void TriggerExit()
        {
            try
            {
                if (_onExit != null)
                {
                    _onExit();
                }
                else
                {
                    var app = Application.Current;
                    if (app != null)
                    {
                        app.Shutdown();
                    }
                    else
                    {
                        Environment.Exit(0);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Error triggering exit", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_notifyIcon != null)
            {
                try
                {
                    _notifyIcon.Visible = false;
                    if (_notifyIcon.ContextMenuStrip != null)
                    {
                        _notifyIcon.ContextMenuStrip.Dispose();
                    }
                    _notifyIcon.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("Error disposing NotifyIcon: " + ex.Message);
                }
                _notifyIcon = null;
            }

            if (_currentIcon != null)
            {
                try { _currentIcon.Dispose(); } catch { }
                _currentIcon = null;
            }

            if (_currentHicon != IntPtr.Zero)
            {
                try
                {
                    NativeMethods.DestroyIcon(_currentHicon);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("Error destroying tray hIcon: " + ex.Message);
                }
                _currentHicon = IntPtr.Zero;
            }
        }
    }
}

