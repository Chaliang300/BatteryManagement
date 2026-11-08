using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PowerDock.Core.Common;
using PowerDock.Core.State;
using PowerDock.Core.Storage;

namespace PowerDock.App.Windows
{
    /// <summary>
    /// 概览页视图（方案 A：结论 → 焦点 → 全貌）。
    /// 契约遵循 AGENTS.md §6.4 与 §6.5：
    /// 1. 第一层：状态带四格（需充电告警、充电中、在线、在组件）；
    /// 2. 第二层：焦点区（左侧最需要关注设备与近 7 天趋势/空态，右侧另外2台异常与其余汇总）；
    /// 3. 第三层：全量设备电量条与基线，点击任一列跳转至设备页定位对应设备；
    /// 4. 空态：无设备时显示规范引导，绝不虚构 0%。
    /// </summary>
    public sealed class OverviewView : UserControl
    {
        // 样式画刷与视觉 Token（对齐 AGENTS.md §6.1 与 docs/mockup/style.css）
        private static readonly Brush HairBrush = Frozen(Color.FromArgb(22, 255, 255, 255));
        private static readonly Brush RowBgBrush = Frozen(Color.FromArgb(16, 255, 255, 255));
        private static readonly Brush CardHoverBrush = Frozen(Color.FromArgb(28, 255, 255, 255));
        private static readonly Brush TrackBrush = Frozen(Color.FromArgb(32, 255, 255, 255));
        private static readonly Brush FillBrush = Frozen(Color.FromArgb(115, 255, 255, 255));

        private static readonly Brush T1Brush = Frozen(Color.FromRgb(235, 235, 240));
        private static readonly Brush T2Brush = Frozen(Color.FromArgb(190, 255, 255, 255));
        private static readonly Brush T3Brush = Frozen(Color.FromArgb(140, 255, 255, 255));
        private static readonly Brush T4Brush = Frozen(Color.FromArgb(90, 255, 255, 255));

        private static readonly Brush DangerBrush = Frozen(Color.FromRgb(255, 107, 107));
        private static readonly Brush DangerBgBrush = Frozen(Color.FromArgb(22, 255, 107, 107));
        private static readonly Brush DangerBorderBrush = Frozen(Color.FromArgb(82, 255, 107, 107));

        private static readonly Brush WarnBrush = Frozen(Color.FromRgb(255, 159, 10));
        private static readonly Brush OkBrush = Frozen(Color.FromRgb(52, 211, 153));
        private static readonly Brush OffBrush = Frozen(Color.FromRgb(138, 143, 153));
        private static readonly Brush AccentBrush = Frozen(Color.FromRgb(107, 166, 255));

        // 布局容器
        private readonly Grid _rootGrid;
        private readonly Grid _statusBand;
        private readonly Grid _midGrid;
        private readonly Border _focusCard;
        private readonly Border _oddCard;
        private readonly Border _stripCard;
        private readonly Border _emptyCard;

        // 第一层格内控件
        private readonly Border _cellNeedCharging;
        private readonly TextBlock _txtNeedChargingKey;
        private readonly TextBlock _txtNeedChargingVal;
        private readonly TextBlock _txtNeedChargingDesc;

        private readonly Border _cellCharging;
        private readonly TextBlock _txtChargingVal;
        private readonly TextBlock _txtChargingDesc;

        private readonly Border _cellOnline;
        private readonly TextBlock _txtOnlineVal;
        private readonly TextBlock _txtOnlineDesc;

        private readonly Border _cellInWidget;
        private readonly TextBlock _txtInWidgetVal;
        private readonly TextBlock _txtInWidgetDesc;

        // 第二层焦点区控件
        private readonly TextBlock _txtFocusNum;
        private readonly TextBlock _txtFocusUnit;
        private readonly TextBlock _txtFocusName;
        private readonly TextBlock _txtFocusMeta;
        private readonly Border _focusTrendContainer;
        private DeviceKey _focusDeviceKey;

        // 第二层右侧另外2台
        private readonly StackPanel _oddRowsStack;
        private readonly TextBlock _txtOddSummaryRow;
        private readonly TextBlock _txtOddFooter;

        // 第三层全貌设备条
        private readonly TextBlock _txtStripCount;
        private readonly Grid _stripColumnsGrid;

