using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerDock.Core.Common;
using PowerDock.Core.Providers;
using PowerDock.Core.Providers.Bluetooth;
using Xunit;

namespace PowerDock.Core.Tests
{
    /// <summary>
    /// 蓝牙状态机单测。判据来自 AGENTS §2.1 C10：配对与连接分开记录；
    /// 以及验收矩阵「已配对但断开仍可识别」「已连接但无电池服务显示在线 + —」。
    /// </summary>
    public sealed class BluetoothProviderTests
    {
        private sealed class FakeBackend : IBluetoothBackend
        {
            public readonly List<string> ReadCalls = new List<string>();
            public BleBatteryReadResult NextResult = BleBatteryReadResult.Unavailable(PercentUnavailableReason.NotProbed, "not set");
            public int StartCalls;
            public bool Disposed;

            public event EventHandler<BluetoothDevicesChangedEventArgs> DevicesChanged;
            public event EventHandler<BluetoothAvailabilityChangedEventArgs> AvailabilityChanged;
            public event EventHandler<BluetoothBatteryNotificationEventArgs> BatteryNotified;

            public void Start() { StartCalls++; }

            public BleBatteryReadResult ReadBattery(string deviceId, TimeSpan timeout)
            {
                ReadCalls.Add(deviceId);
                return NextResult;
            }

            public void EmitDevices(params BluetoothDeviceSnapshot[] devices)
            {
                var handler = DevicesChanged;
                if (handler != null) handler(this, new BluetoothDevicesChangedEventArgs(new List<BluetoothDeviceSnapshot>(devices), true));
            }

            public void EmitAvailability(bool available, string reason)
            {
                var handler = AvailabilityChanged;
                if (handler != null) handler(this, new BluetoothAvailabilityChangedEventArgs(available, reason));
            }

            public void EmitNotify(string deviceId, int percent)
            {
                var handler = BatteryNotified;
                if (handler != null) handler(this, new BluetoothBatteryNotificationEventArgs(deviceId, percent));
            }

            public void Dispose() { Disposed = true; }
        }

        private static BluetoothDeviceSnapshot Device(string id, bool paired, bool connected, string name)
        {
            return new BluetoothDeviceSnapshot
            {
                DeviceId = id,
                Name = name,
                IsPaired = paired,
                IsConnected = connected,
                ContainerId = string.Empty
            };
        }

        private static List<ProviderUpdate> Capture(BluetoothDeviceProvider provider)
        {
            var updates = new List<ProviderUpdate>();
            provider.Updated += delegate(object sender, ProviderUpdate update) { updates.Add(update); };
            return updates;
        }

        [Fact]
        public void PairedButDisconnected_IsDiscoveredAsBluetoothOffline()
        {
            var backend = new FakeBackend();
            var provider = new BluetoothDeviceProvider(backend);
            List<ProviderUpdate> updates = Capture(provider);
            provider.Start();

            backend.EmitDevices(Device("ble-1", true, false, "键盘"));

            Assert.Single(updates);
            Assert.Equal(UpdateKind.Discovered, updates[0].Kind);
            Assert.Equal(LinkKind.Bluetooth, updates[0].Device.Link.Kind);
            Assert.True(updates[0].Device.Link.IsPaired);
            Assert.False(updates[0].Device.Link.IsConnected);
            Assert.Equal(ConnectionState.Offline, updates[0].Connection);
            Assert.Null(updates[0].Device.Percent);
            provider.Dispose();
        }

        [Fact]
        public async Task PairedButDisconnected_RefreshDoesNotCallGatt()
        {
            var backend = new FakeBackend();
            var provider = new BluetoothDeviceProvider(backend);
            List<ProviderUpdate> updates = Capture(provider);
            provider.Start();
            backend.EmitDevices(Device("ble-1", true, false, "键盘"));

            ReadingKey key = updates[0].Key;
            await provider.RefreshAsync(key, CancellationToken.None);

            Assert.Empty(backend.ReadCalls);
            ProviderUpdate last = updates[updates.Count - 1];
            Assert.Equal(UpdateKind.ReadingFailed, last.Kind);
            Assert.Null(last.Device.Percent);
            Assert.Contains("not connected", last.Device.Detail);
            provider.Dispose();
        }

        [Fact]
        public async Task ConnectedWithoutBatteryService_StaysOnlineWithNullPercent()
        {
            var backend = new FakeBackend();
            backend.NextResult = BleBatteryReadResult.Unavailable(PercentUnavailableReason.NoBatteryService, "service 0x180F not found");
            var provider = new BluetoothDeviceProvider(backend);
            List<ProviderUpdate> updates = Capture(provider);
            provider.Start();
            backend.EmitDevices(Device("ble-2", true, true, "鼠标"));

            ReadingKey key = updates[0].Key;
            await provider.RefreshAsync(key, CancellationToken.None);

            ProviderUpdate last = updates[updates.Count - 1];
            Assert.Equal(UpdateKind.ReadingFailed, last.Kind);
            Assert.Equal(ConnectionState.Online, last.Connection);
            Assert.Null(last.Device.Percent);
            Assert.Equal(PercentUnavailableReason.NoBatteryService, last.Device.PercentReason);
            provider.Dispose();
        }

