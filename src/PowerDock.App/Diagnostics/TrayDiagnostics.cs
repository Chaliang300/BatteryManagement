using System;
using System.Drawing;
using System.IO;
using System.Text;
using PowerDock.App.Shell;
using PowerDock.Core.Diagnostics;

namespace PowerDock.App.Diagnostics
{
    /// <summary>
    /// --diagnose-tray: creates the real tray icon, verifies it through Shell_NotifyIconGetRect and
    /// writes data/tray-diagnose.json. Exit code 0 only when a non-empty tray rectangle was found.
    /// </summary>
    public static class TrayDiagnostics
    {
        public static int Run()
        {
            TrayIconService service = null;
            bool initialized = false;
            string detail = null;
            Rectangle rect = Rectangle.Empty;
            int uid = -1;
            string windowClass = null;
            string error = null;

            try
            {
                service = new TrayIconService();
                initialized = service.Initialize();
                detail = service.LastRegistrationDetail;
                rect = service.RegistrationRect;
                uid = service.RegistrationUid;
                windowClass = service.RegistrationWindowClass;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                detail = error;
                AppLogger.Error("--diagnose-tray failed", ex);
            }
            finally
            {
                if (service != null)
                {
                    try { service.Dispose(); } catch { }
                }
            }

            bool passed = initialized && rect.Width > 0 && rect.Height > 0;
            string json = BuildJson(initialized, passed, rect, uid, windowClass, detail, error);
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string dataDir = Path.Combine(baseDir, "data");
            string path = Path.Combine(dataDir, "tray-diagnose.json");
            try
            {
                if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                File.WriteAllText(path, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("--diagnose-tray could not write " + path + ": " + ex.Message);
            }

            Console.WriteLine("[PowerDock Tray Diagnose] " + (passed ? "PASSED" : "FAILED"));
            Console.WriteLine("[PowerDock Tray Diagnose] initialized=" + initialized + ", rect=" + rect);
            Console.WriteLine("[PowerDock Tray Diagnose] uid=" + uid + ", class=" + (windowClass ?? "<none>"));
            Console.WriteLine("[PowerDock Tray Diagnose] detail=" + (detail ?? "<none>"));
            Console.WriteLine("[PowerDock Tray Diagnose] output=" + path);
            return passed ? 0 : 1;
        }

        private static string BuildJson(bool initialized, bool passed, Rectangle rect, int uid, string windowClass, string detail, string error)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"timestampUtc\": \"" + DateTime.UtcNow.ToString("o") + "\",");
            sb.AppendLine("  \"initialized\": " + (initialized ? "true" : "false") + ",");
            sb.AppendLine("  \"passed\": " + (passed ? "true" : "false") + ",");
            sb.AppendLine("  \"rect\": { \"left\": " + rect.Left + ", \"top\": " + rect.Top + ", \"right\": " + rect.Right + ", \"bottom\": " + rect.Bottom + ", \"width\": " + rect.Width + ", \"height\": " + rect.Height + " },");
            sb.AppendLine("  \"uid\": " + uid + ",");
            sb.AppendLine("  \"windowClass\": \"" + Escape(windowClass) + "\",");
            sb.AppendLine("  \"detail\": \"" + Escape(detail) + "\",");
            sb.AppendLine("  \"error\": \"" + Escape(error) + "\"");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        }
    }
}
