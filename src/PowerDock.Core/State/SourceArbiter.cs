using System;
using System.Collections.Generic;
using PowerDock.Core.Common;
using PowerDock.Core.State;

namespace PowerDock.Core.State
{
    /// <summary>
    /// 按 D4 在「可用且未过期」的来源中选电量。规则：
    /// 1. 权威序 Vendor &gt; BluetoothGatt &gt; Hid &gt; Wmi；
    /// 2. 只有 Percent != null 且未超出该来源时效窗口的来源参与裁决；
    /// 3. 参与裁决的来源之间差值 &gt; 5 个百分点时标记冲突，但仍显式取权威源，不静默平均；
    /// 4. 全部不可用时返回 null，并把「最权威那条来源」的原因上抛，UI 据此显示「—」与原因。
    /// </summary>
    public static class SourceArbiter
    {
        public const int ConflictThreshold = 5;

        private static readonly BatterySource[] Priority =
        {
            BatterySource.Vendor,
            BatterySource.BluetoothGatt,
            BatterySource.Hid,
            BatterySource.Wmi
        };

        public sealed class Result
        {
            public int? Percent { get; internal set; }
            public bool? Charging { get; internal set; }
            public BatterySource? Source { get; internal set; }
            public DateTime? TimestampUtc { get; internal set; }
            public bool HasConflict { get; internal set; }
            public bool IsStale { get; internal set; }
            public PercentUnavailableReason Reason { get; internal set; }
        }

        public static int Rank(BatterySource source)
        {
            for (int i = 0; i < Priority.Length; i++)
            {
                if (Priority[i] == source) return i;
            }
            return Priority.Length;
        }

        /// <param name="readings">同一物理设备的所有来源读数。</param>
        /// <param name="nowUtc">判定时效的基准时刻，注入以保证可单测。</param>
        /// <param name="staleAfter">按来源返回时效窗口；返回 null 表示该来源不参与时效判定。</param>
        public static Result Arbitrate(IEnumerable<SourceReading> readings, DateTime nowUtc, Func<BatterySource, TimeSpan?> staleAfter)
        {
            if (readings == null) throw new ArgumentNullException("readings");

            var usable = new List<SourceReading>();
            SourceReading freshestExpired = null;
            SourceReading bestUnavailable = null;

            foreach (SourceReading reading in readings)
            {
                if (!reading.IsAvailable)
                {
                    if (bestUnavailable == null ||
                        Rank(reading.Source) < Rank(bestUnavailable.Source) ||
                        (Rank(reading.Source) == Rank(bestUnavailable.Source) && reading.LastAttemptUtc > bestUnavailable.LastAttemptUtc))
                    {
                        bestUnavailable = reading;
                    }
                    continue;
                }

                if (!reading.Percent.HasValue)
                {
                    // 来源在线但读不到电量：保留原因，供全部不可用时向界面解释「—」的成因。
                    if (bestUnavailable == null ||
                        Rank(reading.Source) < Rank(bestUnavailable.Source) ||
                        (Rank(reading.Source) == Rank(bestUnavailable.Source) && reading.LastAttemptUtc > bestUnavailable.LastAttemptUtc))
                    {
                        bestUnavailable = reading;
                    }
                    continue;
                }

                TimeSpan? window = staleAfter != null ? staleAfter(reading.Source) : null;
                bool expired = window.HasValue && reading.LastSuccessUtc.HasValue &&
                               nowUtc - reading.LastSuccessUtc.Value > window.Value;

                if (expired)
                {
                    if (freshestExpired == null || Rank(reading.Source) < Rank(freshestExpired.Source))
                    {
                        freshestExpired = reading;
                    }
                    continue;
                }

                usable.Add(reading);
            }

            var result = new Result();

            if (usable.Count == 0)
            {
                if (freshestExpired != null)
                {
                    // 有缓存但已过期：保留数值给界面做「上次 N 分钟前」，同时标 stale，不能当在线新值。
                    result.Percent = freshestExpired.Percent;
                    result.Charging = freshestExpired.Charging;
                    result.Source = freshestExpired.Source;
                    result.TimestampUtc = freshestExpired.LastSuccessUtc;
                    result.IsStale = true;
                    result.Reason = PercentUnavailableReason.None;
                    return result;
                }

                result.Reason = bestUnavailable != null ? bestUnavailable.Reason : PercentUnavailableReason.NotProbed;
                return result;
            }

            usable.Sort(delegate(SourceReading a, SourceReading b) { return Rank(a.Source).CompareTo(Rank(b.Source)); });
            SourceReading chosen = usable[0];

            int min = chosen.Percent.Value;
            int max = chosen.Percent.Value;
            for (int i = 1; i < usable.Count; i++)
            {
                int value = usable[i].Percent.Value;
                if (value < min) min = value;
                if (value > max) max = value;
            }

            result.Percent = chosen.Percent;
            result.Charging = chosen.Charging;
            result.Source = chosen.Source;
            result.TimestampUtc = chosen.LastSuccessUtc;
            result.HasConflict = (max - min) > ConflictThreshold;
            result.Reason = PercentUnavailableReason.None;
            return result;
        }
    }
}
