using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows;
using Forms = System.Windows.Forms;
using Microsoft.Win32;
using PowerDock.App.Interop;
using PowerDock.Core.Diagnostics;

namespace PowerDock.App.Shell
{
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

            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                float scale = (float)size / 24.0f;
                g.ScaleTransform(scale, scale);

                Color mainColor = GetSemanticColor(percent, isCharging, isOffline, isDarkTaskbar);
                int litBars = CalculateLitBars(percent, isCharging, isOffline);

                // 24px 规格基线 (AGENTS.md §6.8):
                // 电池外壳 17x11 描边 1.6，外壳矩形 (2.5, 6.5, 17, 11)
                float stroke = 1.6f;
                RectangleF shellPathRect = new RectangleF(2.5f + stroke / 2f, 6.5f + stroke / 2f, 17f - stroke, 11f - stroke);
                using (GraphicsPath shellPath = CreateRoundedRectangle(shellPathRect, 2.2f))
                using (Pen shellPen = new Pen(mainColor, stroke))
                {
                    g.DrawPath(shellPen, shellPath);
                }

                // 电池正极触点端子 (Cap):
                // 贴附在外壳右侧 (X=19.5, Y=9.75, W=1.5, H=4.5)
                RectangleF capRect = new RectangleF(19.5f, 9.75f, 1.5f, 4.5f);
                using (GraphicsPath capPath = CreateRoundedRectangle(capRect, 0.8f))
                using (Brush capBrush = new SolidBrush(mainColor))
                {
                    g.FillPath(capBrush, capPath);
                }

                // 三格电量矩形:
                // 3.6x6.2, r1.1, 未点亮格 fill-opacity .14
                float[] barX = new float[] { 4.9f, 9.2f, 13.5f };
                float barY = 8.9f;
                float barW = 3.6f;
                float barH = 6.2f;

                Color unlitColor = Color.FromArgb((int)(255 * 0.14f), mainColor);

                using (Brush litBrush = new SolidBrush(mainColor))
                using (Brush unlitBrush = new SolidBrush(unlitColor))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        RectangleF barRect = new RectangleF(barX[i], barY, barW, barH);
                        using (GraphicsPath barPath = CreateRoundedRectangle(barRect, 1.1f))
                        {
                            Brush b = (i < litBars) ? litBrush : unlitBrush;
                            g.FillPath(b, barPath);
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
        private bool _isTrayAvailable;
        private bool _disposed;
        private bool _isDarkTaskbar;
        private int _iconSize;
        private readonly bool _forceFailureForTest;

        private Action _onShowMainWindow;
        private Action _onRefreshDevices;
        private Action _onExit;

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
            : this(null, null, null, forceFailureForTest)
        {
        }

        public TrayIconService(Action onShowMainWindow, Action onRefreshDevices, Action onExit)
            : this(onShowMainWindow, onRefreshDevices, onExit, false)
        {
        }

        public TrayIconService(Action onShowMainWindow, Action onRefreshDevices, Action onExit, bool forceFailureForTest)
        {
            _currentHicon = IntPtr.Zero;
            _onShowMainWindow = onShowMainWindow;
            _onRefreshDevices = onRefreshDevices;
            _onExit = onExit;
            _forceFailureForTest = forceFailureForTest;
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
                AppLogger.Warn("TrayIconService: 托盘初始化失败 (测试模拟降级)，标明 IsTrayAvailable = false");
                return false;
            }

            try
            {
                _notifyIcon = new Forms.NotifyIcon();

                // 右键菜单: "显示 PowerDock", "刷新设备", "-", "退出 PowerDock"
                Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();

                Forms.ToolStripMenuItem itemShow = new Forms.ToolStripMenuItem("显示 PowerDock");
                itemShow.Click += delegate(object sender, EventArgs e)
                {
                    TriggerShowMainWindow();
                };

                Forms.ToolStripMenuItem itemRefresh = new Forms.ToolStripMenuItem("刷新设备");
                itemRefresh.Click += delegate(object sender, EventArgs e)
                {
                    TriggerRefreshDevices();
                };

                Forms.ToolStripSeparator itemSep = new Forms.ToolStripSeparator();

                Forms.ToolStripMenuItem itemExit = new Forms.ToolStripMenuItem("退出 PowerDock");
                itemExit.Click += delegate(object sender, EventArgs e)
                {
                    TriggerExit();
                };

                menu.Items.Add(itemShow);
                menu.Items.Add(itemRefresh);
                menu.Items.Add(itemSep);
                menu.Items.Add(itemExit);

                _notifyIcon.ContextMenuStrip = menu;
                _notifyIcon.Text = "PowerDock";

                // 左键点击: 唤醒并置前主窗口委托
                _notifyIcon.MouseClick += delegate(object sender, Forms.MouseEventArgs e)
                {
                    if (e.Button == Forms.MouseButtons.Left)
                    {
                        TriggerShowMainWindow();
                    }
                };

                _notifyIcon.DoubleClick += delegate(object sender, EventArgs e)
                {
                    TriggerShowMainWindow();
                };

                // 初始图标渲染
                UpdateState(100, false, false, "PowerDock");

                _notifyIcon.Visible = true;
                _isTrayAvailable = true;
                AppLogger.Info("TrayIconService: 系统托盘服务初始化成功，IsTrayAvailable = true");
                return true;
            }
            catch (Exception ex)
            {
                _isTrayAvailable = false;
                AppLogger.Warn("TrayIconService: 系统托盘服务初始化失败: " + ex.Message + "，标明 IsTrayAvailable = false");
                if (_notifyIcon != null)
                {
                    try { _notifyIcon.Dispose(); } catch { }
                    _notifyIcon = null;
                }
                return false;
            }
        }

        public void UpdateState(int? percent, bool isCharging, bool isOffline)
        {
            string tip = FormatTooltip(percent, isCharging, isOffline);
            UpdateState(percent, isCharging, isOffline, tip);
        }

        public void UpdateState(int? percent, bool isCharging, bool isOffline, string tooltip)
        {
            if (_disposed || !_isTrayAvailable || _notifyIcon == null)
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
