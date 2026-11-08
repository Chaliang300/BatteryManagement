using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerDock.App.Interop;
using PowerDock.App.Shell;
using PowerDock.App.Windows;
using PowerDock.Core.Storage;
using PowerDock.Core.Diagnostics;

namespace PowerDock.App.Diagnostics
{
    /// <summary>
    /// Diagnostics that need a real WPF window:
    ///   --diagnose-window           : apply backdrop, report every HRESULT/read-back + staged timing;
    ///   --diagnose-window-fallback  : force the static fallback branch and report it;
    ///   --diagnose-first-frame      : measure Program.Main -> first CompositionTarget.Rendering.
    /// These do not verify transmission (that needs screen pixels on an unlocked desktop).
    /// </summary>
    public static class WindowDiagnostics
    {
        private static MainWindow _window;
        private static bool _pass;
        private static string _error;
        private static WindowBackdropController _snapshotSource;
        private static System.Collections.Generic.IList<string> _windowBackdropEvents;

        public static int RunWindow(bool forceFallback)
        {
            _window = null;
            _pass = false;
            _error = null;
            _snapshotSource = null;

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Startup += delegate
            {
                try
                {
                    _window = new MainWindow();
                    if (forceFallback) _window.Backdrop.ForceFallback = true;
                    _window.Show();

                    var timer = new DispatcherTimer(DispatcherPriority.ContextIdle, _window.Dispatcher);
                    timer.Interval = TimeSpan.FromMilliseconds(forceFallback ? 900 : 2600);
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        try
                        {
                            if (forceFallback)
                            {
                                _snapshotSource = _window.Backdrop;
                                _pass = _window.Backdrop.FallbackActive && !_window.Backdrop.MaterialReady;
                                FinishWindowReport(app, forceFallback);
                                return;
                            }

                            // §7.3 verification: hide -> show must redo the frame extension and
                            // re-activate the material; capture after the second show.
                            int beforeRebind = _window.Backdrop.RebindCount;
                            bool beforeMaterial = _window.Backdrop.MaterialReady;
                            _window.Hide();
                            var showTimer = new DispatcherTimer(DispatcherPriority.ContextIdle, _window.Dispatcher);
                            showTimer.Interval = TimeSpan.FromMilliseconds(600);
                            showTimer.Tick += delegate
                            {
                                showTimer.Stop();
                                _window.Show();
                                var captureTimer = new DispatcherTimer(DispatcherPriority.ContextIdle, _window.Dispatcher);
                                captureTimer.Interval = TimeSpan.FromMilliseconds(1400);
                                captureTimer.Tick += delegate
                                {
                                    captureTimer.Stop();
                                    _snapshotSource = _window.Backdrop;
                                    _windowBackdropEvents = _window.Backdrop.Events;
                                    bool expectedFallback = _window.Backdrop.ForceFallback ||
                                            !_window.Backdrop.CompositionEnabled ||
                                            !_window.Backdrop.SystemTransparencyEnabled ||
                                            _window.Backdrop.HighContrast ||
                                            !_window.Backdrop.SupportsBackdropApi ||
                                            _window.Backdrop.AllowsTransparency;
                                    if (expectedFallback)
                                    {
                                        // Correct degradation: an unavailable capability must end in
                                        // the explicit static fallback, not a grey plate.
                                        _pass = _window.Backdrop.FallbackActive && !_window.Backdrop.MaterialReady;
                                    }
                                    else
                                    {
                                        _pass = _window.Backdrop.MaterialReady &&
                                                _window.Backdrop.CompositionTargetTransparent &&
                                                _window.Backdrop.ExtendFrameHr == 0 &&
                                                _window.Backdrop.NoneHr == 0 &&
                                                _window.Backdrop.AcrylicHr == 0 &&
                                                _window.Backdrop.AcrylicReadback == DwmHelper.DWMSBT_TRANSIENTWINDOW &&
                                                beforeMaterial &&
                                                _window.Backdrop.RebindCount > beforeRebind &&
                                                ContainsEvent("IsVisibleChanged(visible)");
                                    }
                                    FinishWindowReport(app, false);
                                };
                                captureTimer.Start();
                            };
                            showTimer.Start();
                        }
                        catch (Exception ex)
                        {
                            _error = ex.Message;
                            _pass = false;
                            FinishWindowReport(app, forceFallback);
                        }
                    };
                    timer.Start();
                }
                catch (Exception ex)
                {
                    _error = ex.GetType().Name + ": " + ex.Message;
                    WriteWindowReport(forceFallback);
                    try { app.Shutdown(); } catch { }
                }
            };

