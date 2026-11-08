using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PowerDock.App.Diagnostics;
using PowerDock.App.Interop;
using PowerDock.App.Shell;
using PowerDock.Core.Diagnostics;

namespace PowerDock.App.Windows
{
    public partial class MainWindow : Window
    {
        public const double TargetWidthDip = 880.0;
        public const double TargetHeightDip = 600.0;
        public const double TitleBarHeightDip = 44.0;
        public const double DockWidthDip = 360.0;
        public const double DockHeightDip = 44.0;

        private Border _rootSurface;
        private WindowBackdropController _backdrop;
        private Grid _mainGrid;
        private Border _titleBar;
        private ContentControl _contentHost;
        private Border _bottomDock;
        private TextBlock _titleText;

        private readonly Dictionary<MainPage, DockItemVisual> _dockItems = new Dictionary<MainPage, DockItemVisual>();
        private readonly Dictionary<MainPage, MainPageVisual> _pages = new Dictionary<MainPage, MainPageVisual>();
        private readonly Dictionary<MainPage, double> _savedScrollOffsets = new Dictionary<MainPage, double>();
        private MainPage _currentPage = MainPage.Overview;
        private bool _contentBound;

        public bool MinimizeToTrayOnClose { get; set; }
        public TrayIconService TrayService { get; set; }

        /// <summary>任意时刻唯一可见页。Dock 选中态与 _contentHost 都由它派生（D19）。</summary>
        public MainPage CurrentPage
        {
            get { return _currentPage; }
        }

        public MainWindow()
        {
            MinimizeToTrayOnClose = true;

            // 规格约束 (AGENTS.md D11 & §6.4: 880x600, 圆角 8)
            Width = TargetWidthDip;
            Height = TargetHeightDip;
            MinWidth = TargetWidthDip;
            MinHeight = TargetHeightDip;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanMinimize;
            Title = "PowerDock";

            // 基础背景
            Background = Brushes.Transparent;

            BuildVisualTree();

            // Backdrop lifecycle: transparent CompositionTarget, checked frame extension, staged
            // Acrylic activation, redo on every show/theme/composition change (PR-S0-05).
            _backdrop = new WindowBackdropController();
            _backdrop.Attach(this, _rootSurface);

            // First-frame instrumentation: Program.Main arms the stopwatch; this is the first
            // CompositionTarget.Rendering callback on the UI thread.
            CompositionTarget.Rendering += OnFirstCompositionRendering;
            FirstFrameTimer.MarkHandlerRegistered();

            Closing += OnWindowClosing;
        }

        private void BuildVisualTree()
        {
            _rootSurface = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(235, 20, 22, 28)), // #14161C 基础底色
                BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                ClipToBounds = true
            };

