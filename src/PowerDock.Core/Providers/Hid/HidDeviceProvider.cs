using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerDock.Core.Common;
using PowerDock.Core.Diagnostics;

namespace PowerDock.Core.Providers.Hid
{
    /// <summary>
    /// USB / 2.4G HID 采集。职责边界：
    /// 1. 发现按「容器 ID 归并」：同一复合设备的多个接口只产生一条设备记录；
    /// 2. 链路判定只看证据，无接收器关联证据时不声称 2.4G 外设在线；
    /// 3. 只探测标准 HID 电量，厂商私有协议不在此实现（属 P2-2）；
    /// 4. 刷新按 ReadingKey 串行，会话失效后丢弃迟到结果。
    /// </summary>
    public sealed class HidDeviceProvider : IDeviceProvider
    {
        public const string ProviderKey = "hid";

        private readonly object _sync = new object();
        private readonly HidDeviceEnumerator _enumerator;
        private readonly HidBatteryProbe _probe;
        private readonly TimeSpan _readTimeout;
        private readonly Dictionary<ReadingKey, SemaphoreSlim> _locks = new Dictionary<ReadingKey, SemaphoreSlim>();
        private readonly Dictionary<ReadingKey, HidEndpoint> _endpoints = new Dictionary<ReadingKey, HidEndpoint>();
        private readonly Dictionary<string, HidGroup> _groups = new Dictionary<string, HidGroup>(StringComparer.OrdinalIgnoreCase);
        private HidDeviceEnumerator.ChangeMonitor _monitor;
        private long _session;
        private bool _disposed;

        public HidDeviceProvider()
            : this(new HidDeviceEnumerator(), new HidBatteryProbe(), TimeSpan.FromSeconds(2))
        {
        }

        public HidDeviceProvider(HidDeviceEnumerator enumerator, HidBatteryProbe probe, TimeSpan readTimeout)
        {
            _enumerator = enumerator ?? new HidDeviceEnumerator();
            _probe = probe ?? new HidBatteryProbe();
            _readTimeout = readTimeout;
        }

        public string ProviderId
        {
            get { return ProviderKey; }
        }

        public event EventHandler<ProviderUpdate> Updated;

        /// <summary>开始监听接口到达/移除；退出时必须 Dispose。注册失败只记日志，不阻塞采集。</summary>
        public bool StartChangeMonitor()
        {
            lock (_sync)
            {
                if (_disposed) return false;
                if (_monitor != null) return true;
                _monitor = new HidDeviceEnumerator.ChangeMonitor(OnInterfacesChanged);
                bool ok = _monitor.Start();
                if (!ok)
                {
                    _monitor.Dispose();
                    _monitor = null;
                }
                return ok;
            }
        }

        private void OnInterfacesChanged()
        {
            // 回调来自配置管理器线程：只做一次轻量重发现，失败必须保留已有设备。
            try
            {
                DiscoverAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AppLogger.Error("HID rediscovery after interface change failed", ex);
            }
        }

        public Task DiscoverAsync(CancellationToken ct)
        {
            IList<HidInterfaceInfo> interfaces = _enumerator.EnumeratePresent();
            var byGroup = new Dictionary<string, HidGroup>(StringComparer.OrdinalIgnoreCase);

            foreach (HidInterfaceInfo info in interfaces)
            {
                if (ct.IsCancellationRequested) return Task.CompletedTask;
                if (string.IsNullOrEmpty(info.InterfacePath)) continue;

                string groupId = GroupKey(info);
                HidGroup group;
                if (!byGroup.TryGetValue(groupId, out group))
                {
                    group = new HidGroup(groupId, info);
                    byGroup.Add(groupId, group);
                }
                group.Add(info);
            }

            foreach (HidGroup group in byGroup.Values)
            {
                if (ct.IsCancellationRequested) return Task.CompletedTask;
                ProbeGroup(group);
            }

            lock (_sync)
            {
                if (_disposed) return Task.CompletedTask;
                _session++;

                var vanished = new List<HidGroup>();
                foreach (string key in _groups.Keys)
                {
                    if (!byGroup.ContainsKey(key)) vanished.Add(_groups[key]);
                }
                foreach (HidGroup group in vanished)
                {
                    _groups.Remove(group.Key);
                    foreach (HidEndpoint endpoint in group.Endpoints)
                    {
                        _endpoints.Remove(endpoint.Key);
                    }
                }

                foreach (HidGroup group in byGroup.Values)
                {
                    _groups[group.Key] = group;
                    foreach (HidEndpoint endpoint in group.Endpoints)
                    {
                        _endpoints[endpoint.Key] = endpoint;
                    }
                }

                // 先发布在场设备，再发布移除，避免界面「先空后满」闪烁。
                foreach (HidGroup group in byGroup.Values)
                {
                    PublishLocked(group);
                }
                foreach (HidGroup group in vanished)
                {
                    foreach (HidEndpoint endpoint in group.Endpoints)
                    {
                        Raise(new ProviderUpdate(UpdateKind.Removed, endpoint.Key,
                            group.ToProviderDevice(endpoint, PercentUnavailableReason.SourceDisabled, "interface removed"),
                            ConnectionState.Offline));
                    }
                }
            }

            return Task.CompletedTask;
        }

