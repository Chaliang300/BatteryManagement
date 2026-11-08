using System;
using System.Collections.Generic;
using System.Globalization;
using PowerDock.Core.Common;
using PowerDock.Core.Storage;

namespace PowerDock.Core.State
{
    public enum TrendTone
    {
        Neutral,
        Warn,
        Danger
    }

    public sealed class DailyTrendPoint
    {
        public DateTime Date { get; private set; }
        public string DayLabel { get; private set; }
        public int? Percent { get; private set; }
        public bool IsGap { get; private set; }
        public bool IsToday { get; private set; }
        public TrendTone Tone { get; private set; }

        public DailyTrendPoint(DateTime date, string dayLabel, int? percent, bool isGap, bool isToday, TrendTone tone)
        {
            Date = date;
            DayLabel = dayLabel ?? string.Empty;
            Percent = percent;
            IsGap = isGap;
            IsToday = isToday;
            Tone = tone;
        }
    }

    public sealed class OverviewState
    {
        // 第一层：状态带四格
        public int NeedChargingCount { get; set; }
        public int? NeedChargingLowestPercent { get; set; }
        public DeviceState NeedChargingDevice { get; set; }

        public int ChargingCount { get; set; }
        public DeviceState PrimaryChargingDevice { get; set; }

        public int OnlineCount { get; set; }
        public int TotalCount { get; set; }
        public string OfflineSummary { get; set; }

        public int InWidgetCount { get; set; }
        public int WidgetMaxRows { get; set; }

        // 第二层：焦点区
        public DeviceState FocusDevice { get; set; }
        public int? FocusPercent { get; set; }
        public string FocusConditionText { get; set; }
        public string FocusMetaText { get; set; }
        public IReadOnlyList<DailyTrendPoint> FocusDailyTrend { get; set; }
        public bool HasTrendHistory { get; set; }

        // 第二层：另外2台要注意 + 其余汇总
        public IReadOnlyList<DeviceState> SecondaryDevices { get; set; }
        public int RemainingCount { get; set; }
        public int? RemainingLowestPercent { get; set; }

        // 第三层：全量设备条
        public IReadOnlyList<DeviceState> AllDevices { get; set; }

        // 全局空态标记
        public bool IsEmpty { get; set; }
    }