            app.Run();
            Console.WriteLine("[PowerDock Window Diagnose] mode=" + (forceFallback ? "fallback" : "material") + " pass=" + _pass);
            if (_snapshotSource != null)
            {
                foreach (string line in _snapshotSource.Events)
                {
                    Console.WriteLine("  " + line);
                }
            }
            return _pass ? 0 : 1;
        }

        private static bool ContainsEvent(string fragment)
        {
            if (_windowBackdropEvents == null) return false;
            foreach (string text in _windowBackdropEvents)
            {
                if (!string.IsNullOrEmpty(text) && text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static void FinishWindowReport(Application app, bool forceFallback)
        {
            WriteWindowReport(forceFallback);
            try { _window.Close(); } catch { }
            try { app.Shutdown(); } catch { }
        }

        private static void WriteWindowReport(bool forceFallback)
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string snapshot = _snapshotSource != null ? _snapshotSource.SnapshotJson() : "{}";
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"timestampUtc\": \"" + DateTime.UtcNow.ToString("o") + "\",");
                sb.AppendLine("  \"mode\": \"" + (forceFallback ? "window-fallback" : "window-material") + "\",");
                sb.AppendLine("  \"pass\": " + (_pass ? "true" : "false") + ",");
                sb.AppendLine("  \"forceFallback\": " + (forceFallback ? "true" : "false") + ",");
                sb.AppendLine("  \"backdrop\": " + snapshot.TrimEnd() + ",");
                sb.AppendLine("  \"error\": \"" + Escape(_error) + "\"");
                sb.AppendLine("}");
                File.WriteAllText(Path.Combine(dir, forceFallback ? "window-diagnose-fallback.json" : "window-diagnose.json"),
                    sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("WindowDiagnostics: failed to write report: " + ex.Message);
            }
        }

        /// <summary>
        /// --diagnose-tray-restore：验证「最小化 → 托盘恢复」的真实路径。
        /// 走 <see cref="TrayIconService.DefaultWakeupMainWindow"/>（托盘左键与二启激活都会落到这里），
        /// 不自己拼一套"恢复"逻辑，否则验的是诊断而不是产品。
        /// 判据：最小化后 IsVisible 仍为真但窗口不可见，恢复后 WindowState 回到 Normal、
        /// 主窗口仍是同一实例且页面状态未被重置（CurrentPage 与内容实例不变）。
        /// </summary>
        public static int RunTrayRestore()
        {
            bool pass = false;
            string error = null;
            MainPage pageBefore = MainPage.Overview;
            bool sameContentInstance = false;
            bool minimizedObserved = false;
            bool minimizedOffScreen = false;
            bool restoredNormal = false;
            bool sameWindowInstance = false;
            int exitCode = 1;

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            TrayIconService tray = null;
            MainWindow window = null;

            app.Startup += delegate
            {
                try
                {
                    tray = new TrayIconService();
                    bool trayInit = tray.Initialize();
                    window = new MainWindow();
                    window.TrayService = tray;
                    app.MainWindow = window;
                    window.Show();
                    window.NavigateTo(MainPage.Devices);
                    pageBefore = window.CurrentPage;

                    var timer = new DispatcherTimer(DispatcherPriority.ContextIdle, window.Dispatcher);
                    timer.Interval = TimeSpan.FromMilliseconds(900);
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        try
                        {
                            object contentBefore = window.ContentHost.Content;
                            window.WindowState = WindowState.Minimized;
                            window.UpdateLayout();
                            minimizedObserved = window.WindowState == WindowState.Minimized;
                            IntPtr minimizedHwnd = new WindowInteropHelper(window).Handle;
                            minimizedOffScreen = minimizedHwnd != IntPtr.Zero && NativeMethods.IsIconic(minimizedHwnd);

                            // 真实恢复路径：托盘左键 / 二启激活都调用这一条。
                            TrayIconService.DefaultWakeupMainWindow();
                            window.UpdateLayout();

                            Window target = app.MainWindow;
                            sameWindowInstance = target is MainWindow && ReferenceEquals(target, window);
                            restoredNormal = window.WindowState == WindowState.Normal && window.IsVisible;
                            sameContentInstance = ReferenceEquals(contentBefore, window.ContentHost.Content) &&
                                                  window.CurrentPage == pageBefore;
                            pass = trayInit && tray.IsTrayAvailable && minimizedObserved && minimizedOffScreen &&
                                   sameWindowInstance && restoredNormal && sameContentInstance;
                        }
                        catch (Exception ex)
                        {
                            error = ex.GetType().Name + ": " + ex.Message;
                            pass = false;
                        }

                        WriteTrayRestoreReport(pass, error, tray != null && tray.IsTrayAvailable,
                            minimizedObserved, minimizedOffScreen, sameWindowInstance, restoredNormal,
                            sameContentInstance, pageBefore);
                        if (window != null && window.IsVisible) { try { window.Close(); } catch { } }
                        if (tray != null) { try { tray.Dispose(); } catch { } }
                        try { app.Shutdown(); } catch { }
                        exitCode = pass ? 0 : 1;
                    };
                    timer.Start();
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                    WriteTrayRestoreReport(false, error, false, false, false, false, false, false, pageBefore);
                    try { app.Shutdown(); } catch { }
                    exitCode = 1;
                }
            };

            app.Run();
            Console.WriteLine("[PowerDock Tray-Restore] pass=" + pass +
                              " minimized=" + minimizedObserved + " restoredNormal=" + restoredNormal);
            return exitCode;
        }