        private void ProbeGroup(HidGroup group)
        {
            foreach (HidInterfaceInfo info in group.Interfaces)
            {
                HidBatteryProbeResult result = _probe.Probe(info.InterfacePath, _readTimeout);
                if (result.Percent.HasValue || result.Capabilities.Count > 0)
                {
                    group.AttachProbe(info, result);
                    continue;
                }

                // 无标准电量字段：设备仍要留在列表里，端点记为不可读并带原因。
                group.AttachUnreadable(info, result);
            }
        }

        private void PublishLocked(HidGroup group)
        {
            HidEndpoint primary = group.PrimaryEndpoint;
            if (primary == null) return;

            ProviderDevice device = group.ToProviderDevice(primary, primary.Result.Reason, primary.Result.Detail);
            UpdateKind kind = primary.Result.Percent.HasValue ? UpdateKind.ReadingUpdated : UpdateKind.Discovered;
            Raise(new ProviderUpdate(kind, primary.Key, device,
                device.Link.IsConnected ? ConnectionState.Online : ConnectionState.Unknown));

            // 同一设备的其余读数端点也各自入服务，保证 D4 与「每端点一条 ReadingKey」成立。
            foreach (HidEndpoint endpoint in group.Endpoints)
            {
                if (ReferenceEquals(endpoint, primary)) continue;
                ProviderDevice other = group.ToProviderDevice(endpoint, endpoint.Result.Reason, endpoint.Result.Detail);
                Raise(new ProviderUpdate(UpdateKind.Discovered, endpoint.Key, other,
                    other.Link.IsConnected ? ConnectionState.Online : ConnectionState.Unknown));
            }
        }

        public async Task RefreshAsync(ReadingKey key, CancellationToken ct)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (!string.Equals(key.ProviderId, ProviderKey, StringComparison.OrdinalIgnoreCase)) return;

            HidEndpoint endpoint;
            HidGroup group;
            long session;

            lock (_sync)
            {
                if (_disposed) return;
                if (!_endpoints.TryGetValue(key, out endpoint)) return;
                group = endpoint.Group;
                session = _session;
            }

            SemaphoreSlim gate = GetGate(key);
            if (!await gate.WaitAsync(_readTimeout, ct).ConfigureAwait(false))
            {
                ProviderDevice busy = group.ToProviderDevice(endpoint, PercentUnavailableReason.Timeout, "refresh already running");
                Raise(new ProviderUpdate(UpdateKind.ReadingFailed, key, busy, ConnectionState.Online));
                return;
            }