    /// <summary>
    /// 概览页（方案 A）聚合计算引擎（AGENTS.md §6.5）。
    /// 纯计算无 UI 依赖，可单测覆盖全部边界情况：
    /// 1. 需充电：基于设置阈值判定，未知电量（null）不参与最低电量与需充电判定；
    /// 2. 充电中：统计正在充电的设备及首要充电设备；
    /// 3. 在线：统计在线台数与总台数，摘要离线情况；
    /// 4. 在组件：统计已固定或依序进组件台数，说明规格上限；
    /// 5. 焦点：低电 > 充电中 > 离线 > 正常最低电量；近 7 天趋势在无数据时标记空态；
    /// 6. 全貌：全部设备按条形呈现，支持点击定位。
    /// </summary>
    public static class OverviewAggregator
    {
        public static OverviewState Aggregate(
            IReadOnlyList<DeviceState> snapshot,
            AppSettings settings,
            IReadOnlyList<BatterySample> focusHistory = null,
            DateTime? nowUtc = null)
        {
            DateTime now = nowUtc ?? DateTime.UtcNow;
            int threshold = settings != null ? settings.LowBatteryThreshold : 20;
            int maxWidgetRows = ResolveWidgetMaxRows(settings);

            if (snapshot == null || snapshot.Count == 0)
            {
                return new OverviewState
                {
                    IsEmpty = true,
                    NeedChargingCount = 0,
                    NeedChargingLowestPercent = null,
                    NeedChargingDevice = null,
                    ChargingCount = 0,
                    PrimaryChargingDevice = null,
                    OnlineCount = 0,
                    TotalCount = 0,
                    OfflineSummary = "未发现设备",
                    InWidgetCount = 0,
                    WidgetMaxRows = maxWidgetRows,
                    FocusDevice = null,
                    FocusPercent = null,
                    FocusConditionText = string.Empty,
                    FocusMetaText = "未发现设备",
                    FocusDailyTrend = BuildEmptyTrend(now),
                    HasTrendHistory = false,
                    SecondaryDevices = new List<DeviceState>(),
                    RemainingCount = 0,
                    RemainingLowestPercent = null,
                    AllDevices = new List<DeviceState>()
                };
            }

            // 1. 统计格 1：需充电
            int needChargingCount = 0;
            DeviceState lowestNeedCharging = null;

            // 统计格 2：充电中
            int chargingCount = 0;
            DeviceState primaryCharging = null;

            // 统计格 3：在线
            int onlineCount = 0;
            var offlineDevices = new List<DeviceState>();

            // 全局有效最低电量设备（未知值 null 不参与）
            DeviceState lowestAnyValid = null;

            foreach (DeviceState dev in snapshot)
            {
                if (dev == null) continue;

                if (dev.State == ConnectionState.Online)
                {
                    onlineCount++;
                }
                else if (dev.State == ConnectionState.Offline)
                {
                    offlineDevices.Add(dev);
                }

                if (dev.Charging == true)
                {
                    chargingCount++;
                    if (primaryCharging == null)
                    {
                        primaryCharging = dev;
                    }
                }

                if (dev.Percent.HasValue)
                {
                    int pct = dev.Percent.Value;
                    if (lowestAnyValid == null || pct < lowestAnyValid.Percent.Value)
                    {
                        lowestAnyValid = dev;
                    }

                    if (pct < threshold && dev.State != ConnectionState.Offline)
                    {
                        needChargingCount++;
                        if (lowestNeedCharging == null || pct < lowestNeedCharging.Percent.Value)
                        {
                            lowestNeedCharging = dev;
                        }
                    }
                }
            }

            // 统计格 4：在组件
            int inWidgetCount = CalculateInWidgetCount(snapshot, settings, maxWidgetRows);

            string offlineSummary;
            if (offlineDevices.Count == 0)
            {
                offlineSummary = "全部设备在线";
            }
            else if (offlineDevices.Count == 1)
            {
                offlineSummary = offlineDevices[0].EffectiveName + " 离线";
            }
            else
            {
                offlineSummary = string.Format(CultureInfo.InvariantCulture, "{0} 等 {1} 台离线",
                    offlineDevices[0].EffectiveName, offlineDevices.Count);
            }

            // 焦点设备选择：需充电 > 正在充 > 离线 > 任意最低电量 > 首台
            DeviceState focusDevice = lowestNeedCharging;
            if (focusDevice == null)
            {
                focusDevice = primaryCharging;
            }
            if (focusDevice == null && offlineDevices.Count > 0)
            {
                focusDevice = offlineDevices[0];
            }
            if (focusDevice == null)
            {
                focusDevice = lowestAnyValid ?? snapshot[0];
            }

            // 焦点设备元信息
            int? focusPercent = focusDevice.Percent;
            string focusCondition;
            if (focusPercent.HasValue && focusPercent.Value < threshold)
            {
                focusCondition = string.Format(CultureInfo.InvariantCulture, "低于 {0}% 阈值", threshold);
            }
            else if (focusDevice.Charging == true)
            {
                focusCondition = "正在充电中";
            }
            else if (focusDevice.State == ConnectionState.Offline)
            {
                focusCondition = "设备已离线";
            }
            else if (focusPercent.HasValue)
            {
                focusCondition = "电量状态正常";
            }
            else
            {
                focusCondition = "等待读数更新";
            }

            string linkText = focusDevice.Link != null ? focusDevice.Link.Describe() : "连接方式待确认";
            string timeAgo = FormatTimeAgo(focusDevice.LastSuccessfulReadingUtc, now);
            string focusMeta = string.Format(CultureInfo.InvariantCulture, "{0} · {1} · {2}", linkText, timeAgo, focusCondition);

            // 7 天趋势计算
            bool hasTrendHistory;
            IReadOnlyList<DailyTrendPoint> trend = Build7DayTrend(focusDevice.Key, focusHistory, now, out hasTrendHistory);

            // 第二层右侧：另外 2 台要注意 + 其余汇总
            var secondary = new List<DeviceState>();
            foreach (DeviceState dev in snapshot)
            {
                if (dev == null || ReferenceEquals(dev, focusDevice)) continue;
                if (secondary.Count >= 2) break;

                // 优先把充电中、离线或偏低的设备排入
                if (dev.Charging == true || dev.State == ConnectionState.Offline ||
                    (dev.Percent.HasValue && dev.Percent.Value <= 40))
                {
                    secondary.Add(dev);
                }
            }

            // 若不足 2 台，把其他设备顺次补齐
            if (secondary.Count < 2)
            {
                foreach (DeviceState dev in snapshot)
                {
                    if (dev == null || ReferenceEquals(dev, focusDevice) || secondary.Contains(dev)) continue;
                    if (secondary.Count >= 2) break;
                    secondary.Add(dev);
                }
            }

            // 计算其余设备数与最低电量
            int remainingCount = snapshot.Count - 1 - secondary.Count;
            if (remainingCount < 0) remainingCount = 0;

            int? remainingLowestPercent = null;
            if (remainingCount > 0)
            {
                foreach (DeviceState dev in snapshot)
                {
                    if (ReferenceEquals(dev, focusDevice) || secondary.Contains(dev)) continue;
                    if (dev.Percent.HasValue)
                    {
                        if (!remainingLowestPercent.HasValue || dev.Percent.Value < remainingLowestPercent.Value)
                        {
                            remainingLowestPercent = dev.Percent.Value;
                        }
                    }
                }
            }

            return new OverviewState
            {
                IsEmpty = false,
                NeedChargingCount = needChargingCount,
                NeedChargingLowestPercent = lowestNeedCharging != null ? lowestNeedCharging.Percent : null,
                NeedChargingDevice = lowestNeedCharging,
                ChargingCount = chargingCount,
                PrimaryChargingDevice = primaryCharging,
                OnlineCount = onlineCount,
                TotalCount = snapshot.Count,
                OfflineSummary = offlineSummary,
                InWidgetCount = inWidgetCount,
                WidgetMaxRows = maxWidgetRows,
                FocusDevice = focusDevice,
                FocusPercent = focusPercent,
                FocusConditionText = focusCondition,
                FocusMetaText = focusMeta,
                FocusDailyTrend = trend,
                HasTrendHistory = hasTrendHistory,
                SecondaryDevices = secondary,
                RemainingCount = remainingCount,
                RemainingLowestPercent = remainingLowestPercent,
                AllDevices = snapshot
            };
        }

