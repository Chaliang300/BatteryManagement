using System;
using System.IO;
using System.Text;

namespace PowerDock.Core.Diagnostics
{
    public static class AppLogger
    {
        private static readonly object SyncLock = new object();
        private static string _logPath;
        private const long MaxLogSizeBytes = 1024 * 1024;

        public static void Initialize(string directory)
        {
            try
            {
                if (string.IsNullOrEmpty(directory))
                    directory = AppDomain.CurrentDomain.BaseDirectory;

                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                _logPath = Path.Combine(directory, "powerdock.log");
            }
            catch
            {
            }
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        public static void Error(string message, Exception ex)
        {
            string text = message;
            if (ex != null)
            {
                text = string.Format("{0} | Exception: {1}: {2}\n{3}", message, ex.GetType().Name, ex.Message, ex.StackTrace);
            }
            Write("ERROR", text);
        }

        public static void Error(string message)
        {
            Error(message, null);
        }

        private static void Write(string level, string message)
        {
            if (string.IsNullOrEmpty(_logPath)) return;

            lock (SyncLock)
            {
                try
                {
                    var fileInfo = new FileInfo(_logPath);
                    if (fileInfo.Exists && fileInfo.Length > MaxLogSizeBytes)
                    {
                        var backupPath = _logPath + ".old";
                        if (File.Exists(backupPath))
                            File.Delete(backupPath);
                        File.Move(_logPath, backupPath);
                    }

                    string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}Z] [{1}] {2}{3}", DateTime.UtcNow, level, message, Environment.NewLine);
                    File.AppendAllText(_logPath, line, Encoding.UTF8);
                }
                catch
                {
                }
            }
        }
    }
}
