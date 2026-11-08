using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using PowerDock.Core.Common;
using PowerDock.Core.Storage;

namespace PowerDock.App.Windows
{
    /// <summary>
    /// MainWindow 概览页挂载与跨页定位接线（AGENTS.md §6.4.2）。
    /// </summary>
    public partial class MainWindow
    {
        private OverviewView _overviewView;
        private SettingsStore _settingsStore;

        public SettingsStore Settings
        {
            get { return _settingsStore; }
            set { _settingsStore = value; }
        }

        /// <summary>
        /// 挂载概览视图到概览页内容槽。
        /// </summary>
        public bool AttachOverview()
        {
            if (_contentHost == null) return false;
            if (_overviewView != null) return true;

            _overviewView = new OverviewView();
            _overviewView.OnNavigateToDevice = delegate(DeviceKey key)
            {
                NavigateToDevice(key);
            };
            return ReplacePageContent(MainPage.Overview, _overviewView);
        }

        /// <summary>
        /// 渲染概览快照。未知电量不参与最低电量计算，无历史数据时呈现趋势空态。
        /// </summary>
        public void RenderOverview(IReadOnlyList<DeviceState> snapshot, IReadOnlyList<BatterySample> focusHistory = null)
        {
            if (_overviewView == null) return;
            AppSettings settings = _settingsStore != null ? _settingsStore.Current : null;
            _overviewView.Render(snapshot, settings, focusHistory);
        }

        /// <summary>
        /// 跨页定位设备：切至设备页，并在设备视图上高亮且滚动到该设备卡片。
        /// 与设备页负责人对齐 IDeviceLocator 接口。
        /// </summary>
        public bool NavigateToDevice(DeviceKey key)
        {
            NavigateTo(MainPage.Devices);

            if (key == null) return true;

            IDeviceLocator locator = _deviceSummary as IDeviceLocator;
            if (locator == null)
            {
                UIElement devContent;
                if (_pageOverrides.TryGetValue(MainPage.Devices, out devContent))
                {
                    locator = devContent as IDeviceLocator;
                }
            }

            if (locator != null)
            {
                return locator.SelectAndScrollToDevice(key);
            }
            return false;
        }

        /// <summary>
        /// 按快照更新标题栏计数，保证与概览和设备列表同源（AGENTS.md §6.4）。
        /// </summary>
        public void UpdateTitleSummary(IReadOnlyList<DeviceState> snapshot)
        {
            if (_titleText == null) return;

            if (snapshot == null || snapshot.Count == 0)
            {
                _titleText.Text = "⚡ PowerDock · 桌面电量中心";
                return;
            }

            int online = 0;
            foreach (DeviceState dev in snapshot)
            {
                if (dev != null && dev.State == ConnectionState.Online)
                {
                    online++;
                }
            }

            int inWidget = snapshot.Count;
            if (_settingsStore != null && _settingsStore.Current != null &&
                _settingsStore.Current.PinnedDevices != null &&
                _settingsStore.Current.PinnedDevices.Count > 0)
            {
                var pinned = new HashSet<string>(_settingsStore.Current.PinnedDevices, StringComparer.OrdinalIgnoreCase);
                inWidget = 0;
                foreach (DeviceState dev in snapshot)
                {
                    if (dev != null && dev.Key != null && pinned.Contains(dev.Key.Value))
                    {
                        inWidget++;
                    }
                }
            }

            _titleText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "⚡ PowerDock · {0}/{1} 在组件 · {2} 台在线",
                inWidget, snapshot.Count, online);
        }

        internal bool OverviewAttached
        {
            get { return _overviewView != null; }
        }

        internal OverviewView OverviewInstance
        {
            get { return _overviewView; }
        }
    }
}
