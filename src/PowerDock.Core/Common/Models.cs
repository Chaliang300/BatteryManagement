using System;

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
        public BatterySource AuthoritativeSource { get; set; }
        public int? Percent { get; set; }
        public bool? Charging { get; set; }
        public ConnectionState State { get; set; }
        public bool HasConflict { get; set; }
        public DateTime? LastSuccessfulReadingUtc { get; set; }

        public DeviceState(DeviceKey key, string displayName, DeviceCategory category)
        {
            if (key == null) throw new ArgumentNullException("key");
            Key = key;
            DisplayName = displayName ?? "Unknown Device";
            Category = category;
            State = ConnectionState.Unknown;
        }

        public string EffectiveName
        {
            get { return !string.IsNullOrEmpty(Alias) ? Alias : DisplayName; }
        }
    }
}
