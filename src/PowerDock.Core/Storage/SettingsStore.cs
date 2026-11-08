using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PowerDock.Core.Diagnostics;

namespace PowerDock.Core.Storage
{
    /// <summary>
    /// 提供应用程序配置的持久化存储与生命周期管理服务。
    /// 约束规范：
    /// 1. 保存过程执行「序列化 -> AtomicWrite -> 从磁盘回读反序列化 -> 校验关键字段」；
    /// 2. 校验成功更新 Current 并触发 SettingsChanged 事件；
    /// 3. 若写盘失败或校验不一致，自动回滚至先前有效配置并抛出异常；
    /// 4. 加载时若文件不存在或损坏，采用默认配置并保留上次有效配置。
    /// </summary>
    public sealed class SettingsStore
    {
        private readonly object _syncLock = new object();
        private readonly string _filePath;
        private AppSettings _current;
        private AppSettings _lastValidSettings;

        /// <summary>
        /// 当配置成功保存、通过磁盘回读校验并更新时触发。
        /// </summary>
        public event EventHandler<AppSettings> SettingsChanged;

        /// <summary>
        /// 获取当前的应用程序设置快照。
        /// </summary>
        public AppSettings Current
        {
            get
            {
                lock (_syncLock)
                {
                    return _current;
                }
            }
        }

        /// <summary>
        /// 获取当前配置文件的完整路径。
        /// </summary>
        public string FilePath
        {
            get { return _filePath; }
        }

        /// <summary>
        /// 使用默认路径 (%BaseDirectory%\settings.json) 初始化设置存储。
        /// </summary>
        public SettingsStore()
            : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"))
        {
        }

        /// <summary>
        /// 使用指定的配置文件路径初始化设置存储。
        /// </summary>
        /// <param name="filePath">配置文件路径，不能为 null 或空字符串。</param>
        public SettingsStore(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentNullException("filePath", "配置文件路径不能为 null 或空。");
            }

            _filePath = Path.GetFullPath(filePath);
            _current = new AppSettings();
            _lastValidSettings = _current.Clone();
        }

