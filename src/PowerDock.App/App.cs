using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using PowerDock.App.Acquisition;
using PowerDock.App.Shell;
using PowerDock.App.Windows;
using PowerDock.Core.Common;
using PowerDock.Core.Diagnostics;
using PowerDock.Core.State;
using PowerDock.Core.Storage;

namespace PowerDock.App
{
    public partial class App : Application
    {
        private TrayIconService _trayIconService;
        private MainWindow _mainWindow;
        private AcquisitionHost _acquisition;

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

        /// <summary>设备页、托盘与后续挂件共用的采集宿主；三处只消费它的同一份快照。</summary>
        public AcquisitionHost Acquisition
        {
            get { return _acquisition; }
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

                // Bind the close behaviour from settings (default true). Previously the property was
                // only hard-coded in the MainWindow constructor and AppSettings was never applied.
                try
                {
                    SettingsStore shellSettings = new SettingsStore();
                    shellSettings.Load();
                    AppSettings current = shellSettings.Current ?? new AppSettings();
                    _mainWindow.Settings = shellSettings;
                    _mainWindow.MinimizeToTrayOnClose = current.MinimizeToTrayOnClose;
                    AppLogger.Info("Shell settings: MinimizeToTrayOnClose=" + _mainWindow.MinimizeToTrayOnClose +
                                   " source=" + shellSettings.FilePath);
                }
                catch (Exception settingsEx)
                {
                    _mainWindow.MinimizeToTrayOnClose = true;
                    AppLogger.Warn("Failed to load shell settings; MinimizeToTrayOnClose defaults to true: " + settingsEx.Message);
                }

                MainWindow = _mainWindow;

                // 托盘点击联动唤醒主窗口
                _trayIconService.OnShowMainWindow = delegate
                {
                    if (_mainWindow != null)
                    {
                        _mainWindow.ActivateAndRestore();
                    }
                };

                // 真实采集：设备页与托盘消费同一份快照（AGENTS §6.2）。
                StartAcquisition();

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

        /// <summary>
        /// 装配真实 Provider 并把同一份快照分发给设备页与托盘。
        /// 「刷新设备」必须走真实刷新路径，不能只是重画界面。
        /// </summary>
        private void StartAcquisition()
        {
            try
            {
                _acquisition = new AcquisitionHost(Dispatcher);
                bool overviewAttached = _mainWindow != null && _mainWindow.AttachOverview();
                bool devicesAttached = _mainWindow != null && _mainWindow.AttachDeviceSummary();
                bool widgetsAttached = _mainWindow != null && _mainWindow.AttachWidgetsPage();

                _acquisition.Subscribe(delegate(IReadOnlyList<DeviceState> snapshot)
                {
                    if (_mainWindow != null)
                    {
                        _mainWindow.RenderOverview(snapshot);
                        _mainWindow.RenderDevices(snapshot);
                        _mainWindow.RenderWidgets(snapshot);
                        _mainWindow.UpdateTitleSummary(snapshot);
                    }
                    ApplyTraySnapshot(snapshot);
                });

                _trayIconService.OnRefreshDevices = delegate
                {
                    AppLogger.Info("Manual refresh requested from tray.");
                    _acquisition.RefreshNowAsync().ContinueWith(delegate(Task t)
                    {
                        if (t.IsFaulted && t.Exception != null)
                        {
                            AppLogger.Error("Manual refresh failed", t.Exception.GetBaseException());
                        }
                    }, TaskScheduler.Default);
                };

                // 兜底刷新：S0 阶段先用 60s（AGENTS §5.6 空闲可见周期）；省电/锁屏暂停留到 S1 后续 PR。
                _acquisition.StartAsync(TimeSpan.FromSeconds(60)).ContinueWith(delegate(Task t)
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        AppLogger.Error("Acquisition start failed", t.Exception.GetBaseException());
                    }
                }, TaskScheduler.Default);

                if (!overviewAttached || !devicesAttached)
                {
                    AppLogger.Warn("One or more page views not attached (window content host missing).");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to start acquisition host", ex);
            }
        }

        /// <summary>
        /// 托盘只反映聚合结果：取最低电量设备；没有任何有效读数时呈现未知，绝不回退到固定 100%。
        /// </summary>
        private void ApplyTraySnapshot(IReadOnlyList<DeviceState> snapshot)
        {
            if (_trayIconService == null) return;
            TrayAggregate aggregate = AcquisitionHost.AggregateForTray(snapshot);
            _trayIconService.UpdateState(aggregate.Percent, aggregate.IsCharging, aggregate.IsOffline, aggregate.Tooltip);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppLogger.Info("PowerDock application exiting.");
            if (_acquisition != null)
            {
                _acquisition.Dispose();
                _acquisition = null;
            }
            if (_trayIconService != null)
            {
                _trayIconService.Dispose();
                _trayIconService = null;
            }
            base.OnExit(e);
        }
    }
}


