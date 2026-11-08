using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerDock.Core.Common;
using PowerDock.Core.Diagnostics;
using PowerDock.Core.Providers;

namespace PowerDock.Core.State
{
    /// <summary>
    /// 汇总所有 Provider 的事件，产出唯一一份 <see cref="Snapshot"/> 供设备页、托盘、挂件消费。
    /// 约束：
    /// 1. 高频更新按 <see cref="MergeIntervalMs"/> 合并后才广播，数值、状态和时效仍各自更新；
    /// 2. 三态、冲突、过期都在这里裁决，UI 不得再自行推断；
    /// 3. 身份未确认（IsContainerVerified=false）必须原样透传到快照，不允许悄悄合并；
    /// 4. 任何 Provider 异常都记日志并保留已有记录，不得因一次发现失败清空列表。
    /// </summary>
    public sealed class DeviceStateService : IDisposable
    {
        public const int MergeIntervalMs = 200;

        private readonly object _sync = new object();
        private readonly DeviceRegistry _registry = new DeviceRegistry();
        private readonly List<IDeviceProvider> _providers = new List<IDeviceProvider>();
        private readonly Func<DateTime> _utcNow;
        private readonly Func<BatterySource, TimeSpan?> _staleAfter;
        private Timer _publishTimer;
        private bool _dirty;
        private bool _disposed;
        private IReadOnlyList<DeviceState> _snapshot = new List<DeviceState>();

        /// <summary>合并窗口到点后触发；在定时器线程上调用，订阅方需自行切到 UI 线程。</summary>
        public event EventHandler<DeviceStateSnapshotEventArgs> SnapshotUpdated;

        public DeviceStateService()
            : this(null, null, null)
        {
        }

        public DeviceStateService(Func<DateTime> utcNow, Func<BatterySource, TimeSpan?> staleAfter, IEnumerable<IDeviceProvider> providers)
        {
            _utcNow = utcNow ?? (delegate { return DateTime.UtcNow; });
            _staleAfter = staleAfter ?? DefaultStaleWindow;
            if (providers != null)
            {
                foreach (IDeviceProvider provider in providers)
                {
                    Attach(provider);
                }
            }
        }

        public IReadOnlyList<DeviceState> Snapshot
        {
            get
            {
                lock (_sync)
                {
                    return _snapshot;
                }
            }
        }

        /// <summary>
        /// 时效窗口按来源单独绑定（AGENTS §5.6）。默认值取该来源两倍兜底周期，留一次重试余量。
        /// </summary>
        public static TimeSpan? DefaultStaleWindow(BatterySource source)
        {
            switch (source)
            {
                case BatterySource.Wmi:
                    return TimeSpan.FromSeconds(30);
                default:
                    return TimeSpan.FromSeconds(120);
            }
        }

        public void Attach(IDeviceProvider provider)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            lock (_sync)
            {
                _providers.Add(provider);
            }
            provider.Updated += OnProviderUpdate;
        }

        public void Detach(IDeviceProvider provider)
        {
            if (provider == null) return;
            provider.Updated -= OnProviderUpdate;
            lock (_sync)
            {
                _providers.Remove(provider);
            }
        }