            HidBatteryProbeResult result;
            try
            {
                result = await Task.Run(delegate
                {
                    return _probe.Probe(endpoint.Interface.InterfacePath, _readTimeout);
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Error("HID refresh failed for " + key, ex);
                result = HidBatteryProbeResult.Unavailable(PercentUnavailableReason.Timeout, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                gate.Release();
            }

            lock (_sync)
            {
                // 会话已变（拔出或重新枚举）时丢弃迟到结果，避免把离线设备重新标在线。
                if (_disposed || session != _session) return;
            }

            endpoint.Result = result;
            ProviderDevice device = group.ToProviderDevice(endpoint, result.Reason, result.Detail);
            Raise(new ProviderUpdate(
                result.Percent.HasValue ? UpdateKind.ReadingUpdated : UpdateKind.ReadingFailed,
                key, device, device.Link.IsConnected ? ConnectionState.Online : ConnectionState.Unknown));
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
                AppLogger.Error("HID provider subscriber threw for " + update.Key, ex);
            }
        }

        /// <summary>
        /// 归并键：容器 ID 优先；没有可用容器 ID 时按「父节点 + VID/PID」聚合；两者都没有时退回实例路径。
        /// 名称、型号、仅有 VID/PID 相同都不构成合并依据。
        /// </summary>
        internal static string GroupKey(HidInterfaceInfo info)
        {
            if (DeviceIdentity.IsUsableContainer(info.ContainerId))
            {
                return "container:" + info.ContainerId.Trim().Trim('{', '}').ToLowerInvariant();
            }

            bool hasVidPid = !string.IsNullOrEmpty(info.VendorId) && !string.IsNullOrEmpty(info.ProductId);
            if (!string.IsNullOrEmpty(info.ParentInstanceId) && hasVidPid)
            {
                // 同一接收器/复合设备的多个接口共享父节点与 VID/PID。
                return "parent:" + info.ParentInstanceId + "|" + info.VendorId + ":" + info.ProductId;
            }

            // 没有容器也没有 VID/PID（例如 ROOT\HIDCLASS 下的虚拟输入设备）：
            // 父节点相同的设备可能彼此无关，只能用实例路径区分，且必须标身份未确认。
            return "instance:" + info.InstanceId;
        }

        public void Dispose()
        {
            HidDeviceEnumerator.ChangeMonitor monitor;
            List<SemaphoreSlim> gates = new List<SemaphoreSlim>();
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                monitor = _monitor;
                _monitor = null;
                gates.AddRange(_locks.Values);
                _locks.Clear();
                _endpoints.Clear();
                _groups.Clear();
            }

            if (monitor != null) monitor.Dispose();
            foreach (SemaphoreSlim gate in gates)
            {
                try { gate.Dispose(); } catch { }
            }
        }
    }

    /// <summary>一个电量采集端点：接口 + 最近一次探测结果，各自独立成 ReadingKey。</summary>
    internal sealed class HidEndpoint
    {
        public HidInterfaceInfo Interface { get; private set; }
        public HidBatteryProbeResult Result { get; set; }
        public ReadingKey Key { get; private set; }
        public HidGroup Group { get; private set; }

        public HidEndpoint(HidGroup group, HidInterfaceInfo info, HidBatteryProbeResult result)
        {
            Group = group;
            Interface = info;
            Result = result;
        }

        public void SetKey(ReadingKey key)
        {
            Key = key;
        }
    }

    /// <summary>
    /// 按归并键聚起来的 HID 接口集合：一个复合设备只算一台设备，但每个接口是独立采集端点。
    /// </summary>
    internal sealed class HidGroup
    {
        private readonly List<HidInterfaceInfo> _interfaces = new List<HidInterfaceInfo>();
        private readonly List<HidEndpoint> _endpoints = new List<HidEndpoint>();
        private readonly HidInterfaceInfo _primary;

        public HidGroup(string key, HidInterfaceInfo primary)
        {
            Key = key;
            _primary = primary;
        }

        public string Key { get; private set; }

        public IList<HidInterfaceInfo> Interfaces
        {
            get { return _interfaces; }
        }

        public IList<HidEndpoint> Endpoints
        {
            get { return _endpoints; }
        }

        public HidEndpoint PrimaryEndpoint
        {
            get { return _endpoints.Count > 0 ? _endpoints[0] : null; }
        }

        public void Add(HidInterfaceInfo info)
        {
            _interfaces.Add(info);
        }

        /// <summary>
        /// 登记一次探测。一个接口可能暴露多个电量值能力（例如 Input 与 Feature 各一个），
        /// 每个能力各自成端点，互不覆盖。
        /// </summary>
        public void AttachProbe(HidInterfaceInfo info, HidBatteryProbeResult result)
        {
            IList<HidBatteryCapability> capabilities = HidBatterySelector.SelectBatteryCapabilities(result.Capabilities);
            if (capabilities.Count == 0)
            {
                AttachUnreadable(info, result);
                return;
            }

            for (int i = 0; i < capabilities.Count; i++)
            {
                HidBatteryProbeResult endpointResult = new HidBatteryProbeResult
                {
                    Percent = i == 0 ? result.Percent : null,
                    Reason = i == 0 && result.Percent.HasValue ? PercentUnavailableReason.None : result.Reason,
                    Detail = result.Detail,
                    Capabilities = result.Capabilities
                };

                var endpoint = new HidEndpoint(this, info, endpointResult);
                endpoint.SetKey(new ReadingKey(HidDeviceProvider.ProviderKey, EndpointId(info), capabilities[i].Signature()));
                _endpoints.Add(endpoint);
            }
        }

