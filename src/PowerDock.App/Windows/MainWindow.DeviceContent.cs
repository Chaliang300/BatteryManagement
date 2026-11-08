using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using PowerDock.Core.Common;

namespace PowerDock.App.Windows
{
    /// <summary>
    /// MainWindow 的设备内容挂载点。单独成文件，避免与 Dock 导航 PR 改动同一个窗口文件：
    /// 这里只负责把 <see cref="DeviceSummaryView"/> 放进**设备页**，不重建视觉树。
    /// 调用方必须在 UI 线程上调用；Host 为空（窗口尚未构建）时返回 false 且不抛异常。
    /// </summary>
    /// <remarks>
    /// 与 D19 导航状态机的接法（合并 PR #13 时对齐）：设备视图必须作为
    /// <see cref="MainPage.Devices"/> 页的内容注入，**不得直接改写 _contentHost.Content**。
    /// 导航状态机是 _contentHost 的唯一控制者；绕过它会导致「切到设置页后采集回调把设置页顶掉，
    /// 而 CurrentPage 仍停留在 Settings」这类状态不一致。因此这里走
    /// <see cref="MainWindow.ReplacePageContent"/>，由导航层负责当前可见性。
    /// </remarks>
    public partial class MainWindow
    {
        private DeviceSummaryView _deviceSummary;

        /// <summary>
        /// 首次挂载设备视图到设备页并保持其位置。返回 false 表示窗口或导航尚未就绪。
        /// 已挂载时直接返回 true，不重复构造。
        /// </summary>
        public bool AttachDeviceSummary()
        {
            if (_contentHost == null) return false;
            if (_deviceSummary != null) return true;

            _deviceSummary = new DeviceSummaryView();
            return ReplacePageContent(MainPage.Devices, _deviceSummary);
        }

        /// <summary>渲染设备快照；未挂载时静默忽略，避免采集线程与窗口生命周期竞争。</summary>
        public void RenderDevices(IReadOnlyList<DeviceState> snapshot)
        {
            if (_deviceSummary == null) return;
            _deviceSummary.Render(snapshot);
        }

        /// <summary>设备视图是否已挂载（诊断用，不参与导航判定）。</summary>
        internal bool DeviceSummaryAttached
        {
            get { return _deviceSummary != null; }
        }
    }
}
