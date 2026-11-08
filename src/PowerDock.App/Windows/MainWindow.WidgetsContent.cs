using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PowerDock.App.Windows.Widgets;
using PowerDock.Core.Common;
using PowerDock.Core.Storage;

namespace PowerDock.App.Windows
{
    /// <summary>
    /// MainWindow 的组件页挂载点（专属分部类，AGENTS.md §6.5）。
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
        /// 挂载组件页实例到主窗口。已挂载时不重复构造。
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

            // 若 Dock 未合并导航状态机，无侵入式增强 Dock 点击，使点击「▤ 组件」可切换展示
            TryHookDockItemClick();

            return true;
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
        /// 将内容宿主切换到组件页（供诊断或外部导航触发）。
        /// </summary>
        public bool ShowWidgetsPage()
        {
            if (_contentHost == null) return false;
            if (_widgetsPage == null)
            {
                if (!AttachWidgetsPage()) return false;
            }

            _contentHost.Content = _widgetsPage;
            return true;
        }

        private void TryHookDockItemClick()
        {
            try
            {
                if (_bottomDock == null || !(_bottomDock.Child is StackPanel dockStack)) return;

                foreach (UIElement child in dockStack.Children)
                {
                    if (child is TextBlock item)
                    {
                        if (item.Text != null && item.Text.Contains("组件"))
                        {
                            item.MouseLeftButtonDown += delegate
                            {
                                ShowWidgetsPage();
                                UpdateDockItemVisuals(dockStack, item);
                            };
                        }
                        else if (item.Text != null && (item.Text.Contains("设备") || item.Text.Contains("概览")))
                        {
                            item.MouseLeftButtonDown += delegate
                            {
                                if (_deviceSummary != null)
                                {
                                    _contentHost.Content = _deviceSummary;
                                    UpdateDockItemVisuals(dockStack, item);
                                }
                            };
                        }
                    }
                }
            }
            catch
            {
                // 仅作为在非导航状态机合并阶段的无侵入辅助，不抛异常
            }
        }

        private static void UpdateDockItemVisuals(StackPanel dockStack, TextBlock selectedItem)
        {
            foreach (UIElement child in dockStack.Children)
            {
                if (child is TextBlock tb)
                {
                    bool isCur = tb == selectedItem;
                    tb.Foreground = isCur
                        ? WidgetUiTokens.AccentBrush
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(160, 255, 255, 255));
                    tb.FontWeight = isCur ? FontWeights.SemiBold : FontWeights.Normal;
                }
            }
        }
    }
}
