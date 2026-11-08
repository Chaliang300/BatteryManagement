using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PowerDock.App.Windows.Widgets;
using PowerDock.Core.Common;
using PowerDock.Core.Storage;

namespace PowerDock.App.Windows
{
    /// <summary>
    /// MainWindow 的组件页挂载点（专属分部类，AGENTS.md §6.4.2 / §6.5）。
    /// 公共窗口与 Dock 导航由概览负责人管理，挂载只改约定的组件页注册点，避免多负责人代码冲突。
    /// </summary>
    public partial class MainWindow
    {
        private WidgetsPageView _widgetsPage;

        public WidgetsPageView WidgetsPage
        {
            get { return _widgetsPage; }
        }

        /// <summary>
        /// 挂载组件页实例到主窗口组件页（MainPage.Widgets）。已挂载时不重复构造。
        /// </summary>
        public bool AttachWidgetsPage(SettingsStore settingsStore = null)
        {
            if (_contentHost == null) return false;
            if (_widgetsPage != null) return true;

            _widgetsPage = new WidgetsPageView(settingsStore);
            _widgetsPage.WidgetSelectionChanged += delegate
            {
                UpdateTitleBarCounts();
            };

            return ReplacePageContent(MainPage.Widgets, _widgetsPage);
        }

        private IReadOnlyList<DeviceState> _lastWidgetsSnapshot;

        /// <summary>
        /// 渲染挂件快照；未挂载时静默忽略，避免采集线程与窗口生命周期竞争。
        /// </summary>
        public void RenderWidgets(IReadOnlyList<DeviceState> snapshot)
        {
            _lastWidgetsSnapshot = snapshot;
            if (_widgetsPage == null) return;
            _widgetsPage.Render(snapshot);
            UpdateTitleBarCounts();
        }

        private void UpdateTitleBarCounts()
        {
            if (_titleText == null) return;
            var snap = _lastWidgetsSnapshot;
            int total = snap != null ? snap.Count : 0;
            int online = 0;
            if (snap != null)
            {
                for (int i = 0; i < snap.Count; i++)
                {
                    if (snap[i].State == ConnectionState.Online) online++;
                }
            }

            int inWidget = 0;
            if (_widgetsPage != null && _widgetsPage.SelectedKeys != null && snap != null)
            {
                for (int i = 0; i < snap.Count; i++)
                {
                    string key = snap[i].Key != null ? snap[i].Key.Value : string.Empty;
                    if (_widgetsPage.SelectedKeys.Contains(key))
                    {
                        inWidget++;
                    }
                }
            }

            _titleText.Text = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "⚡ PowerDock · {0}/{1} 在组件 · {2} 台在线",
                inWidget,
                total,
                online);
        }

        /// <summary>
        /// 将当前导航切换到组件页（供诊断或外部导航触发）。
        /// </summary>
        public bool ShowWidgetsPage()
        {
            if (_widgetsPage == null)
            {
                if (!AttachWidgetsPage()) return false;
            }

            return NavigateTo(MainPage.Widgets);
        }

        /// <summary>组件页是否已挂载（诊断用，不参与导航判定）。</summary>
        internal bool WidgetsPageAttached
        {
            get { return _widgetsPage != null; }
        }
    }
}
