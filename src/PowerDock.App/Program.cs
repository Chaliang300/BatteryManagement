using System;
using System.IO;
using System.Threading;
using System.Windows;
using PowerDock.App.Diagnostics;
using PowerDock.App.Interop;
using PowerDock.App.Shell;
using PowerDock.Core.Diagnostics;

namespace PowerDock.App
{
    public static class Program
    {
        private const string FeedbackCaption = "PowerDock - 无法唤醒已有实例";
        private const int FeedbackAutoCloseMs = 4000;

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
                else if (cmd == "--diagnose-close-tray")
                {
                    return WindowDiagnostics.RunCloseBehavior(true);
                }
                else if (cmd == "--diagnose-close-no-tray")
                {
                    return WindowDiagnostics.RunCloseBehavior(false);
                }
                else if (cmd == "--diagnose-tray")
                {
                    return TrayDiagnostics.Run();
                }
                else if (cmd == "--diagnose-widgets")
                {
                    return WidgetDiagnostics.Run();
                }
                else if (cmd == "--diagnose-read")
                {
                    int seconds = 20;
                    int parsedSeconds;
                    if (args.Length > 1 && int.TryParse(args[1], out parsedSeconds))
                    {
                        seconds = parsedSeconds;
                    }

                    Console.WriteLine(string.Format("[PowerDock Diagnose] Sampling device discovery and readings for {0} seconds...", seconds));
                    return ReadDiagnosticsRunner.Run(seconds);
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
                    Console.WriteLine("  --diagnose-read [sec]  Sample discovery, link evidence, online state, reading source and failures");
                    Console.WriteLine("  --diagnose-window          Report DWM backdrop HRESULTs, read-backs and staged activation");
                    Console.WriteLine("  --diagnose-window-fallback Force the static fallback branch and report it");
                    Console.WriteLine("  --diagnose-first-frame     Measure Program.Main -> first CompositionTarget.Rendering");
                    Console.WriteLine("  --diagnose-close-tray      Exercise real close behavior with tray available (hide, stay alive)");
                    Console.WriteLine("  --diagnose-close-no-tray   Exercise real close behavior without tray (process exit)");
                    Console.WriteLine("  --diagnose-tray-failure  Inject tray failure and verify close=>process exit (diagnostic)");
                    Console.WriteLine("  --diagnose [sec]  Run diagnostic sampling mode");
                    Console.WriteLine("  --autostart       Start hidden (used by the logon autostart entry)");
                    Console.WriteLine("  --no-feedback     Suppress the secondary-instance failure dialog (automation)");
                    Console.WriteLine("  --help            Show this help text");
                    Console.WriteLine();
                    Console.WriteLine("Single-instance exit codes (secondary instance only):");
                    Console.WriteLine("  0  success: a running primary was activated");
                    Console.WriteLine("  20 no primary instance: mutex is held but the readiness event/pipe is absent");
                    Console.WriteLine("  21 primary not ready: the primary is starting but its pipe is not listening yet");
                    Console.WriteLine("  22 foreground activation denied: the primary flashed its taskbar button");
                    Console.WriteLine("  23 pipe client identity rejected: client token SID != current user SID");
                    Console.WriteLine("  24 protocol/response timeout: bounded request/response failed");
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

            bool suppressFeedback = false;
            if (args != null)
            {
                foreach (string arg in args)
                {
                    if (string.Equals(arg, "--no-feedback", StringComparison.OrdinalIgnoreCase))
                    {
                        suppressFeedback = true;
                    }
                }
            }

            SingleInstanceService singleInstance = new SingleInstanceService();
            try
            {
                Func<int, ActivationResult> onActivate = delegate(int clientPid)
                {
                    AppLogger.Info("Main instance activation triggered via named pipe. clientPid=" + clientPid);
                    Application currentApp = Application.Current;
                    if (currentApp == null)
                    {
                        return ActivationResult.NoWindow;
                    }

                    Window targetWindow = currentApp.MainWindow;
                    if (targetWindow == null && currentApp.Windows.Count > 0)
                    {
                        targetWindow = currentApp.Windows[0];
                    }
                    if (targetWindow == null)
                    {
                        AppLogger.Warn("Activation request received but no main window exists yet.");
                        return ActivationResult.NoWindow;
                    }

                    if (targetWindow.WindowState == WindowState.Minimized)
                    {
                        targetWindow.WindowState = WindowState.Normal;
                    }
                    targetWindow.Show();
                    targetWindow.Activate();

                    var helper = new System.Windows.Interop.WindowInteropHelper(targetWindow);
                    IntPtr handle = helper.Handle;
                    if (handle == IntPtr.Zero)
                    {
                        AppLogger.Warn("Activation request received but the main window has no HWND.");
                        return ActivationResult.NoWindow;
                    }

                    return singleInstance.ActivateWindow(handle) ? ActivationResult.Activated : ActivationResult.Denied;
                };

                if (!singleInstance.TryStartAsFirstInstance(onActivate))
                {
                    int exitCode = singleInstance.SecondaryExitCode;
                    AppLogger.Info("Secondary instance finished wake-up call and exiting. exit=" + exitCode);
                    // Foreground-denied (22) already gives feedback through the primary's taskbar
                    // flash; a modal dialog here would delay automated runs and could stack under
                    // concurrent launches. Other failures get a readable auto-closing dialog.
                    if (!suppressFeedback && exitCode != SingleInstanceExitCodes.Success && exitCode != SingleInstanceExitCodes.ForegroundDenied)
                    {
                        ShowSecondaryFeedback(exitCode, singleInstance.SecondaryFailureDetail);
                    }
                    return exitCode;
                }

                var app = new App();
                return app.Run();
            }
            finally
            {
                singleInstance.Dispose();
            }
        }

        /// <summary>
        /// Shows a readable, auto-closing warning for secondary-instance failures (never for the
        /// success path). The auto-close keeps automated acceptance runs bounded while still giving
        /// the user an explanation when they double-click the app and nothing appears.
        /// </summary>
        private static void ShowSecondaryFeedback(int exitCode, string detail)
        {
            string text = SingleInstanceExitCodes.Describe(exitCode);
            if (!string.IsNullOrEmpty(detail))
            {
                text += Environment.NewLine + Environment.NewLine + detail;
            }

            try
            {
                Thread watchdog = new Thread(delegate()
                {
                    Thread.Sleep(FeedbackAutoCloseMs);
                    try
                    {
                        IntPtr dialog = NativeMethods.FindWindow("#32770", FeedbackCaption);
                        if (dialog != IntPtr.Zero)
                        {
                            NativeMethods.PostMessage(dialog, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        }
                    }
                    catch
                    {
                    }
                });
                watchdog.IsBackground = true;
                watchdog.Name = "PowerDock_FeedbackWatchdog";
                watchdog.Start();

                NativeMethods.MessageBox(IntPtr.Zero, text, FeedbackCaption, NativeMethods.MB_OK | NativeMethods.MB_ICONWARNING);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Failed to show activation failure feedback: " + ex.Message);
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




