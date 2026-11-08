using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PowerDock.Core.Common;
using PowerDock.Core.Widgets;

namespace PowerDock.App.Windows.Widgets
{
    /// <summary>
    /// 1:1 挂件卡片预览控件（严格遵循 AGENTS.md §2.1 C1 / D10 / §6.3 规格与外壳约束）。
    /// 尺寸：173×173 / 362×173 / 362×362 DIP；
    /// 圆角：22 / 27 / 27 DIP；
    /// 行数上限：3 / 5 / 7 行。
    /// </summary>
    public sealed class WidgetPreviewControl : UserControl
    {
        private readonly Border _widgetSurface;
        private readonly Grid _contentGrid;
        private readonly StackPanel _rowsContainer;
        private readonly TextBlock _footnoteText;
        private readonly StackPanel _emptyContainer;
        private readonly TextBlock _emptyTitle;
        private readonly TextBlock _emptySub;

        private WidgetPresentationModel _currentModel;

        public WidgetPresentationModel CurrentModel => _currentModel;

        public WidgetPreviewControl()
        {
            _contentGrid = new Grid();

            _rowsContainer = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            _footnoteText = new TextBlock
            {
                FontSize = 10,
                Foreground = WidgetUiTokens.Text3Brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };

            _emptyContainer = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            _emptyTitle = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = WidgetUiTokens.Text2Brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };

            _emptySub = new TextBlock
            {
                FontSize = 11,
                Foreground = WidgetUiTokens.Text3Brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };

            _emptyContainer.Children.Add(_emptyTitle);
            _emptyContainer.Children.Add(_emptySub);

            _contentGrid.Children.Add(_rowsContainer);
            _contentGrid.Children.Add(_footnoteText);
            _contentGrid.Children.Add(_emptyContainer);

            // 根表面：三层材质自绘（D10 / D15 / 外壳硬约束 2 圆角裁切）
            _widgetSurface = new Border
            {
                Background = WidgetUiTokens.CreateWidgetBackgroundBrush(),
                BorderBrush = WidgetUiTokens.CardBorderBrush,
                BorderThickness = new Thickness(1),
                Child = _contentGrid
            };

            _widgetSurface.SizeChanged += OnSurfaceSizeChanged;
            Content = _widgetSurface;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
        }