        [Fact]
        public async Task ConnectedWithReadableBattery_ReportsPercent()
        {
            var backend = new FakeBackend();
            backend.NextResult = new BleBatteryReadResult
            {
                Percent = 0,
                Reason = PercentUnavailableReason.None,
                Detail = "read 0x2A19=0",
                SupportsNotify = true
            };
            var provider = new BluetoothDeviceProvider(backend);
            List<ProviderUpdate> updates = Capture(provider);
            provider.Start();
            backend.EmitDevices(Device("ble-3", true, true, "耳机"));

            await provider.RefreshAsync(updates[0].Key, CancellationToken.None);

            ProviderUpdate last = updates[updates.Count - 1];
            Assert.Equal(UpdateKind.ReadingUpdated, last.Kind);
            Assert.Equal(0, last.Device.Percent);
            Assert.Equal(PercentUnavailableReason.None, last.Device.PercentReason);
            provider.Dispose();
        }

        [Fact]
        public async Task NotifyUpdatesBatteryWithoutPolling()
        {
            var backend = new FakeBackend();
            var provider = new BluetoothDeviceProvider(backend);
            List<ProviderUpdate> updates = Capture(provider);
            provider.Start();
            backend.EmitDevices(Device("ble-4", true, true, "耳机"));

            // 等待初次发现的异步读取任务结算并清空计数，专注于验证 Notify 不触发多余轮询
            await Task.Delay(100);
            backend.ReadCalls.Clear();

            backend.EmitNotify("ble-4", 42);

            ProviderUpdate last = updates[updates.Count - 1];
            Assert.Equal(UpdateKind.ReadingUpdated, last.Kind);
            Assert.Equal(42, last.Device.Percent);
            Assert.Empty(backend.ReadCalls);
            provider.Dispose();
        }

        [Fact]
        public async Task AdapterUnavailable_DoesNotDropDevicesOrCachedReading()
        {
            var backend = new FakeBackend();
            backend.NextResult = new BleBatteryReadResult { Percent = 88, Reason = PercentUnavailableReason.None, Detail = "read" };
            var provider = new BluetoothDeviceProvider(backend);
            List<ProviderUpdate> updates = Capture(provider);
            provider.Start();
            backend.EmitDevices(Device("ble-5", true, true, "键盘"));
            await provider.RefreshAsync(updates[0].Key, CancellationToken.None);

            backend.EmitAvailability(false, "radio off");

            ProviderUpdate last = updates[updates.Count - 1];
            Assert.Equal(UpdateKind.SourceUnavailable, last.Kind);
            Assert.Equal(LinkKind.Bluetooth, last.Device.Link.Kind);
            Assert.Null(last.Device.Percent);
            Assert.Equal(PercentUnavailableReason.SourceDisabled, last.Device.PercentReason);
            Assert.Contains("adapter=unavailable", last.Device.Link.Evidence);
            provider.Dispose();
        }

        [Fact]
        public void RemovedDevice_EmitsRemoved()
        {
            var backend = new FakeBackend();
            var provider = new BluetoothDeviceProvider(backend);
            List<ProviderUpdate> updates = Capture(provider);
            provider.Start();
            backend.EmitDevices(Device("ble-6", true, true, "键盘"));

            backend.EmitDevices();

            ProviderUpdate last = updates[updates.Count - 1];
            Assert.Equal(UpdateKind.Removed, last.Kind);
            Assert.Equal(ConnectionState.Offline, last.Connection);
            provider.Dispose();
        }

        [Fact]
        public void DiscoverIsIdempotentForSameDevice()
        {
            var backend = new FakeBackend();
            var provider = new BluetoothDeviceProvider(backend);
            List<ProviderUpdate> updates = Capture(provider);
            provider.Start();

            backend.EmitDevices(Device("ble-7", true, true, "键盘"));
            backend.EmitDevices(Device("ble-7", true, true, "键盘"));

            Assert.Equal(updates[0].Key, updates[1].Key);
            provider.Dispose();
        }

        [Fact]
        public void ConnectionChangeIsReportedSeparately()
        {
            var backend = new FakeBackend();
            var provider = new BluetoothDeviceProvider(backend);
            List<ProviderUpdate> updates = Capture(provider);
            provider.Start();

            backend.EmitDevices(Device("ble-8", true, true, "键盘"));
            backend.EmitDevices(Device("ble-8", true, false, "键盘"));

            ProviderUpdate last = updates[updates.Count - 1];
            Assert.Equal(UpdateKind.ConnectionChanged, last.Kind);
            Assert.Equal(ConnectionState.Offline, last.Connection);
            Assert.True(last.Device.Link.IsPaired);
            provider.Dispose();
        }

        [Fact]
        public void DisposeDisposesBackend()
        {
            var backend = new FakeBackend();
            var provider = new BluetoothDeviceProvider(backend);
            provider.Dispose();

            Assert.True(backend.Disposed);
        }
    }
}
