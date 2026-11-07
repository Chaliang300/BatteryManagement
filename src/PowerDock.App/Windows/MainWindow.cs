using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PowerDock.App.Diagnostics;
using PowerDock.App.Interop;
using PowerDock.App.Shell;
using PowerDock.Core.Diagnostics;

namespace PowerDock.App.Windows
{
    public class MainWindow : Window
    {
        public const double TargetWidthDip = 880.0;
        public const double TargetHeightDip = 600.0;
        public const double TitleBarHeightDip = 44.0;

        private Border _rootSurface;
        private WindowBackdropController _backdrop;
        private Grid _mainGrid;
        private Border _titleBar;
        private ContentControl _contentHost;
        private Border _bottomDock;
        private TextBlock _titleText;

        public bool MinimizeToTrayOnClose { get; set; }
        public TrayIconService TrayService { get; set; }

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
                Margin = new Thickness(18, 16, 18, 84) // 底部留 84 DIP 为 Dock 让位 (AGENTS.md §6.4)
            };

            var placeholder = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            placeholder.Children.Add(new TextBlock
            {
                Text = "PowerDock S0 工程底座就绪",
                Foreground = new SolidColorBrush(Color.FromRgb(107, 166, 255)), // --accent
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            });
            placeholder.Children.Add(new TextBlock
            {
                Text = "单实例互斥 · 命名管道唤醒 · 注册表自启 · 三格托盘 · DWM Acrylic 880×600",
                Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            _contentHost.Content = placeholder;
            contentContainer.Children.Add(_contentHost);

            // 3. 底部居中浮动 Dock 胶囊 (bottom 14, AGENTS.md §6.4)
            _bottomDock = new Border
            {
                Width = 360,
                Height = 44,
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
            dockStack.Children.Add(CreateDockItem("▣ 概览", true));
            dockStack.Children.Add(CreateDockItem("☰ 设备", false));
            dockStack.Children.Add(CreateDockItem("▤ 组件", false));
            dockStack.Children.Add(new Border
            {
                Width = 1,
                Height = 16,
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                Margin = new Thickness(8, 0, 8, 0)
            });
            dockStack.Children.Add(CreateDockItem("⚙ 设置", false));
            _bottomDock.Child = dockStack;

            contentContainer.Children.Add(_bottomDock);

            Grid.SetRow(contentContainer, 1);
            _mainGrid.Children.Add(contentContainer);

            _rootSurface.Child = _mainGrid;
            Content = _rootSurface;
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

        private TextBlock CreateDockItem(string label, bool selected)
        {
            return new TextBlock
            {
                Text = label,
                Foreground = selected ? new SolidColorBrush(Color.FromRgb(107, 166, 255)) : new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                FontSize = 12,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(12, 0, 12, 0),
                Cursor = Cursors.Hand
            };
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
            if (MinimizeToTrayOnClose && TrayService != null && TrayService.IsTrayAvailable)
            {
                e.Cancel = true;
                Hide();
                AppLogger.Info("MainWindow hidden to tray.");
            }
            else
            {
                // Tray unavailable: never hide into an invisible process (AGENTS.md §7.6).
                AppLogger.Warn("Tray unavailable. MainWindow closing directly shuts down the application.");
                var app = Application.Current;
                if (app != null)
                {
                    app.Shutdown();
                }
            }
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