            _mainGrid = new Grid();
            _mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleBarHeightDip) });
            _mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 1. 标题栏 (44 DIP)
            _titleBar = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(16, 0, 16, 0)
            };
            _titleBar.MouseLeftButtonDown += OnTitleBarMouseDown;

            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

            _titleText = new TextBlock
            {
                Text = "⚡ PowerDock · 桌面电量中心",
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_titleText, 0);
            titleGrid.Children.Add(_titleText);

            // 窗口操作按钮区
            var btnStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            var btnMin = CreateCaptionButton("—", "最小化", delegate { WindowState = WindowState.Minimized; });
            var btnClose = CreateCaptionButton("✕", "关闭", delegate { Close(); });
            btnStack.Children.Add(btnMin);
            btnStack.Children.Add(btnClose);
            Grid.SetColumn(btnStack, 1);
            titleGrid.Children.Add(btnStack);

            _titleBar.Child = titleGrid;
            Grid.SetRow(_titleBar, 0);
            _mainGrid.Children.Add(_titleBar);

            // 2. 主内容区
            var contentContainer = new Grid();
            _contentHost = new ContentControl
            {
                Margin = new Thickness(18, 16, 18, 84), // 底部留 84 DIP 为 Dock 让位 (AGENTS.md §6.4)
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch
            };
            AutomationProperties.SetAutomationId(_contentHost, MainPageCatalog.ContentHostAutomationId);
            AutomationProperties.SetName(_contentHost, "PowerDock 内容区");
            contentContainer.Children.Add(_contentHost);

            // 3. 底部居中浮动 Dock 胶囊 (bottom 14, AGENTS.md §6.4)
            _bottomDock = new Border
            {
                Width = DockWidthDip,
                Height = DockHeightDip,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 14),
                CornerRadius = new CornerRadius(22),
                Background = new SolidColorBrush(Color.FromArgb(200, 30, 34, 44)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                BorderThickness = new Thickness(1)
            };

            var dockStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            foreach (MainPage page in MainPageCatalog.All())
            {
                DockItemVisual item = DockItemFactory.Create(page);
                item.Button.Click += OnDockItemClick;
                _dockItems[page] = item;
                dockStack.Children.Add(item.Button);

                // 分隔线固定落在「组件」与「设置」之间（AGENTS.md §6.4）
                if (page == MainPage.Widgets)
                {
                    dockStack.Children.Add(new Border
                    {
                        Width = 1,
                        Height = 16,
                        Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                        Margin = new Thickness(8, 0, 8, 0)
                    });
                }
            }

            _bottomDock.Child = dockStack;
            contentContainer.Children.Add(_bottomDock);

            Grid.SetRow(contentContainer, 1);
            _mainGrid.Children.Add(contentContainer);

            _rootSurface.Child = _mainGrid;
            Content = _rootSurface;

            // 四页实例一次性建好并被缓存：切页只换引用，不重建（D19「保留页面状态」）。
            foreach (MainPage page in MainPageCatalog.All())
            {
                _pages[page] = MainPageContentFactory.Create(page);
            }

            // 默认进入概览
            NavigateTo(MainPage.Overview);
        }

        private Button CreateCaptionButton(string symbol, string tooltip, RoutedEventHandler onClick)
        {
            var btn = new Button
            {
                Content = symbol,
                ToolTip = tooltip,
                Width = 28,
                Height = 24,
                Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 11,
                Margin = new Thickness(4, 0, 0, 0)
            };
            btn.Click += onClick;
            return btn;
        }

        private void OnDockItemClick(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            if (button == null)
            {
                return;
            }

            foreach (KeyValuePair<MainPage, DockItemVisual> pair in _dockItems)
            {
                if (ReferenceEquals(pair.Value.Button, button))
                {
                    NavigateTo(pair.Key);
                    return;
                }
            }
        }

        /// <summary>
        /// 统一导航入口。重复点击当前页直接返回：不重建页面、不重置滚动偏移、不打断页内状态。
        /// </summary>
        /// <returns>内容是否真的换了页。</returns>
        public bool NavigateTo(MainPage page)
        {
            MainPageVisual target;
            if (!_pages.TryGetValue(page, out target))
            {
                throw new ArgumentOutOfRangeException("page");
            }

            if (_contentBound && page == _currentPage)
            {
                return false;
            }

            SaveScrollOffset(_currentPage);

            _currentPage = page;
            _contentHost.Content = target.Scroll;
            RestoreScrollOffset(page);
            UpdateDockSelection();
            _contentBound = true;
            return true;
        }

        private void UpdateDockSelection()
        {
            foreach (MainPage page in MainPageCatalog.All())
            {
                DockItemVisual item;
                if (!_dockItems.TryGetValue(page, out item))
                {
                    continue;
                }

                bool selected = page == _currentPage;
                item.SelectionDot.Opacity = selected ? 1.0 : 0.0;
                Brush foreground = selected ? DockItemFactory.SelectedForeground() : DockItemFactory.UnselectedForeground();
                item.IconText.Foreground = foreground;
                item.LabelText.Foreground = foreground;
                item.LabelText.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
                AutomationProperties.SetItemStatus(item.Button,
                    selected ? MainPageCatalog.ItemStatusSelected : MainPageCatalog.ItemStatusUnselected);
            }
        }

        private void SaveScrollOffset(MainPage page)
        {
            MainPageVisual visual;
            if (_pages.TryGetValue(page, out visual) && visual.Scroll != null)
            {
                _savedScrollOffsets[page] = visual.Scroll.VerticalOffset;
            }
        }

        /// <summary>
        /// 页面重新挂进视觉树后 extent 可能尚未就绪，先立即恢复一次，再在 Loaded 优先级纠正一次。
        /// </summary>
        private void RestoreScrollOffset(MainPage page)
        {
            MainPageVisual visual;
            double offset;
            if (!_pages.TryGetValue(page, out visual) || !_savedScrollOffsets.TryGetValue(page, out offset) || offset <= 0.0)
            {
                return;
            }

            ScrollViewer scroll = visual.Scroll;
            scroll.UpdateLayout();
            scroll.ScrollToVerticalOffset(offset);
            scroll.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                try
                {
                    scroll.ScrollToVerticalOffset(offset);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("Restore scroll offset failed for page " + page + ": " + ex.Message);
                }
            }));
        }

        private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try
                {
                    DragMove();
                }
                catch
                {
                }
            }
        }

        public WindowBackdropController Backdrop
        {
            get { return _backdrop; }
        }

        private void OnFirstCompositionRendering(object sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnFirstCompositionRendering;
            FirstFrameTimer.NotifyFirstFrame();
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            ApplyCloseBehavior(e);
        }

        /// <summary>
        /// The single close-behavior implementation (AGENTS.md §7.6):
        /// tray available -> cancel close and hide to tray; tray unavailable -> exit the process.
        /// Used by Closing and by --diagnose-close* so diagnostics exercise the real path.
        /// </summary>
        internal void ApplyCloseBehavior(CancelEventArgs e)
        {
            if (MinimizeToTrayOnClose && TrayService != null && TrayService.IsTrayAvailable)
            {
                e.Cancel = true;
                Hide();
                AppLogger.Info("MainWindow hidden to tray. trayAvailable=true pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);
            }
            else
            {
                // Never hide into an invisible process (AGENTS.md §7.6). Distinguish the two reasons:
                // the setting asked for exit-on-close, or the tray is genuinely unavailable.
                e.Cancel = false;
                bool trayAvailable = TrayService != null && TrayService.IsTrayAvailable;
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                if (!MinimizeToTrayOnClose)
                {
                    AppLogger.Info("Close behavior: exit on close (reason=setting MinimizeToTrayOnClose=false, trayAvailable=" + trayAvailable + " pid=" + pid + ")");
                }
                else
                {
                    AppLogger.Warn("Tray unavailable. MainWindow closing directly shuts down the application. trayAvailable=" + trayAvailable + " pid=" + pid);
                }
                var app = Application.Current;
                if (app != null)
                {
                    app.Shutdown();
                }
            }
        }

        /// <summary>Diagnostic-only entry that invokes the real close behavior on the real window.</summary>
        internal CancelEventArgs SimulateCloseForDiagnostics()
        {
            CancelEventArgs args = new CancelEventArgs();
            ApplyCloseBehavior(args);
            return args;
        }

        // ---- 诊断/自检入口（同程序集内可见）。真正的外部验证走 UIA，不依赖这些入口。 ----

        internal Button DockButton(MainPage page)
        {
            DockItemVisual item;
            return _dockItems.TryGetValue(page, out item) ? item.Button : null;
        }

        internal Border DockSelectionDot(MainPage page)
        {
            DockItemVisual item;
            return _dockItems.TryGetValue(page, out item) ? item.SelectionDot : null;
        }

        internal TextBlock DockIconText(MainPage page)
        {
            DockItemVisual item;
            return _dockItems.TryGetValue(page, out item) ? item.IconText : null;
        }

        internal TextBlock DockLabelText(MainPage page)
        {
            DockItemVisual item;
            return _dockItems.TryGetValue(page, out item) ? item.LabelText : null;
        }

        internal ScrollViewer PageScrollViewer(MainPage page)
        {
            MainPageVisual visual;
            return _pages.TryGetValue(page, out visual) ? visual.Scroll : null;
        }

        internal Border DockCapsule
        {
            get { return _bottomDock; }
        }

        internal ContentControl ContentHost
        {
            get { return _contentHost; }
        }

        internal UIElement PageView(MainPage page)
        {
            MainPageVisual visual;
            return _pages.TryGetValue(page, out visual) ? visual.Scroll : null;
        }

        internal StackPanel PagePanel(MainPage page)
        {
            MainPageVisual visual;
            return _pages.TryGetValue(page, out visual) ? visual.Panel : null;
        }

        internal TextBlock PageTitleText(MainPage page)
        {
            MainPageVisual visual;
            return _pages.TryGetValue(page, out visual) ? visual.TitleText : null;
        }

        internal List<string> PageEmptyStateLines(MainPage page)
        {
            MainPageVisual visual;
            return _pages.TryGetValue(page, out visual) ? visual.EmptyStateLines : null;
        }

        internal int SelectedDockItemCount()
        {
            int count = 0;
            foreach (MainPage page in MainPageCatalog.All())
            {
                if (page == _currentPage)
                {
                    count++;
                }
            }
            return count;
        }

        internal double SavedScrollOffset(MainPage page)
        {
            double offset;
            return _savedScrollOffsets.TryGetValue(page, out offset) ? offset : 0.0;
        }

        public void ActivateAndRestore()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ActivateAndRestore));
                return;
            }

            if (!IsVisible)
            {
                Show();
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();
            Focus();

            var helper = new WindowInteropHelper(this);
            if (helper.Handle != IntPtr.Zero)
            {
                NativeMethods.SetForegroundWindow(helper.Handle);
            }
        }
    }
}
