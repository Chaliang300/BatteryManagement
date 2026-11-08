using System;
using System.Collections.Generic;

namespace PowerDock.Core.Common
{
    public enum BatterySource
    {
        Vendor = 1,
        BluetoothGatt = 2,
        Hid = 3,
        Wmi = 4
    }

    public enum DeviceCategory
    {
        Laptop,
        Mouse,
        Keyboard,
        Headset,
        Gamepad,
        Other
    }

    public enum ConnectionState
    {
        Online,
        Offline,
        Unknown
    }

    public sealed class DeviceKey : IEquatable<DeviceKey>
    {
        public string Value { get; private set; }

        public DeviceKey(string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            Value = value;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as DeviceKey);
        }

        public bool Equals(DeviceKey other)
        {
            if (other == null) return false;
            return string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
        }

        public override int GetHashCode()
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
        }

        public override string ToString()
        {
            return Value;
        }
    }

    public sealed class ReadingKey : IEquatable<ReadingKey>
    {
        public string ProviderId { get; private set; }
        public string EndpointId { get; private set; }
        public string BatteryId { get; private set; }

        public ReadingKey(string providerId, string endpointId, string batteryId)
        {
            ProviderId = providerId ?? string.Empty;
            EndpointId = endpointId ?? string.Empty;
            BatteryId = batteryId ?? string.Empty;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ReadingKey);
        }

        public bool Equals(ReadingKey other)
        {
            if (other == null) return false;
            return string.Equals(ProviderId, other.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(EndpointId, other.EndpointId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(BatteryId, other.BatteryId, StringComparison.OrdinalIgnoreCase);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(ProviderId);
                hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(EndpointId);
                hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(BatteryId);
                return hash;
            }
        }

        public override string ToString()
        {
            return string.Format("{0}:{1}:{2}", ProviderId, EndpointId, BatteryId);
        }
    }

    public sealed class BatteryReading
    {
        public ReadingKey Key { get; private set; }
        public int? Percent { get; private set; }
        public bool? Charging { get; private set; }
        public DateTime TsUtc { get; private set; }
        public BatterySource Source { get; private set; }

        public BatteryReading(ReadingKey key, int? percent, bool? charging, DateTime tsUtc, BatterySource source)
        {
            if (key == null) throw new ArgumentNullException("key");
            Key = key;
            Percent = percent;
            Charging = charging;
            TsUtc = tsUtc;
            Source = source;
        }
    }

    public sealed class DeviceState
    {
        public DeviceKey Key { get; private set; }
        public string DisplayName { get; set; }
        public string Alias { get; set; }
        public DeviceCategory Category { get; set; }
        public BatterySource? AuthoritativeSource { get; set; }
        public int? Percent { get; set; }
        public bool? Charging { get; set; }
        public ConnectionState State { get; set; }
        public bool HasConflict { get; set; }
        public DateTime? LastSuccessfulReadingUtc { get; set; }

        /// <summary>链路与在线证据；链路未知时不得由 UI 猜成有线或 2.4G。</summary>
        public LinkInfo Link { get; set; }

        /// <summary>为 false 表示身份是按端点派生的临时键，界面与诊断必须标「身份未确认」。</summary>
        public bool IdentityVerified { get; set; }

        /// <summary>当 <see cref="Percent"/> 为 null 时说明为什么读不到，UI 显示「—」并标注原因。</summary>
        public PercentUnavailableReason PercentReason { get; set; }

        /// <summary>各来源的最新读数，按来源键分别保留，用于 D4 裁决与冲突标注。</summary>
        public IReadOnlyList<SourceReading> Sources { get; set; }

        public DeviceState(DeviceKey key, string displayName, DeviceCategory category)
        {
            if (key == null) throw new ArgumentNullException("key");
            Key = key;
            DisplayName = displayName ?? "未知设备";
            Category = category;
            State = ConnectionState.Unknown;
            PercentReason = PercentUnavailableReason.NotProbed;
            Sources = new List<SourceReading>();
        }

        public string EffectiveName
        {
            get { return !string.IsNullOrEmpty(Alias) ? Alias : DisplayName; }
        }

        /// <summary>有读数但已超出该来源的时效窗口时为 true；界面标「N 分钟前」而不是把旧值当新值。</summary>
        public bool IsPercentStale { get; set; }
    }
}