        /// <summary>
        /// 加载 settings.json。若文件不存在或损坏，采用默认配置并保留上次有效配置。
        /// </summary>
        public void Load()
        {
            lock (_syncLock)
            {
                if (!File.Exists(_filePath))
                {
                    if (_lastValidSettings != null)
                    {
                        _current = _lastValidSettings.Clone();
                    }
                    else
                    {
                        _current = new AppSettings();
                        _lastValidSettings = _current.Clone();
                    }
                    AppLogger.Info(string.Format("设置文件不存在: '{0}'，使用默认/保留配置。", _filePath));
                    return;
                }

                try
                {
                    string json = File.ReadAllText(_filePath, Encoding.UTF8);
                    AppSettings loaded = SettingsJson.Deserialize(json);

                    if (loaded == null)
                    {
                        throw new InvalidDataException("settings.json 反序列化结果为 null。");
                    }

                    NormalizeSettings(loaded);
                    _current = loaded;
                    _lastValidSettings = loaded.Clone();
                    AppLogger.Info(string.Format("成功加载设置: '{0}'", _filePath));
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(string.Format("加载 settings.json 失败或文件损坏: {0}。尝试回退恢复。", ex.Message));

                    string backupPath = _filePath + ".bak";
                    bool recoveredFromBackup = false;

                    if (File.Exists(backupPath))
                    {
                        try
                        {
                            string bakJson = File.ReadAllText(backupPath, Encoding.UTF8);
                            AppSettings bakLoaded = SettingsJson.Deserialize(bakJson);
                            if (bakLoaded != null)
                            {
                                NormalizeSettings(bakLoaded);
                                _current = bakLoaded;
                                _lastValidSettings = bakLoaded.Clone();
                                recoveredFromBackup = true;
                                AppLogger.Info(string.Format("已从备份文件恢复设置: '{0}'", backupPath));
                            }
                        }
                        catch (Exception bakEx)
                        {
                            AppLogger.Warn(string.Format("从备份文件恢复设置亦失败: {0}", bakEx.Message));
                        }
                    }

                    if (!recoveredFromBackup)
                    {
                        if (_lastValidSettings != null)
                        {
                            _current = _lastValidSettings.Clone();
                        }
                        else
                        {
                            _current = new AppSettings();
                            _lastValidSettings = _current.Clone();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 保存设置：序列化 -> AtomicWrite -> 从磁盘回读反序列化 -> 校验关键字段 -> 成功则更新 Current 并触发 SettingsChanged 事件；若校验不一致或写盘失败，回滚并抛出异常。
        /// </summary>
        /// <param name="settings">待持久化的设置对象，不能为 null。</param>
        public void Save(AppSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings", "待保存的设置不能为 null。");
            }

            AppSettings snapshotToBroadcast = null;

            lock (_syncLock)
            {
                AppSettings previousValid = _lastValidSettings != null ? _lastValidSettings.Clone() : _current.Clone();
                string backupPath = _filePath + ".bak";
                bool fileExistedBefore = File.Exists(_filePath);

                string originalBackupContent = null;
                if (fileExistedBefore && File.Exists(backupPath))
                {
                    try
                    {
                        originalBackupContent = File.ReadAllText(backupPath, Encoding.UTF8);
                    }
                    catch
                    {
                    }
                }

                AppSettings candidate = settings.Clone();
                NormalizeSettings(candidate);

                string json;
                try
                {
                    json = SettingsJson.Serialize(candidate);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("序列化设置失败", ex);
                    throw;
                }

                try
                {
                    // 1. 原子写入
                    AtomicFile.AtomicWrite(_filePath, json);

                    // 2. 从磁盘回读反序列化
                    string diskJson = File.ReadAllText(_filePath, Encoding.UTF8);
                    AppSettings diskSettings = SettingsJson.Deserialize(diskJson);
                    if (diskSettings == null)
                    {
                        throw new InvalidDataException("从磁盘回读反序列化结果为 null。");
                    }
                    NormalizeSettings(diskSettings);

                    // 3. 校验关键字段
                    if (!ValidateSettings(candidate, diskSettings))
                    {
                        throw new InvalidOperationException("设置持久化回读校验不一致：磁盘数据与待保存数据关键字段不符。");
                    }

                    // 4. 成功更新 Current
                    _current = diskSettings;
                    _lastValidSettings = diskSettings.Clone();
                    snapshotToBroadcast = _current.Clone();

                    AppLogger.Info(string.Format("设置已成功保存并校验通过: '{0}'", _filePath));
                }
                catch (Exception ex)
                {
                    AppLogger.Error(string.Format("保存设置失败，正在执行回滚: {0}", ex.Message), ex);

                    Rollback(fileExistedBefore, backupPath, originalBackupContent);

                    _current = previousValid.Clone();
                    throw;
                }
            }

            if (snapshotToBroadcast != null)
            {
                OnSettingsChanged(snapshotToBroadcast);
            }
        }

        private void Rollback(bool fileExistedBefore, string backupPath, string originalBackupContent)
        {
            try
            {
                if (fileExistedBefore)
                {
                    if (File.Exists(backupPath))
                    {
                        File.Copy(backupPath, _filePath, true);
                    }

                    if (originalBackupContent != null)
                    {
                        File.WriteAllText(backupPath, originalBackupContent, Encoding.UTF8);
                    }
                }
                else
                {
                    if (File.Exists(_filePath))
                    {
                        File.Delete(_filePath);
                    }
                }
            }
            catch (Exception rollbackEx)
            {
                AppLogger.Error(string.Format("回滚磁盘设置文件失败: '{0}'", _filePath), rollbackEx);
            }
        }

        private void OnSettingsChanged(AppSettings settings)
        {
            EventHandler<AppSettings> handler = SettingsChanged;
            if (handler != null)
            {
                try
                {
                    handler(this, settings);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("SettingsChanged 事件订阅者执行异常", ex);
                }
            }
        }

        private static void NormalizeSettings(AppSettings settings)
        {
            if (settings == null) return;

            if (settings.Theme == null)
            {
                settings.Theme = "dark";
            }
            if (settings.WidgetSize == null)
            {
                settings.WidgetSize = "medium";
            }

            if (settings.DeviceAliases == null)
            {
                settings.DeviceAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                settings.DeviceAliases = new Dictionary<string, string>(settings.DeviceAliases, StringComparer.OrdinalIgnoreCase);
            }

            if (settings.IgnoredDevices == null)
            {
                settings.IgnoredDevices = new List<string>();
            }

            if (settings.PinnedDevices == null)
            {
                settings.PinnedDevices = new List<string>();
            }

            if (settings.WidgetDevices == null)
            {
                settings.WidgetDevices = new List<string>();
            }
        }

        private static bool ValidateSettings(AppSettings expected, AppSettings actual)
        {
            if (expected == null || actual == null)
                return false;

            if (!string.Equals(expected.Theme, actual.Theme, StringComparison.Ordinal))
                return false;
            if (!string.Equals(expected.WidgetSize, actual.WidgetSize, StringComparison.Ordinal))
                return false;
            if (expected.WidgetTopMost != actual.WidgetTopMost)
                return false;
            if (Math.Abs(expected.WidgetLeft - actual.WidgetLeft) > 0.001)
                return false;
            if (Math.Abs(expected.WidgetTop - actual.WidgetTop) > 0.001)
                return false;
            if (expected.AutoStart != actual.AutoStart)
                return false;
            if (expected.MinimizeToTrayOnClose != actual.MinimizeToTrayOnClose)
                return false;
            if (expected.LowBatteryThreshold != actual.LowBatteryThreshold)
                return false;
            if (expected.AlertCooldownMinutes != actual.AlertCooldownMinutes)
                return false;
            if (expected.ShowStatusDot != actual.ShowStatusDot)
                return false;
            if (expected.ShowBatteryBar != actual.ShowBatteryBar)
                return false;
            if (expected.ShowPercentage != actual.ShowPercentage)
                return false;

            // DeviceAliases
            int expectedAliasCount = expected.DeviceAliases != null ? expected.DeviceAliases.Count : 0;
            int actualAliasCount = actual.DeviceAliases != null ? actual.DeviceAliases.Count : 0;
            if (expectedAliasCount != actualAliasCount)
                return false;

            if (expected.DeviceAliases != null && actual.DeviceAliases != null)
            {
                foreach (KeyValuePair<string, string> kvp in expected.DeviceAliases)
                {
                    string actualVal;
                    if (!actual.DeviceAliases.TryGetValue(kvp.Key, out actualVal))
                        return false;
                    if (!string.Equals(kvp.Value, actualVal, StringComparison.Ordinal))
                        return false;
                }
            }

            // IgnoredDevices
            int expectedIgnoredCount = expected.IgnoredDevices != null ? expected.IgnoredDevices.Count : 0;
            int actualIgnoredCount = actual.IgnoredDevices != null ? actual.IgnoredDevices.Count : 0;
            if (expectedIgnoredCount != actualIgnoredCount)
                return false;

            if (expected.IgnoredDevices != null && actual.IgnoredDevices != null)
            {
                for (int i = 0; i < expected.IgnoredDevices.Count; i++)
                {
                    if (!string.Equals(expected.IgnoredDevices[i], actual.IgnoredDevices[i], StringComparison.Ordinal))
                        return false;
                }
            }

            // PinnedDevices
            int expectedPinnedCount = expected.PinnedDevices != null ? expected.PinnedDevices.Count : 0;
            int actualPinnedCount = actual.PinnedDevices != null ? actual.PinnedDevices.Count : 0;
            if (expectedPinnedCount != actualPinnedCount)
                return false;

            if (expected.PinnedDevices != null && actual.PinnedDevices != null)
            {
                for (int i = 0; i < expected.PinnedDevices.Count; i++)
                {
                    if (!string.Equals(expected.PinnedDevices[i], actual.PinnedDevices[i], StringComparison.Ordinal))
                        return false;
                }
            }

            // WidgetDevices
            int expectedWidgetCount = expected.WidgetDevices != null ? expected.WidgetDevices.Count : 0;
            int actualWidgetCount = actual.WidgetDevices != null ? actual.WidgetDevices.Count : 0;
            if (expectedWidgetCount != actualWidgetCount)
                return false;

            if (expected.WidgetDevices != null && actual.WidgetDevices != null)
            {
                for (int i = 0; i < expected.WidgetDevices.Count; i++)
                {
                    if (!string.Equals(expected.WidgetDevices[i], actual.WidgetDevices[i], StringComparison.Ordinal))
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 原子更新设备别名，经磁盘回读校验成功后生效并返回 true；失败回滚并返回 false。
        /// </summary>
        public bool TrySetDeviceAlias(string deviceKey, string alias)
        {
            if (string.IsNullOrEmpty(deviceKey)) return false;
            try
            {
                AppSettings next = Current.Clone();
                if (string.IsNullOrWhiteSpace(alias))
                {
                    next.DeviceAliases.Remove(deviceKey);
                }
                else
                {
                    next.DeviceAliases[deviceKey] = alias.Trim();
                }
                Save(next);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("TrySetDeviceAlias 失败: " + ex.Message, ex);
                return false;
            }
        }

        /// <summary>
        /// 切换设备置顶状态，经磁盘回读校验成功后生效；失败回滚并返回 false。
        /// </summary>
        public bool TryTogglePinnedDevice(string deviceKey, out bool isPinned)
        {
            isPinned = false;
            if (string.IsNullOrEmpty(deviceKey)) return false;
            try
            {
                AppSettings next = Current.Clone();
                bool exists = next.PinnedDevices.Contains(deviceKey);
                if (exists)
                {
                    next.PinnedDevices.Remove(deviceKey);
                    isPinned = false;
                }
                else
                {
                    next.PinnedDevices.Add(deviceKey);
                    isPinned = true;
                }
                Save(next);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("TryTogglePinnedDevice 失败: " + ex.Message, ex);
                return false;
            }
        }

        /// <summary>
        /// 切换设备忽略状态，经磁盘回读校验成功后生效；失败回滚并返回 false。
        /// </summary>
        public bool TryToggleIgnoredDevice(string deviceKey, out bool isIgnored)
        {
            isIgnored = false;
            if (string.IsNullOrEmpty(deviceKey)) return false;
            try
            {
                AppSettings next = Current.Clone();
                bool exists = next.IgnoredDevices.Contains(deviceKey);
                if (exists)
                {
                    next.IgnoredDevices.Remove(deviceKey);
                    isIgnored = false;
                }
                else
                {
                    next.IgnoredDevices.Add(deviceKey);
                    isIgnored = true;
                }
                Save(next);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("TryToggleIgnoredDevice 失败: " + ex.Message, ex);
                return false;
            }
        }

        /// <summary>
        /// 设置设备在挂件中的勾选状态，经磁盘回读校验成功后生效；失败回滚并返回 false。
        /// </summary>
        public bool TrySetWidgetSelected(string deviceKey, bool selected)
        {
            if (string.IsNullOrEmpty(deviceKey)) return false;
            try
            {
                AppSettings next = Current.Clone();
                bool exists = next.WidgetDevices.Contains(deviceKey);
                if (selected && !exists)
                {
                    next.WidgetDevices.Add(deviceKey);
                }
                else if (!selected && exists)
                {
                    next.WidgetDevices.Remove(deviceKey);
                }
                Save(next);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("TrySetWidgetSelected 失败: " + ex.Message, ex);
                return false;
            }
        }
    }
}
