using System;
using System.IO;
using System.Threading;
using System.Windows;
using PowerDock.App.Diagnostics;
using PowerDock.App.Shell;
using PowerDock.Core.Diagnostics;

namespace PowerDock.App
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // First line of Main: arm the first-frame stopwatch (Program.Main -> first Rendering).
            FirstFrameTimer.Arm();

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            AppLogger.Initialize(baseDir);
            FirstFrameTimer.LogArmed();

            // Self-test/AUTO-01 values can survive if a previous self-test process was killed
            // between enable and disable; clean them up on every startup (PR-S0-04 / S0-AUTO-03).
            int leftoverAutoStartValues = AppShellCleanup();

            if (args != null && args.Length > 0)
            {
                string cmd = args[0].Trim().ToLowerInvariant();

                if (cmd == "--self-test")
                {
                    return SelfTestRunner.Run();
                }
                else if (cmd == "--diagnose-window")
                {
                    return WindowDiagnostics.RunWindow(false);
                }
                else if (cmd == "--diagnose-window-fallback")
                {
                    return WindowDiagnostics.RunWindow(true);
                }
                else if (cmd == "--diagnose-first-frame")
                {
                    return WindowDiagnostics.RunFirstFrame();
                }
                else if (cmd == "--diagnose-tray")
                {
                    return TrayDiagnostics.Run();
                }
                else if (cmd == "--diagnose")
                {
                    int seconds = 5;
                    int parsedSec;
                    if (args.Length > 1 && int.TryParse(args[1], out parsedSec))
                        seconds = parsedSec;

                    Console.WriteLine(string.Format("[PowerDock Diagnose] Running diagnostic mode for {0} seconds...", seconds));
                    Console.WriteLine(string.Format("[PowerDock Diagnose] OS: {0}", Environment.OSVersion.VersionString));
                    Console.WriteLine(string.Format("[PowerDock Diagnose] 64-bit: {0}", Environment.Is64BitProcess));
                    Console.WriteLine(string.Format("[PowerDock Diagnose] Base Directory: {0}", baseDir));
                    Thread.Sleep(seconds * 1000);
                    Console.WriteLine("[PowerDock Diagnose] Completed.");
                    return 0;
                }
                else if (cmd == "--help" || cmd == "-h" || cmd == "/?")
                {
                    Console.WriteLine("PowerDock - Windows Desktop Battery Center");
                    Console.WriteLine("Usage: PowerDock.exe [options]");
                    Console.WriteLine("Options:");
                    Console.WriteLine("  --self-test       Run built-in hardware, window, and storage self-tests");
                    Console.WriteLine("  --diagnose-tray   Verify tray icon registration via Shell_NotifyIconGetRect");
                    Console.WriteLine("  --diagnose-window          Report DWM backdrop HRESULTs, read-backs and staged activation");
                    Console.WriteLine("  --diagnose-window-fallback Force the static fallback branch and report it");
                    Console.WriteLine("  --diagnose-first-frame     Measure Program.Main -> first CompositionTarget.Rendering");
                    Console.WriteLine("  --diagnose-tray-failure  Inject tray failure and verify close=>process exit (diagnostic)");
                    Console.WriteLine("  --diagnose [sec]  Run diagnostic sampling mode");
                    Console.WriteLine("  --help            Show this help text");
                    return 0;
                }
            }

            bool trayFailureDiagnostic = false;
            if (args != null)
            {
                foreach (string arg in args)
                {
                    if (string.Equals(arg, "--diagnose-tray-failure", StringComparison.OrdinalIgnoreCase))
                    {
                        trayFailureDiagnostic = true;
                    }
                }
            }
            if (trayFailureDiagnostic)
            {
                App.TrayFailureInjection = true;
                App.AutoCloseMainWindowMs = 900;
                AppLogger.Info("Diagnostic tray failure mode: tray registration will fail; window auto-closes after 900ms.");
            }

            using (SingleInstanceService singleInstance = new SingleInstanceService())
            {
                Action onActivate = delegate()
                {
                    AppLogger.Info("Main instance activation triggered via named pipe.");
                    var currentApp = Application.Current;
                    if (currentApp != null)
                    {
                        Window targetWindow = currentApp.MainWindow;
                        if (targetWindow == null && currentApp.Windows.Count > 0)
                        {
                            targetWindow = currentApp.Windows[0];
                        }

                        if (targetWindow != null)
                        {
                            if (targetWindow.WindowState == WindowState.Minimized)
                            {
                                targetWindow.WindowState = WindowState.Normal;
                            }
                            targetWindow.Show();
                            targetWindow.Activate();

                            var helper = new System.Windows.Interop.WindowInteropHelper(targetWindow);
                            IntPtr handle = helper.Handle;
                            if (handle != IntPtr.Zero)
                            {
                                singleInstance.ActivateWindow(handle);
                            }
                        }
                    }
                };

                if (!singleInstance.TryStartAsFirstInstance(onActivate))
                {
                    AppLogger.Info("Secondary instance finished wake-up call and exiting.");
                    return 0;
                }

                var app = new App();
                return app.Run();
            }
        }

        private static int AppShellCleanup()
        {
            try
            {
                return PowerDock.App.Shell.AutoStartService.CleanupLeftoverTestValues();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Startup cleanup of autostart test values failed: " + ex.Message);
                return 0;
            }
        }
}
}




