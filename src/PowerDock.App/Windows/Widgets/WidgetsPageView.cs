using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PowerDock.Core.Common;
using PowerDock.Core.Diagnostics;
using PowerDock.Core.Storage;
using PowerDock.Core.Widgets;

namespace PowerDock.App.Windows.Widgets
{
    /// <summary>
    /// 组件页专属视图（AGENTS.md §6.5 / mockup 方案 A）：
    /// 左侧真实设备清单（可勾选，实时已选 N/Total）+ 右侧 1:1 挂件预览及三档切换。
    /// </summary>
    public sealed class WidgetsPageView : UserControl
    {
        private readonly SettingsStore _settingsStore;
        private readonly HashSet<string> _selectedKeys;
        private WidgetSizeKind _currentSizeKind;
        private IReadOnlyList<DeviceState> _latestSnapshot;

        // UI 控件引用
        private readonly Grid _mainGrid;
        private readonly Border _bannerBorder;
        private readonly TextBlock _bannerText;
        private readonly DispatcherTimer _bannerTimer;

        // 顶栏尺寸 Seg 按钮
        private readonly Button _btnSmall;
        private readonly Button _btnMedium;
        private readonly Button _btnLarge;

        // 左侧清单
        private readonly StackPanel _pickListStack;
        private readonly TextBlock _pickFootText;

        // 右侧预览舞台
        private readonly Border _stageBorder;
        private readonly TextBlock _stageHostText;
        private readonly TextBlock _stageDimensionText;
        private readonly WidgetPreviewControl _previewControl;

        /// <summary>
        /// 当勾选集合变更并通过持久化回读校验时触发，便于概览页与标题栏同帧同步计数。
        /// </summary>
        public event EventHandler<IReadOnlyCollection<string>> WidgetSelectionChanged;

        public WidgetPreviewControl PreviewControl => _previewControl;
        public IReadOnlySet<string> SelectedKeys => _selectedKeys;
        public WidgetSizeKind CurrentSizeKind => _currentSizeKind;
        public Border StageBorder => _stageBorder;

        public WidgetsPageView(SettingsStore settingsStore = null)
        {
            _settingsStore = settingsStore ?? new SettingsStore();
            try
            {
                _settingsStore.Load();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("WidgetsPageView: SettingsStore.Load 异常: " + ex.Message);
            }

            var currentSettings = _settingsStore.Current ?? new AppSettings();
            _currentSizeKind = WidgetSizeSpecs.Parse(currentSettings.WidgetSize);
            _selectedKeys = new HashSet<string>(currentSettings.WidgetDevices ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            _mainGrid = new Grid();
            _mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header + Banner
            _mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Main content

            // 1. 顶部 Header
            var headerContainer = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 14) };

