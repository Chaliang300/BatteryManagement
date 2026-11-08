using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
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







