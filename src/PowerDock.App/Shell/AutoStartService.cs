using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using PowerDock.Core.Diagnostics;

namespace PowerDock.App.Shell
{
    public sealed class AutoStartService
    {
        public const string DefaultValueName = "PowerDock";
        public const string RunRegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private readonly string _exePath;
        private readonly string _valueName;

        public AutoStartService()
            : this(GetDefaultExecutablePath(), DefaultValueName)
        {
        }

        public AutoStartService(string exePath, string valueName)
        {
            if (string.IsNullOrEmpty(exePath))
            {
                exePath = GetDefaultExecutablePath();
            }
            _exePath = exePath;
            _valueName = string.IsNullOrEmpty(valueName) ? DefaultValueName : valueName;
        }

        public string ExePath
        {
            get { return _exePath; }
        }

        public string ValueName
        {
            get { return _valueName; }
        }

        public string ExpectedCommandLine
        {
            get
            {
                return string.Format("\"{0}\" --autostart", _exePath);
            }
        }

        public static string GetDefaultExecutablePath()
        {
            try
            {
                Process proc = Process.GetCurrentProcess();
                if (proc.MainModule != null && !string.IsNullOrEmpty(proc.MainModule.FileName))
                {
                    return Path.GetFullPath(proc.MainModule.FileName);
                }
            }
            catch
            {
            }

            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PowerDock.exe"));
        }

        public bool IsAutoStartEnabled()
        {
            RegistryKey runKey = null;
            try
            {
                runKey = Registry.CurrentUser.OpenSubKey(RunRegistrySubKey, false);
                if (runKey == null)
                {
                    return false;
                }

                object val = runKey.GetValue(_valueName, null);
                if (val == null)
                {
                    return false;
                }

                string strVal = val.ToString().Trim();
                string expected = ExpectedCommandLine.Trim();

                if (string.Equals(strVal, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (strVal.IndexOf(_exePath, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Warn(string.Format("Error reading AutoStart registry key: {0}", ex.Message));
                return false;
            }
            finally
            {
                if (runKey != null)
                {
                    try { runKey.Close(); } catch { }
                }
            }
        }

        public bool SetAutoStart(bool enable)
        {
            string expected = ExpectedCommandLine;
            RegistryKey runKey = null;
            try
            {
                // 1. 读当前状态
                runKey = Registry.CurrentUser.OpenSubKey(RunRegistrySubKey, true);
                if (runKey == null)
                {
                    runKey = Registry.CurrentUser.CreateSubKey(RunRegistrySubKey);
                }

                if (runKey == null)
                {
                    AppLogger.Warn("Failed to open or create HKCU Run registry key for writing.");
                    return false;
                }

                object currentVal = runKey.GetValue(_valueName, null);

                if (enable)
                {
                    // 2. 改与写
                    runKey.SetValue(_valueName, expected, RegistryValueKind.String);

                    // 3. 回读校验确认一致
                    object readBack = runKey.GetValue(_valueName, null);
                    string readBackStr = readBack != null ? readBack.ToString() : null;

                    if (!string.Equals(readBackStr, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        AppLogger.Warn(string.Format("AutoStart read-back mismatch! Expected: {0}, Actual: {1}", expected, readBackStr));
                        return false;
                    }

                    AppLogger.Info(string.Format("AutoStart successfully enabled and verified for {0}.", _valueName));
                    return true;
                }
                else
                {
                    // 2. 改与写：删除
                    if (currentVal != null)
                    {
                        runKey.DeleteValue(_valueName, false);
                    }

                    // 3. 回读校验确认无残留
                    object readBack = runKey.GetValue(_valueName, null);
                    if (readBack != null)
                    {
                        AppLogger.Warn(string.Format("AutoStart delete failed! Residual value exists: {0}", readBack));
                        return false;
                    }

                    AppLogger.Info(string.Format("AutoStart successfully disabled and verified clean for {0}.", _valueName));
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(string.Format("Error in SetAutoStart({0}) for {1}", enable, _valueName), ex);
                return false;
            }
            finally
            {
                if (runKey != null)
                {
                    try { runKey.Close(); } catch { }
                }
            }
        }
    }
}
