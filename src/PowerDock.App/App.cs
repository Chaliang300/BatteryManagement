using System;
using System.Windows;
using PowerDock.App.Shell;
using PowerDock.App.Windows;
using PowerDock.Core.Diagnostics;

namespace PowerDock.App
{
    public partial class App : Application
    {
        private TrayIconService _trayIconService;
        private MainWindow _mainWindow;

        /// <summary>Diagnostic-only switches used by --diagnose-tray-failure (S0-TRAY-03 branch).</summary>
        public static bool TrayFailureInjection;
        public static int AutoCloseMainWindowMs;

        public TrayIconService TrayIconService
        {
            get { return _trayIconService; }
        }

        public MainWindow MainWindowInstance
        {
            get { return _mainWindow; }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppLogger.Info("PowerDock application starting...");

            try
            {
                // 1. 初始化托盘服务
                _trayIconService = new TrayIconService(false, TrayFailureInjection);
                _trayIconService.Initialize();

                // 2. 初始化主窗口 (880x600, 圆角 8, DWM Acrylic)
                _mainWindow = new MainWindow();
                _mainWindow.TrayService = _trayIconService;
                MainWindow = _mainWindow;

                // 托盘点击联动唤醒主窗口
                _trayIconService.OnShowMainWindow = delegate
                {
                    if (_mainWindow != null)
                    {
                        _mainWindow.ActivateAndRestore();
                    }
                };

                // 判断是否以自启隐藏模式启动
                bool autostart = false;
                if (e.Args != null)
                {
                    foreach (var arg in e.Args)
                    {
                        if (string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase))
                        {
                            autostart = true;
                            break;
                        }
                    }
                }

                if (!autostart)
                {
                    _mainWindow.Show();
                    _mainWindow.ActivateAndRestore();
                }
                else
                {
                    AppLogger.Info("Started in --autostart mode. Main window remains hidden.");
                }

                if (AutoCloseMainWindowMs > 0)
                {
                    AppLogger.Info("Diagnostic mode: auto-closing the main window after " + AutoCloseMainWindowMs + "ms (tray failure injection=" + TrayFailureInjection + ").");
                    if (!_mainWindow.IsVisible)
                    {
                        _mainWindow.Show();
                    }
                    var timer = new System.Windows.Threading.DispatcherTimer();
                    timer.Interval = TimeSpan.FromMilliseconds(AutoCloseMainWindowMs);
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        AppLogger.Info("Diagnostic mode: closing the main window now.");
                        _mainWindow.Close();
                    };
                    timer.Start();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Exception during App OnStartup", ex);
            }
        }

        public void HandleActivateReceived()
        {
            if (_mainWindow != null)
            {
                _mainWindow.ActivateAndRestore();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppLogger.Info("PowerDock application exiting.");
            if (_trayIconService != null)
            {
                _trayIconService.Dispose();
                _trayIconService = null;
            }
            base.OnExit(e);
        }
    }
}

