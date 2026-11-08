using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerDock.Core.Common;
using PowerDock.Core.Diagnostics;
using PowerDock.Core.Providers;

namespace PowerDock.Core.Providers.Bluetooth
{
    /// <summary>
    /// 蓝牙采集状态机。只依赖 <see cref="IBluetoothBackend"/>，因此可以在无蓝牙的机器上单测。
    /// 硬约束（AGENTS §2.1 C10 / §5.3.1）：
    /// 1. 已配对与当前连接分开记录：已配对但断开仍要作为蓝牙设备出现在列表里，状态如实为离线/未知；
    /// 2. 电量读不到时保留设备与连接信息，只把百分比置 null 并给出原因；
    /// 3. 同一端点刷新不重叠；断开或来源失效后丢弃旧会话的迟到回调；
    /// 4. 适配器不可用只标「蓝牙已关闭」，不清空已缓存读数。
    /// </summary>
    public sealed class BluetoothDeviceProvider : IDeviceProvider
    {
        public const string ProviderKey = "bluetooth";

        private readonly object _sync = new object();
        private readonly IBluetoothBackend _backend;
        private readonly TimeSpan _readTimeout;
        private readonly Dictionary<ReadingKey, SemaphoreSlim> _locks = new Dictionary<ReadingKey, SemaphoreSlim>();
        private readonly Dictionary<string, BluetoothDeviceSnapshot> _devices = new Dictionary<string, BluetoothDeviceSnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ReadingKey> _keys = new Dictionary<string, ReadingKey>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BleBatteryReadResult> _results = new Dictionary<string, BleBatteryReadResult>(StringComparer.OrdinalIgnoreCase);
        private long _session;
        private bool _adapterAvailable = true;
        private string _adapterDetail = string.Empty;
        private bool _disposed;

        public BluetoothDeviceProvider(IBluetoothBackend backend)
            : this(backend, TimeSpan.FromSeconds(10))
        {
        }

        public BluetoothDeviceProvider(IBluetoothBackend backend, TimeSpan readTimeout)
        {
            if (backend == null) throw new ArgumentNullException("backend");
            _backend = backend;
            _readTimeout = readTimeout;
            _backend.DevicesChanged += OnDevicesChanged;
            _backend.AvailabilityChanged += OnAvailabilityChanged;
            _backend.BatteryNotified += OnBatteryNotified;
        }

        public string ProviderId
        {
            get { return ProviderKey; }
        }

        public event EventHandler<ProviderUpdate> Updated;

        public void Start()
        {
            try
            {
                _backend.Start();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Bluetooth backend failed to start", ex);
                HandleAvailability(false, "backend start failed: " + ex.Message);
            }
        }

        public Task DiscoverAsync(CancellationToken ct)
        {
            // 枚举由后端事件驱动；这里只要求后端启动，避免与 Watcher 生命周期打架。
            Start();
            return Task.CompletedTask;
        }

        private void OnDevicesChanged(object sender, BluetoothDevicesChangedEventArgs args)
        {
            if (args == null || args.Devices == null) return;

            lock (_sync)
            {
                if (_disposed) return;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (BluetoothDeviceSnapshot snapshot in args.Devices)
                {
                    if (snapshot == null || string.IsNullOrEmpty(snapshot.DeviceId)) continue;
                    seen.Add(snapshot.DeviceId);

                    BluetoothDeviceSnapshot previous;
                    bool isNew = !_devices.TryGetValue(snapshot.DeviceId, out previous);
                    _devices[snapshot.DeviceId] = snapshot;

                    ReadingKey key = GetOrCreateKeyLocked(snapshot.DeviceId);
                    if (!_results.ContainsKey(snapshot.DeviceId))
                    {
                        _results[snapshot.DeviceId] = BleBatteryReadResult.Unavailable(
                            _adapterAvailable ? PercentUnavailableReason.NotProbed : PercentUnavailableReason.SourceDisabled,
                            _adapterAvailable ? "not probed yet" : _adapterDetail);
                    }

                    UpdateKind kind = isNew
                        ? UpdateKind.Discovered
                        : (previous.IsConnected != snapshot.IsConnected ? UpdateKind.ConnectionChanged : UpdateKind.Discovered);

                    Raise(BuildUpdate(kind, snapshot, key, _results[snapshot.DeviceId]));
                }

                var vanished = new List<string>();
                foreach (string deviceId in _devices.Keys)
                {
                    if (!seen.Contains(deviceId)) vanished.Add(deviceId);
                }
                foreach (string deviceId in vanished)
                {
                    BluetoothDeviceSnapshot snapshot = _devices[deviceId];
                    ReadingKey key = GetOrCreateKeyLocked(deviceId);
                    _devices.Remove(deviceId);
                    _results.Remove(deviceId);
                    ProviderDevice device = BuildDevice(snapshot, key,
                        BleBatteryReadResult.Unavailable(PercentUnavailableReason.SourceDisabled, "removed"));
                    Raise(new ProviderUpdate(UpdateKind.Removed, key, device, ConnectionState.Offline));
                }
            }
        }

