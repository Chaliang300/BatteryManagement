using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PowerDock.Core.Common;

namespace PowerDock.App.Windows
{
    /// <summary>
    /// 单张四列设备卡片控件。
    /// 严格遵循 AGENTS.md §6.5/§6.6/§6.7 与 docs/mockup 方案 A：
    /// 1. 右上角组件勾选圆点（与持久化状态源同步）；
    /// 2. 链路图标与真实名称/别名；
    /// 3. 大字电量百分比（真实 0% 显示 0%，缺失显示「—」）、充电指示与语义色；
    /// 4. 进度条；
    /// 5. 脚注：区分 USB 总线已知、USB 有线已确认、2.4G 外设已确认及未知连接，离线展示上次读数与时效；
    /// 6. 选中态：--accent 描边 + --accent-soft 淡底，基于 DeviceKey 保持。
    /// </summary>
    public sealed class DeviceCardControl : Border
    {
        private static readonly Color AccentColor = Color.FromRgb(107, 166, 255);       // #6BA6FF
        private static readonly Color AccentSoftColor = Color.FromArgb(36, 107, 166, 255); // --accent-soft
        private static readonly Color NormalBorderColor = Color.FromArgb(32, 255, 255, 255); // --hair
        private static readonly Color NormalBgColor = Color.FromArgb(210, 26, 30, 40);     // --row-bg
        private static readonly Color OkColor = Color.FromRgb(52, 211, 153);             // --ok #34D399
        private static readonly Color WarnColor = Color.FromRgb(255, 159, 10);           // --warn #FF9F0A
        private static readonly Color DangerColor = Color.FromRgb(255, 107, 107);        // --danger #FF6B6B
        private static readonly Color OffColor = Color.FromRgb(138, 143, 153);           // --off #8A8F99
        private static readonly Color TrackColor = Color.FromArgb(28, 255, 255, 255);    // --track

        private readonly DeviceState _state;
        private readonly bool _isWidgetChecked;
        private readonly bool _isPinned;
        private readonly bool _isIgnored;
        private bool _isSelected;

        public DeviceState State
        {
            get { return _state; }
        }