        public Action<DeviceKey> OnNavigateToDevice { get; set; }

        public OverviewView()
        {
            _rootGrid = new Grid();
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: 状态带
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: 焦点区
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: 全貌设备条
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 3: 全局空态

            // 1. 第一层：状态带四格 (1.5fr 1fr 1fr 1fr, gap 10)
            _statusBand = new Grid();
            AutomationProperties.SetAutomationId(_statusBand, "PowerDock.Overview.StatusBand");
            _statusBand.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            _statusBand.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            _statusBand.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            _statusBand.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            _cellNeedCharging = CreateCellBorder();
            AutomationProperties.SetAutomationId(_cellNeedCharging, "PowerDock.Overview.Cell.NeedCharging");
            _cellNeedCharging.Margin = new Thickness(0, 0, 8, 0);
            var sp1 = new StackPanel { Orientation = Orientation.Vertical };
            _txtNeedChargingKey = CreateCellKeyText("需充电");
            _txtNeedChargingVal = CreateCellValText("—");
            _txtNeedChargingDesc = CreateCellDescText("无低电量设备");
            sp1.Children.Add(_txtNeedChargingKey);
            sp1.Children.Add(_txtNeedChargingVal);
            sp1.Children.Add(_txtNeedChargingDesc);
            _cellNeedCharging.Child = sp1;
            Grid.SetColumn(_cellNeedCharging, 0);
            _statusBand.Children.Add(_cellNeedCharging);

            _cellCharging = CreateCellBorder();
            AutomationProperties.SetAutomationId(_cellCharging, "PowerDock.Overview.Cell.Charging");
            _cellCharging.Margin = new Thickness(0, 0, 8, 0);
            var sp2 = new StackPanel { Orientation = Orientation.Vertical };
            sp2.Children.Add(CreateCellKeyText("充电中"));
            _txtChargingVal = CreateCellValText("0");
            _txtChargingDesc = CreateCellDescText("无设备充电中");
            sp2.Children.Add(_txtChargingVal);
            sp2.Children.Add(_txtChargingDesc);
            _cellCharging.Child = sp2;
            Grid.SetColumn(_cellCharging, 1);
            _statusBand.Children.Add(_cellCharging);

            _cellOnline = CreateCellBorder();
            AutomationProperties.SetAutomationId(_cellOnline, "PowerDock.Overview.Cell.Online");
            _cellOnline.Margin = new Thickness(0, 0, 8, 0);
            var sp3 = new StackPanel { Orientation = Orientation.Vertical };
            sp3.Children.Add(CreateCellKeyText("在线"));
            _txtOnlineVal = CreateCellValText("0 / 0");
            _txtOnlineDesc = CreateCellDescText("全部设备在线");
            sp3.Children.Add(_txtOnlineVal);
            sp3.Children.Add(_txtOnlineDesc);
            _cellOnline.Child = sp3;
            Grid.SetColumn(_cellOnline, 2);
            _statusBand.Children.Add(_cellOnline);

            _cellInWidget = CreateCellBorder();
            AutomationProperties.SetAutomationId(_cellInWidget, "PowerDock.Overview.Cell.InWidget");
            _cellInWidget.Margin = new Thickness(0, 0, 0, 0);
            var sp4 = new StackPanel { Orientation = Orientation.Vertical };
            sp4.Children.Add(CreateCellKeyText("在组件"));
            _txtInWidgetVal = CreateCellValText("0 / 0");
            _txtInWidgetDesc = CreateCellDescText("上限 5 台");
            sp4.Children.Add(_txtInWidgetVal);
            sp4.Children.Add(_txtInWidgetDesc);
            _cellInWidget.Child = sp4;
            Grid.SetColumn(_cellInWidget, 3);
            _statusBand.Children.Add(_cellInWidget);

            Grid.SetRow(_statusBand, 0);
            _rootGrid.Children.Add(_statusBand);

            // 2. 第二层：焦点区 (1.25fr 1fr, gap 12, margin-top 12)
            _midGrid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            _midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            _midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            // 2.1 左侧焦点卡片
            _focusCard = new Border
            {
                Background = RowBgBrush,
                BorderBrush = HairBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand
            };
            AutomationProperties.SetAutomationId(_focusCard, "PowerDock.Overview.FocusCard");
            _focusCard.MouseLeftButtonUp += delegate
            {
                if (_focusDeviceKey != null) OnNavigateToDevice?.Invoke(_focusDeviceKey);
            };

            var focusStack = new StackPanel { Orientation = Orientation.Vertical };
            focusStack.Children.Add(new TextBlock
            {
                Text = "最需要关注",
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = T3Brush
            });

            var numRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            _txtFocusNum = new TextBlock
            {
                Text = "—",
                FontSize = 36,
                FontWeight = FontWeights.ExtraBold,
                Foreground = DangerBrush,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            AutomationProperties.SetAutomationId(_txtFocusNum, "PowerDock.Overview.FocusNumber");
            _txtFocusUnit = new TextBlock
            {
                Text = "%",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = T3Brush,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(2, 0, 0, 6)
            };
            numRow.Children.Add(_txtFocusNum);
            numRow.Children.Add(_txtFocusUnit);
            focusStack.Children.Add(numRow);

            _txtFocusName = new TextBlock
            {
                Text = "设备名称",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = T1Brush,
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            focusStack.Children.Add(_txtFocusName);

            _txtFocusMeta = new TextBlock
            {
                Text = "2.4G HID · 刚刚更新 · 状态正常",
                FontSize = 10,
                Foreground = T2Brush,
                Margin = new Thickness(0, 2, 0, 0)
            };
            focusStack.Children.Add(_txtFocusMeta);

            // 7 天趋势区
            var trendHead = new Grid { Margin = new Thickness(0, 8, 0, 4) };
            trendHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            trendHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var thLeft = new TextBlock { Text = "近 7 天", FontSize = 9, Foreground = T4Brush };
            var thRight = new TextBlock { Text = "断连留空", FontSize = 9, Foreground = T4Brush };
            Grid.SetColumn(thLeft, 0);
            Grid.SetColumn(thRight, 1);
            trendHead.Children.Add(thLeft);
            trendHead.Children.Add(thRight);
            focusStack.Children.Add(trendHead);

            _focusTrendContainer = new Border
            {
                Height = 54,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            AutomationProperties.SetAutomationId(_focusTrendContainer, "PowerDock.Overview.FocusTrend");
            focusStack.Children.Add(_focusTrendContainer);

            _focusCard.Child = focusStack;
            Grid.SetColumn(_focusCard, 0);
            _midGrid.Children.Add(_focusCard);

            // 2.2 右侧另外 2 台卡片
            _oddCard = new Border
            {
                Background = RowBgBrush,
                BorderBrush = HairBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(2, 0, 0, 0)
            };
            AutomationProperties.SetAutomationId(_oddCard, "PowerDock.Overview.OddCard");

            var oddStack = new StackPanel { Orientation = Orientation.Vertical };
            oddStack.Children.Add(new TextBlock
            {
                Text = "另外 2 台要注意",
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = T3Brush,
                Margin = new Thickness(0, 0, 0, 4)
            });

            _oddRowsStack = new StackPanel { Orientation = Orientation.Vertical };
            oddStack.Children.Add(_oddRowsStack);

            // 其余汇总行
            _txtOddSummaryRow = new TextBlock
            {
                Text = "其余 0 台 · 状态正常",
                FontSize = 10,
                Foreground = T3Brush,
                Margin = new Thickness(0, 6, 0, 0)
            };
            oddStack.Children.Add(_txtOddSummaryRow);

            _txtOddFooter = new TextBlock
            {
                Text = "全部设备见「设备」页",
                FontSize = 9.5,
                Foreground = T4Brush,
                Margin = new Thickness(0, 8, 0, 0),
                Cursor = Cursors.Hand
            };
            _txtOddFooter.MouseLeftButtonUp += delegate
            {
                OnNavigateToDevice?.Invoke(null);
            };
            oddStack.Children.Add(_txtOddFooter);

            _oddCard.Child = oddStack;
            Grid.SetColumn(_oddCard, 1);
            _midGrid.Children.Add(_oddCard);

            Grid.SetRow(_midGrid, 1);
            _rootGrid.Children.Add(_midGrid);

            // 3. 第三层：全貌设备条 (margin-top 12)
            _stripCard = new Border
            {
                Background = RowBgBrush,
                BorderBrush = HairBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 12, 0, 0)
            };
            AutomationProperties.SetAutomationId(_stripCard, "PowerDock.Overview.Strip");

            var stripStack = new StackPanel { Orientation = Orientation.Vertical };
            var stripHead = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            stripHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            stripHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _txtStripCount = new TextBlock
            {
                Text = "全部设备 · 0 台",
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = T3Brush
            };
            var stripTip = new TextBlock
            {
                Text = "点任一列进「设备」页",
                FontSize = 9.5,
                Foreground = T4Brush
            };
            Grid.SetColumn(_txtStripCount, 0);
            Grid.SetColumn(stripTip, 1);
            stripHead.Children.Add(_txtStripCount);
            stripHead.Children.Add(stripTip);
            stripStack.Children.Add(stripHead);

            _stripColumnsGrid = new Grid
            {
                Height = 98,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            stripStack.Children.Add(_stripColumnsGrid);

            _stripCard.Child = stripStack;
            Grid.SetRow(_stripCard, 2);
            _rootGrid.Children.Add(_stripCard);

            // 4. 全局空态提示卡片（当无设备时展示）
            _emptyCard = new Border
            {
                Background = RowBgBrush,
                BorderBrush = HairBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 16, 18, 16),
                Margin = new Thickness(0, 14, 0, 0),
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(_emptyCard, "PowerDock.Overview.EmptyState");
            var emptyStack = new StackPanel { Orientation = Orientation.Vertical };
            emptyStack.Children.Add(new TextBlock
            {
                Text = "未发现设备",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = T1Brush
            });
            emptyStack.Children.Add(new TextBlock
            {
                Text = "· 请检查蓝牙是否已开启，并在 Windows 系统设置中配对设备",
                FontSize = 11.5,
                Foreground = T2Brush,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            emptyStack.Children.Add(new TextBlock
            {
                Text = "· 2.4G / USB 外设请确认无线接收器已插入电脑",
                FontSize = 11.5,
                Foreground = T2Brush,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            emptyStack.Children.Add(new TextBlock
            {
                Text = "没有读数时一律显示「—」，不用零值充数。",
                FontSize = 11,
                Foreground = T4Brush,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            _emptyCard.Child = emptyStack;
            Grid.SetRow(_emptyCard, 3);
            _rootGrid.Children.Add(_emptyCard);

            Content = _rootGrid;
        }

        public void Render(
            IReadOnlyList<DeviceState> snapshot,
            AppSettings settings,
            IReadOnlyList<BatterySample> focusHistory = null)
        {
            OverviewState state = OverviewAggregator.Aggregate(snapshot, settings, focusHistory);
            ApplyState(state);
        }

        private void ApplyState(OverviewState state)
        {
            if (state == null) return;

            // 1. 状态带四格
            // 格 1: 需充电
            if (state.NeedChargingCount > 0)
            {
                _cellNeedCharging.Background = DangerBgBrush;
                _cellNeedCharging.BorderBrush = DangerBorderBrush;
                _txtNeedChargingKey.Foreground = DangerBrush;
                _txtNeedChargingKey.Text = string.Format(CultureInfo.InvariantCulture, "需充电 {0}", state.NeedChargingCount);
                _txtNeedChargingVal.Foreground = DangerBrush;
                _txtNeedChargingVal.Text = state.NeedChargingLowestPercent.HasValue
                    ? string.Format(CultureInfo.InvariantCulture, "{0}%", state.NeedChargingLowestPercent.Value)
                    : "—";
                string devName = state.NeedChargingDevice != null ? state.NeedChargingDevice.EffectiveName : "设备";
                string linkName = state.NeedChargingDevice != null && state.NeedChargingDevice.Link != null
                    ? state.NeedChargingDevice.Link.Describe()
                    : "未知链路";
                _txtNeedChargingDesc.Text = string.Format(CultureInfo.InvariantCulture, "{0} · {1}", devName, linkName);
                _txtNeedChargingDesc.Foreground = DangerBrush;
            }
            else
            {
                _cellNeedCharging.Background = RowBgBrush;
                _cellNeedCharging.BorderBrush = HairBrush;
                _txtNeedChargingKey.Foreground = T3Brush;
                _txtNeedChargingKey.Text = "需充电 0";
                _txtNeedChargingVal.Foreground = T1Brush;
                _txtNeedChargingVal.Text = "—";
                _txtNeedChargingDesc.Text = "全部电量充足";
                _txtNeedChargingDesc.Foreground = T3Brush;
            }

            // 格 2: 充电中
            _txtChargingVal.Text = state.ChargingCount.ToString(CultureInfo.InvariantCulture);
            if (state.ChargingCount > 0 && state.PrimaryChargingDevice != null)
            {
                string chargingPct = state.PrimaryChargingDevice.Percent.HasValue
                    ? string.Format(CultureInfo.InvariantCulture, " {0}%", state.PrimaryChargingDevice.Percent.Value)
                    : string.Empty;
                _txtChargingDesc.Text = state.PrimaryChargingDevice.EffectiveName + chargingPct;
            }
            else
            {
                _txtChargingDesc.Text = "无设备充电中";
            }

            // 格 3: 在线
            _txtOnlineVal.Text = string.Format(CultureInfo.InvariantCulture, "{0} / {1}", state.OnlineCount, state.TotalCount);
            _txtOnlineDesc.Text = state.OfflineSummary;

            // 格 4: 在组件
            _txtInWidgetVal.Text = string.Format(CultureInfo.InvariantCulture, "{0} / {1}", state.InWidgetCount, state.TotalCount);
            _txtInWidgetDesc.Text = string.Format(CultureInfo.InvariantCulture, "上限 {0} 台", state.WidgetMaxRows);

            // 全局空态处理
            if (state.IsEmpty)
            {
                _midGrid.Visibility = Visibility.Collapsed;
                _stripCard.Visibility = Visibility.Collapsed;
                _emptyCard.Visibility = Visibility.Visible;
                return;
            }

            _midGrid.Visibility = Visibility.Visible;
            _stripCard.Visibility = Visibility.Visible;
            _emptyCard.Visibility = Visibility.Collapsed;

            // 2. 焦点区更新
            _focusDeviceKey = state.FocusDevice != null ? state.FocusDevice.Key : null;
            if (state.FocusPercent.HasValue)
            {
                _txtFocusNum.Text = state.FocusPercent.Value.ToString(CultureInfo.InvariantCulture);
                _txtFocusUnit.Visibility = Visibility.Visible;
                if (state.FocusPercent.Value < (state.NeedChargingDevice != null ? 20 : 20))
                {
                    _txtFocusNum.Foreground = DangerBrush;
                }
                else if (state.FocusPercent.Value <= 40)
                {
                    _txtFocusNum.Foreground = WarnBrush;
                }
                else if (state.FocusDevice != null && state.FocusDevice.Charging == true)
                {
                    _txtFocusNum.Foreground = OkBrush;
                }
                else
                {
                    _txtFocusNum.Foreground = T1Brush;
                }
            }
            else
            {
                _txtFocusNum.Text = "—";
                _txtFocusUnit.Visibility = Visibility.Collapsed;
                _txtFocusNum.Foreground = T3Brush;
            }

            _txtFocusName.Text = state.FocusDevice != null ? state.FocusDevice.EffectiveName : "设备";
            _txtFocusMeta.Text = state.FocusMetaText;

            // 2.1 趋势渲染（空态或 7 柱）
            RenderTrend(state.FocusDailyTrend, state.HasTrendHistory);

            // 2.2 右侧另外 2 台行
            RenderOddSection(state);

            // 3. 第三层全貌设备条
            RenderStrip(state.AllDevices);
        }

        private void RenderTrend(IReadOnlyList<DailyTrendPoint> points, bool hasHistory)
        {
            if (!hasHistory || points == null || points.Count == 0)
            {
                // 明确趋势空态：不编造假柱子
                var emptyTrend = new Border
                {
                    Background = Frozen(Color.FromArgb(10, 255, 255, 255)),
                    BorderBrush = HairBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8)
                };
                var sp = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                sp.Children.Add(new TextBlock
                {
                    Text = "暂无 7 天趋势数据 · 历史样本积累中",
                    FontSize = 10,
                    Foreground = T4Brush,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                emptyTrend.Child = sp;
                _focusTrendContainer.Child = emptyTrend;
                return;
            }

            var grid = new Grid();
            for (int i = 0; i < points.Count; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            for (int i = 0; i < points.Count; i++)
            {
                DailyTrendPoint p = points[i];
                var col = new Grid { Margin = new Thickness(2, 0, 2, 0) };
                col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                if (p.IsGap || !p.Percent.HasValue)
                {
                    var stub = new Border
                    {
                        Height = 3,
                        Background = TrackBrush,
                        CornerRadius = new CornerRadius(1),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(2, 0, 2, 0)
                    };
                    Grid.SetRow(stub, 0);
                    col.Children.Add(stub);
                }
                else
                {
                    double heightRatio = Math.Max(0.08, Math.Min(1.0, p.Percent.Value / 100.0));
                    double barHeight = Math.Max(4.0, heightRatio * 36.0);
                    Brush toneBrush;
                    switch (p.Tone)
                    {
                        case TrendTone.Danger: toneBrush = DangerBrush; break;
                        case TrendTone.Warn: toneBrush = WarnBrush; break;
                        default: toneBrush = FillBrush; break;
                    }

                    var bar = new Border
                    {
                        Height = barHeight,
                        Background = toneBrush,
                        CornerRadius = new CornerRadius(2, 2, 0, 0),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(2, 0, 2, 0)
                    };
                    Grid.SetRow(bar, 0);
                    col.Children.Add(bar);
                }

                var label = new TextBlock
                {
                    Text = p.DayLabel,
                    FontSize = 9,
                    Foreground = p.IsToday ? T1Brush : T4Brush,
                    FontWeight = p.IsToday ? FontWeights.Bold : FontWeights.Normal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                Grid.SetRow(label, 1);
                col.Children.Add(label);

                Grid.SetColumn(col, i);
                grid.Children.Add(col);
            }

            _focusTrendContainer.Child = grid;
        }

        private void RenderOddSection(OverviewState state)
        {
            _oddRowsStack.Children.Clear();
            if (state.SecondaryDevices != null)
            {
                foreach (DeviceState dev in state.SecondaryDevices)
                {
                    if (dev == null) continue;
                    _oddRowsStack.Children.Add(BuildOddRow(dev));
                }
            }

            if (state.RemainingCount > 0)
            {
                string lowestText = state.RemainingLowestPercent.HasValue
                    ? string.Format(CultureInfo.InvariantCulture, "最低 {0}%", state.RemainingLowestPercent.Value)
                    : "读数更新中";
                _txtOddSummaryRow.Text = string.Format(CultureInfo.InvariantCulture, "其余 {0} 台 · 正常 · {1}",
                    state.RemainingCount, lowestText);
                _txtOddSummaryRow.Visibility = Visibility.Visible;
            }
            else
            {
                _txtOddSummaryRow.Visibility = Visibility.Collapsed;
            }

            _txtOddFooter.Text = string.Format(CultureInfo.InvariantCulture, "全部 {0} 台见「设备」页", state.TotalCount);
        }

        private UIElement BuildOddRow(DeviceState dev)
        {
            var grid = new Grid
            {
                Height = 36,
                Margin = new Thickness(0, 2, 0, 2),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 链路符号
            var ico = new TextBlock
            {
                Text = ResolveLinkSymbol(dev.Link != null ? dev.Link.Kind : LinkKind.Unknown),
                FontSize = 11,
                Foreground = T3Brush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(ico, 0);
            grid.Children.Add(ico);

            // 设备名称
            var name = new TextBlock
            {
                Text = dev.EffectiveName,
                FontSize = 11.5,
                Foreground = T1Brush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 6, 0)
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            // 状态文字
            string statusText;
            Brush statusBrush = T3Brush;
            if (dev.Charging == true)
            {
                statusText = string.Format(CultureInfo.InvariantCulture, "充电中 {0}%", dev.Percent.HasValue ? dev.Percent.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
                statusBrush = OkBrush;
            }
            else if (dev.State == ConnectionState.Offline)
            {
                statusText = "离线 · 6 分钟前";
                statusBrush = OffBrush;
            }
            else if (dev.Percent.HasValue)
            {
                statusText = string.Format(CultureInfo.InvariantCulture, "{0}%", dev.Percent.Value);
                if (dev.Percent.Value < 20) statusBrush = DangerBrush;
                else if (dev.Percent.Value <= 40) statusBrush = WarnBrush;
            }
            else
            {
                statusText = "—";
            }

            var st = new TextBlock
            {
                Text = statusText,
                FontSize = 10,
                Foreground = statusBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(st, 2);
            grid.Children.Add(st);

            DeviceKey key = dev.Key;
            grid.MouseLeftButtonUp += delegate
            {
                if (key != null) OnNavigateToDevice?.Invoke(key);
            };

            return grid;
        }

        private void RenderStrip(IReadOnlyList<DeviceState> devices)
        {
            _stripColumnsGrid.Children.Clear();
            _stripColumnsGrid.ColumnDefinitions.Clear();

            int count = devices != null ? devices.Count : 0;
            _txtStripCount.Text = string.Format(CultureInfo.InvariantCulture, "全部设备 · {0} 台", count);

            if (count == 0) return;

            for (int i = 0; i < count; i++)
            {
                _stripColumnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            for (int i = 0; i < count; i++)
            {
                DeviceState dev = devices[i];
                var col = BuildStripColumn(dev);
                Grid.SetColumn(col, i);
                _stripColumnsGrid.Children.Add(col);
            }
        }

        private UIElement BuildStripColumn(DeviceState dev)
        {
            var col = new Grid
            {
                Cursor = Cursors.Hand,
                Margin = new Thickness(4, 0, 4, 0),
                Background = Brushes.Transparent
            };
            col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) }); // 柱子区
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });     // 百分比
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });     // 名称

            // 柱子区域：底部 1px 基线
            var barContainer = new Border
            {
                Height = 64,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = TrackBrush,
                VerticalAlignment = VerticalAlignment.Bottom
            };

            int pct = dev.Percent.HasValue ? dev.Percent.Value : 0;
            double height = Math.Max(4.0, (pct / 100.0) * 60.0);
            Brush barBrush = FillBrush;
            Brush pctBrush = T1Brush;

            if (dev.State == ConnectionState.Offline)
            {
                barBrush = TrackBrush;
                pctBrush = OffBrush;
            }
            else if (dev.Charging == true)
            {
                barBrush = OkBrush;
                pctBrush = OkBrush;
            }
            else if (dev.Percent.HasValue)
            {
                if (pct < 20)
                {
                    barBrush = DangerBrush;
                    pctBrush = DangerBrush;
                }
                else if (pct <= 40)
                {
                    barBrush = WarnBrush;
                    pctBrush = WarnBrush;
                }
            }
            else
            {
                barBrush = TrackBrush;
                pctBrush = T4Brush;
            }

            var bar = new Border
            {
                Height = height,
                Background = barBrush,
                CornerRadius = new CornerRadius(3, 3, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MaxWidth = 48
            };
            barContainer.Child = bar;
            Grid.SetRow(barContainer, 0);
            col.Children.Add(barContainer);

            // 百分比
            var pctText = new TextBlock
            {
                Text = (dev.State == ConnectionState.Offline && !dev.Percent.HasValue) || !dev.Percent.HasValue
                    ? "—"
                    : string.Format(CultureInfo.InvariantCulture, "{0}%", pct),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = pctBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };
            Grid.SetRow(pctText, 1);
            col.Children.Add(pctText);

            // 设备名称
            var nameText = new TextBlock
            {
                Text = dev.EffectiveName,
                FontSize = 9,
                Foreground = T4Brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            };
            Grid.SetRow(nameText, 2);
            col.Children.Add(nameText);

            DeviceKey key = dev.Key;
            col.MouseLeftButtonUp += delegate
            {
                if (key != null) OnNavigateToDevice?.Invoke(key);
            };

            return col;
        }

        private static string ResolveLinkSymbol(LinkKind kind)
        {
            switch (kind)
            {
                case LinkKind.Bluetooth: return "ᛒ";
                case LinkKind.Wireless24G: return "📶";
                case LinkKind.UsbWired: return "🔌";
                case LinkKind.Internal: return "💻";
                default: return "⚡";
            }
        }

        private static Border CreateCellBorder()
        {
            return new Border
            {
                Background = RowBgBrush,
                BorderBrush = HairBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(12, 8, 12, 8)
            };
        }

        private static TextBlock CreateCellKeyText(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = T3Brush
            };
        }

        private static TextBlock CreateCellValText(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = T1Brush,
                Margin = new Thickness(0, 2, 0, 2)
            };
        }

        private static TextBlock CreateCellDescText(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 9.5,
                Foreground = T3Brush,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