            var vheadGrid = new Grid();
            vheadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            vheadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            vheadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = "组件内容",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = WidgetUiTokens.Text1Brush,
                Margin = new Thickness(0, 0, 10, 0)
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "勾选即预览",
                FontSize = 12,
                Foreground = WidgetUiTokens.Text3Brush,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 2)
            });
            Grid.SetColumn(titleStack, 0);
            vheadGrid.Children.Add(titleStack);

            // 右上角 Seg 控制器 (小 2×2 / 中 4×2 / 大 4×4)
            var segBorder = new Border
            {
                Background = WidgetUiTokens.RowBgBrush,
                BorderBrush = WidgetUiTokens.HairBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(2)
            };

            var segStack = new StackPanel { Orientation = Orientation.Horizontal };
            _btnSmall = CreateSegButton("小 2×2", WidgetSizeKind.Small2x2);
            _btnMedium = CreateSegButton("中 4×2", WidgetSizeKind.Medium4x2);
            _btnLarge = CreateSegButton("大 4×4", WidgetSizeKind.Large4x4);

            segStack.Children.Add(_btnSmall);
            segStack.Children.Add(_btnMedium);
            segStack.Children.Add(_btnLarge);
            segBorder.Child = segStack;

            Grid.SetColumn(segBorder, 2);
            vheadGrid.Children.Add(segBorder);
            headerContainer.Children.Add(vheadGrid);

            // 错误/回滚提示横幅
            _bannerBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(50, 255, 107, 107)),
                BorderBrush = WidgetUiTokens.DangerBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };
            _bannerText = new TextBlock
            {
                FontSize = 11,
                Foreground = WidgetUiTokens.Text1Brush
            };
            _bannerBorder.Child = _bannerText;
            headerContainer.Children.Add(_bannerBorder);

            _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _bannerTimer.Tick += delegate
            {
                _bannerTimer.Stop();
                _bannerBorder.Visibility = Visibility.Collapsed;
            };

            Grid.SetRow(headerContainer, 0);
            _mainGrid.Children.Add(headerContainer);

            // 2. 主区域（左右两列）
            var contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) }); // 左侧设备清单
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); // 间距
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 右侧挂件预览

            // 2.1 左侧清单列
            var leftStack = new Grid();
            leftStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            leftStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _pickListStack = new StackPanel { Orientation = Orientation.Vertical };
            var listScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _pickListStack
            };
            Grid.SetRow(listScroll, 0);
            leftStack.Children.Add(listScroll);

            _pickFootText = new TextBlock
            {
                FontSize = 11,
                Foreground = WidgetUiTokens.Text3Brush,
                Margin = new Thickness(0, 10, 0, 0)
            };
            Grid.SetRow(_pickFootText, 1);
            leftStack.Children.Add(_pickFootText);

            Grid.SetColumn(leftStack, 0);
            contentGrid.Children.Add(leftStack);

            // 2.2 右侧预览舞台
            _stageBorder = new Border
            {
                Background = WidgetUiTokens.CreateStageBackgroundBrush(),
                BorderBrush = WidgetUiTokens.StageBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14)
            };

            var stageGrid = new Grid();
            stageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            stageGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 舞台顶栏
            var stageBar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            stageBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            stageBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _stageHostText = new TextBlock
            {
                Text = "挂件预览（桌面宿主未连接 / 预览模式）",
                FontSize = 10,
                Foreground = WidgetUiTokens.Text3Brush,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_stageHostText, 0);
            stageBar.Children.Add(_stageHostText);

            _stageDimensionText = new TextBlock
            {
                FontSize = 10,
                Foreground = WidgetUiTokens.Text3Brush,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_stageDimensionText, 1);
            stageBar.Children.Add(_stageDimensionText);

            Grid.SetRow(stageBar, 0);
            stageGrid.Children.Add(stageBar);

            // 挂件 1:1 预览卡片
            _previewControl = new WidgetPreviewControl
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(_previewControl, 1);
            stageGrid.Children.Add(_previewControl);

            _stageBorder.Child = stageGrid;
            Grid.SetColumn(_stageBorder, 2);
            contentGrid.Children.Add(_stageBorder);

            Grid.SetRow(contentGrid, 1);
            _mainGrid.Children.Add(contentGrid);

            Content = _mainGrid;

            UpdateSegButtonsHighlight();
            UpdateStageBarInfo();

            // 订阅配置变更广播（设备页/设置页改动时同帧联动，AGENTS.md §6.6）
            _settingsStore.SettingsChanged += OnStoreSettingsChanged;
            Unloaded += delegate { _settingsStore.SettingsChanged -= OnStoreSettingsChanged; };
        }

        private void OnStoreSettingsChanged(object sender, AppSettings newSettings)
        {
            if (newSettings == null) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<object, AppSettings>(OnStoreSettingsChanged), sender, newSettings);
                return;
            }

            var newSize = WidgetSizeSpecs.Parse(newSettings.WidgetSize);
            bool sizeChanged = newSize != _currentSizeKind;
            _currentSizeKind = newSize;

            var newKeys = new HashSet<string>(newSettings.WidgetDevices ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            bool selectionChanged = !_selectedKeys.SetEquals(newKeys);

            if (selectionChanged)
            {
                _selectedKeys.Clear();
                foreach (string k in newKeys)
                {
                    _selectedKeys.Add(k);
                }
            }

            if (sizeChanged)
            {
                UpdateSegButtonsHighlight();
                UpdateStageBarInfo();
            }

            if (sizeChanged || selectionChanged)
            {
                RefreshListVisualSelection();
                RefreshPresentation();
            }
        }

        private Button CreateSegButton(string label, WidgetSizeKind size)
        {
            var btn = new Button
            {
                Content = label,
                FontSize = 11,
                Foreground = WidgetUiTokens.Text2Brush,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 4, 10, 4),
                Cursor = Cursors.Hand,
                Tag = size
            };
            btn.Click += delegate
            {
                SetWidgetSize(size);
            };
            return btn;
        }

        public void SetWidgetSize(WidgetSizeKind size)
        {
            if (_currentSizeKind == size) return;

            WidgetSizeKind previousSize = _currentSizeKind;
            _currentSizeKind = size;
            UpdateSegButtonsHighlight();
            UpdateStageBarInfo();

            // 持久化尺寸（切尺寸不清空勾选集合，AGENTS.md §6.3 / §6.6）
            bool success = PersistSettings(delegate(AppSettings settings)
            {
                settings.WidgetSize = WidgetSizeSpecs.GetCode(size);
            });

            if (!success)
            {
                // 回滚尺寸
                _currentSizeKind = previousSize;
                UpdateSegButtonsHighlight();
                UpdateStageBarInfo();
                ShowBanner("尺寸设置保存失败，已自动恢复上次有效尺寸。");
            }

            RefreshPresentation();
        }

        public void ToggleDeviceSelection(string deviceKey)
        {
            if (string.IsNullOrWhiteSpace(deviceKey)) return;

            bool wasSelected = _selectedKeys.Contains(deviceKey);
            if (wasSelected)
            {
                _selectedKeys.Remove(deviceKey);
            }
            else
            {
                _selectedKeys.Add(deviceKey);
            }

            // 持久化勾选列表
            bool success = PersistSettings(delegate(AppSettings settings)
            {
                settings.WidgetDevices = new List<string>(_selectedKeys);
            });

            if (success)
            {
                NotifySelectionChanged();
            }
            else
            {
                // 回滚勾选状态
                if (wasSelected)
                {
                    _selectedKeys.Add(deviceKey);
                }
                else
                {
                    _selectedKeys.Remove(deviceKey);
                }
                ShowBanner("设备勾选保存失败，已自动恢复上次有效状态。");
            }

            RefreshListVisualSelection();
            RefreshPresentation();
        }

        private bool PersistSettings(Action<AppSettings> modify)
        {
            if (_settingsStore == null) return false;

            try
            {
                AppSettings candidate = _settingsStore.Current != null
                    ? _settingsStore.Current.Clone()
                    : new AppSettings();

                modify(candidate);
                _settingsStore.Save(candidate);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("WidgetsPageView: 持久化 settings.json 失败: " + ex.Message, ex);
                return false;
            }
        }

        private void ShowBanner(string message)
        {
            _bannerText.Text = message;
            _bannerBorder.Visibility = Visibility.Visible;
            _bannerTimer.Stop();
            _bannerTimer.Start();
        }

        private void UpdateSegButtonsHighlight()
        {
            ApplySegStyle(_btnSmall, _currentSizeKind == WidgetSizeKind.Small2x2);
            ApplySegStyle(_btnMedium, _currentSizeKind == WidgetSizeKind.Medium4x2);
            ApplySegStyle(_btnLarge, _currentSizeKind == WidgetSizeKind.Large4x4);
        }

        private static void ApplySegStyle(Button btn, bool isSelected)
        {
            if (btn == null) return;
            if (isSelected)
            {
                btn.Background = WidgetUiTokens.AccentSoftBrush;
                btn.Foreground = WidgetUiTokens.AccentBrush;
                btn.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                btn.Background = Brushes.Transparent;
                btn.Foreground = WidgetUiTokens.Text2Brush;
                btn.FontWeight = FontWeights.Normal;
            }
        }

        private void UpdateStageBarInfo()
        {
            _stageDimensionText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} · {1} DIP",
                WidgetSizeSpecs.GetDisplayName(_currentSizeKind),
                WidgetSizeSpecs.GetDimensionText(_currentSizeKind));
        }

        private void NotifySelectionChanged()
        {
            var handler = WidgetSelectionChanged;
            if (handler != null)
            {
                try
                {
                    handler(this, new HashSet<string>(_selectedKeys, StringComparer.OrdinalIgnoreCase));
                }
                catch (Exception ex)
                {
                    AppLogger.Error("WidgetsPageView.WidgetSelectionChanged 执行异常: " + ex.Message, ex);
                }
            }
        }

        /// <summary>
        /// 消费设备快照更新左侧设备清单与右侧 1:1 挂件预览。
        /// </summary>
        public void Render(IReadOnlyList<DeviceState> snapshot)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<IReadOnlyList<DeviceState>>(Render), snapshot);
                return;
            }

            _latestSnapshot = snapshot ?? Array.Empty<DeviceState>();
            BuildDeviceListUI();
            RefreshPresentation();
        }

        private void BuildDeviceListUI()
        {
            _pickListStack.Children.Clear();

            if (_latestSnapshot == null || _latestSnapshot.Count == 0)
            {
                var emptyHint = new TextBlock
                {
                    Text = "未发现可用设备",
                    FontSize = 12,
                    Foreground = WidgetUiTokens.Text3Brush,
                    Margin = new Thickness(4, 12, 4, 12)
                };
                _pickListStack.Children.Add(emptyHint);
                _pickFootText.Text = "已选 0 / 0 · 挂件最多 " + WidgetSizeSpecs.GetMaxRows(_currentSizeKind) + " 台";
                return;
            }

            foreach (var dev in _latestSnapshot)
            {
                string devKey = dev.Key != null ? dev.Key.Value : string.Empty;
                bool isSelected = _selectedKeys.Contains(devKey);
                _pickListStack.Children.Add(CreatePickItemRow(dev, isSelected));
            }

            UpdateFootText();
        }

        private UIElement CreatePickItemRow(DeviceState dev, bool isSelected)
        {
            string devKey = dev.Key != null ? dev.Key.Value : string.Empty;

            var border = new Border
            {
                Background = WidgetUiTokens.RowBgBrush,
                BorderBrush = WidgetUiTokens.HairBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand,
                Tag = devKey
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); // Checkbox
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); // 链路图标
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 名称
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 百分比

            // 1. Checkbox
            var checkBorder = CreateCheckVisual(isSelected);
            Grid.SetColumn(checkBorder, 0);
            grid.Children.Add(checkBorder);

            // 2. 链路标识
            var linkIco = CreateLinkIcon(dev.Link != null ? dev.Link.Kind : LinkKind.Unknown);
            Grid.SetColumn(linkIco, 1);
            grid.Children.Add(linkIco);

            // 3. 名称
            var nameBlock = new TextBlock
            {
                Text = dev.EffectiveName,
                FontSize = 12,
                Foreground = WidgetUiTokens.Text1Brush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 6, 0)
            };
            Grid.SetColumn(nameBlock, 2);
            grid.Children.Add(nameBlock);

            // 4. 百分比（离线显示「—」）
            string pctStr = dev.State == ConnectionState.Offline
                ? "—"
                : (dev.Percent.HasValue ? string.Format(CultureInfo.InvariantCulture, "{0}%", dev.Percent.Value) : "—");

            var pctBlock = new TextBlock
            {
                Text = pctStr,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetDeviceToneBrush(dev),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(pctBlock, 3);
            grid.Children.Add(pctBlock);

            border.Child = grid;

            // 悬停反馈
            border.MouseEnter += delegate { border.Background = WidgetUiTokens.RowHoverBrush; };
            border.MouseLeave += delegate { border.Background = WidgetUiTokens.RowBgBrush; };

            // 点击切换勾选
            border.MouseLeftButtonDown += delegate
            {
                ToggleDeviceSelection(devKey);
            };

            return border;
        }

        private static Border CreateCheckVisual(bool isChecked)
        {
            var b = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            if (isChecked)
            {
                b.Background = WidgetUiTokens.AccentBrush;
                b.BorderBrush = WidgetUiTokens.AccentBrush;
                b.Child = new TextBlock
                {
                    Text = "✓",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            else
            {
                b.Background = Brushes.Transparent;
                b.BorderBrush = WidgetUiTokens.Text3Brush;
            }

            return b;
        }

        private void RefreshListVisualSelection()
        {
            foreach (UIElement child in _pickListStack.Children)
            {
                if (child is Border b && b.Tag is string devKey)
                {
                    bool isSelected = _selectedKeys.Contains(devKey);
                    if (b.Child is Grid g && g.Children.Count > 0 && g.Children[0] is Border checkVisual)
                    {
                        var newCheck = CreateCheckVisual(isSelected);
                        g.Children.RemoveAt(0);
                        Grid.SetColumn(newCheck, 0);
                        g.Children.Insert(0, newCheck);
                    }
                }
            }
            UpdateFootText();
        }

        private void UpdateFootText()
        {
            int total = _latestSnapshot != null ? _latestSnapshot.Count : 0;
            int selected = _selectedKeys.Count;
            int maxCap = WidgetSizeSpecs.GetMaxRows(_currentSizeKind);
            _pickFootText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "已选 {0} / {1} · 挂件最多 {2} 台",
                selected,
                total,
                maxCap);
        }

        private void RefreshPresentation()
        {
            var model = WidgetPipeline.Project(
                _latestSnapshot,
                _selectedKeys,
                _currentSizeKind,
                lowBatteryThreshold: 20,
                isHostConnected: false);

            _previewControl.ApplyModel(model);
            UpdateStageBarInfo();
            UpdateFootText();
        }

        private static Brush GetDeviceToneBrush(DeviceState dev)
        {
            if (dev.State == ConnectionState.Offline) return WidgetUiTokens.OffBrush;
            if (dev.Charging == true) return WidgetUiTokens.OkBrush;
            if (dev.Percent.HasValue)
            {
                if (dev.Percent.Value < 20) return WidgetUiTokens.DangerBrush;
                if (dev.Percent.Value <= 40) return WidgetUiTokens.WarnBrush;
                return WidgetUiTokens.OkBrush;
            }
            return WidgetUiTokens.Text2Brush;
        }

        private static TextBlock CreateLinkIcon(LinkKind kind)
        {
            string symbol;
            switch (kind)
            {
                case LinkKind.Bluetooth: symbol = "ᛒ"; break;
                case LinkKind.Wireless24G: symbol = "∿"; break;
                case LinkKind.UsbWired: symbol = "ψ"; break;
                case LinkKind.Internal: symbol = "💻"; break;
                default: symbol = "•"; break;
            }

            return new TextBlock
            {
                Text = symbol,
                FontSize = 10,
                Foreground = WidgetUiTokens.Text3Brush,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }
    }
}
