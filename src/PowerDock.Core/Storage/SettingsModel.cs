using System;
using System.Collections.Generic;

namespace PowerDock.Core.Storage
{
    public sealed class AppSettings
    {
        public string Theme { get; set; }
        public string WidgetSize { get; set; }
        public bool WidgetTopMost { get; set; }
        public double WidgetLeft { get; set; }
        public double WidgetTop { get; set; }
        public bool AutoStart { get; set; }
        public bool MinimizeToTrayOnClose { get; set; }
        public int LowBatteryThreshold { get; set; }
        public int AlertCooldownMinutes { get; set; }
        public Dictionary<string, string> DeviceAliases { get; set; }
        public List<string> IgnoredDevices { get; set; }
        public List<string> PinnedDevices { get; set; }
        public List<string> WidgetDevices { get; set; }
        public bool ShowStatusDot { get; set; }
        public bool ShowBatteryBar { get; set; }
        public bool ShowPercentage { get; set; }

        public AppSettings()
        {
            Theme = "dark";
            WidgetSize = "medium";
            WidgetTopMost = true;
            WidgetLeft = -1;
            WidgetTop = -1;
            AutoStart = false;
            MinimizeToTrayOnClose = true;
            LowBatteryThreshold = 20;
            AlertCooldownMinutes = 30;
            ShowStatusDot = true;
            ShowBatteryBar = true;
            ShowPercentage = true;
            DeviceAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IgnoredDevices = new List<string>();
            PinnedDevices = new List<string>();
            WidgetDevices = new List<string>();
        }

        public AppSettings Clone()
        {
            var clone = new AppSettings();
            clone.Theme = Theme;
            clone.WidgetSize = WidgetSize;
            clone.WidgetTopMost = WidgetTopMost;
            clone.WidgetLeft = WidgetLeft;
            clone.WidgetTop = WidgetTop;
            clone.AutoStart = AutoStart;
            clone.MinimizeToTrayOnClose = MinimizeToTrayOnClose;
            clone.LowBatteryThreshold = LowBatteryThreshold;
            clone.AlertCooldownMinutes = AlertCooldownMinutes;
            clone.ShowStatusDot = ShowStatusDot;
            clone.ShowBatteryBar = ShowBatteryBar;
            clone.ShowPercentage = ShowPercentage;
            clone.DeviceAliases = new Dictionary<string, string>(DeviceAliases, StringComparer.OrdinalIgnoreCase);
            clone.IgnoredDevices = new List<string>(IgnoredDevices);
            clone.PinnedDevices = new List<string>(PinnedDevices);
            clone.WidgetDevices = new List<string>(WidgetDevices);
            return clone;
        }
    }
}