        private static int ResolveWidgetMaxRows(AppSettings settings)
        {
            if (settings == null) return 5;
            string size = (settings.WidgetSize ?? "medium").ToLowerInvariant();
            if (size == "small") return 3;
            if (size == "large") return 7;
            return 5;
        }

        private static int CalculateInWidgetCount(IReadOnlyList<DeviceState> snapshot, AppSettings settings, int maxRows)
        {
            if (snapshot == null || snapshot.Count == 0) return 0;
            if (settings != null && settings.PinnedDevices != null && settings.PinnedDevices.Count > 0)
            {
                var pinnedSet = new HashSet<string>(settings.PinnedDevices, StringComparer.OrdinalIgnoreCase);
                int count = 0;
                foreach (DeviceState dev in snapshot)
                {
                    if (dev != null && dev.Key != null && pinnedSet.Contains(dev.Key.Value))
                    {
                        count++;
                    }
                }
                return count;
            }
            return Math.Min(snapshot.Count, maxRows);
        }

        private static IReadOnlyList<DailyTrendPoint> BuildEmptyTrend(DateTime now)
        {
            var list = new List<DailyTrendPoint>(7);
            for (int i = 6; i >= 0; i--)
            {
                DateTime day = now.Date.AddDays(-i);
                list.Add(new DailyTrendPoint(day, GetDayLabel(day.DayOfWeek), null, true, i == 0, TrendTone.Neutral));
            }
            return list;
        }

        private static IReadOnlyList<DailyTrendPoint> Build7DayTrend(
            DeviceKey targetKey,
            IReadOnlyList<BatterySample> samples,
            DateTime now,
            out bool hasHistory)
        {
            hasHistory = false;
            var list = new List<DailyTrendPoint>(7);
            if (targetKey == null || samples == null || samples.Count == 0)
            {
                return BuildEmptyTrend(now);
            }

            // 过滤该设备的样本
            var dayBuckets = new Dictionary<DateTime, int>();
            foreach (BatterySample s in samples)
            {
                if (s == null || s.Key == null || !s.Key.Equals(targetKey)) continue;
                DateTime d = s.TsUtc.Date;
                dayBuckets[d] = s.Percent; // 按时间顺序后覆盖，取当天最新
            }

            if (dayBuckets.Count == 0)
            {
                return BuildEmptyTrend(now);
            }

            hasHistory = true;
            for (int i = 6; i >= 0; i--)
            {
                DateTime day = now.Date.AddDays(-i);
                int pct;
                if (dayBuckets.TryGetValue(day, out pct))
                {
                    TrendTone tone = pct < 20 ? TrendTone.Danger : (pct <= 40 ? TrendTone.Warn : TrendTone.Neutral);
                    list.Add(new DailyTrendPoint(day, GetDayLabel(day.DayOfWeek), pct, false, i == 0, tone));
                }
                else
                {
                    list.Add(new DailyTrendPoint(day, GetDayLabel(day.DayOfWeek), null, true, i == 0, TrendTone.Neutral));
                }
            }

            return list;
        }

        public static string GetDayLabel(DayOfWeek dow)
        {
            switch (dow)
            {
                case DayOfWeek.Sunday: return "日";
                case DayOfWeek.Monday: return "一";
                case DayOfWeek.Tuesday: return "二";
                case DayOfWeek.Wednesday: return "三";
                case DayOfWeek.Thursday: return "四";
                case DayOfWeek.Friday: return "五";
                case DayOfWeek.Saturday: return "六";
                default: return "?";
            }
        }

        public static string FormatTimeAgo(DateTime? timeUtc, DateTime nowUtc)
        {
            if (!timeUtc.HasValue) return "暂无更新";
            TimeSpan diff = nowUtc - timeUtc.Value;
            if (diff.TotalSeconds < 0) diff = TimeSpan.Zero;
            if (diff.TotalSeconds < 45) return "刚刚更新";
            if (diff.TotalMinutes < 60)
            {
                int mins = Math.Max(1, (int)diff.TotalMinutes);
                return string.Format(CultureInfo.InvariantCulture, "{0} 分钟前", mins);
            }
            if (diff.TotalHours < 24)
            {
                int hrs = (int)diff.TotalHours;
                return string.Format(CultureInfo.InvariantCulture, "{0} 小时前", hrs);
            }
            int days = (int)diff.TotalDays;
            return string.Format(CultureInfo.InvariantCulture, "{0} 天前", days);
        }
    }
}