        private void OnAvailabilityChanged(object sender, BluetoothAvailabilityChangedEventArgs args)
        {
            HandleAvailability(args.IsAvailable, args.Reason);
        }

        private void HandleAvailability(bool available, string reason)
        {
            lock (_sync)
            {
                if (_disposed) return;
                if (_adapterAvailable == available && string.Equals(_adapterDetail, reason ?? string.Empty, StringComparison.Ordinal))
                {
                    return;
                }

                _adapterAvailable = available;
                _adapterDetail = reason ?? string.Empty;
                _session++;

                foreach (BluetoothDeviceSnapshot snapshot in _devices.Values)
                {
                    ReadingKey key = GetOrCreateKeyLocked(snapshot.DeviceId);
                    BleBatteryReadResult result = available
                        ? BleBatteryReadResult.Unavailable(PercentUnavailableReason.NotProbed, "adapter restored, repolling")
                        : BleBatteryReadResult.Unavailable(PercentUnavailableReason.SourceDisabled, _adapterDetail);

                    _results[snapshot.DeviceId] = result;
                    Raise(BuildUpdate(
                        available ? UpdateKind.Discovered : UpdateKind.SourceUnavailable,
                        snapshot, key, result));
                }
            }
        }

        private void OnBatteryNotified(object sender, BluetoothBatteryNotificationEventArgs args)
        {
            if (args == null || string.IsNullOrEmpty(args.DeviceId)) return;

            lock (_sync)
            {
                if (_disposed) return;
                BluetoothDeviceSnapshot snapshot;
                if (!_devices.TryGetValue(args.DeviceId, out snapshot)) return;

                BleBatteryReadResult previous;
                bool supportsNotify = _results.TryGetValue(args.DeviceId, out previous) && previous.SupportsNotify;
                var result = new BleBatteryReadResult
                {
                    Percent = args.Percent,
                    Reason = PercentUnavailableReason.None,
                    Detail = "notify 0x2A19",
                    SupportsNotify = supportsNotify
                };
                _results[args.DeviceId] = result;

                Raise(BuildUpdate(UpdateKind.ReadingUpdated, snapshot, GetOrCreateKeyLocked(args.DeviceId), result));
            }
        }

        public async Task RefreshAsync(ReadingKey key, CancellationToken ct)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (!string.Equals(key.ProviderId, ProviderKey, StringComparison.OrdinalIgnoreCase)) return;

            string deviceId = null;
            BluetoothDeviceSnapshot snapshot = null;
            long session;

            lock (_sync)
            {
                if (_disposed) return;
                foreach (var pair in _keys)
                {
                    if (pair.Value.Equals(key))
                    {
                        deviceId = pair.Key;
                        break;
                    }
                }
                if (deviceId == null) return;
                if (!_devices.TryGetValue(deviceId, out snapshot)) return;
                session = _session;
            }

            if (!snapshot.IsConnected)
            {
                // 已配对但未连接：不发起 GATT，只如实报告离线原因，电量保持未知。
                lock (_sync)
                {
                    if (_disposed) return;
                    var offline = BleBatteryReadResult.Unavailable(PercentUnavailableReason.NotProbed, "paired but not connected");
                    _results[deviceId] = offline;
                    Raise(BuildUpdate(UpdateKind.ReadingFailed, snapshot, key, offline));
                }
                return;
            }

            SemaphoreSlim gate = GetGate(key);
            if (!await gate.WaitAsync(_readTimeout, ct).ConfigureAwait(false))
            {
                Raise(new ProviderUpdate(UpdateKind.ReadingFailed, key,
                    BuildDevice(snapshot, key, BleBatteryReadResult.Unavailable(PercentUnavailableReason.Timeout, "refresh already running")),
                    ConnectionState.Online));
                return;
            }

