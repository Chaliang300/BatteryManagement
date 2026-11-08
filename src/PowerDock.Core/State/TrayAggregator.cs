using System;
using System.Collections.Generic;
using System.Globalization;
using PowerDock.Core.Common;

namespace PowerDock.Core.State
{
    /// <summary>
    /// 托盘聚合结果。Percent 为 null 表示「没有有效读数」，渲染器按未知处理（C9）。
    /// </summary>
    public sealed class TrayAggregate
    {
        public int? Percent { get; private set; }
        public bool IsCharging { get; private set; }
        public bool IsOffline { get; private set; }
        public string Tooltip { get; private set; }

        public TrayAggregate(int? percent, bool isCharging, bool isOffline, string tooltip)
        {
            Percent = percent;
            IsCharging = isCharging;
            IsOffline = isOffline;
            Tooltip = tooltip ?? string.Empty;
        }
    }

    /// <summary>
    /// 托盘聚合计算（AGENTS P0-2 / D14）：取在场设备的最低电量。
    /// 约束（C9 / §8）：
    /// 1. 无有效读数时绝不得显示固定 100% 或 0%，必须呈现未知（Percent = null）；
    /// 2. 充电状态与离线状态取该最低电量设备的实际状态；
    /// 3. Tooltip 格式必须清晰：有读数显示「设备名 - XX% (充电中)」，无读数显示「N 台设备，电量未知」。
    /// </summary>
    public static class TrayAggregator
    {
        public static TrayAggregate Aggregate(IReadOnlyList<DeviceState> snapshot)
        {
            if (snapshot == null || snapshot.Count == 0)
            {
                return new TrayAggregate(null, false, true, "PowerDock - 未发现设备");
            }

            DeviceState lowest = null;
            foreach (DeviceState state in snapshot)
            {
                if (!state.Percent.HasValue) continue;
                if (lowest == null || state.Percent.Value < lowest.Percent.Value)
                {
                    lowest = state;
                }
            }

            if (lowest == null)
            {
                return new TrayAggregate(null, false, false, string.Format(
                    CultureInfo.InvariantCulture, "PowerDock - {0} 台设备，电量未知", snapshot.Count));
            }

            bool charging = lowest.Charging == true;
            string tooltip = string.Format(
                CultureInfo.InvariantCulture,
                "{0} - {1}%{2}",
                string.IsNullOrEmpty(lowest.EffectiveName) ? "PowerDock" : lowest.EffectiveName,
                lowest.Percent.Value,
                charging ? " (充电中)" : string.Empty);

            return new TrayAggregate(lowest.Percent, charging, lowest.State == ConnectionState.Offline, tooltip);
        }
    }
}
