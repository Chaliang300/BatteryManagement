using System;

namespace PowerDock.Core.Common
{
    /// <summary>
    /// 单个来源读数。按 ReadingKey 独立保留，D4 裁决只在这些快照上做，不覆盖原始值。
    /// 百分比为 null 时必须带 reason，且不允许把 null 换算成 0。
    /// </summary>
    public sealed class SourceReading
    {
        public ReadingKey Key { get; private set; }
        public BatterySource Source { get; private set; }
        public int? Percent { get; private set; }
        public bool? Charging { get; private set; }
        public PercentUnavailableReason Reason { get; private set; }
        public DateTime LastAttemptUtc { get; private set; }
        public DateTime? LastSuccessUtc { get; private set; }
        public int ConsecutiveFailures { get; private set; }
        public bool IsAvailable { get; private set; }
        public string Detail { get; private set; }

        public SourceReading(ReadingKey key, BatterySource source)
        {
            if (key == null) throw new ArgumentNullException("key");
            Key = key;
            Source = source;
            Reason = PercentUnavailableReason.NotProbed;
            IsAvailable = true;
        }

        internal void ApplyReading(int? percent, bool? charging, PercentUnavailableReason reason, DateTime tsUtc, string detail)
        {
            LastAttemptUtc = tsUtc;
            Detail = detail ?? string.Empty;
            if (percent.HasValue)
            {
                Percent = percent;
                Charging = charging;
                Reason = PercentUnavailableReason.None;
                LastSuccessUtc = tsUtc;
                ConsecutiveFailures = 0;
                IsAvailable = true;
                return;
            }

            if (reason == PercentUnavailableReason.None)
            {
                throw new ArgumentException("有效读数缺失时必须给出不可用原因。", "reason");
            }
            Reason = reason;
        }

        internal void ApplyFailure(PercentUnavailableReason reason, DateTime tsUtc, bool sourceAvailable, string detail)
        {
            LastAttemptUtc = tsUtc;
            Detail = detail ?? string.Empty;
            Reason = reason;
            IsAvailable = sourceAvailable;
            if (sourceAvailable)
            {
                ConsecutiveFailures++;
            }
        }

        internal void MarkSourceUnavailable(PercentUnavailableReason reason, DateTime tsUtc, string detail)
        {
            LastAttemptUtc = tsUtc;
            Reason = reason;
            Detail = detail ?? string.Empty;
            IsAvailable = false;
        }
    }
}