        public DeviceKey DeviceKey
        {
            get { return _state.Key; }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    UpdateSelectionVisual();
                }
            }
        }

        public bool IsWidgetChecked
        {
            get { return _isWidgetChecked; }
        }

        public bool IsPinned
        {
            get { return _isPinned; }
        }

        public bool IsIgnored
        {
            get { return _isIgnored; }
        }

        /// <summary>卡片主体被点击回调</summary>
        public event Action<DeviceCardControl> CardClicked;

        /// <summary>右上角勾选圆点被点击回调（参数为目标勾选值）</summary>
        public event Action<DeviceCardControl, bool> CheckClicked;

        public DeviceCardControl(
            DeviceState state,
            bool isSelected,
            bool isWidgetChecked,
            bool isPinned,
            bool isIgnored)
        {
            if (state == null) throw new ArgumentNullException("state");

            _state = state;
            _isSelected = isSelected;
            _isWidgetChecked = isWidgetChecked;
            _isPinned = isPinned;
            _isIgnored = isIgnored;

            CornerRadius = new CornerRadius(10);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(12, 11, 12, 12);
            MinHeight = 118;
            Cursor = Cursors.Hand;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            // 离线态整卡半透明 50% (AGENTS.md §6.6 / mockup .dcard.is-off)
            Opacity = state.State == ConnectionState.Offline ? 0.5 : 1.0;

            UpdateSelectionVisual();
            BuildCardContent();

            MouseLeftButtonDown += OnCardMouseLeftButtonDown;
        }

        private void OnCardMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            CardClicked?.Invoke(this);
        }

        private void UpdateSelectionVisual()
        {
            if (_isSelected)
            {
                BorderBrush = new SolidColorBrush(AccentColor);
                Background = new SolidColorBrush(AccentSoftColor);
            }
            else
            {
                BorderBrush = new SolidColorBrush(NormalBorderColor);
                Background = new SolidColorBrush(NormalBgColor);
            }
        }

        private void BuildCardContent()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 顶部：链路图标 + 名称 + 勾选圆点
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 中部：大字百分比 + 充电中
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 电量槽
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 脚注

            // 1. 顶部栏 (Row 0)
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // 链路图标
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 名称
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // 置顶与勾选圆点

            // 链路图标
            var iconImg = new Image
            {
                Width = 15,
                Height = 15,
                Source = GetLinkDrawingImage(_state),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            };
            Grid.SetColumn(iconImg, 0);
            topGrid.Children.Add(iconImg);

            // 设备名称 (优先显示别名)
            string displayName = _state.EffectiveName;
            if (_isPinned)
            {
                displayName = "📌 " + displayName;
            }
            if (_isIgnored)
            {
                displayName += " (已忽略)";
            }

            var nameBlock = new TextBlock
            {
                Text = displayName,
                Foreground = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameBlock, 1);
            topGrid.Children.Add(nameBlock);

            // 右上角勾选圆点 (19x19 DIP)
            var checkBtn = CreateCheckButton();
            Grid.SetColumn(checkBtn, 2);
            topGrid.Children.Add(checkBtn);

            Grid.SetRow(topGrid, 0);
            grid.Children.Add(topGrid);

            // 2. 中部：大字百分比 + 充电状态 (Row 1)
            var pctPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            };

            // 真实 0% 正常显示 "0%"，缺失显示 "—" (AGENTS.md §5.3.1 / C6)
            string pctText;
            if (_state.Percent.HasValue)
            {
                pctText = string.Format(CultureInfo.InvariantCulture, "{0}%", _state.Percent.Value);
            }
            else
            {
                pctText = "—";
            }

            Color pctColor = GetToneColor(_state);
            var pctBlock = new TextBlock
            {
                Text = pctText,
                Foreground = new SolidColorBrush(pctColor),
                FontSize = 23,
                FontWeight = FontWeights.ExtraBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            pctPanel.Children.Add(pctBlock);

            // 充电状态
            if (_state.Charging == true)
            {
                var chargeIcon = new Image
                {
                    Width = 12,
                    Height = 12,
                    Source = DeviceIcons.CreateChargingIcon(),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                    ToolTip = "正在充电"
                };
                pctPanel.Children.Add(chargeIcon);

                var chargeLabel = new TextBlock
                {
                    Text = "充电中",
                    Foreground = new SolidColorBrush(OkColor),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(3, 0, 0, 0)
                };
                pctPanel.Children.Add(chargeLabel);
            }

            Grid.SetRow(pctPanel, 1);
            grid.Children.Add(pctPanel);

            // 3. 电量条 (Row 2, 高度 4 DIP)
            var barContainer = new Border
            {
                Height = 4,
                Background = new SolidColorBrush(TrackColor),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 8, 0, 0),
                ClipToBounds = true
            };

            var barGrid = new Grid();
            double fillRatio = 0.0;
            if (_state.Percent.HasValue)
            {
                fillRatio = Math.Max(0.0, Math.Min(1.0, _state.Percent.Value / 100.0));
            }

            if (fillRatio > 0.0)
            {
                barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fillRatio, GridUnitType.Star) });
                barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0 - fillRatio, GridUnitType.Star) });

                var fillBar = new Border
                {
                    Background = new SolidColorBrush(pctColor),
                    CornerRadius = new CornerRadius(2)
                };
                Grid.SetColumn(fillBar, 0);
                barGrid.Children.Add(fillBar);
            }
            barContainer.Child = barGrid;
            Grid.SetRow(barContainer, 2);
            grid.Children.Add(barContainer);

            // 4. 脚注 (Row 3: 组件状态 + 链路细分 + 读数时效)
            var footBlock = new TextBlock
            {
                FontSize = 9.5,
                Margin = new Thickness(0, 9, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                TextTrimming = TextTrimming.CharacterEllipsis,
                LineHeight = 13
            };
            BuildFootnote(footBlock);
            Grid.SetRow(footBlock, 3);
            grid.Children.Add(footBlock);

            Child = grid;

            // 悬停提示
            ToolTip = BuildToolTip();
        }

        private Border CreateCheckButton()
        {
            var border = new Border
            {
                Width = 19,
                Height = 19,
                CornerRadius = new CornerRadius(9.5),
                BorderThickness = new Thickness(1.4),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };

            if (_isWidgetChecked)
            {
                border.Background = new SolidColorBrush(AccentColor);
                border.BorderBrush = new SolidColorBrush(AccentColor);
                border.ToolTip = "在组件中显示（点击移出）";

                var checkMark = new TextBlock
                {
                    Text = "✓",
                    Foreground = Brushes.White,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -1, 0, 0)
                };
                border.Child = checkMark;
            }
            else
            {
                border.Background = Brushes.Transparent;
                border.BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255));
                border.ToolTip = "未在组件显示（点击加入）";
            }

            border.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true; // 阻止冒泡触发卡片选中
                CheckClicked?.Invoke(this, !_isWidgetChecked);
            };

            return border;
        }

        private void BuildFootnote(TextBlock block)
        {
            // 组件状态
            if (_state.State == ConnectionState.Offline)
            {
                block.Inlines.Add(new System.Windows.Documents.Run("离线")
                {
                    Foreground = new SolidColorBrush(OffColor),
                    FontWeight = FontWeights.SemiBold
                });
            }
            else if (_isWidgetChecked)
            {
                block.Inlines.Add(new System.Windows.Documents.Run("在组件中")
                {
                    Foreground = new SolidColorBrush(AccentColor),
                    FontWeight = FontWeights.SemiBold
                });
            }
            else
            {
                block.Inlines.Add(new System.Windows.Documents.Run("未显示")
                {
                    Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255))
                });
            }

            // 链路细分 (严格遵守 §5.3.1)
            string linkDesc = DescribeLinkExact(_state);
            block.Inlines.Add(new System.Windows.Documents.Run(" · " + linkDesc)
            {
                Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255))
            });

            // 时效与离线历史读数
            if (_state.State == ConnectionState.Offline)
            {
                if (_state.LastSuccessfulReadingUtc.HasValue && _state.Percent.HasValue)
                {
                    string timeAgo = FormatRelativeTime(_state.LastSuccessfulReadingUtc.Value);
                    block.Inlines.Add(new System.Windows.Documents.Run(string.Format(" · 上次 {0}% {1}", _state.Percent.Value, timeAgo))
                    {
                        Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255))
                    });
                }
            }
            else
            {
                if (!_state.Percent.HasValue)
                {
                    block.Inlines.Add(new System.Windows.Documents.Run(" · " + DescribeReason(_state.PercentReason))
                    {
                        Foreground = new SolidColorBrush(WarnColor)
                    });
                }
                else if (_state.IsPercentStale && _state.LastSuccessfulReadingUtc.HasValue)
                {
                    block.Inlines.Add(new System.Windows.Documents.Run(" · 读数过期")
                    {
                        Foreground = new SolidColorBrush(WarnColor)
                    });
                }
                else if (_state.LastSuccessfulReadingUtc.HasValue)
                {
                    block.Inlines.Add(new System.Windows.Documents.Run(" · " + FormatRelativeTime(_state.LastSuccessfulReadingUtc.Value))
                    {
                        Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255))
                    });
                }
            }

            // 异常标记
            if (!_state.IdentityVerified)
            {
                block.Inlines.Add(new System.Windows.Documents.Run(" · 临时身份")
                {
                    Foreground = new SolidColorBrush(WarnColor)
                });
            }
            if (_state.HasConflict)
            {
                block.Inlines.Add(new System.Windows.Documents.Run(" · 冲突")
                {
                    Foreground = new SolidColorBrush(DangerColor)
                });
            }
        }

        /// <summary>
        /// 链路细分严格遵循 AGENTS.md §5.3.1：
        /// 区分 USB 总线已知、USB 有线已确认、2.4G 外设已确认及连接方式未知。
        /// </summary>
        public static string DescribeLinkExact(DeviceState state)
        {
            if (state == null || state.Link == null)
            {
                return "连接方式未知";
            }

            LinkInfo link = state.Link;
            switch (link.Kind)
            {
                case LinkKind.Bluetooth:
                    if (!link.IsPaired) return "蓝牙（未配对）";
                    return link.IsConnected ? "蓝牙 GATT" : "蓝牙（已配对，未连接）";

                case LinkKind.Wireless24G:
                    // 只能识别接收器时，不得宣称外设在线
                    return link.IsReceiverOnly ? "接收器已连接，外设状态未知" : "2.4G HID";

                case LinkKind.UsbWired:
                    return link.IsConnected ? "USB 有线" : "USB 有线（已断开）";

                case LinkKind.Internal:
                    return "系统 WMI";

                case LinkKind.Unknown:
                default:
                    // 仅知在 USB 总线上而不知道设备本身是否 USB 有线
                    if (link.Evidence != null && link.Evidence.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return "USB 设备，连接方式待确认";
                    }
                    return "连接方式未知";
            }
        }

        private static ImageSource GetLinkDrawingImage(DeviceState state)
        {
            if (state == null || state.Link == null)
            {
                return DeviceIcons.CreateUnknownIcon();
            }

            switch (state.Link.Kind)
            {
                case LinkKind.Bluetooth:
                    return DeviceIcons.CreateBluetoothIcon();
                case LinkKind.Wireless24G:
                    return DeviceIcons.Create24GIcon();
                case LinkKind.UsbWired:
                    return DeviceIcons.CreateUsbIcon();
                case LinkKind.Internal:
                    return DeviceIcons.CreateLaptopIcon();
                default:
                    if (state.Link.Evidence != null && state.Link.Evidence.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return DeviceIcons.CreateUsbIcon();
                    }
                    return DeviceIcons.CreateUnknownIcon();
            }
        }

        private static Color GetToneColor(DeviceState state)
        {
            if (state.State == ConnectionState.Offline)
            {
                return OffColor;
            }

            if (state.Charging == true)
            {
                return OkColor;
            }

            if (!state.Percent.HasValue)
            {
                return OffColor;
            }

            int p = state.Percent.Value;
            if (p < 20) return DangerColor;
            if (p <= 40) return WarnColor;
            return Color.FromRgb(255, 255, 255);
        }

        public static string DescribeReason(PercentUnavailableReason reason)
        {
            switch (reason)
            {
                case PercentUnavailableReason.NoStandardBatteryUsage: return "无标准电量字段";
                case PercentUnavailableReason.NoBatteryService: return "无电池服务";
                case PercentUnavailableReason.AccessDenied: return "读取被拒绝";
                case PercentUnavailableReason.Timeout: return "读取超时";
                case PercentUnavailableReason.SourceDisabled: return "数据源不可用";
                case PercentUnavailableReason.VendorUnsupported: return "厂商协议未启用";
                case PercentUnavailableReason.NotProbed: return "等待探测";
                case PercentUnavailableReason.None: return "电量未知";
                default: return "电量不可读";
            }
        }

        public static string FormatRelativeTime(DateTime utc)
        {
            TimeSpan diff = DateTime.UtcNow - utc;
            if (diff.TotalSeconds < 30) return "刚刚更新";
            if (diff.TotalMinutes < 1) return string.Format("{0} 秒前", Math.Max(1, (int)diff.TotalSeconds));
            if (diff.TotalMinutes < 60) return string.Format("{0} 分钟前", Math.Max(1, (int)diff.TotalMinutes));
            if (diff.TotalHours < 24) return string.Format("{0} 小时前", (int)diff.TotalHours);
            return string.Format("{0} 天前", (int)diff.TotalDays);
        }

        private string BuildToolTip()
        {
            string tip = string.Format("设备: {0}\n物理键: {1}\n链路: {2}",
                _state.DisplayName,
                _state.Key.Value,
                DescribeLinkExact(_state));

            if (!string.IsNullOrEmpty(_state.Alias))
            {
                tip += "\n别名: " + _state.Alias;
            }
            if (_state.Percent.HasValue)
            {
                tip += string.Format("\n电量: {0}%", _state.Percent.Value);
            }
            else
            {
                tip += "\n电量: — (" + DescribeReason(_state.PercentReason) + ")";
            }

            if (_state.Charging.HasValue)
            {
                tip += "\n充电: " + (_state.Charging.Value ? "正在充电" : "未充电");
            }

            if (_state.LastSuccessfulReadingUtc.HasValue)
            {
                tip += string.Format("\n最后读数: {0:yyyy-MM-dd HH:mm:ss} UTC", _state.LastSuccessfulReadingUtc.Value);
            }

            if (_state.Link != null && !string.IsNullOrEmpty(_state.Link.Evidence))
            {
                tip += "\n依据: " + _state.Link.Evidence;
            }

            return tip;
        }
    }
}