            BleBatteryReadResult result;
            try
            {
                result = await Task.Run(delegate { return _backend.ReadBattery(deviceId, _readTimeout); }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Bluetooth refresh failed for device " + Redact(deviceId), ex);
                result = BleBatteryReadResult.Unavailable(PercentUnavailableReason.Timeout, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                gate.Release();
            }

            lock (_sync)
            {
                // 会话已变（断开/重新发现/适配器变化）时丢弃迟到结果，避免把离线设备标回在线。
                if (_disposed || session != _session) return;
                BluetoothDeviceSnapshot current;
                if (!_devices.TryGetValue(deviceId, out current) || !current.IsConnected) return;
                _results[deviceId] = result;
                Raise(BuildUpdate(
                    result.Percent.HasValue ? UpdateKind.ReadingUpdated : UpdateKind.ReadingFailed,
                    current, key, result));
            }
        }

        private ReadingKey GetOrCreateKeyLocked(string deviceId)
        {
            ReadingKey key;
            if (!_keys.TryGetValue(deviceId, out key))
            {
                key = new ReadingKey(ProviderKey, deviceId, "battery");
                _keys[deviceId] = key;
            }
            return key;
        }

        private ProviderUpdate BuildUpdate(UpdateKind kind, BluetoothDeviceSnapshot snapshot, ReadingKey key, BleBatteryReadResult result)
        {
            return new ProviderUpdate(kind, key, BuildDevice(snapshot, key, result), ToConnectionState(snapshot));
        }

        private ProviderDevice BuildDevice(BluetoothDeviceSnapshot snapshot, ReadingKey key, BleBatteryReadResult result)
        {
            DeviceIdentity identity = BuildIdentity(snapshot);
            LinkInfo link = BuildLink(snapshot);
            BleBatteryReadResult actual = result ?? BleBatteryReadResult.Unavailable(PercentUnavailableReason.NotProbed, string.Empty);

            return new ProviderDevice(
                identity,
                link,
                key,
                string.IsNullOrEmpty(snapshot.Name) ? "蓝牙设备" : snapshot.Name,
                DeviceCategory.Other,
                BatterySource.BluetoothGatt,
                actual.Percent,
                null,
                actual.Percent.HasValue ? PercentUnavailableReason.None : actual.Reason,
                DateTime.UtcNow,
                actual.Detail);
        }

        private static DeviceIdentity BuildIdentity(BluetoothDeviceSnapshot snapshot)
        {
            if (!string.IsNullOrEmpty(snapshot.ContainerId))
            {
                return DeviceIdentity.FromContainer(snapshot.ContainerId, snapshot.DeviceId, string.Empty, string.Empty);
            }
            return DeviceIdentity.FromFallback(ProviderKey, snapshot.DeviceId, snapshot.DeviceId, string.Empty, string.Empty);
        }

        private LinkInfo BuildLink(BluetoothDeviceSnapshot snapshot)
        {
            string evidence = string.Format(
                "paired={0}; connected={1}; container={2}; adapter={3}",
                snapshot.IsPaired ? "true" : "false",
                snapshot.IsConnected ? "Connected" : "Disconnected",
                string.IsNullOrEmpty(snapshot.ContainerId) ? "unavailable" : snapshot.ContainerId,
                _adapterAvailable ? "available" : "unavailable(" + _adapterDetail + ")");

            return new LinkInfo(LinkKind.Bluetooth, evidence, snapshot.IsPaired, snapshot.IsConnected, false, DateTime.UtcNow);
        }

        private static ConnectionState ToConnectionState(BluetoothDeviceSnapshot snapshot)
        {
            if (snapshot.IsConnected) return ConnectionState.Online;
            // 已配对但断开是「明确断开」，可以判离线；从未连过的未知状态仍归 Unknown。
            return snapshot.IsPaired ? ConnectionState.Offline : ConnectionState.Unknown;
        }

        private SemaphoreSlim GetGate(ReadingKey key)
        {
            lock (_sync)
            {
                SemaphoreSlim gate;
                if (!_locks.TryGetValue(key, out gate))
                {
                    gate = new SemaphoreSlim(1, 1);
                    _locks.Add(key, gate);
                }
                return gate;
            }
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
                AppLogger.Error("Bluetooth provider subscriber threw for " + update.Key, ex);
            }
        }

        /// <summary>蓝牙地址与设备 ID 都会含硬件标识，诊断输出一律截断。</summary>
        internal static string Redact(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            int separator = value.LastIndexOf('-');
            string tail = separator >= 0 && value.Length - separator - 1 <= 4 ? value.Substring(separator + 1) : string.Empty;
            return "***" + tail;
        }

        public void Dispose()
        {
            List<SemaphoreSlim> gates = new List<SemaphoreSlim>();
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                gates.AddRange(_locks.Values);
                _locks.Clear();
                _devices.Clear();
                _keys.Clear();
                _results.Clear();
            }

            try
            {
                _backend.DevicesChanged -= OnDevicesChanged;
                _backend.AvailabilityChanged -= OnAvailabilityChanged;
                _backend.BatteryNotified -= OnBatteryNotified;
                _backend.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Bluetooth backend dispose failed", ex);
            }

            foreach (SemaphoreSlim gate in gates)
            {
                try { gate.Dispose(); } catch { }
            }
        }
    }
}
