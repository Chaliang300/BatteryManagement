using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using PowerDock.Core.Common;
using PowerDock.Core.Diagnostics;
using PowerDock.Core.Providers;
using PowerDock.Core.Providers.Bluetooth;
using PowerDock.Core.Providers.Hid;
using PowerDock.Core.Providers.Wmi;
using PowerDock.Core.Running;
using PowerDock.Core.State;
using PowerDock.Providers.Bluetooth;

namespace PowerDock.App.Acquisition
{
    /// <summary>
    /// 应用侧采集宿主：把真实 Provider 装配进 <see cref="AcquisitionRuntime"/>，
    /// 并把同一份快照分发到设备页与托盘（AGENTS §6.2「三处必须来自同一快照」）。
    /// 约束：
    /// 1. 所有 UI 触碰都切到 Dispatcher，采集线程从不直接改控件；
    /// 2. 托盘与设备页使用同一次快照事件，不允许各自再算最低电量；
    /// 3. 初始化失败只降级（例如蓝牙不可用），不得让应用无法启动。
    /// </summary>
    public sealed class AcquisitionHost : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly DeviceStateService _service;
        private readonly AcquisitionRuntime _runtime;
        private readonly List<Action<IReadOnlyList<DeviceState>>> _sinks = new List<Action<IReadOnlyList<DeviceState>>>();
        private readonly object _sinkSync = new object();
        private bool _disposed;
        private IReadOnlyList<DeviceState> _lastSnapshot = new List<DeviceState>();

        public AcquisitionHost(Dispatcher dispatcher)
        {
            if (dispatcher == null) throw new ArgumentNullException("dispatcher");
            _dispatcher = dispatcher;
            _service = new DeviceStateService();
            _service.SnapshotUpdated += OnSnapshotUpdated;
            _runtime = new AcquisitionRuntime(_service);
        }

        public AcquisitionRuntime Runtime
        {
            get { return _runtime; }
        }

        public IReadOnlyList<DeviceState> LastSnapshot
        {
            get { lock (_sinkSync) { return _lastSnapshot; } }
        }

        /// <summary>订阅快照；订阅时立刻回放当前快照，避免首帧空列表。</summary>
        public void Subscribe(Action<IReadOnlyList<DeviceState>> sink)
        {
            if (sink == null) throw new ArgumentNullException("sink");
            IReadOnlyList<DeviceState> replay;
            lock (_sinkSync)
            {
                _sinks.Add(sink);
                replay = _lastSnapshot;
            }

            if (replay.Count > 0)
            {
                try { sink(replay); } catch (Exception ex) { AppLogger.Error("Snapshot replay failed", ex); }
            }
        }

        /// <summary>
        /// 装配真实 Provider 并启动采集。顺序按 AGENTS §11：本机电池 → HID → 蓝牙。
        /// 每个 Provider 的失败都单独降级，不影响其余来源。
        /// </summary>
        public async Task StartAsync(TimeSpan fallbackInterval)
        {
            try
            {
                _runtime.Add(new WmiBatteryProvider());
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to create WMI provider", ex);
            }

            try
            {
                var hid = new HidDeviceProvider();
                _runtime.Add(hid);
                if (!hid.StartChangeMonitor())
                {
                    AppLogger.Warn("HID hot-plug notifications unavailable; falling back to scheduled refresh only.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to create HID provider", ex);
            }

            try
            {
                var bluetooth = new BluetoothDeviceProvider(new WinRtBluetoothBackend());
                _runtime.Add(bluetooth);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to create Bluetooth provider", ex);
            }

            await _runtime.StartAsync(fallbackInterval, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>手动「刷新设备」入口：走与定时刷新相同的路径。</summary>
        public Task RefreshNowAsync()
        {
            return _runtime.RefreshNowAsync(CancellationToken.None);
        }

        private void OnSnapshotUpdated(object sender, DeviceStateSnapshotEventArgs e)
        {
            IReadOnlyList<DeviceState> snapshot = e != null ? e.Snapshot : new List<DeviceState>();
            Action<IReadOnlyList<DeviceState>>[] sinks;
            lock (_sinkSync)
            {
                if (_disposed) return;
                _lastSnapshot = snapshot;
                sinks = _sinks.ToArray();
            }

            _dispatcher.BeginInvoke(new Action(delegate
            {
                foreach (Action<IReadOnlyList<DeviceState>> sink in sinks)
                {
                    try
                    {
                        sink(snapshot);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Snapshot sink failed", ex);
                    }
                }
            }));
        }

        /// <summary>
        /// 托盘聚合（AGENTS P0-2 / D14）：取最低电量设备；无任何有效读数时不得显示固定 100%，
        /// 而是呈现未知（percent=null）。充电态取该设备的充电标志。
        /// </summary>
        public static TrayAggregate AggregateForTray(IReadOnlyList<DeviceState> snapshot)
        {
            return TrayAggregator.Aggregate(snapshot);
        }

        public void Dispose()
        {
            lock (_sinkSync)
            {
                if (_disposed) return;
                _disposed = true;
                _sinks.Clear();
            }

            try
            {
                _service.SnapshotUpdated -= OnSnapshotUpdated;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Failed to detach snapshot handler: " + ex.Message);
            }

            _runtime.Dispose();
        }
    }
}
