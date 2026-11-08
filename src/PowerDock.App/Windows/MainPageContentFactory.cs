using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerDock.App.Windows
{
    /// <summary>
    /// 一页的视觉与其状态容器。Scroll 是切页时唯一挂进 _contentHost 的元素；
    /// 页面实例被缓存复用，所以切页不会重建、滚动位置与页内状态得以保留（D19）。
    /// </summary>
    internal sealed class MainPageVisual
    {
        public MainPage Page { get; set; }
        public ScrollViewer Scroll { get; set; }
        public StackPanel Panel { get; set; }
        public TextBlock TitleText { get; set; }
        public List<string> EmptyStateLines { get; set; }

        /// <summary>
        /// 外部内容槽（采集层挂设备视图用）。标题永远在槽外，所以注入内容不会抹掉页标题。
        /// </summary>
        public StackPanel ContentSlot { get; set; }

        /// <summary>空态卡片。有真实内容注入时折叠，避免"既显示设备列表又显示未发现设备"。</summary>
        public Border EmptyStateCard { get; set; }
    }

    /// <summary>
    /// 四页内容。本阶段（D19 / AGENTS.md §6.4.1）只交付「标题 + 明确空态」：
    /// 没有采集数据就不显示任何电量数字，未实现的功能不呈现为可操作控件。
    /// </summary>
    internal static class MainPageContentFactory
    {
        private static readonly Brush TitleBrush = Frozen(Color.FromRgb(255, 255, 255));
        private static readonly Brush LeadBrush = Frozen(Color.FromArgb(214, 255, 255, 255));
        private static readonly Brush BodyBrush = Frozen(Color.FromArgb(158, 255, 255, 255));
        private static readonly Brush AccentBrush = Frozen(Color.FromRgb(107, 166, 255));
        private static readonly Brush CardBrush = Frozen(Color.FromArgb(26, 255, 255, 255));
        private static readonly Brush CardBorderBrush = Frozen(Color.FromArgb(40, 255, 255, 255));

        public static MainPageVisual Create(MainPage page)
        {
            string lead;
            string[] lines;
            GetEmptyState(page, out lead, out lines);

            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var title = new TextBlock
            {
                Text = MainPageCatalog.Title(page),
                Foreground = TitleBrush,
                FontSize = 20,
                FontWeight = FontWeights.Bold
            };
            AutomationProperties.SetAutomationId(title, MainPageCatalog.PageTitleAutomationId);
            AutomationProperties.SetName(title, MainPageCatalog.Title(page));
            panel.Children.Add(title);

            var emptyCard = new Border
            {
                Background = CardBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 14, 0, 0)
            };

            var cardStack = new StackPanel { Orientation = Orientation.Vertical };
            cardStack.Children.Add(new TextBlock
            {
                Text = lead,
                Foreground = LeadBrush,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            });

            var allLines = new List<string>();
            allLines.Add(lead);
            foreach (string line in lines)
            {
                allLines.Add(line);
                cardStack.Children.Add(new TextBlock
                {
                    Text = line,
                    Foreground = BodyBrush,
                    FontSize = 12,
                    Margin = new Thickness(0, 8, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            emptyCard.Child = cardStack;

            // 内容槽在空态卡片之上：外部注入真实内容后折叠空态，标题始终在两者之外。
            var contentSlot = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetAutomationId(contentSlot, MainPageCatalog.PageAutomationId(page) + ".ContentSlot");
            panel.Children.Add(contentSlot);
            panel.Children.Add(emptyCard);

            var scroll = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
                Focusable = false
            };
            AutomationProperties.SetAutomationId(scroll, MainPageCatalog.PageAutomationId(page));
            AutomationProperties.SetName(scroll, MainPageCatalog.Title(page) + "页内容");

            return new MainPageVisual
            {
                Page = page,
                Scroll = scroll,
                Panel = panel,
                TitleText = title,
                EmptyStateLines = allLines,
                ContentSlot = contentSlot,
                EmptyStateCard = emptyCard
            };
        }

        /// <summary>
        /// 空态文案必须与实际能力一致：不承诺未实现的东西，也不出现任何电量数字。
        /// </summary>
        private static void GetEmptyState(MainPage page, out string lead, out string[] lines)
        {
            switch (page)
            {
                case MainPage.Overview:
                    lead = "尚未接入设备采集，本页暂不显示电量数字。";
                    lines = new[]
                    {
                        "S1 阶段接入 WMI / HID / BLE 后，这里显示需充电、充电中、在线、在组件四项汇总。",
                        "· 检查蓝牙是否开启",
                        "· 在系统设置中配对设备",
                        "没有读数时一律显示「—」，不用零值充数。"
                    };
                    return;

                case MainPage.Devices:
                    lead = "未发现设备。";
                    lines = new[]
                    {
                        "· 检查蓝牙是否开启",
                        "· 在系统设置中配对设备",
                        "设备卡、别名与排序将在 S2 阶段接入；当前这一页没有可操作项。"
                    };
                    return;

                case MainPage.Widgets:
                    lead = "桌面挂件尚未实现（S2 阶段）。";
                    lines = new[]
                    {
                        "这里将显示挂件三档尺寸（173×173 / 362×173 / 362×362 DIP）的 1:1 预览与行清单。",
                        "预览与真实挂件同源同一份行数据；当前没有可配置项。"
                    };
                    return;

                case MainPage.Settings:
                    lead = "设置界面尚未实现。本页只列出外壳层已经接通的项，不提供可操作控件。";
                    lines = new[]
                    {
                        "· 开机自启：写入当前用户 Run 注册表项，关闭后无残留入口（外壳已实现）",
                        "· 关闭窗口行为：按已保存设置选择最小化到托盘或直接退出（外壳已实现）",
                        "界面开关、低电量阈值、刷新策略与历史保留将在 S2 / S3 接入。"
                    };
                    return;

                default:
                    lead = "未知页面。";
                    lines = new[] { "这不是一个有效的导航目标。" };
                    return;
            }
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