        /// <summary>真实刷新入口：对所有已知读端点调用 provider.RefreshAsync，失败按来源记账并保留旧值。</summary>
        public async Task RefreshAllAsync(CancellationToken ct)
        {
            List<KeyValuePair<IDeviceProvider, ReadingKey>> work = new List<KeyValuePair<IDeviceProvider, ReadingKey>>();

            lock (_sync)
            {
                IDeviceProvider[] providers = _providers.ToArray();
                foreach (DeviceRegistry.Record record in _registry.All())
                {
                    if (record.Removed) continue;
                    foreach (ReadingKey key in record.Readings.Keys)
                    {
                        IDeviceProvider owner = FindOwner(providers, key);
                        if (owner != null)
                        {
                            work.Add(new KeyValuePair<IDeviceProvider, ReadingKey>(owner, key));
                        }
                    }
                }
            }

            foreach (var item in work)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    await item.Key.RefreshAsync(item.Value, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("RefreshAllAsync failed for " + item.Value, ex);
                }
            }
        }

        private static IDeviceProvider FindOwner(IDeviceProvider[] providers, ReadingKey key)
        {
            for (int i = 0; i < providers.Length; i++)
            {
                if (string.Equals(providers[i].ProviderId, key.ProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    return providers[i];
                }
            }
            return null;
        }

        private void OnProviderUpdate(object sender, ProviderUpdate update)
        {
            if (update == null) return;

            lock (_sync)
            {
                if (_disposed) return;
                Apply(update);
                MarkDirtyLocked();
            }
        }

        private void Apply(ProviderUpdate update)
        {
            if (update.Kind == UpdateKind.Removed)
            {
                if (update.Device != null)
                {
                    DeviceRegistry.Record removed = _registry.GetOrAdd(update.Device.Identity);
                    removed.Removed = true;
                }
                return;
            }

            ProviderDevice device = update.Device;
            if (device == null)
            {
                AppLogger.Warn("Provider update without device payload key=" + update.Key);
                return;
            }

            DeviceRegistry.Record record = _registry.GetOrAdd(device.Identity);
            record.Identity = device.Identity;
            // 同一物理设备可能被多条链路枚举（BLE + HOGP HID 接口）：链路显式裁决，不按到达顺序覆盖。
            record.Link = LinkArbiter.Choose(record.Link, device.Link);
            // 名称同样不许被后到的匿名回退名覆盖掉蓝牙通告的真实名称。
            if (string.IsNullOrEmpty(record.DisplayName) || IsMoreSpecificName(device.DisplayName, record.DisplayName))
            {
                record.DisplayName = device.DisplayName;
            }
            if (record.Category == DeviceCategory.Other && device.Category != DeviceCategory.Other)
            {
                record.Category = device.Category;
            }

            SourceReading reading;
            if (!record.Readings.TryGetValue(device.Key, out reading))
            {
                reading = new SourceReading(device.Key, device.Source);
                record.Readings.Add(device.Key, reading);
            }

            switch (update.Kind)
            {
                case UpdateKind.Discovered:
                    if (device.Percent == null && device.PercentReason == PercentUnavailableReason.NotProbed)
                    {
                        break;
                    }
                    ApplyReading(reading, device);
                    break;
                case UpdateKind.ConnectionChanged:
                    break;
                case UpdateKind.ReadingUpdated:
                    ApplyReading(reading, device);
                    break;
                case UpdateKind.ReadingFailed:
                    reading.ApplyFailure(
                        device.PercentReason == PercentUnavailableReason.None ? PercentUnavailableReason.Timeout : device.PercentReason,
                        EffectiveTimestamp(device),
                        true,
                        device.Detail);
                    break;
                case UpdateKind.SourceUnavailable:
                    reading.MarkSourceUnavailable(
                        device.PercentReason == PercentUnavailableReason.None ? PercentUnavailableReason.SourceDisabled : device.PercentReason,
                        EffectiveTimestamp(device),
                        device.Detail);
                    break;
            }
        }

        /// <summary>
        /// 名称回退判据：匿名名（如「HID 设备」「HID 设备 (VID ...)」）不得覆盖已有真实名称，
        /// 真实名称可以覆盖匿名名或空值。避免后到的 HID 端点把蓝牙通告的名称刷掉。
        /// </summary>
        private static bool IsMoreSpecificName(string incoming, string existing)
        {
            if (string.IsNullOrEmpty(incoming)) return false;
            if (string.IsNullOrEmpty(existing)) return true;

            bool incomingAnonymous = incoming.StartsWith("HID 设备", StringComparison.Ordinal);
            bool existingAnonymous = existing.StartsWith("HID 设备", StringComparison.Ordinal);
            if (incomingAnonymous == existingAnonymous) return false;
            return existingAnonymous;
        }

        private DateTime EffectiveTimestamp(ProviderDevice device)
        {
            return device.TsUtc == default(DateTime) ? _utcNow() : device.TsUtc;
        }

        private void ApplyReading(SourceReading reading, ProviderDevice device)
        {
            reading.ApplyReading(device.Percent, device.Charging, device.PercentReason, EffectiveTimestamp(device), device.Detail);
        }

        private void MarkDirtyLocked()
        {
            _dirty = true;
            if (_publishTimer == null)
            {
                _publishTimer = new Timer(OnPublishTimer, null, MergeIntervalMs, Timeout.Infinite);
            }
        }

        private void OnPublishTimer(object state)
        {
            List<DeviceState> built;
            lock (_sync)
            {
                if (_disposed) return;
                _publishTimer = null;
                if (!_dirty) return;
                _dirty = false;
                built = BuildSnapshotLocked();
                _snapshot = built;
            }

            EventHandler<DeviceStateSnapshotEventArgs> handler = SnapshotUpdated;
            if (handler != null)
            {
                try
                {
                    handler(this, new DeviceStateSnapshotEventArgs(built));
                }
                catch (Exception ex)
                {
                    AppLogger.Error("SnapshotUpdated subscriber threw", ex);
                }
            }
        }

        /// <summary>测试与诊断用：立即结算挂起更新并返回最新快照，不等待合并窗口。</summary>
        public IReadOnlyList<DeviceState> PublishNow()
        {
            lock (_sync)
            {
                _dirty = false;
                _snapshot = BuildSnapshotLocked();
                return _snapshot;
            }
        }

        private List<DeviceState> BuildSnapshotLocked()
        {
            DateTime now = _utcNow();
            var list = new List<DeviceState>();

            foreach (DeviceRegistry.Record record in _registry.All())
            {
                if (record.Removed) continue;

                var state = new DeviceState(record.Key, record.DisplayName, record.Category);
                state.Link = record.Link;
                state.IdentityVerified = record.Identity != null && record.Identity.IsContainerVerified;

                var readings = new List<SourceReading>(record.Readings.Values);
                SourceArbiter.Result result = SourceArbiter.Arbitrate(readings, now, _staleAfter);

                state.Percent = result.Percent;
                state.Charging = result.Charging;
                state.AuthoritativeSource = result.Source;
                state.HasConflict = result.HasConflict;
                state.IsPercentStale = result.IsStale || (result.TimestampUtc.HasValue && IsBeyondWindow(result.Source, now, result.TimestampUtc.Value));
                state.PercentReason = result.Reason;
                state.LastSuccessfulReadingUtc = LatestSuccessUtc(readings);
                state.State = DetermineState(record, result, readings);
                state.Sources = readings;

                list.Add(state);
            }

            // 电量升序即「谁该充电」；没有有效电量的设备排在末尾，避免未知冒充最低电量。
            list.Sort(delegate(DeviceState a, DeviceState b)
            {
                if (!a.Percent.HasValue && !b.Percent.HasValue)
                {
                    return string.Compare(a.EffectiveName, b.EffectiveName, StringComparison.OrdinalIgnoreCase);
                }
                if (!a.Percent.HasValue) return 1;
                if (!b.Percent.HasValue) return -1;
                int byPercent = a.Percent.Value.CompareTo(b.Percent.Value);
                if (byPercent != 0) return byPercent;
                return string.Compare(a.EffectiveName, b.EffectiveName, StringComparison.OrdinalIgnoreCase);
            });

            return list;
        }

        private bool IsBeyondWindow(BatterySource? source, DateTime now, DateTime ts)
        {
            if (!source.HasValue) return false;
            TimeSpan? window = _staleAfter(source.Value);
            return window.HasValue && now - ts > window.Value;
        }

        private static DateTime? LatestSuccessUtc(List<SourceReading> readings)
        {
            DateTime? latest = null;
            foreach (SourceReading reading in readings)
            {
                if (reading.LastSuccessUtc.HasValue && (!latest.HasValue || reading.LastSuccessUtc.Value > latest.Value))
                {
                    latest = reading.LastSuccessUtc;
                }
            }
            return latest;
        }

        /// <summary>
        /// 三态裁决（AGENTS §5.3.1 / §6.7）：
        /// 在线 = 有连接证据或新鲜成功读数；离线 = 明确断开，或可用来源连续 3 次失败且无在线证据；
        /// 未知 = 其余情况（含只识别到接收器、来源不可用、只有过期缓存）。
        /// 未知与离线必须分开，混用会让用户排查一台没坏的设备。
        /// </summary>
        private ConnectionState DetermineState(DeviceRegistry.Record record, SourceArbiter.Result result, List<SourceReading> readings)
        {
            LinkInfo link = record.Link;
            bool hasFreshReading = result.Percent.HasValue && !result.IsStale;

            if (link != null)
            {
                // 接收器在场只证明接收器在；外设是否开机未知，不能因为收到接收器就宣称在线。
                if (link.Kind == LinkKind.Wireless24G && link.IsReceiverOnly)
                {
                    return ConnectionState.Unknown;
                }

                if (link.IsConnected)
                {
                    return ConnectionState.Online;
                }
            }

            if (hasFreshReading && !AllSourcesFailing(readings))
            {
                return ConnectionState.Online;
            }

            if (link != null && IsExplicitDisconnect(link))
            {
                return ConnectionState.Offline;
            }

            if (AllSourcesFailing(readings) && readings.Count > 0)
            {
                return ConnectionState.Offline;
            }

            return ConnectionState.Unknown;
        }

        private static bool IsExplicitDisconnect(LinkInfo link)
        {
            switch (link.Kind)
            {
                case LinkKind.Bluetooth:
                    return link.IsPaired && !link.IsConnected;
                case LinkKind.UsbWired:
                    return !link.IsConnected;
                default:
                    return false;
            }
        }

        private static bool AllSourcesFailing(List<SourceReading> readings)
        {
            bool anyAvailable = false;
            foreach (SourceReading reading in readings)
            {
                if (!reading.IsAvailable) continue;
                anyAvailable = true;
                if (reading.ConsecutiveFailures < 3) return false;
            }
            return anyAvailable;
        }

        public void Dispose()
        {
            IDeviceProvider[] providers;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                if (_publishTimer != null)
                {
                    _publishTimer.Dispose();
                    _publishTimer = null;
                }
                providers = _providers.ToArray();
                _providers.Clear();
            }

            foreach (IDeviceProvider provider in providers)
            {
                try
                {
                    provider.Updated -= OnProviderUpdate;
                    provider.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Failed to dispose provider " + provider.ProviderId, ex);
                }
            }
        }
    }

    public sealed class DeviceStateSnapshotEventArgs : EventArgs
    {
        public IReadOnlyList<DeviceState> Snapshot { get; private set; }

        public DeviceStateSnapshotEventArgs(IReadOnlyList<DeviceState> snapshot)
        {
            Snapshot = snapshot;
        }
    }
}