        private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateClip(e.NewSize.Width, e.NewSize.Height);
        }

        private void UpdateClip(double width, double height)
        {
            if (width <= 0 || height <= 0 || _currentModel == null) return;
            double radius = _currentModel.CornerRadiusDip;
            // 外壳硬约束 2：Border.CornerRadius 不裁 Child，设置 RectangleGeometry 防止直角蒙层盖住圆弧
            _widgetSurface.CornerRadius = new CornerRadius(radius);
            _widgetSurface.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
        }

        public void ApplyModel(WidgetPresentationModel model)
        {
            _currentModel = model;
            if (model == null)
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            Visibility = Visibility.Visible;
            Width = model.WidthDip;
            Height = model.HeightDip;
            _widgetSurface.Width = model.WidthDip;
            _widgetSurface.Height = model.HeightDip;
            _widgetSurface.Padding = new Thickness(model.PaddingDip);
            UpdateClip(model.WidthDip, model.HeightDip);

            _rowsContainer.Children.Clear();

            if (!model.HasDevices)
            {
                _emptyContainer.Visibility = Visibility.Visible;
                _footnoteText.Visibility = Visibility.Collapsed;
                _rowsContainer.Visibility = Visibility.Collapsed;
                _emptyTitle.Text = "未发现设备";
                _emptySub.Text = "请检查蓝牙已开启或连接外设";
                return;
            }

            if (!model.HasSelectedDevices || model.DisplayedRows.Count == 0)
            {
                _emptyContainer.Visibility = Visibility.Visible;
                _footnoteText.Visibility = Visibility.Collapsed;
                _rowsContainer.Visibility = Visibility.Collapsed;
                _emptyTitle.Text = "组件未添加设备";
                _emptySub.Text = "在左侧设备清单中勾选设备";
                return;
            }

            _emptyContainer.Visibility = Visibility.Collapsed;
            _rowsContainer.Visibility = Visibility.Visible;

            if (model.SizeKind == WidgetSizeKind.Small2x2)
            {
                // 小号：三行居中 + 10px 固定间距，满宽电量条重排（AGENTS §6.3 / D12）
                _rowsContainer.VerticalAlignment = VerticalAlignment.Center;
                _footnoteText.Visibility = Visibility.Collapsed;

                for (int i = 0; i < model.DisplayedRows.Count; i++)
                {
                    var row = model.DisplayedRows[i];
                    var itemElement = BuildSmallRowElement(row);
                    if (i > 0)
                    {
                        itemElement.Margin = new Thickness(0, 10, 0, 0); // 固定 10px 间距
                    }
                    _rowsContainer.Children.Add(itemElement);
                }
            }
            else if (model.SizeKind == WidgetSizeKind.Medium4x2)
            {
                // 中号：单行四列，最多 5 行，等距分布
                _rowsContainer.VerticalAlignment = VerticalAlignment.Center;
                _footnoteText.Visibility = Visibility.Collapsed;

                int count = model.DisplayedRows.Count;
                double verticalGap = count > 1 ? Math.Min(12.0, (model.HeightDip - model.PaddingDip * 2 - count * 20.0) / (count - 1)) : 8.0;
                if (verticalGap < 4.0) verticalGap = 4.0;

                for (int i = 0; i < count; i++)
                {
                    var row = model.DisplayedRows[i];
                    var itemElement = BuildMediumOrLargeRowElement(row, 66.0);
                    if (i > 0)
                    {
                        itemElement.Margin = new Thickness(0, verticalGap, 0, 0);
                    }
                    _rowsContainer.Children.Add(itemElement);
                }
            }
            else // Large4x4
            {
                // 大号：单行四列，最多 7 行 + 底部唯一汇总脚注（AGENTS §6.3）
                _rowsContainer.VerticalAlignment = VerticalAlignment.Top;
                _footnoteText.Visibility = Visibility.Visible;
                _footnoteText.Text = model.Footnote;

                int count = model.DisplayedRows.Count;
                double availableHeight = model.HeightDip - model.PaddingDip * 2 - 28.0; // 预留脚注高度
                double verticalGap = count > 1 ? Math.Min(16.0, (availableHeight - count * 22.0) / (count - 1)) : 10.0;
                if (verticalGap < 4.0) verticalGap = 4.0;

                for (int i = 0; i < count; i++)
                {
                    var row = model.DisplayedRows[i];
                    var itemElement = BuildMediumOrLargeRowElement(row, 86.0);
                    if (i > 0)
                    {
                        itemElement.Margin = new Thickness(0, verticalGap, 0, 0);
                    }
                    _rowsContainer.Children.Add(itemElement);
                }
            }
        }

        /// <summary>
        /// 小号双行重排：上层（状态点 + 名称 + 百分比），下层（满宽电量条）。
        /// </summary>
        private static FrameworkElement BuildSmallRowElement(WidgetRowItem row)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Opacity = row.IsOffline ? 0.5 : 1.0 // 离线整行 50%
            };

            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) }); // 状态点
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 名称
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 百分比

            // 状态点
            var dot = CreateStatusDot(row.Tone);
            Grid.SetColumn(dot, 0);
            topGrid.Children.Add(dot);

            // 名称
            var nameBlock = new TextBlock
            {
                Text = row.Name,
                FontSize = 11,
                Foreground = WidgetUiTokens.Text1Brush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 6, 0)
            };
            Grid.SetColumn(nameBlock, 1);
            topGrid.Children.Add(nameBlock);

            // 百分比
            var pctBlock = new TextBlock
            {
                Text = row.DisplayPercent,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = WidgetUiTokens.GetToneBrush(row.Tone),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(pctBlock, 2);
            topGrid.Children.Add(pctBlock);

            stack.Children.Add(topGrid);

            // 第二行：满宽电量条
            var barTrack = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = WidgetUiTokens.TrackBrush,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ClipToBounds = true
            };

            if (row.FillRatio > 0.001)
            {
                var barFill = new Border
                {
                    Height = 3,
                    CornerRadius = new CornerRadius(1.5),
                    Background = WidgetUiTokens.GetBarFillBrush(row.Tone),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                // 满宽条：按百分比绑定宽度
                barTrack.Loaded += delegate
                {
                    double trackW = barTrack.ActualWidth;
                    if (trackW > 0)
                    {
                        barFill.Width = Math.Max(2.0, trackW * row.FillRatio);
                    }
                };
                barTrack.SizeChanged += delegate(object s, SizeChangedEventArgs e)
                {
                    if (e.NewSize.Width > 0)
                    {
                        barFill.Width = Math.Max(2.0, e.NewSize.Width * row.FillRatio);
                    }
                };
                barTrack.Child = barFill;
            }

            stack.Children.Add(barTrack);
            return stack;
        }

        /// <summary>
        /// 中号与大号单行四列布局：状态点 · 链路与名称 · 电量条 · 百分比。
        /// </summary>
        private static FrameworkElement BuildMediumOrLargeRowElement(WidgetRowItem row, double barPreferredWidth)
        {
            var grid = new Grid
            {
                Height = 22,
                Opacity = row.IsOffline ? 0.5 : 1.0 // 离线整行 50%
            };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) }); // 状态点
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 链路图标与名称
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(barPreferredWidth) }); // 电量槽
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) }); // 百分比

            // 1. 状态点
            var dot = CreateStatusDot(row.Tone);
            Grid.SetColumn(dot, 0);
            grid.Children.Add(dot);

            // 2. 链路符号与名称
            var nameStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var linkSymbol = CreateLinkIcon(row.LinkKind);
            if (linkSymbol != null)
            {
                linkSymbol.Margin = new Thickness(0, 0, 5, 0);
                nameStack.Children.Add(linkSymbol);
            }

            var nameText = new TextBlock
            {
                Text = row.Name,
                FontSize = 11,
                Foreground = WidgetUiTokens.Text1Brush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            nameStack.Children.Add(nameText);
            Grid.SetColumn(nameStack, 1);
            grid.Children.Add(nameStack);

            // 3. 电量槽与填充条
            var track = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = WidgetUiTokens.TrackBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                ClipToBounds = true
            };

            if (row.FillRatio > 0.001)
            {
                var fill = new Border
                {
                    Height = 3,
                    CornerRadius = new CornerRadius(1.5),
                    Background = WidgetUiTokens.GetBarFillBrush(row.Tone),
                    HorizontalAlignment = HorizontalAlignment.Left
                };

                track.Loaded += delegate
                {
                    double w = track.ActualWidth;
                    if (w > 0)
                    {
                        fill.Width = Math.Max(2.0, w * row.FillRatio);
                    }
                };
                track.SizeChanged += delegate(object s, SizeChangedEventArgs e)
                {
                    if (e.NewSize.Width > 0)
                    {
                        fill.Width = Math.Max(2.0, e.NewSize.Width * row.FillRatio);
                    }
                };
                track.Child = fill;
            }

            Grid.SetColumn(track, 2);
            grid.Children.Add(track);

            // 4. 百分比（右对齐）
            var pctText = new TextBlock
            {
                Text = row.DisplayPercent,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = WidgetUiTokens.GetToneBrush(row.Tone),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(pctText, 3);
            grid.Children.Add(pctText);

            return grid;
        }

        private static Ellipse CreateStatusDot(WidgetTone tone)
        {
            return new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = WidgetUiTokens.GetToneBrush(tone),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static TextBlock CreateLinkIcon(LinkKind kind)
        {
            string symbol;
            switch (kind)
            {
                case LinkKind.Bluetooth:
                    symbol = "ᛒ";
                    break;
                case LinkKind.Wireless24G:
                    symbol = "∿";
                    break;
                case LinkKind.UsbWired:
                    symbol = "ψ";
                    break;
                case LinkKind.Internal:
                    symbol = "💻";
                    break;
                default:
                    return null;
            }

            return new TextBlock
            {
                Text = symbol,
                FontSize = 10,
                Foreground = WidgetUiTokens.Text3Brush,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
    }
}
