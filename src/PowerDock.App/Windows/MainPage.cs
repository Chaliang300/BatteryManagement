using System;

namespace PowerDock.App.Windows
{
    /// <summary>
    /// 主窗口四页。枚举值即 Dock 从左到右的顺序，也是 Tab 顺序（AGENTS.md §6.4）。
    /// 顺序是契约：改动需同步 §6.4.1 与 tools/verify-dock-navigation.ps1。
    /// </summary>
    public enum MainPage
    {
        Overview = 0,
        Devices = 1,
        Widgets = 2,
        Settings = 3
    }

    /// <summary>
    /// 页面元数据。Label / AutomationId / ItemStatus 文案是外部验证契约（AGENTS.md §6.4.1），
    /// 改动必须同步验证脚本，否则外部验证会静默失配。
    /// </summary>
    public static class MainPageCatalog
    {
        public const string DockAutomationIdPrefix = "PowerDock.Dock.";
        public const string PageAutomationIdPrefix = "PowerDock.Page.";
        public const string PageTitleAutomationId = "PowerDock.PageTitle";
        public const string ContentHostAutomationId = "PowerDock.ContentHost";
        public const string ItemStatusSelected = "已选中";
        public const string ItemStatusUnselected = "未选中";

        private static readonly MainPage[] OrderedPages =
        {
            MainPage.Overview,
            MainPage.Devices,
            MainPage.Widgets,
            MainPage.Settings
        };

        /// <summary>返回副本，避免调用方改到内部顺序。</summary>
        public static MainPage[] All()
        {
            return (MainPage[])OrderedPages.Clone();
        }

        /// <summary>
        /// 枚举合法性。导航入口与外部页内容注入都必须先过这一关：
        /// 传入未定义值时抛 <see cref="ArgumentOutOfRangeException"/> 而不是静默显示空页。
        /// </summary>
        public static bool IsDefined(MainPage page)
        {
            return Array.IndexOf(OrderedPages, page) >= 0;
        }

        public static string Icon(MainPage page)
        {
            switch (page)
            {
                case MainPage.Overview: return "▣";
                case MainPage.Devices: return "☰";
                case MainPage.Widgets: return "▤";
                case MainPage.Settings: return "⚙";
                default: throw new ArgumentOutOfRangeException("page");
            }
        }

        public static string Label(MainPage page)
        {
            switch (page)
            {
                case MainPage.Overview: return "概览";
                case MainPage.Devices: return "设备";
                case MainPage.Widgets: return "组件";
                case MainPage.Settings: return "设置";
                default: throw new ArgumentOutOfRangeException("page");
            }
        }

        public static string Title(MainPage page)
        {
            switch (page)
            {
                case MainPage.Overview: return "概览";
                case MainPage.Devices: return "设备管理";
                case MainPage.Widgets: return "组件";
                case MainPage.Settings: return "设置";
                default: throw new ArgumentOutOfRangeException("page");
            }
        }

        public static string DockAutomationId(MainPage page)
        {
            return DockAutomationIdPrefix + page.ToString();
        }

        public static string PageAutomationId(MainPage page)
        {
            return PageAutomationIdPrefix + page.ToString();
        }
    }
}
