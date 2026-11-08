using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerDock.Core.Common;

namespace PowerDock.App.Windows
{
    /// <summary>
    /// 设备概览视图。只消费 <see cref="DeviceState"/> 快照，不做任何采集或推断：
    /// 1. 无设备时显示「未发现设备」与两条指引，不显示 0%；
    /// 2. 每行显示名称、链路文案、在线状态与电量（或「—」+ 原因）；
    /// 3. 身份未确认、来源冲突、读数过期都在行内明示，不偷偷隐藏。
    /// 独立成文件是为了不与 Dock 导航 PR 抢同一个窗口文件。
    /// </summary>
    public sealed class DeviceSummaryView : UserControl, IDeviceLocator
    {
        private readonly StackPanel _rows;
        private string _selectedDeviceKey;

        public DeviceSummaryView()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new TextBlock
            {
                Text = "设备",
                Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(header);

            _rows = new StackPanel { Orientation = Orientation.Vertical };
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _rows
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            Content = root;
            ShowEmpty(false);
        }

        /// <summary>快照为 null 或空时进入空态。text 明确「未发现设备」而不是 0%。</summary>
        public void Render(IReadOnlyList<DeviceState> snapshot)
        {
            _rows.Children.Clear();

            if (snapshot == null || snapshot.Count == 0)
            {
                ShowEmpty(true);
                return;
            }

            ShowEmpty(false);
            foreach (DeviceState state in snapshot)
            {
                _rows.Children.Add(BuildRow(state));
            }
            if (!string.IsNullOrEmpty(_selectedDeviceKey))
            {
                SelectAndScrollToDevice(_selectedDeviceKey);
            }
        }

        private void ShowEmpty(bool empty)
        {
            if (empty)
            {
                _rows.Children.Clear();
                _rows.Children.Add(BuildHint("未发现设备"));
                _rows.Children.Add(BuildHint("检查蓝牙是否已开启，并确认设备已配对"));
                _rows.Children.Add(BuildHint("USB / 2.4G 外设请确认接收器已插入"));
            }
        }

        private static TextBlock BuildHint(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };
        }

        private static Border BuildRow(DeviceState state)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

            var left = new StackPanel { Orientation = Orientation.Vertical };
            left.Children.Add(new TextBlock
            {
                Text = state.EffectiveName,
                Foreground = new SolidColorBrush(Color.FromArgb(238, 255, 255, 255)),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            left.Children.Add(new TextBlock
            {
                Text = DescribeFoot(state),
                Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var status = new TextBlock
            {
                Text = DescribeState(state.State),
                Foreground = new SolidColorBrush(StateColor(state.State)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(status, 1);
            grid.Children.Add(status);

            var percent = new TextBlock
            {
                Text = state.Percent.HasValue
                    ? string.Format(CultureInfo.InvariantCulture, "{0}%", state.Percent.Value)
                    : "—",
                Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(percent, 2);
            grid.Children.Add(percent);

            return new Border
            {
                Tag = state.Key != null ? state.Key.Value : string.Empty,
                Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Child = grid,
                Opacity = state.State == ConnectionState.Offline ? 0.6 : 1.0
            };
        }

        public bool SelectAndScrollToDevice(DeviceKey key)
        {
            if (key == null) return false;
            return SelectAndScrollToDevice(key.Value);
        }

        public bool SelectAndScrollToDevice(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            _selectedDeviceKey = key;
            bool found = false;
            foreach (UIElement elem in _rows.Children)
            {
                Border border = elem as Border;
                if (border == null) continue;
                string tag = border.Tag as string;
                bool match = string.Equals(tag, key, StringComparison.OrdinalIgnoreCase);
                if (match)
                {
                    border.BorderBrush = new SolidColorBrush(Color.FromRgb(107, 166, 255)); // --accent
                    border.BringIntoView();
                    found = true;
                }
                else
                {
                    border.BorderBrush = Brushes.Transparent;
                }
            }
            return found;
        }

        private static string DescribeFoot(DeviceState state)
        {
            string link = state.Link != null ? state.Link.Describe() : "连接方式未知";
            string text = link;

            if (!state.IdentityVerified)
            {
                text += " · 身份未确认";
            }
            if (state.HasConflict)
            {
                text += " · 来源不一致";
            }
            if (!state.Percent.HasValue)
            {
                text += " · " + DescribeReason(state.PercentReason);
            }
            else if (state.IsPercentStale && state.LastSuccessfulReadingUtc.HasValue)
            {
                text += " · 读数过期";
            }
            return text;
        }

        private static string DescribeState(ConnectionState state)
        {
            switch (state)
            {
                case ConnectionState.Online: return "在线";
                case ConnectionState.Offline: return "离线";
                default: return "未知";
            }
        }

        private static string DescribeReason(PercentUnavailableReason reason)
        {
            switch (reason)
            {
                case PercentUnavailableReason.NoStandardBatteryUsage: return "无标准电量字段";
                case PercentUnavailableReason.NoBatteryService: return "无电池服务";
                case PercentUnavailableReason.AccessDenied: return "读取被拒绝";
                case PercentUnavailableReason.Timeout: return "读取超时";
                case PercentUnavailableReason.SourceDisabled: return "数据源不可用";
                case PercentUnavailableReason.VendorUnsupported: return "厂商协议未启用";
                case PercentUnavailableReason.None: return "电量未知";
                default: return "等待探测";
            }
        }

        private static Color StateColor(ConnectionState state)
        {
            switch (state)
            {
                case ConnectionState.Online: return Color.FromArgb(255, 52, 211, 153);
                case ConnectionState.Offline: return Color.FromArgb(255, 138, 143, 153);
                default: return Color.FromArgb(255, 255, 159, 10);
            }
        }
    }
}
