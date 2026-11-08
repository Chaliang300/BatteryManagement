using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace PowerDock.App.Windows
{
    /// <summary>
    /// Dock 项。继承 Button 只为一件事：把 Enter 补进按键通路。
    /// 走 <c>OnKeyDown</c> 重写而不是 Preview 事件，是因为真实输入与诊断的
    /// <c>RaiseEvent(KeyDownEvent)</c> 都必经这里，而 Preview 只在真实输入链路上触发。
    /// </summary>
    internal sealed class DockButton : Button
    {
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                RaiseEvent(new RoutedEventArgs(Button.ClickEvent, this));
                e.Handled = true;
                return;
            }

            // Space 交给 WPF ButtonBase 原生路径（KeyDown 置 IsPressed，KeyUp 触发 Click）。
            base.OnKeyDown(e);
        }
    }

    /// <summary>Dock 一项的视觉件。选中态由圆点 + accent 文字共同表达（AGENTS.md §6.4）。</summary>
    internal sealed class DockItemVisual
    {
        public MainPage Page { get; set; }
        public Button Button { get; set; }
        public Border SelectionDot { get; set; }
        public TextBlock IconText { get; set; }
        public TextBlock LabelText { get; set; }
    }

    /// <summary>
    /// 构造 Dock 项。用 <see cref="Button"/> 而不是 TextBlock：只有 Button 才自带
    /// Click 路由、Tab 焦点、Space/Enter 激活与 UIA Invoke 语义（D19）。
    /// 模板根 Border 铺满按钮矩形（含 Padding），所以文字、图标与内部留白同一命中区域。
    /// </summary>
    internal static class DockItemFactory
    {
        internal static readonly Color AccentColor = Color.FromRgb(107, 166, 255);
        internal const double ItemHeightDip = 34.0;
        internal const double HorizontalPaddingDip = 10.0;

        private static readonly Brush AccentBrush = Frozen(AccentColor);
        private static readonly Brush IdleTextBrush = Frozen(Color.FromArgb(160, 255, 255, 255));

        private static ControlTemplate _template;

        /// <summary>
        /// 只解析一次。触发器里的 TargetName 依赖模板命名范围，不能跨模板复用实例。
        /// 触发器顺序是**契约**：同一属性被多个触发器命中时，WPF 属性引擎让**后出现者生效**
        /// （见 Nick Kramer 关于属性引擎的说明）。所以 <c>IsPressed</c> 必须排在
        /// <c>IsKeyboardFocused</c> 之后；否则「已聚焦时按下」的底色会被聚焦底色吞掉，按下看不见反馈。
        /// <c>BorderBrush</c>（焦点环）只由聚焦触发器设置，按下时焦点环与按下底色仍同时成立。
        /// </summary>
        private static ControlTemplate Template()
        {
            if (_template == null)
            {
                _template = (ControlTemplate)XamlReader.Parse(
                    "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                    "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" TargetType=\"{x:Type Button}\">" +
                    "<Border x:Name=\"DockSurface\" Background=\"{TemplateBinding Background}\" " +
                    "BorderBrush=\"Transparent\" BorderThickness=\"1.5\" CornerRadius=\"8\" " +
                    "Padding=\"{TemplateBinding Padding}\" SnapsToDevicePixels=\"True\">" +
                    "<ContentPresenter HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\" />" +
                    "</Border>" +
                    "<ControlTemplate.Triggers>" +
                    "<Trigger Property=\"IsMouseOver\" Value=\"True\">" +
                    "<Setter TargetName=\"DockSurface\" Property=\"Background\" Value=\"#1FFFFFFF\" />" +
                    "</Trigger>" +
                    "<Trigger Property=\"IsKeyboardFocused\" Value=\"True\">" +
                    "<Setter TargetName=\"DockSurface\" Property=\"BorderBrush\" Value=\"#FF6BA6FF\" />" +
                    "<Setter TargetName=\"DockSurface\" Property=\"Background\" Value=\"#1A6BA6FF\" />" +
                    "</Trigger>" +
                    // 必须排在聚焦触发器之后：同一属性被多个触发器命中时 WPF 让后出现者生效，
                    // 否则「已聚焦时按下」的底色会被聚焦底色吞掉，按下就没有视觉反馈。
                    "<Trigger Property=\"IsPressed\" Value=\"True\">" +
                    "<Setter TargetName=\"DockSurface\" Property=\"Background\" Value=\"#336BA6FF\" />" +
                    "</Trigger>" +
                    "</ControlTemplate.Triggers>" +
                    "</ControlTemplate>");
            }

            return _template;
        }

        public static DockItemVisual Create(MainPage page)
        {
            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var icon = new TextBlock
            {
                Text = MainPageCatalog.Icon(page),
                FontSize = 12,
                Foreground = IdleTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            var label = new TextBlock
            {
                Text = MainPageCatalog.Label(page),
                FontSize = 12,
                Foreground = IdleTextBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(icon);
            row.Children.Add(label);
            Grid.SetRow(row, 0);
            content.Children.Add(row);

            // 选中指示：3px 圆点（AGENTS.md §6.4）。不承担命中，命中由按钮矩形负责。
            var dot = new Border
            {
                Width = 3,
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 1, 0, 1),
                Opacity = 0.0,
                IsHitTestVisible = false
            };
            Grid.SetRow(dot, 1);
            content.Children.Add(dot);

            var button = new DockButton
            {
                Content = content,
                Height = ItemHeightDip,
                Padding = new Thickness(HorizontalPaddingDip, 0, HorizontalPaddingDip, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = true,
                IsTabStop = true,
                FocusVisualStyle = null,   // 焦点环由模板的 IsKeyboardFocused 触发器绘制
                Template = Template(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(button, MainPageCatalog.Label(page));
            AutomationProperties.SetAutomationId(button, MainPageCatalog.DockAutomationId(page));
            AutomationProperties.SetItemStatus(button, MainPageCatalog.ItemStatusUnselected);
            AutomationProperties.SetHelpText(button, "切换到" + MainPageCatalog.Title(page) + "页");

            return new DockItemVisual
            {
                Page = page,
                Button = button,
                SelectionDot = dot,
                IconText = icon,
                LabelText = label
            };
        }

        internal static Brush SelectedForeground()
        {
            return AccentBrush;
        }

        internal static Brush UnselectedForeground()
        {
            return IdleTextBrush;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
