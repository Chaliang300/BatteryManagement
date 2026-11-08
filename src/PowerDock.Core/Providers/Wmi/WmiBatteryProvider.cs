using System;
using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using PowerDock.Core.Common;
using PowerDock.Core.Diagnostics;
using PowerDock.Core.Providers;

namespace PowerDock.Core.Providers.Wmi
{
    /// <summary>
    /// 本机电池采集。只读 Win32_Battery：
    /// 1. 本机电池没有配对/接收器概念，链路固定 Internal；
    /// 2. 充电状态与电量来自同一次查询，但分别记录（D5 要求充电判定不得靠电量趋势）；
    /// 3. WMI 抛错或没有电池时给明确原因，绝不写 0%。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WmiBatteryProvider : IDeviceProvider
    {
        public const string ProviderKey = "wmi";

        private const string InstanceName = "internal-battery";

        private readonly object _sync = new object();
        private int? _lastPercent;
        private bool? _lastCharging;
        private DateTime? _lastSuccessUtc;
        private bool _disposed;

        public string ProviderId
        {
            get { return ProviderKey; }
        }

        public event EventHandler<ProviderUpdate> Updated;

        public static ReadingKey Key()
        {
            return new ReadingKey(ProviderKey, InstanceName, "battery-0");
        }

        public Task DiscoverAsync(CancellationToken ct)
        {
            RefreshAsync(Key(), ct).GetAwaiter().GetResult();
            return Task.CompletedTask;
        }

        public Task RefreshAsync(ReadingKey key, CancellationToken ct)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (!string.Equals(key.ProviderId, ProviderKey, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;

            var query = new BatteryQuery();
            try
            {
                query = ReadBattery();
            }
            catch (Exception ex)
            {
                AppLogger.Error("WMI battery query failed", ex);
                query = BatteryQuery.Failed(PercentUnavailableReason.Timeout, ex.GetType().Name + ": " + ex.Message);
            }

            lock (_sync)
            {
                if (_disposed) return Task.CompletedTask;

                if (query.Percent.HasValue)
                {
                    _lastPercent = query.Percent;
                    _lastCharging = query.Charging;
                    _lastSuccessUtc = DateTime.UtcNow;
                }
            }

            Raise(BuildUpdate(query));
            return Task.CompletedTask;
        }

        private static BatteryQuery ReadBattery()
        {
            using (var searcher = new ManagementObjectSearcher("select Name, EstimatedChargeRemaining, BatteryStatus from Win32_Battery"))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementObject instance in results)
                {
                    using (instance)
                    {
                        object remaining = instance["EstimatedChargeRemaining"];
                        if (remaining == null)
                        {
                            return BatteryQuery.Failed(PercentUnavailableReason.NotProbed, "EstimatedChargeRemaining missing");
                        }

                        int percent = Convert.ToInt32(remaining, CultureInfo.InvariantCulture);
                        if (percent < 0 || percent > 100)
                        {
                            return BatteryQuery.Failed(PercentUnavailableReason.NotProbed, "EstimatedChargeRemaining out of range: " + percent);
                        }

                        object status = instance["BatteryStatus"];
                        int statusCode = status == null ? -1 : Convert.ToInt32(status, CultureInfo.InvariantCulture);
                        bool? charging = statusCode < 0 ? (bool?)null : (statusCode == 6 || statusCode == 7 || statusCode == 8 || statusCode == 9);

                        object name = instance["Name"];
                        string detail = "Win32_Battery name=" + (name == null ? "?" : name.ToString()) +
                                        "; status=" + statusCode.ToString(CultureInfo.InvariantCulture);
                        return new BatteryQuery { Percent = percent, Charging = charging, Detail = detail };
                    }
                }
            }

            return BatteryQuery.Failed(PercentUnavailableReason.NoStandardBatteryUsage, "no Win32_Battery instance (desktop or battery not exposed)");
        }

        private ProviderUpdate BuildUpdate(BatteryQuery query)
        {
            var identity = DeviceIdentity.FromContainer(
                "internal:" + Environment.MachineName, "ACPI\\PNP0C0A", string.Empty, string.Empty);
            var link = LinkInfo.Internal("Win32_Battery", true);

            int? percent;
            bool? charging;
            PercentUnavailableReason reason;
            DateTime ts;

            lock (_sync)
            {
                // 来源不可用时保留上次成功读数并交给 Service 标过期，不在这里丢弃。
                percent = query.Percent.HasValue ? query.Percent : _lastPercent;
                charging = query.Percent.HasValue ? query.Charging : _lastCharging;
                reason = query.Percent.HasValue
                    ? PercentUnavailableReason.None
                    : (query.Reason == PercentUnavailableReason.None ? PercentUnavailableReason.NotProbed : query.Reason);
                ts = DateTime.UtcNow;
            }

            var device = new ProviderDevice(
                identity,
                link,
                Key(),
                "本机电池",
                DeviceCategory.Laptop,
                BatterySource.Wmi,
                percent,
                charging,
                reason,
                ts,
                query.Detail);

            return new ProviderUpdate(query.Percent.HasValue ? UpdateKind.ReadingUpdated : UpdateKind.ReadingFailed,
                Key(), device, ConnectionState.Online);
        }

        private void Raise(ProviderUpdate update)
        {
            EventHandler<ProviderUpdate> handler = Updated;
            if (handler == null) return;
            try
            {
                handler(this, update);
            }
            catch (Exception ex)
            {
                AppLogger.Error("WMI provider subscriber threw", ex);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
            }
        }

        private sealed class BatteryQuery
        {
            public int? Percent;
            public bool? Charging;
            public PercentUnavailableReason Reason;
            public string Detail;

            public static BatteryQuery Failed(PercentUnavailableReason reason, string detail)
            {
                return new BatteryQuery { Reason = reason, Detail = detail ?? string.Empty };
            }
        }
    }
}
