using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using PowerDock.Core.Diagnostics;

namespace PowerDock.App.Diagnostics
{
    /// <summary>
    /// Arms at the first line of Program.Main and marks the first CompositionTarget.Rendering
    /// callback. This is deliberately separate from process uptime: PERF-01 records uptime, while
    /// the first-frame number is only valid when a window actually rendered.
    /// </summary>
    public static class FirstFrameTimer
    {
        private static readonly object Gate = new object();
        private static readonly Stopwatch Watch = new Stopwatch();
        private static bool _armed;
        private static bool _handlerRegistered;
        private static bool _measured;
        private static double _elapsedMs = -1;
        private static string _buildType = "unknown";
        private static string _round = "1";
        private static DateTime _armedUtc = DateTime.MinValue;
        private static string _measuredUtc = null;

        public static bool Armed { get { lock (Gate) return _armed; } }
        public static bool HandlerRegistered { get { lock (Gate) return _handlerRegistered; } }
        public static bool Measured { get { lock (Gate) return _measured; } }
        public static double ElapsedMs { get { lock (Gate) return _elapsedMs; } }
        public static string BuildType { get { lock (Gate) return _buildType; } }
        public static string Round { get { lock (Gate) return _round; } }

        public static void Arm()
        {
            lock (Gate)
            {
                if (_armed) return;
                _armedUtc = DateTime.UtcNow;
                Watch.Start();
                _armed = true;
#if DEBUG
                _buildType = "Debug";
#else
                _buildType = "Release";
#endif
                string round = Environment.GetEnvironmentVariable("POWERDOCK_FRAME_ROUND");
                if (!string.IsNullOrEmpty(round)) _round = round;
            }
        }

        public static void LogArmed()
        {
            string buildType;
            string round;
            DateTime armedUtc;
            lock (Gate)
            {
                if (!_armed) return;
                buildType = _buildType;
                round = _round;
                armedUtc = _armedUtc;
            }
            AppLogger.Info(string.Format(
                "FirstFrame: armed build={0} round={1} osBuild={2} session={3} pid={4} at={5:o}",
                buildType, round, SafeBuild(), SafeSession(), SafePid(), armedUtc));
        }

        public static void MarkHandlerRegistered()
        {
            lock (Gate) { _handlerRegistered = true; }
            AppLogger.Info("FirstFrame: CompositionTarget.Rendering handler registered");
        }

        public static void NotifyFirstFrame()
        {
            string buildType;
            string round;
            double elapsed;
            lock (Gate)
            {
                if (!_armed || _measured) return;
                _measured = true;
                Watch.Stop();
                _elapsedMs = Watch.Elapsed.TotalMilliseconds;
                _measuredUtc = DateTime.UtcNow.ToString("o");
                buildType = _buildType;
                round = _round;
                elapsed = _elapsedMs;
            }

            AppLogger.Info(string.Format(
                "FirstFrame: measured elapsedMs={0:F2} build={1} round={2} osBuild={3} session={4} pid={5} measuredUtc={6}",
                elapsed, buildType, round, SafeBuild(), SafeSession(), SafePid(), _measuredUtc));
            WriteJson();
        }

        public static string SnapshotJson()
        {
            lock (Gate)
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"armed\": " + (_armed ? "true" : "false") + ",");
                sb.AppendLine("  \"handlerRegistered\": " + (_handlerRegistered ? "true" : "false") + ",");
                sb.AppendLine("  \"measured\": " + (_measured ? "true" : "false") + ",");
                sb.AppendLine("  \"elapsedMs\": " + (_measured ? _elapsedMs.ToString("F2") : "-1") + ",");
                sb.AppendLine("  \"buildType\": \"" + _buildType + "\",");
                sb.AppendLine("  \"round\": \"" + _round + "\",");
                sb.AppendLine("  \"armedUtc\": \"" + (_armed ? _armedUtc.ToString("o") : "") + "\",");
                sb.AppendLine("  \"measuredUtc\": \"" + (_measuredUtc ?? "") + "\",");
                sb.AppendLine("  \"osBuild\": \"" + SafeBuild() + "\",");
                sb.AppendLine("  \"sessionId\": " + SafeSession() + ",");
                sb.AppendLine("  \"pid\": " + SafePid());
                sb.AppendLine("}");
                return sb.ToString();
            }
        }

        private static void WriteJson()
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "first-frame.json"), SnapshotJson(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FirstFrame: failed to write data/first-frame.json: " + ex.Message);
            }
        }

        private static string SafeBuild()
        {
            try
            {
                object val = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", null);
                return val != null ? val.ToString() : Environment.OSVersion.Version.Build.ToString();
            }
            catch { return "unknown"; }
        }

        private static int SafeSession()
        {
            try { return Process.GetCurrentProcess().SessionId; } catch { return -1; }
        }

        private static int SafePid()
        {
            try { return Process.GetCurrentProcess().Id; } catch { return -1; }
        }
    }
}