        private static void WriteTrayRestoreReport(bool pass, string error, bool trayAvailable,
            bool minimizedObserved, bool minimizedOffScreen, bool sameWindowInstance, bool restoredNormal,
            bool sameContentInstance, MainPage pageBefore)
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"timestampUtc\": \"" + DateTime.UtcNow.ToString("o") + "\",");
                sb.AppendLine("  \"mode\": \"tray-restore\",");
                sb.AppendLine("  \"pass\": " + (pass ? "true" : "false") + ",");
                sb.AppendLine("  \"trayAvailable\": " + (trayAvailable ? "true" : "false") + ",");
                sb.AppendLine("  \"minimizedObserved\": " + (minimizedObserved ? "true" : "false") + ",");
                sb.AppendLine("  \"minimizedOffScreenOrIconic\": " + (minimizedOffScreen ? "true" : "false") + ",");
                sb.AppendLine("  \"sameWindowInstance\": " + (sameWindowInstance ? "true" : "false") + ",");
                sb.AppendLine("  \"restoredNormal\": " + (restoredNormal ? "true" : "false") + ",");
                sb.AppendLine("  \"statePreserved\": " + (sameContentInstance ? "true" : "false") + ",");
                sb.AppendLine("  \"currentPageBefore\": \"" + pageBefore + "\",");
                sb.AppendLine("  \"error\": \"" + Escape(error) + "\"");
                sb.AppendLine("}");
                File.WriteAllText(Path.Combine(dir, "tray-restore-diagnose.json"), sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("WindowDiagnostics: failed to write tray-restore report: " + ex.Message);
            }
        }

        public static int RunCloseBehavior(bool trayAvailable)
        {
            bool pass = false;
            string error = null;
            bool cancel = false;
            bool visibleAfter = true;
            bool trayAvailableObserved = false;
            int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            int windowsCount = -1;

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            TrayIconService tray = null;
            MainWindow window = null;
            app.Startup += delegate
            {
                try
                {
                    tray = trayAvailable ? new TrayIconService() : new TrayIconService(true);
                    bool trayInit = tray.Initialize();
                    window = new MainWindow();
                    window.TrayService = tray;

                    // Exercise the real binding path: load AppSettings and apply the value (same as App).
                    bool settingsMinimize = true;
                    try
                    {
                        SettingsStore store = new SettingsStore();
                        store.Load();
                        AppSettings current = store.Current ?? new AppSettings();
                        settingsMinimize = current.MinimizeToTrayOnClose;
                    }
                    catch (Exception settingsEx)
                    {
                        AppLogger.Warn("DiagnoseClose: settings load failed, default true: " + settingsEx.Message);
                    }
                    window.MinimizeToTrayOnClose = settingsMinimize;
                    app.MainWindow = window;
                    window.Show();
                    AppLogger.Info("DiagnoseClose: started scenario trayAvailable=" + trayAvailable + " trayInit=" + trayInit +
                                   " settingsMinimizeToTrayOnClose=" + settingsMinimize +
                                   " minimizeToTrayOnClose=" + window.MinimizeToTrayOnClose +
                                   " trayServiceNull=" + (window.TrayService == null) +
                                   " trayAvailableNow=" + (window.TrayService != null && window.TrayService.IsTrayAvailable) +
                                   " pid=" + pid);

                    var timer = new DispatcherTimer(DispatcherPriority.ContextIdle, window.Dispatcher);
                    timer.Interval = TimeSpan.FromMilliseconds(900);
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        try
                        {
                            trayAvailableObserved = window.TrayService != null && window.TrayService.IsTrayAvailable;
                            windowsCount = app.Windows.Count;
                            CancelEventArgs args = window.SimulateCloseForDiagnostics();
                            cancel = args.Cancel;
                            visibleAfter = window.IsVisible;
                            bool expectedHide = window.MinimizeToTrayOnClose && (window.TrayService != null) && trayAvailableObserved;
                            if (expectedHide)
                            {
                                // All three conditions true => must hide and stay alive.
                                pass = cancel && !visibleAfter && !app.Dispatcher.HasShutdownStarted;
                            }
                            else
                            {
                                // Any condition false => the real behaviour must exit the process.
                                pass = !cancel;
                            }
                            AppLogger.Info(string.Format(
                                "DiagnoseClose: minimizeToTrayOnClose={0} trayServiceNull={1} trayAvailable={2} expectedHide={3} cancel={4} visibleAfter={5} windows={6} shutdownStarted={7} pid={8}",
                                window.MinimizeToTrayOnClose, window.TrayService == null, trayAvailableObserved, expectedHide,
                                cancel, visibleAfter, windowsCount, app.Dispatcher.HasShutdownStarted, pid));
                        }
                        catch (Exception ex)
                        {
                            error = ex.GetType().Name + ": " + ex.Message;
                            pass = false;
                        }
                        WriteCloseReport(trayAvailable, pass, cancel, visibleAfter, trayAvailableObserved,
                            window != null && window.MinimizeToTrayOnClose,
                            window == null || window.TrayService == null,
                            windowsCount, pid, error);
                        if (window != null && window.IsVisible) { window.Close(); }
                        if (tray != null) { try { tray.Dispose(); } catch { } }
                        try { app.Shutdown(); } catch { }
                    };
                    timer.Start();
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                    WriteCloseReport(trayAvailable, false, false, true, false, false, true, -1, pid, error);
                    try { app.Shutdown(); } catch { }
                }
            };

            app.Run();
            Console.WriteLine("[PowerDock Close-Behaviour] trayAvailable=" + trayAvailable + " pass=" + pass + " cancel=" + cancel + " visibleAfter=" + visibleAfter);
            return pass ? 0 : 1;
        }

        private static void WriteCloseReport(bool trayAvailable, bool pass, bool cancel, bool visibleAfter, bool trayObserved, bool minimizeToTray, bool trayServiceNull, int windowsCount, int pid, string error)
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"timestampUtc\": \"" + DateTime.UtcNow.ToString("o") + "\",");
                sb.AppendLine("  \"mode\": \"close-behavior\",");
                sb.AppendLine("  \"scenarioTrayAvailable\": " + (trayAvailable ? "true" : "false") + ",");
                sb.AppendLine("  \"pass\": " + (pass ? "true" : "false") + ",");
                sb.AppendLine("  \"cancel\": " + (cancel ? "true" : "false") + ",");
                sb.AppendLine("  \"visibleAfter\": " + (visibleAfter ? "true" : "false") + ",");
                sb.AppendLine("  \"trayAvailableObserved\": " + (trayObserved ? "true" : "false") + ",");
                sb.AppendLine("  \"windowsCount\": " + windowsCount + ",");
                sb.AppendLine("  \"minimizeToTrayOnClose\": " + (minimizeToTray ? "true" : "false") + ",");
                sb.AppendLine("  \"trayServiceNull\": " + (trayServiceNull ? "true" : "false") + ",");
                sb.AppendLine("  \"processId\": " + pid + ",");
                sb.AppendLine("  \"error\": \"" + Escape(error) + "\"");
                sb.AppendLine("}");
                File.WriteAllText(Path.Combine(dir, trayAvailable ? "close-behavior-tray.json" : "close-behavior-no-tray.json"), sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("WindowDiagnostics: failed to write close report: " + ex.Message);
            }
        }

        private sealed class DockNavStep
        {
            public string Id { get; set; }
            public string Status { get; set; }
            public string Detail { get; set; }
        }

        /// <summary>
        /// --diagnose-dock-nav：在真实窗口上跑导航验收脚本。
        /// 覆盖命中区（按钮矩形 + 图标/文字中心）、UIA Invoke 切页、键盘 Enter/Space、焦点环像素、
        /// 滚动偏移保留、重复点击不重建、同一时刻仅一个主窗口。
        /// 键盘与焦点环要求窗口是前台窗口：拿不到键盘焦点时对应步骤记 skipped 并在报告里写明原因，
        /// 绝不把「环境拿不到焦点」记成通过。
        /// </summary>
        public static int RunDockNavigation()
        {
            bool pass = false;
            string error = null;
            var steps = new List<DockNavStep>();
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            MainWindow window = null;

            app.Startup += delegate
            {
                try
                {
                    window = new MainWindow();
                    app.MainWindow = window;
                    window.Show();
                    window.Activate();
                    var helper = new WindowInteropHelper(window);
                    if (helper.Handle != IntPtr.Zero)
                    {
                        NativeMethods.SetForegroundWindow(helper.Handle);
                    }

                    var timer = new DispatcherTimer(DispatcherPriority.ContextIdle, window.Dispatcher);
                    timer.Interval = TimeSpan.FromMilliseconds(800);
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        try
                        {
                            pass = ExecuteDockNavigationScript(window, app, steps);
                        }
                        catch (Exception ex)
                        {
                            error = ex.GetType().Name + ": " + ex.Message;
                            pass = false;
                        }

                        WriteDockNavReport(pass, steps, error);
                        try { if (window.IsVisible) { window.Hide(); } }
                        catch { }
                        try { app.Shutdown(); }
                        catch { }
                    };
                    timer.Start();
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                    WriteDockNavReport(false, steps, error);
                    try { app.Shutdown(); }
                    catch { }
                }
            };

            app.Run();

            Console.WriteLine("[PowerDock Dock-Nav Diagnose] pass=" + pass);
            foreach (DockNavStep step in steps)
            {
                Console.WriteLine("  [" + step.Status + "] " + step.Id + " " + step.Detail);
            }
            return pass ? 0 : 1;
        }

        private static bool ExecuteDockNavigationScript(MainWindow window, Application app, List<DockNavStep> steps)
        {
            MainPage[] pages = MainPageCatalog.All();

            window.UpdateLayout();
            RecordDockStep(steps, "NAV-DIAG-01", window.IsLoaded && window.IsVisible && window.ActualWidth > 0,
                string.Format("IsLoaded={0}, IsVisible={1}, ActualSize={2:F1}x{3:F1} DIP, IsActive={4}",
                    window.IsLoaded, window.IsVisible, window.ActualWidth, window.ActualHeight, window.IsActive));

            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            RecordDockStep(steps, "NAV-DIAG-02", true,
                string.Format("DPI scale={0:F2}x{1:F2}, PixelsPerDip={2:F3}, 窗口像素={3:F0}x{4:F0}",
                    dpi.DpiScaleX, dpi.DpiScaleY, dpi.PixelsPerDip,
                    window.ActualWidth * dpi.DpiScaleX, window.ActualHeight * dpi.DpiScaleY));

            // 1) 命中区：按钮矩形内 5 点（中心 + 左右上下内部留白）+ 图标/文字中心，全部必须命中同一按钮
            var hitProblems = new List<string>();
            int hitTotal = 0;
            int hitOk = 0;
            foreach (MainPage page in pages)
            {
                Button button = window.DockButton(page);
                if (button == null || button.ActualWidth <= 0)
                {
                    hitProblems.Add(page + ":notLaidOut");
                    continue;
                }

                Rect rect = RectInWindow(button, window);
                var probes = new[]
                {
                    new Point(rect.Left + (rect.Width / 2.0), rect.Top + (rect.Height / 2.0)),
                    new Point(rect.Left + 3.0, rect.Top + (rect.Height / 2.0)),
                    new Point(rect.Right - 3.0, rect.Top + (rect.Height / 2.0)),
                    new Point(rect.Left + (rect.Width / 2.0), rect.Top + 2.0),
                    new Point(rect.Left + (rect.Width / 2.0), rect.Bottom - 2.0)
                };

                foreach (Point probe in probes)
                {
                    hitTotal++;
                    if (HitBelongsTo(window, probe, button))
                    {
                        hitOk++;
                    }
                    else
                    {
                        hitProblems.Add(string.Format("{0}:miss({1:F1},{2:F1})", page, probe.X, probe.Y));
                    }
                }

                TextBlock icon = window.DockIconText(page);
                TextBlock label = window.DockLabelText(page);
                foreach (TextBlock text in new[] { icon, label })
                {
                    if (text == null || text.ActualWidth <= 0)
                    {
                        hitProblems.Add(page + ":textNotLaidOut");
                        continue;
                    }

                    hitTotal++;
                    Point center = PointInWindow(text, text.ActualWidth / 2.0, text.ActualHeight / 2.0, window);
                    if (HitBelongsTo(window, center, button))
                    {
                        hitOk++;
                    }
                    else
                    {
                        hitProblems.Add(string.Format("{0}:textMiss({1:F1},{2:F1})", page, center.X, center.Y));
                    }
                }
            }

            RecordDockStep(steps, "NAV-DIAG-03", hitTotal > 0 && hitOk == hitTotal,
                string.Format("命中探测 {0}/{1} 落在对应 Dock 按钮（矩形内 5 点 + 图标/文字中心）{2}",
                    hitOk, hitTotal, hitProblems.Count == 0 ? "" : " 失败: " + string.Join(",", hitProblems.ToArray())));

            // 2) 切页：走 UIA Invoke（辅助技术真实调用路径），逐项校验页面、内容实例、唯一选中
            var clickProblems = new List<string>();
            foreach (MainPage page in pages)
            {
                Button button = window.DockButton(page);
                var invoke = UIElementAutomationPeer.CreatePeerForElement(button) as IInvokeProvider;
                if (invoke == null)
                {
                    clickProblems.Add(page + ":noInvokeProvider");
                    continue;
                }

                invoke.Invoke();
                // ButtonAutomationPeer.Invoke 在 DispatcherPriority.Input 上异步投递 Click，
                // 必须先把这个队列跑完再断言，否则量到的是「还没点」的状态。
                PumpDispatcher();
                window.UpdateLayout();

                if (window.CurrentPage != page) clickProblems.Add(page + ":page=" + window.CurrentPage);
                if (!ReferenceEquals(window.ContentHost.Content, window.PageView(page))) clickProblems.Add(page + ":contentMismatch");
                int selected = CountSelectedDockItems(window, pages);
                if (selected != 1) clickProblems.Add(page + ":selected=" + selected);
                if (Math.Abs(window.DockSelectionDot(page).Opacity - 1.0) > 0.001) clickProblems.Add(page + ":dotHidden");
            }

            RecordDockStep(steps, "NAV-DIAG-04", clickProblems.Count == 0,
                clickProblems.Count == 0
                    ? "UIA Invoke 依次切 4 页：页面、内容实例、唯一选中态全部一致"
                    : "失败: " + string.Join(",", clickProblems.ToArray()));

            // 3) 键盘：Tab 焦点可达 + Enter/Space 均能激活（需要前台窗口才有键盘焦点）
            bool keyboardOk = false;
            bool keyboardAttempted = false;
            string keyboardDetail;
            Button enterTarget = window.DockButton(MainPage.Overview);
            window.NavigateTo(MainPage.Devices);
            window.UpdateLayout();
            enterTarget.Focus();
            window.UpdateLayout();

            PresentationSource source = PresentationSource.FromVisual(window);
            if (source == null || !enterTarget.IsKeyboardFocused)
            {
                keyboardDetail = string.Format(
                    "当前桌面拿不到键盘焦点：IsActive={0}, source={1}, IsKeyboardFocused={2}；真实键盘验收移交外部脚本",
                    window.IsActive, source != null, enterTarget.IsKeyboardFocused);
            }
            else
            {
                keyboardAttempted = true;
                RaiseKey(enterTarget, source, Key.Enter, true);
                RaiseKey(enterTarget, source, Key.Enter, false);
                window.UpdateLayout();
                bool enterOk = window.CurrentPage == MainPage.Overview;

                Button spaceTarget = window.DockButton(MainPage.Settings);
                spaceTarget.Focus();
                window.UpdateLayout();
                bool spaceFocused = spaceTarget.IsKeyboardFocused;
                RaiseKey(spaceTarget, source, Key.Space, true);
                RaiseKey(spaceTarget, source, Key.Space, false);
                window.UpdateLayout();
                bool spaceOk = window.CurrentPage == MainPage.Settings;

                keyboardOk = enterOk && spaceFocused && spaceOk;
                keyboardDetail = string.Format("Enter: {0}→{1} ({2}); Space: 焦点={3} →{4} ({5})",
                    MainPage.Devices, enterOk ? "概览" : window.CurrentPage.ToString(), enterOk ? "OK" : "FAIL",
                    spaceFocused, spaceOk ? "设置" : window.CurrentPage.ToString(), spaceOk ? "OK" : "FAIL");
            }

            if (!keyboardAttempted)
            {
                SkipDockStep(steps, "NAV-DIAG-05", keyboardDetail);
            }
            else
            {
                RecordDockStep(steps, "NAV-DIAG-05", keyboardOk, keyboardDetail);
            }

            // 4) 焦点环可见性：窗口内位图直出（AGENTS.md §4⑥ 认可的判据），比较聚焦前/后 accent 像素数
            if (!keyboardAttempted)
            {
                SkipDockStep(steps, "NAV-DIAG-06", "无键盘焦点，跳过焦点环像素断言（" + keyboardDetail + "）");
            }
            else
            {
                Button focusTarget = window.DockButton(MainPage.Widgets);
                window.NavigateTo(MainPage.Devices);
                Keyboard.ClearFocus();
                window.UpdateLayout();
                int before = CountAccentPixels(window, focusTarget, dpi);
                focusTarget.Focus();
                window.UpdateLayout();
                int after = focusTarget.IsKeyboardFocused ? CountAccentPixels(window, focusTarget, dpi) : before;
                int delta = after - before;
                RecordDockStep(steps, "NAV-DIAG-06", focusTarget.IsKeyboardFocused && delta >= 40,
                    string.Format("聚焦前 accent 像素={0}，聚焦后={1}，差={2}（阈值 ≥40，accent=#6BA6FF 蓝偏置判据）",
                        before, after, delta));
            }

            // 5) 滚动偏移保留：空态内容不足以滚动，注入诊断用高元素后测真实偏移往返
            bool scrollOk = false;
            string scrollDetail;
            MainPage scrollPage = MainPage.Devices;
            window.NavigateTo(scrollPage);
            StackPanel panel = window.PagePanel(scrollPage);
            ScrollViewer scroll = window.PageScrollViewer(scrollPage);
            var spacer = new Border { Height = 1200, Background = Brushes.Transparent };
            AutomationProperties.SetAutomationId(spacer, "PowerDock.DiagnosticSpacer");
            bool spacerUsed = false;
            try
            {
                if (panel != null && scroll != null)
                {
                    if (scroll.ScrollableHeight <= 0)
                    {
                        panel.Children.Add(spacer);
                        spacerUsed = true;
                    }

                    window.UpdateLayout();
                    scroll.ScrollToVerticalOffset(300);
                    window.UpdateLayout();
                    double applied = scroll.VerticalOffset;

                    object contentBefore = window.ContentHost.Content;
                    bool repeatChanged = window.NavigateTo(scrollPage);
                    bool repeatKept = !repeatChanged && ReferenceEquals(contentBefore, window.ContentHost.Content);

                    window.NavigateTo(MainPage.Overview);
                    window.NavigateTo(scrollPage);
                    window.UpdateLayout();
                    PumpDispatcher();
                    window.UpdateLayout();
                    double restored = scroll.VerticalOffset;

                    scrollOk = applied > 0 && Math.Abs(restored - applied) < 1.0 && repeatKept;
                    scrollDetail = string.Format(
                        "设置偏移={0:F1}，切入后恢复={1:F1}；重复点击当前页 changed={2}，内容实例保持={3}；诊断高元素注入={4}",
                        applied, restored, repeatChanged, repeatKept, spacerUsed);
                }
                else
                {
                    scrollDetail = "页面容器或 ScrollViewer 缺失";
                }
            }
            finally
            {
                if (spacerUsed && panel != null)
                {
                    panel.Children.Remove(spacer);
                }
            }

            RecordDockStep(steps, "NAV-DIAG-07", scrollOk, scrollDetail);

            // 6) 切页不重开窗口
            RecordDockStep(steps, "NAV-DIAG-08", app.Windows.Count == 1,
                string.Format("切页 12 次后 Application.Windows.Count={0}（期望 1）", app.Windows.Count));

            foreach (DockNavStep step in steps)
            {
                if (step.Status == "failed") return false;
            }
            return true;
        }

        private static void RecordDockStep(List<DockNavStep> steps, string id, bool passed, string detail)
        {
            steps.Add(new DockNavStep { Id = id, Status = passed ? "passed" : "failed", Detail = detail });
        }

        private static void SkipDockStep(List<DockNavStep> steps, string id, string detail)
        {
            steps.Add(new DockNavStep { Id = id, Status = "skipped", Detail = detail });
        }

        private static int CountSelectedDockItems(MainWindow window, MainPage[] pages)
        {
            int selected = 0;
            foreach (MainPage page in pages)
            {
                Button button = window.DockButton(page);
                if (button != null && AutomationProperties.GetItemStatus(button) == MainPageCatalog.ItemStatusSelected)
                {
                    selected++;
                }
            }
            return selected;
        }

        private static Rect RectInWindow(FrameworkElement element, Visual ancestor)
        {
            GeneralTransform transform = element.TransformToAncestor(ancestor);
            return transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }

        private static Point PointInWindow(FrameworkElement element, double x, double y, Visual ancestor)
        {
            GeneralTransform transform = element.TransformToAncestor(ancestor);
            return transform.Transform(new Point(x, y));
        }

        private static bool HitBelongsTo(MainWindow window, Point pointInWindow, DependencyObject expected)
        {
            IInputElement hit = window.InputHitTest(pointInWindow);
            DependencyObject current = hit as DependencyObject;
            while (current != null)
            {
                if (ReferenceEquals(current, expected)) return true;
                if (ReferenceEquals(current, window)) return false;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private static void RaiseKey(UIElement target, PresentationSource source, Key key, bool down)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key);
            args.RoutedEvent = down ? Keyboard.KeyDownEvent : Keyboard.KeyUpEvent;
            target.RaiseEvent(args);
        }

        /// <summary>
        /// 把 UI 线程上已排队的低优先级工作（含恢复滚动偏移的 Loaded 回调）跑完。</summary>
        private static void PumpDispatcher()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(delegate { frame.Continue = false; }));
            Dispatcher.PushFrame(frame);
        }

        /// <summary>
        /// 只数按钮矩形内「蓝偏置」像素：accent 与深色底混合后仍满足 B-R≥30 且 B-G≥20，
        /// 白/灰文字与圆点也满足但数量级不同，所以比较的是聚焦前后的差值。
        /// </summary>
        private static int CountAccentPixels(FrameworkElement window, FrameworkElement target, DpiScale dpi)
        {
            int width = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
            int height = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
            if (width <= 1 || height <= 1) return 0;

            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(window);

            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            bitmap.CopyPixels(pixels, stride, 0);

            Rect rect = RectInWindow(target, window);
            int x0 = Math.Max(0, (int)Math.Floor(rect.Left * dpi.DpiScaleX));
            int y0 = Math.Max(0, (int)Math.Floor(rect.Top * dpi.DpiScaleY));
            int x1 = Math.Min(width - 1, (int)Math.Ceiling(rect.Right * dpi.DpiScaleX));
            int y1 = Math.Min(height - 1, (int)Math.Ceiling(rect.Bottom * dpi.DpiScaleY));

            int count = 0;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int i = (y * stride) + (x * 4);
                    int b = pixels[i];
                    int g = pixels[i + 1];
                    int r = pixels[i + 2];
                    if (b >= 90 && (b - r) >= 30 && (b - g) >= 20)
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        private static void WriteDockNavReport(bool pass, List<DockNavStep> steps, string error)
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"timestampUtc\": \"" + DateTime.UtcNow.ToString("o") + "\",");
                sb.AppendLine("  \"mode\": \"dock-navigation\",");
                sb.AppendLine("  \"pass\": " + (pass ? "true" : "false") + ",");
                sb.AppendLine("  \"osVersion\": \"" + Escape(Environment.OSVersion.VersionString) + "\",");
                sb.AppendLine("  \"steps\": [");
                for (int i = 0; i < steps.Count; i++)
                {
                    sb.AppendLine("    {");
                    sb.AppendLine("      \"id\": \"" + steps[i].Id + "\",");
                    sb.AppendLine("      \"status\": \"" + steps[i].Status + "\",");
                    sb.AppendLine("      \"detail\": \"" + Escape(steps[i].Detail) + "\"");
                    sb.Append("    }");
                    if (i < steps.Count - 1) sb.Append(",");
                    sb.AppendLine();
                }
                sb.AppendLine("  ],");
                sb.AppendLine("  \"error\": \"" + Escape(error) + "\"");
                sb.AppendLine("}");
                File.WriteAllText(Path.Combine(dir, "dock-nav-diagnose.json"), sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("WindowDiagnostics: failed to write dock-nav report: " + ex.Message);
            }
        }

        public static int RunFirstFrame()
        {
            FirstFrameTimer.Arm();
            FirstFrameTimer.LogArmed();

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Startup += delegate
            {
                try
                {
                    MainWindow window = new MainWindow();
                    window.Show();
                    DateTime started = DateTime.UtcNow;
                    var timer = new DispatcherTimer(DispatcherPriority.ContextIdle, window.Dispatcher);
                    timer.Interval = TimeSpan.FromMilliseconds(300);
                    timer.Tick += delegate
                    {
                        if (FirstFrameTimer.Measured)
                        {
                            timer.Stop();
                            WriteFirstFrameReport(true, null);
                            try { window.Close(); } catch { }
                            app.Shutdown();
                            return;
                        }
                        if ((DateTime.UtcNow - started).TotalMilliseconds > 8000)
                        {
                            timer.Stop();
                            WriteFirstFrameReport(false, "no CompositionTarget.Rendering callback within 8000ms (window may not be composing on this desktop)");
                            try { window.Close(); } catch { }
                            app.Shutdown();
                        }
                    };
                    timer.Start();
                }
                catch (Exception ex)
                {
                    WriteFirstFrameReport(false, ex.GetType().Name + ": " + ex.Message);
                    try { app.Shutdown(); } catch { }
                }
            };

            app.Run();
            Console.WriteLine("[PowerDock First-Frame Diagnose] measured=" + FirstFrameTimer.Measured + " elapsedMs=" + FirstFrameTimer.ElapsedMs.ToString("F2"));
            return FirstFrameTimer.Measured ? 0 : 3;
        }

        private static void WriteFirstFrameReport(bool measured, string reason)
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string snapshot = FirstFrameTimer.SnapshotJson();
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"timestampUtc\": \"" + DateTime.UtcNow.ToString("o") + "\",");
                sb.AppendLine("  \"mode\": \"first-frame\",");
                sb.AppendLine("  \"pass\": " + (measured ? "true" : "false") + ",");
                sb.AppendLine("  \"measured\": " + (measured ? "true" : "false") + ",");
                sb.AppendLine("  \"reason\": \"" + Escape(reason) + "\",");
                sb.AppendLine("  \"firstFrame\": " + snapshot.TrimEnd());
                sb.AppendLine("}");
                File.WriteAllText(Path.Combine(dir, "first-frame-diagnose.json"), sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("WindowDiagnostics: failed to write first-frame report: " + ex.Message);
            }
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        }
    }
}