        public void AttachUnreadable(HidInterfaceInfo info, HidBatteryProbeResult result)
        {
            var endpoint = new HidEndpoint(this, info, result);
            endpoint.SetKey(new ReadingKey(HidDeviceProvider.ProviderKey, EndpointId(info), "battery"));
            _endpoints.Add(endpoint);
        }

        public DeviceIdentity BuildIdentity()
        {
            if (!string.IsNullOrEmpty(_primary.ContainerId))
            {
                return DeviceIdentity.FromContainer(_primary.ContainerId, _primary.InstanceId, _primary.VendorId, _primary.ProductId);
            }
            return DeviceIdentity.FromFallback(HidDeviceProvider.ProviderKey, Key, _primary.InstanceId, _primary.VendorId, _primary.ProductId);
        }

        public ProviderDevice ToProviderDevice(HidEndpoint endpoint, PercentUnavailableReason reason, string detail)
        {
            return new ProviderDevice(
                BuildIdentity(),
                BuildLink(endpoint),
                endpoint.Key,
                DisplayName(),
                DeviceCategory.Other,
                BatterySource.Hid,
                endpoint.Result != null ? endpoint.Result.Percent : null,
                null,
                reason,
                DateTime.UtcNow,
                BuildDetail(endpoint, detail));
        }

        private string DisplayName()
        {
            // 名称只用于展示，不参与归并。禁止回退到原始 PnP 实例串：它对用户没有意义，
            // 且会把蓝牙地址一类的硬件标识直接显示到界面上。
            if (!string.IsNullOrEmpty(_primary.FriendlyName)) return _primary.FriendlyName;
            if (!string.IsNullOrEmpty(_primary.DeviceDescription)) return _primary.DeviceDescription;

            if (!string.IsNullOrEmpty(_primary.VendorId) && !string.IsNullOrEmpty(_primary.ProductId))
            {
                return "HID 设备 (VID " + _primary.VendorId + " PID " + _primary.ProductId + ")";
            }
            return "HID 设备";
        }

        /// <summary>
        /// 链路判定只依证据：
        /// 1. 接口自身的 PnP 实例以 USB\ 开头 → USB 有线，依据写明 VID/PID 与容器；
        /// 2. 仅能确认位于 USB 总线/父节点 → Unknown 与「USB 设备，连接方式待确认」；
        /// 3. 2.4G 需要可验证接收器身份与外设关联，本 Provider 目前没有该证据，故不作 2.4G 断言。
        /// </summary>
        private LinkInfo BuildLink(HidEndpoint endpoint)
        {
            bool wired = endpoint.Interface.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);
            string container = string.IsNullOrEmpty(_primary.ContainerId) ? "unavailable" : _primary.ContainerId;
            if (wired)
            {
                string evidence = "instance=" + ShortHash(HidBatteryProbe.Redact(endpoint.Interface.InterfacePath)) +
                                  "; vid=" + endpoint.Interface.VendorId + "; pid=" + endpoint.Interface.ProductId +
                                  "; container=" + container;
                return new LinkInfo(LinkKind.UsbWired, evidence, false, true, false, DateTime.UtcNow);
            }

            string busEvidence = "parent=" + ShortHash(endpoint.Interface.ParentInstanceId) +
                                 "; vid=" + endpoint.Interface.VendorId + "; pid=" + endpoint.Interface.ProductId +
                                 "; container=" + container + "; no wired endpoint evidence";
            return new LinkInfo(LinkKind.Unknown, busEvidence, false, true, false, DateTime.UtcNow);
        }

        private string BuildDetail(HidEndpoint endpoint, string detail)
        {
            string text = string.IsNullOrEmpty(detail) ? string.Empty : detail;
            if (endpoint.Result != null && !string.IsNullOrEmpty(endpoint.Result.Detail))
            {
                text = text.Length == 0 ? endpoint.Result.Detail : text + "; " + endpoint.Result.Detail;
            }
            return "interfaces=" + _interfaces.Count + "; " + text;
        }

        private static string EndpointId(HidInterfaceInfo info)
        {
            if (!string.IsNullOrEmpty(info.InstanceId)) return info.InstanceId.ToLowerInvariant();
            return info.InterfacePath.ToLowerInvariant();
        }

        /// <summary>诊断与界面不需要完整实例路径：用短哈希保留「同一设备」的可比性，同时不外泄序列号。</summary>
        private static string ShortHash(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return "h" + (uint)value.GetHashCode();
        }
    }
}
