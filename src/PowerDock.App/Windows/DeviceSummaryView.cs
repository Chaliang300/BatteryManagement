using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PowerDock.Core.Common;
using PowerDock.Core.Diagnostics;
using PowerDock.Core.Storage;

namespace PowerDock.App.Windows
{
    /// <summary>
    /// 设备管理页面视图。
    /// 严格遵循 AGENTS.md §6.4.2 / §6.5 / §6.6 / §6.7 与 docs/mockup 方案 A：
    /// 1. 实现 IDeviceLocator 接口，支持概览页跨页定位（SelectAndScrollToDevice）；
    /// 2. 四列设备卡片网格（DIP 像素对齐，响应式等宽，不随 DPI 撕裂）；
    /// 3. 无设备时呈现明确空态（双指引，绝不显示 0%）；
    /// 4. 每张卡片显示真实名称或别名、连接方式细分、在线三态、电量、充电状态和读数时效；
    /// 5. 离线卡片半透明并显示上次读数与时间；
    /// 6. 按 DeviceKey 记录选中项，采集刷新与切页往返时保持选中状态与滚动位置，两台同型号设备绝不串选；
    /// 7. 别名、置顶、忽略与组件勾选必须经 SettingsStore 磁盘回读校验成功后才开放生效。
    /// </summary>
    public sealed class DeviceSummaryView : UserControl, IDeviceLocator
    {
        private readonly Grid _rootGrid;
        private readonly TextBlock _titleBlock;
        private readonly TextBlock _subTitleBlock;
        private readonly ScrollViewer _scrollViewer;
        private readonly StackPanel _cardsContainer;
        private readonly Border _emptyPanel;
        private readonly Border _actionBar;

        // 底部操作条控件
        private readonly Image _actIcon;
        private readonly TextBox _aliasInput;
        private readonly TextBlock _actDetail;
        private readonly Button _btnSaveAlias;
        private readonly Button _btnPin;
        private readonly Button _btnIgnore;
        private readonly Button _btnLog;

        // 数据与持久化状态
        private SettingsStore _settingsStore;
        private IReadOnlyList<DeviceState> _lastSnapshot;
        private DeviceKey _selectedDeviceKey;
        private readonly Dictionary<DeviceKey, DeviceCardControl> _cardControls = new Dictionary<DeviceKey, DeviceCardControl>();

        /// <summary>当前选中的持久设备键</summary>
        public DeviceKey SelectedDeviceKey
        {
            get { return _selectedDeviceKey; }
        }

        public DeviceSummaryView()
        {
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            // 1. 初始化设置存储
            InitSettingsStore();

            _rootGrid = new Grid();
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: 顶部标题栏
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1: 卡片滚动区
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: 提示栏
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 3: 底部操作条

            // 顶部标题栏 (vhead)
            var headPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var headTop = new Grid();
            headTop.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headTop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _titleBlock = new TextBlock
            {
                Text = "设备管理",
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.Bold
            };
            Grid.SetColumn(_titleBlock, 0);
            headTop.Children.Add(_titleBlock);

            headPanel.Children.Add(headTop);

            _subTitleBlock = new TextBlock
            {
                Text = "勾选 = 进组件 · 当前 0 台",
                Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 0, 0)
            };
            headPanel.Children.Add(_subTitleBlock);

            Grid.SetRow(headPanel, 0);
            _rootGrid.Children.Add(headPanel);

            // 卡片区域与滚动条
            _cardsContainer = new StackPanel
            {
                Orientation = Orientation.Vertical
            };

            _scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
                Content = _cardsContainer,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(_scrollViewer, 1);
            _rootGrid.Children.Add(_scrollViewer);

            // 空态面板 (未发现设备)
            _emptyPanel = BuildEmptyPanel();
            Grid.SetRow(_emptyPanel, 1);
            _rootGrid.Children.Add(_emptyPanel);

            // 提示栏 (hintbar)
            var hintBar = BuildHintBar();
            Grid.SetRow(hintBar, 2);
            _rootGrid.Children.Add(hintBar);

            // 底部操作条 (actbar)
            _actionBar = BuildActionBar(out _actIcon, out _aliasInput, out _actDetail, out _btnSaveAlias, out _btnPin, out _btnIgnore, out _btnLog);
            Grid.SetRow(_actionBar, 3);
            _rootGrid.Children.Add(_actionBar);

            Content = _rootGrid;

