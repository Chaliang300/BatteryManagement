using System;
using System.Globalization;
using PowerDock.Core.Common;

namespace PowerDock.Core.Widgets
{
    public enum WidgetTone
    {
        Ok,
        Warn,
        Danger,
        Neutral,
        Off
    }

    /// <summary>
    /// 挂件单行渲染只读投影数据（组件页预览与桌面挂件宿主复用同一模型，AGENTS.md §6.3 / §6.5）。
    /// </summary>
    public sealed class WidgetRowItem
    {
        public DeviceKey Key { get; }
        public string Name { get; }
        public int? Percent { get; }
        public bool? IsCharging { get; }
        public bool IsOffline { get; }
        public LinkKind LinkKind { get; }
        public WidgetTone Tone { get; }
        public string DisplayPercent { get; }
        public double FillRatio { get; }

        public WidgetRowItem(
            DeviceKey key,
            string name,
            int? percent,
            bool? isCharging,
            bool isOffline,
            LinkKind linkKind,
            int lowBatteryThreshold = 20)
        {
            Key = key ?? new DeviceKey(string.Empty);
            Name = !string.IsNullOrWhiteSpace(name) ? name : "未命名设备";
            Percent = percent;
            IsCharging = isCharging;
            IsOffline = isOffline;
            LinkKind = linkKind;

            if (isOffline)
            {
                Tone = WidgetTone.Off;
                DisplayPercent = "—";
                FillRatio = percent.HasValue ? Math.Clamp(percent.Value / 100.0, 0.0, 1.0) : 0.0;
            }
            else
            {
                if (isCharging == true)
                {
                    Tone = WidgetTone.Ok;
                }
                else if (percent.HasValue)
                {
                    if (percent.Value < lowBatteryThreshold)
                    {
                        Tone = WidgetTone.Danger;
                    }
                    else if (percent.Value <= 40)
                    {
                        Tone = WidgetTone.Warn;
                    }
                    else
                    {
                        Tone = WidgetTone.Ok;
                    }
                }
                else
                {
                    Tone = WidgetTone.Neutral;
                }

                DisplayPercent = percent.HasValue
                    ? string.Format(CultureInfo.InvariantCulture, "{0}%", percent.Value)
                    : "—";

                FillRatio = percent.HasValue ? Math.Clamp(percent.Value / 100.0, 0.0, 1.0) : 0.0;
            }
        }
    }
}
