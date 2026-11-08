using System;
using System.Collections.Generic;
using System.Globalization;
using PowerDock.Core.Common;

namespace PowerDock.Core.Widgets
{
    /// <summary>
    /// 挂件数据投影管道：将底层设备快照按唯一的勾选集合与尺寸规则计算为挂件行数据。
    /// 供组件页预览与桌面挂件宿主复用（AGENTS.md §6.3 / §6.5）。
    /// </summary>
    public static class WidgetPipeline
    {
        public static WidgetPresentationModel Project(
            IReadOnlyList<DeviceState> snapshot,
            IReadOnlyCollection<string> selectedDeviceKeys,
            WidgetSizeKind sizeKind,
            int lowBatteryThreshold = 20,
            bool isHostConnected = false)
        {
            var rawList = snapshot ?? Array.Empty<DeviceState>();
            int totalCount = rawList.Count;

            int onlineCount = 0;
            for (int i = 0; i < rawList.Count; i++)
            {
                if (rawList[i].State == ConnectionState.Online)
                {
                    onlineCount++;
                }
            }

            var selectedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (selectedDeviceKeys != null)
            {
                foreach (string key in selectedDeviceKeys)
                {
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        selectedSet.Add(key.Trim());
                    }
                }
            }

            var filtered = new List<DeviceState>();
            for (int i = 0; i < rawList.Count; i++)
            {
                var s = rawList[i];
                string keyVal = s.Key != null ? s.Key.Value : string.Empty;
                if (selectedSet.Contains(keyVal))
                {
                    filtered.Add(s);
                }
            }

            int selectedCount = filtered.Count;

            // 契约排序：电量升序（有效电量优先，null 排末尾，同电量按名称升序）
            filtered.Sort(delegate(DeviceState a, DeviceState b)
            {
                if (!a.Percent.HasValue && !b.Percent.HasValue)
                {
                    return string.Compare(a.EffectiveName, b.EffectiveName, StringComparison.OrdinalIgnoreCase);
                }
                if (!a.Percent.HasValue) return 1;
                if (!b.Percent.HasValue) return -1;
                int cmp = a.Percent.Value.CompareTo(b.Percent.Value);
                if (cmp != 0) return cmp;
                return string.Compare(a.EffectiveName, b.EffectiveName, StringComparison.OrdinalIgnoreCase);
            });

            int maxRows = WidgetSizeSpecs.GetMaxRows(sizeKind);
            int takeCount = Math.Min(filtered.Count, maxRows);

            var rowItems = new List<WidgetRowItem>(takeCount);
            for (int i = 0; i < takeCount; i++)
            {
                var s = filtered[i];
                bool isOffline = s.State == ConnectionState.Offline;
                LinkKind linkKind = s.Link != null ? s.Link.Kind : LinkKind.Unknown;
                rowItems.Add(new WidgetRowItem(
                    s.Key,
                    s.EffectiveName,
                    s.Percent,
                    s.Charging,
                    isOffline,
                    linkKind,
                    lowBatteryThreshold));
            }

            string footnote = string.Format(
                CultureInfo.InvariantCulture,
                "共 {0} 台 · {1} 在线 · 按电量升序 · 空闲 60s 刷新",
                totalCount,
                onlineCount);

            return new WidgetPresentationModel(
                sizeKind,
                rowItems,
                totalCount,
                onlineCount,
                selectedCount,
                footnote,
                isHostConnected);
        }
    }
}