            // 初始空态
            ShowEmpty(true);
            UpdateActionBarState(null);
        }

        private void InitSettingsStore()
        {
            try
            {
                _settingsStore = new SettingsStore();
                _settingsStore.Load();
                _settingsStore.SettingsChanged += OnSettingsStoreChanged;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("初始化设备页 SettingsStore 失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 允许外部注入共用的 SettingsStore（如由 MainWindow/App 统一管理）。
        /// </summary>
        public void SetSettingsStore(SettingsStore store)
        {
            if (store == null) return;
            if (_settingsStore != null)
            {
                _settingsStore.SettingsChanged -= OnSettingsStoreChanged;
            }
            _settingsStore = store;
            _settingsStore.SettingsChanged += OnSettingsStoreChanged;
            if (_lastSnapshot != null)
            {
                Render(_lastSnapshot);
            }
        }

        private void OnSettingsStoreChanged(object sender, AppSettings settings)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate { OnSettingsStoreChanged(sender, settings); }));
                return;
            }

            if (_lastSnapshot != null)
            {
                // 收到设置更新时刷新卡片展示（别名、勾选、置顶、忽略）
                Render(_lastSnapshot);
            }
        }

        /// <summary>
        /// 外部主入口：消费 DeviceState 快照并渲染四列卡片。
        /// 保持滚动位置与 DeviceKey 选中状态，防串选。
        /// </summary>
        public void Render(IReadOnlyList<DeviceState> snapshot)
        {
            _lastSnapshot = snapshot;

            if (snapshot == null || snapshot.Count == 0)
            {
                _selectedDeviceKey = null;
                _cardControls.Clear();
                _cardsContainer.Children.Clear();
                ShowEmpty(true);
                UpdateSubTitle(0, 0, 0);
                UpdateActionBarState(null);
                return;
            }

            ShowEmpty(false);

            // 1. 记录当前滚动偏移
            double scrollOffset = _scrollViewer.VerticalOffset;

            // 2. 检查 _selectedDeviceKey 是否仍在新的快照中
            DeviceState selectedState = null;
            if (_selectedDeviceKey != null)
            {
                foreach (var item in snapshot)
                {
                    if (item.Key.Equals(_selectedDeviceKey))
                    {
                        selectedState = item;
                        break;
                    }
                }
                if (selectedState == null)
                {
                    // 选中的设备被彻底移除/拔出，优雅清空选中
                    _selectedDeviceKey = null;
                }
            }

            // 3. 读取当前设置
            AppSettings currentSettings = _settingsStore != null ? _settingsStore.Current : new AppSettings();
            var aliases = currentSettings.DeviceAliases ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pinned = currentSettings.PinnedDevices ?? new List<string>();
            var ignored = currentSettings.IgnoredDevices ?? new List<string>();
            var widgetSelected = currentSettings.WidgetDevices ?? new List<string>();

            // 更新设备别名到 DeviceState（若存在）
            foreach (var dev in snapshot)
            {
                string alias;
                if (aliases.TryGetValue(dev.Key.Value, out alias))
                {
                    dev.Alias = alias;
                }
            }

            // 4. 统计数量：进入组件数、在线数、总数
            int widgetCount = 0;
            int onlineCount = 0;
            foreach (var dev in snapshot)
            {
                if (widgetSelected.Contains(dev.Key.Value))
                {
                    widgetCount++;
                }
                if (dev.State == ConnectionState.Online)
                {
                    onlineCount++;
                }
            }
            UpdateSubTitle(widgetCount, onlineCount, snapshot.Count);

            // 5. 排序：置顶设备优先（按置顶次序），其余按电量升序排序 (AGENTS.md D13)
            var sortedList = snapshot.OrderBy(d =>
            {
                int pinIdx = pinned.IndexOf(d.Key.Value);
                return pinIdx >= 0 ? pinIdx : int.MaxValue;
            }).ThenBy(d =>
            {
                // 置顶之后：电量升序，无电量或离线放在后面
                if (d.State == ConnectionState.Offline) return 300;
                if (!d.Percent.HasValue) return 200;
                return d.Percent.Value;
            }).ToList();

            // 6. 重建四列卡片网格
            _cardControls.Clear();
            _cardsContainer.Children.Clear();

            // 使用 4 列等宽 Star Grid，每 4 个卡片一行
            var grid = new Grid
            {
                Margin = new Thickness(0, 2, 0, 8),
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            for (int c = 0; c < 4; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            int totalRows = (sortedList.Count + 3) / 4;
            for (int r = 0; r < totalRows; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            for (int i = 0; i < sortedList.Count; i++)
            {
                var dev = sortedList[i];
                int row = i / 4;
                int col = i % 4;

                bool isSel = _selectedDeviceKey != null && dev.Key.Equals(_selectedDeviceKey);
                bool isCk = widgetSelected.Contains(dev.Key.Value);
                bool isPin = pinned.Contains(dev.Key.Value);
                bool isIgn = ignored.Contains(dev.Key.Value);

                var card = new DeviceCardControl(dev, isSel, isCk, isPin, isIgn)
                {
                    Margin = new Thickness(col == 0 ? 0 : 6, row == 0 ? 0 : 6, col == 3 ? 0 : 6, 6)
                };

                // 事件绑定
                card.CardClicked += OnCardClicked;
                card.CheckClicked += OnCardCheckClicked;

                Grid.SetRow(card, row);
                Grid.SetColumn(card, col);
                grid.Children.Add(card);

                _cardControls[dev.Key] = card;
            }

            _cardsContainer.Children.Add(grid);

            // 7. 更新底部操作条
            UpdateActionBarState(selectedState);

            // 8. 恢复滚动位置
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_scrollViewer != null)
                {
                    _scrollViewer.ScrollToVerticalOffset(scrollOffset);
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void OnCardClicked(DeviceCardControl clickedCard)
        {
            if (clickedCard == null) return;

            _selectedDeviceKey = clickedCard.DeviceKey;

            // 更新所有卡片的选中状态视觉
            foreach (var kvp in _cardControls)
            {
                kvp.Value.IsSelected = kvp.Key.Equals(_selectedDeviceKey);
            }

            UpdateActionBarState(clickedCard.State);
        }

        private void OnCardCheckClicked(DeviceCardControl card, bool targetChecked)
        {
            if (card == null || _settingsStore == null) return;

            string key = card.DeviceKey.Value;
            bool success = _settingsStore.TrySetWidgetSelected(key, targetChecked);

            if (!success)
            {
                MessageBox.Show("组件勾选持久化保存失败，已恢复原状。", "PowerDock", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 保存成功后刷新（Render 会重新构建并同步最新勾选与计数）
            if (_lastSnapshot != null)
            {
                Render(_lastSnapshot);
            }
        }

        /// <summary>
        /// 跨页定位接口：依据 DeviceKey 选中并平滑滚动到指定设备卡片（实现 IDeviceLocator）。
        /// 供概览页点击设备全貌柱状图时跨页定位调用。
        /// </summary>
        public bool SelectAndScrollToDevice(DeviceKey key)
        {
            if (key == null) return false;

            DeviceCardControl targetCard;
            if (_cardControls.TryGetValue(key, out targetCard))
            {
                _selectedDeviceKey = key;
                foreach (var kvp in _cardControls)
                {
                    kvp.Value.IsSelected = kvp.Key.Equals(key);
                }
                UpdateActionBarState(targetCard.State);
                targetCard.BringIntoView();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 跨页定位重载：通过设备 Key 字符串或名称定位对应卡片（实现 IDeviceLocator）。
        /// </summary>
        public bool SelectAndScrollToDevice(string keyOrName)
        {
            if (string.IsNullOrWhiteSpace(keyOrName)) return false;

            // 优先按 Key 匹配
            foreach (var kvp in _cardControls)
            {
                if (string.Equals(kvp.Key.Value, keyOrName, StringComparison.OrdinalIgnoreCase))
                {
                    return SelectAndScrollToDevice(kvp.Key);
                }
            }

            // 其次按 EffectiveName / DisplayName 匹配
            foreach (var kvp in _cardControls)
            {
                var s = kvp.Value.State;
                if (string.Equals(s.EffectiveName, keyOrName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.DisplayName, keyOrName, StringComparison.OrdinalIgnoreCase))
                {
                    return SelectAndScrollToDevice(kvp.Key);
                }
            }

            return false;
        }

        private void UpdateSubTitle(int widgetCount, int onlineCount, int totalCount)
        {
            _subTitleBlock.Text = string.Format(
                "勾选 = 进组件 · 当前 {0} 台在组件 · 共 {1} 台 · {2} 台在线",
                widgetCount, totalCount, onlineCount);
        }

        private void ShowEmpty(bool empty)
        {
            _emptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            _scrollViewer.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private static Border BuildEmptyPanel()
        {
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 40)
            };

            var iconImg = new Image
            {
                Width = 48,
                Height = 48,
                Source = DeviceIcons.CreateEmptyStateIcon(),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            };
            panel.Children.Add(iconImg);

            panel.Children.Add(new TextBlock
            {
                Text = "未发现设备",
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            });

            panel.Children.Add(new TextBlock
            {
                Text = "检查蓝牙是否已开启，并确认设备已配对",
                Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            });

            panel.Children.Add(new TextBlock
            {
                Text = "USB / 2.4G 外设请确认接收器已插入",
                Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            return new Border
            {
                Child = panel,
                Background = Brushes.Transparent
            };
        }

        private static Border BuildHintBar()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            panel.Children.Add(new TextBlock
            {
                Text = "💡 ",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });

            panel.Children.Add(new TextBlock
            {
                Text = "勾选决定谁出现在桌面挂件里，不影响列表统计与低电量提醒。挂件最多 7 台，超出按电量升序截断。",
                Foreground = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center
            });

            return new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 4, 0, 10),
                Background = new SolidColorBrush(Color.FromArgb(90, 24, 28, 36)),
                Child = panel
            };
        }

        private Border BuildActionBar(
            out Image actIcon,
            out TextBox aliasInput,
            out TextBlock actDetail,
            out Button btnSaveAlias,
            out Button btnPin,
            out Button btnIgnore,
            out Button btnLog)
        {
            var root = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(12, 8, 12, 8),
                Background = new SolidColorBrush(Color.FromArgb(220, 24, 28, 38))
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 左侧：链路图标 + 别名输入框 + 读数时效
            var leftPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            actIcon = new Image
            {
                Width = 14,
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            leftPanel.Children.Add(actIcon);

            aliasInput = new TextBox
            {
                Width = 136,
                Height = 26,
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            aliasInput.KeyDown += OnAliasInputKeyDown;
            leftPanel.Children.Add(aliasInput);

            actDetail = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            leftPanel.Children.Add(actDetail);

            Grid.SetColumn(leftPanel, 0);
            grid.Children.Add(leftPanel);

            // 右侧按钮组
            var btnStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            btnSaveAlias = CreateActionButton("设别名", OnSaveAliasClicked);
            btnPin = CreateActionButton("置顶", OnPinClicked);
            btnIgnore = CreateActionButton("忽略此设备", OnIgnoreClicked);
            btnLog = CreateActionButton("查看详情", OnLogClicked);

            btnStack.Children.Add(btnSaveAlias);
            btnStack.Children.Add(btnPin);
            btnStack.Children.Add(btnIgnore);
            btnStack.Children.Add(btnLog);

            Grid.SetColumn(btnStack, 1);
            grid.Children.Add(btnStack);

            root.Child = grid;
            return root;
        }

        private static Button CreateActionButton(string title, RoutedEventHandler onClick)
        {
            var btn = new Button
            {
                Content = title,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            btn.Click += onClick;
            return btn;
        }

        private void OnAliasInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OnSaveAliasClicked(sender, e);
            }
        }

        private void OnSaveAliasClicked(object sender, RoutedEventArgs e)
        {
            if (_selectedDeviceKey == null || _settingsStore == null) return;

            string key = _selectedDeviceKey.Value;
            string newAlias = _aliasInput.Text != null ? _aliasInput.Text.Trim() : string.Empty;

            bool success = _settingsStore.TrySetDeviceAlias(key, newAlias);
            if (!success)
            {
                MessageBox.Show("别名保存失败，已回滚更改。", "PowerDock", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_lastSnapshot != null)
            {
                Render(_lastSnapshot);
            }
        }

        private void OnPinClicked(object sender, RoutedEventArgs e)
        {
            if (_selectedDeviceKey == null || _settingsStore == null) return;

            string key = _selectedDeviceKey.Value;
            bool isPinned;
            bool success = _settingsStore.TryTogglePinnedDevice(key, out isPinned);
            if (!success)
            {
                MessageBox.Show("置顶操作保存失败，已回滚。", "PowerDock", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_lastSnapshot != null)
            {
                Render(_lastSnapshot);
            }
        }

        private void OnIgnoreClicked(object sender, RoutedEventArgs e)
        {
            if (_selectedDeviceKey == null || _settingsStore == null) return;

            string key = _selectedDeviceKey.Value;
            bool isIgnored;
            bool success = _settingsStore.TryToggleIgnoredDevice(key, out isIgnored);
            if (!success)
            {
                MessageBox.Show("忽略设备保存失败，已回滚。", "PowerDock", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_lastSnapshot != null)
            {
                Render(_lastSnapshot);
            }
        }

        private void OnLogClicked(object sender, RoutedEventArgs e)
        {
            if (_selectedDeviceKey == null) return;

            DeviceState dev = null;
            if (_lastSnapshot != null)
            {
                foreach (var item in _lastSnapshot)
                {
                    if (item.Key.Equals(_selectedDeviceKey))
                    {
                        dev = item;
                        break;
                    }
                }
            }

            if (dev == null) return;

            string info = string.Format(
                "【设备详情与诊断日志】\n" +
                "物理设备键 (Key): {0}\n" +
                "真实名称: {1}\n" +
                "当前别名: {2}\n" +
                "类别: {3}\n" +
                "连接三态: {4}\n" +
                "链路细分: {5}\n" +
                "链路依据 (Evidence): {6}\n" +
                "容器验证 (IdentityVerified): {7}\n" +
                "电量百分比: {8}\n" +
                "充电状态: {9}\n" +
                "最后成功读数: {10}\n" +
                "来源不一致冲突 (HasConflict): {11}\n" +
                "来源数量: {12}",
                dev.Key.Value,
                dev.DisplayName,
                dev.Alias ?? "(无)",
                dev.Category,
                dev.State,
                DeviceCardControl.DescribeLinkExact(dev),
                dev.Link != null ? dev.Link.Evidence : "(无)",
                dev.IdentityVerified ? "已验证 (Windows ContainerId)" : "未确认 (临时端点身份)",
                dev.Percent.HasValue ? dev.Percent.Value + "%" : "— (" + DeviceCardControl.DescribeReason(dev.PercentReason) + ")",
                dev.Charging.HasValue ? (dev.Charging.Value ? "正在充电" : "未充电") : "未知",
                dev.LastSuccessfulReadingUtc.HasValue ? dev.LastSuccessfulReadingUtc.Value.ToString("yyyy-MM-dd HH:mm:ss UTC") : "(从未出数)",
                dev.HasConflict ? "是 (多来源差值 >5%)" : "否",
                dev.Sources != null ? dev.Sources.Count.ToString() : "0"
            );

            MessageBox.Show(info, "设备诊断信息 - " + dev.EffectiveName, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void UpdateActionBarState(DeviceState dev)
        {
            if (dev == null)
            {
                _actIcon.Source = DeviceIcons.CreateUnknownIcon();
                _aliasInput.Text = string.Empty;
                _aliasInput.IsEnabled = false;
                _actDetail.Text = "点击任意设备卡片进行别名、置顶与管理";
                _btnSaveAlias.IsEnabled = false;
                _btnPin.IsEnabled = false;
                _btnPin.Content = "置顶";
                _btnIgnore.IsEnabled = false;
                _btnIgnore.Content = "忽略此设备";
                _btnLog.IsEnabled = false;
                return;
            }

            _aliasInput.IsEnabled = true;
            _aliasInput.Text = dev.Alias ?? dev.DisplayName;
            _btnSaveAlias.IsEnabled = true;
            _btnLog.IsEnabled = true;

            AppSettings current = _settingsStore != null ? _settingsStore.Current : new AppSettings();
            bool isPinned = current.PinnedDevices != null && current.PinnedDevices.Contains(dev.Key.Value);
            bool isIgnored = current.IgnoredDevices != null && current.IgnoredDevices.Contains(dev.Key.Value);

            _btnPin.IsEnabled = true;
            _btnPin.Content = isPinned ? "取消置顶" : "置顶";

            _btnIgnore.IsEnabled = true;
            _btnIgnore.Content = isIgnored ? "取消忽略" : "忽略此设备";

            string linkText = DeviceCardControl.DescribeLinkExact(dev);
            string pctText = dev.Percent.HasValue ? dev.Percent.Value + "%" : "—";
            string timeText = dev.LastSuccessfulReadingUtc.HasValue ? DeviceCardControl.FormatRelativeTime(dev.LastSuccessfulReadingUtc.Value) : "从未出数";

            _actDetail.Text = string.Format("{0} · {1} · {2}", linkText, pctText, timeText);
        }
    }
}
