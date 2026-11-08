using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using PowerDock.Core.Common;

namespace PowerDock.App.Windows
{
    /// <summary>
    /// MainWindow 的设备内容挂载点。单独成文件，避免与 Dock 导航 PR 改动同一个窗口文件：
    /// 这里只负责把 <see cref="DeviceSummaryView"/> 放进现有内容宿主，不重建视觉树。
    /// 调用方必须在 UI 线程上调用；Host 为空（窗口尚未构建）时返回 false 且不抛异常。
    /// </summary>
    public partial class MainWindow
    {
        private DeviceSummaryView _deviceSummary;

        public bool AttachDeviceSummary()
        {
            if (_contentHost == null) return false;
            if (_deviceSummary != null) return true;

            _deviceSummary = new DeviceSummaryView();
            _contentHost.Content = _deviceSummary;
            return true;
        }

        /// <summary>渲染设备快照；未挂载时静默忽略，避免采集线程与窗口生命周期竞争。</summary>
        public void RenderDevices(IReadOnlyList<DeviceState> snapshot)
        {
            if (_deviceSummary == null) return;
            _deviceSummary.Render(snapshot);
        }
    }
}
