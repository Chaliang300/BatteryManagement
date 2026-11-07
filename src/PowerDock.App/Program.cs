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
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            AppLogger.Initialize(baseDir);

            if (args != null && args.Length > 0)
            {
                string cmd = args[0].Trim().ToLowerInvariant();

                if (cmd == "--self-test")
                {
                    return SelfTestRunner.Run();
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
                    Console.WriteLine("  --diagnose [sec]  Run diagnostic sampling mode");
                    Console.WriteLine("  --help            Show this help text");
                    return 0;
                }
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
    }
}
