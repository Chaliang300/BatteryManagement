using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PowerDock.Core.Common;
using PowerDock.Core.Providers;
using PowerDock.Core.State;
using Xunit;

namespace PowerDock.Core.Tests
{
    /// <summary>
    /// D4 裁决与三态单测。判据只依赖注入的时间与读数，不依赖真实设备。
    /// </summary>
    public sealed class SourceArbiterTests
    {
        private static SourceReading Make(BatterySource source, int? percent, DateTime? successUtc, bool available = true)
        {
            var reading = new SourceReading(new ReadingKey("p", source.ToString(), "battery"), source);
            if (available)
            {
                if (percent.HasValue)
                {
                    reading.ApplyReading(percent, null, PercentUnavailableReason.None, successUtc ?? DateTime.UtcNow, string.Empty);
                }
                else
                {
                    reading.ApplyReading(null, null, PercentUnavailableReason.NoBatteryService, successUtc ?? DateTime.UtcNow, string.Empty);
                }
            }
            else
            {
                reading.MarkSourceUnavailable(PercentUnavailableReason.SourceDisabled, successUtc ?? DateTime.UtcNow, string.Empty);
            }
            return reading;
        }

        private static Func<BatterySource, TimeSpan?> Window(int seconds)
        {
            return delegate { return (TimeSpan?)TimeSpan.FromSeconds(seconds); };
        }

        [Fact]
        public void PrefersVendorOverBluetoothGatt()
        {
            DateTime now = new DateTime(2026, 11, 8, 4, 0, 0, DateTimeKind.Utc);
            var result = SourceArbiter.Arbitrate(new[]
            {
                Make(BatterySource.Hid, 55, now),
                Make(BatterySource.BluetoothGatt, 56, now),
                Make(BatterySource.Vendor, 57, now)
            }, now, Window(120));

            Assert.Equal(57, result.Percent);
            Assert.Equal(BatterySource.Vendor, result.Source);
            Assert.False(result.HasConflict);
        }

        [Fact]
        public void ConflictFlaggedWhenSpreadExceedsFivePoints()
        {
            DateTime now = new DateTime(2026, 11, 8, 4, 0, 0, DateTimeKind.Utc);
            var result = SourceArbiter.Arbitrate(new[]
            {
                Make(BatterySource.Hid, 50, now),
                Make(BatterySource.BluetoothGatt, 70, now)
            }, now, Window(120));

            Assert.Equal(70, result.Percent);
            Assert.Equal(BatterySource.BluetoothGatt, result.Source);
            Assert.True(result.HasConflict);
        }

        [Fact]
        public void ExactlyFivePointSpreadIsNotConflict()
        {
            DateTime now = new DateTime(2026, 11, 8, 4, 0, 0, DateTimeKind.Utc);
            var result = SourceArbiter.Arbitrate(new[]
            {
                Make(BatterySource.Hid, 50, now),
                Make(BatterySource.BluetoothGatt, 55, now)
            }, now, Window(120));

            Assert.False(result.HasConflict);
        }

        [Fact]
        public void ExpiredReadingIsKeptButMarkedStale()
        {
            DateTime now = new DateTime(2026, 11, 8, 4, 10, 0, DateTimeKind.Utc);
            var result = SourceArbiter.Arbitrate(new[]
            {
                Make(BatterySource.BluetoothGatt, 42, now.AddMinutes(-8))
            }, now, Window(120));

            Assert.Equal(42, result.Percent);
            Assert.True(result.IsStale);
        }

        [Fact]
        public void NoUsableReading_ReportsMostAuthoritativeReason()
        {
            DateTime now = new DateTime(2026, 11, 8, 4, 0, 0, DateTimeKind.Utc);
            var result = SourceArbiter.Arbitrate(new[]
            {
                Make(BatterySource.Wmi, null, now),
                Make(BatterySource.BluetoothGatt, null, now)
            }, now, Window(120));

            Assert.Null(result.Percent);
            Assert.Equal(PercentUnavailableReason.NoBatteryService, result.Reason);
        }

        [Fact]
        public void UnavailableSourceIsSkippedButNotZeroed()
        {
            DateTime now = new DateTime(2026, 11, 8, 4, 0, 0, DateTimeKind.Utc);
            var result = SourceArbiter.Arbitrate(new[]
            {
                Make(BatterySource.BluetoothGatt, 33, now, false),
                Make(BatterySource.Hid, 31, now)
            }, now, Window(120));

            Assert.Equal(31, result.Percent);
            Assert.Equal(BatterySource.Hid, result.Source);
        }

        [Fact]
        public void ZeroPercentIsNotTreatedAsMissing()
        {
            DateTime now = new DateTime(2026, 11, 8, 4, 0, 0, DateTimeKind.Utc);
            var result = SourceArbiter.Arbitrate(new[] { Make(BatterySource.Hid, 0, now) }, now, Window(120));

            Assert.Equal(0, result.Percent);
            Assert.False(result.IsStale);
        }

        [Fact]
        public void RankOrderMatchesD4()
        {
            Assert.True(SourceArbiter.Rank(BatterySource.Vendor) < SourceArbiter.Rank(BatterySource.BluetoothGatt));
            Assert.True(SourceArbiter.Rank(BatterySource.BluetoothGatt) < SourceArbiter.Rank(BatterySource.Hid));
            Assert.True(SourceArbiter.Rank(BatterySource.Hid) < SourceArbiter.Rank(BatterySource.Wmi));
        }
    }

    /// <summary>
    /// 快照服务测试：三态、身份透传、200ms 合并与手动刷新路径。
    /// </summary>
    public sealed class DeviceStateServiceTests
    {
        private sealed class FakeProvider : IDeviceProvider
        {
            public string ProviderId { get; set; }
            public int RefreshCalls;
            public event EventHandler<ProviderUpdate> Updated;

            public FakeProvider() { ProviderId = "hid"; }

            public Task DiscoverAsync(CancellationToken ct) { return Task.CompletedTask; }

            public Task RefreshAsync(ReadingKey key, CancellationToken ct)
            {
                Interlocked.Increment(ref RefreshCalls);
                return Task.CompletedTask;
            }

            public void Emit(ProviderUpdate update)
            {
                var handler = Updated;
                if (handler != null) handler(this, update);
            }

            public void Dispose() { }
        }

        private static ProviderDevice Device(
            string endpoint,
            int? percent,
            LinkKind kind,
            bool connected,
            bool paired,
            bool receiverOnly,
            bool containerVerified,
            PercentUnavailableReason reason,
            DateTime ts)
        {
            DeviceIdentity identity = containerVerified
                ? DeviceIdentity.FromContainer("{" + endpoint + "}", endpoint, "0001", "0002")
                : DeviceIdentity.FromFallback("hid", endpoint, endpoint, "0001", "0002");
            var link = new LinkInfo(kind, "evidence:" + endpoint, paired, connected, receiverOnly, ts);
            var key = new ReadingKey("hid", endpoint, "battery");
            return new ProviderDevice(identity, link, key, "设备-" + endpoint, DeviceCategory.Mouse, BatterySource.Hid,
                percent, null, reason, ts, string.Empty);
        }

        [Fact]
        public void ReceiverOnly_IsUnknownOnlineState()
        {
            DateTime now = new DateTime(2026, 11, 8, 4, 0, 0, DateTimeKind.Utc);
            var service = new DeviceStateService(delegate { return now; }, null, null);
            var provider = new FakeProvider();
            service.Attach(provider);

            var device = Device("rx", null, LinkKind.Wireless24G, true, false, true, true, PercentUnavailableReason.NoStandardBatteryUsage, now);
            provider.Emit(new ProviderUpdate(UpdateKind.Discovered, device.Key, device, ConnectionState.Unknown));

            var snapshot = service.PublishNow();
            Assert.Single(snapshot);
            Assert.Equal(ConnectionState.Unknown, snapshot[0].State);
            Assert.Null(snapshot[0].Percent);
            Assert.Equal(PercentUnavailableReason.NoStandardBatteryUsage, snapshot[0].PercentReason);
            service.Dispose();
        }

        [Fact]
        public void PairedButDisconnectedBluetooth_IsOfflineWithNoPercent()
        {
            DateTime now = new DateTime(2026, 11, 8, 4, 0, 0, DateTimeKind.Utc);
            var service = new DeviceStateService(delegate { return now; }, null, null);
            var provider = new FakeProvider();
            provider.ProviderId = "bluetooth";
            service.Attach(provider);

            var device = Device("ble1", null, LinkKind.Bluetooth, false, true, false, true, PercentUnavailableReason.NotProbed, now);
            var key = new ReadingKey("bluetooth", "ble1", "battery");
            var bleDevice = new ProviderDevice(device.Identity, device.Link, key, device.DisplayName, DeviceCategory.Keyboard,
                BatterySource.BluetoothGatt, null, null, PercentUnavailableReason.NoBatteryService, now, "no 0x180F");
            provider.Emit(new ProviderUpdate(UpdateKind.Discovered, key, bleDevice, ConnectionState.Offline));

            var snapshot = service.PublishNow();
            Assert.Single(snapshot);
            Assert.Equal(ConnectionState.Offline, snapshot[0].State);
            Assert.Null(snapshot[0].Percent);
            Assert.True(snapshot[0].Link.IsPaired);
            Assert.False(snapshot[0].Link.IsConnected);
            service.Dispose();
        }

        [Fact]
        public void ThreeConsecutiveFailures_TurnDeviceOfflineWithoutLineEvidence()
        {
            DateTime now = new DateTime(2026, 11, 8, 4, 0, 0, DateTimeKind.Utc);
            DateTime clock = now;
            var service = new DeviceStateService(delegate { return clock; }, null, null);
            var provider = new FakeProvider();
            service.Attach(provider);

            var device = Device("hid1", 64, LinkKind.Unknown, false, false, false, true, PercentUnavailableReason.None, now);
            provider.Emit(new ProviderUpdate(UpdateKind.ReadingUpdated, device.Key, device, ConnectionState.Online));
            Assert.Equal(ConnectionState.Online, service.PublishNow()[0].State);

            clock = now.AddMinutes(5);
            var failing = Device("hid1", null, LinkKind.Unknown, false, false, false, true, PercentUnavailableReason.Timeout, clock);
            for (int i = 0; i < 3; i++)
            {
                provider.Emit(new ProviderUpdate(UpdateKind.ReadingFailed, failing.Key, failing, ConnectionState.Unknown));
            }

            var snapshot = service.PublishNow();
            Assert.Equal(ConnectionState.Offline, snapshot[0].State);
            Assert.Equal(64, snapshot[0].Percent);
            Assert.True(snapshot[0].IsPercentStale);
            service.Dispose();
        }

        [Fact]
        public void PresentButUnreadableWiredDevice_StaysOnlineWithUnknownPercent()
        {
            DateTime now = new DateTime(2026, 11, 8, 4, 0, 0, DateTimeKind.Utc);
            var service = new DeviceStateService(delegate { return now; }, null, null);
            var provider = new FakeProvider();
            service.Attach(provider);

            var failing = Device("wired", null, LinkKind.UsbWired, true, false, false, true, PercentUnavailableReason.NoStandardBatteryUsage, now);
            for (int i = 0; i < 3; i++)
            {
                provider.Emit(new ProviderUpdate(UpdateKind.ReadingFailed, failing.Key, failing, ConnectionState.Online));
            }

            var snapshot = service.PublishNow();
            Assert.Equal(ConnectionState.Online, snapshot[0].State);
            Assert.Null(snapshot[0].Percent);
            Assert.Equal(PercentUnavailableReason.NoStandardBatteryUsage, snapshot[0].PercentReason);
            service.Dispose();
        }

        [Fact]
        public void UnverifiedIdentity_IsPropagatedNotMerged()
        {
            DateTime now = new DateTime(2026, 11, 8, 4, 0, 0, DateTimeKind.Utc);
            var service = new DeviceStateService(delegate { return now; }, null, null);
            var provider = new FakeProvider();
            service.Attach(provider);

            var a = Device("e1", 50, LinkKind.UsbWired, true, false, false, false, PercentUnavailableReason.None, now);
            var b = Device("e2", 60, LinkKind.UsbWired, true, false, false, false, PercentUnavailableReason.None, now);
            provider.Emit(new ProviderUpdate(UpdateKind.ReadingUpdated, a.Key, a, ConnectionState.Online));
            provider.Emit(new ProviderUpdate(UpdateKind.ReadingUpdated, b.Key, b, ConnectionState.Online));

            var snapshot = service.PublishNow();
            Assert.Equal(2, snapshot.Count);
            Assert.False(snapshot[0].IdentityVerified);
            Assert.False(snapshot[1].IdentityVerified);
            service.Dispose();
        }

        [Fact]
        public void SnapshotSortsByPercentAscendingWithUnknownLast()
        {
            DateTime now = new DateTime(2026, 11, 8, 4, 0, 0, DateTimeKind.Utc);
            var service = new DeviceStateService(delegate { return now; }, null, null);
            var provider = new FakeProvider();
            service.Attach(provider);

            var low = Device("low", 12, LinkKind.UsbWired, true, false, false, true, PercentUnavailableReason.None, now);
            var high = Device("high", 88, LinkKind.UsbWired, true, false, false, true, PercentUnavailableReason.None, now);
            var unknown = Device("unk", null, LinkKind.Unknown, true, false, false, true, PercentUnavailableReason.NoStandardBatteryUsage, now);

            provider.Emit(new ProviderUpdate(UpdateKind.ReadingUpdated, high.Key, high, ConnectionState.Online));
            provider.Emit(new ProviderUpdate(UpdateKind.ReadingUpdated, unknown.Key, unknown, ConnectionState.Unknown));
            provider.Emit(new ProviderUpdate(UpdateKind.ReadingUpdated, low.Key, low, ConnectionState.Online));

            var snapshot = service.PublishNow();
            Assert.Equal(3, snapshot.Count);
            Assert.Equal(12, snapshot[0].Percent);
            Assert.Equal(88, snapshot[1].Percent);
            Assert.Null(snapshot[2].Percent);
            service.Dispose();
        }

        [Fact]
        public void RemovedDevice_DisappearsFromSnapshot()
        {
            DateTime now = new DateTime(2026, 11, 8, 4, 0, 0, DateTimeKind.Utc);
            var service = new DeviceStateService(delegate { return now; }, null, null);
            var provider = new FakeProvider();
            service.Attach(provider);

            var device = Device("gone", 30, LinkKind.UsbWired, true, false, false, true, PercentUnavailableReason.None, now);
            provider.Emit(new ProviderUpdate(UpdateKind.ReadingUpdated, device.Key, device, ConnectionState.Online));
            Assert.Single(service.PublishNow());

            provider.Emit(new ProviderUpdate(UpdateKind.Removed, device.Key, device, ConnectionState.Offline));
            Assert.Empty(service.PublishNow());
            service.Dispose();
        }

        [Fact]
        public async Task RefreshAllAsync_CallsProviderForKnownReadings()
        {
            DateTime now = new DateTime(2026, 11, 8, 4, 0, 0, DateTimeKind.Utc);
            var service = new DeviceStateService(delegate { return now; }, null, null);
            var provider = new FakeProvider();
            service.Attach(provider);

            var device = Device("r1", 44, LinkKind.UsbWired, true, false, false, true, PercentUnavailableReason.None, now);
            provider.Emit(new ProviderUpdate(UpdateKind.ReadingUpdated, device.Key, device, ConnectionState.Online));
            service.PublishNow();

            await service.RefreshAllAsync(CancellationToken.None);

            Assert.Equal(1, provider.RefreshCalls);
            service.Dispose();
        }

        [Fact]
        public void HighestSourceWinsAndLowerFailureDoesNotOverride()
        {
            DateTime now = new DateTime(2026, 11, 8, 4, 0, 0, DateTimeKind.Utc);
            var service = new DeviceStateService(delegate { return now; }, null, null);
            var bluetooth = new FakeProvider { ProviderId = "bluetooth" };
            service.Attach(bluetooth);

            var identity = DeviceIdentity.FromContainer("{aaa}", "p", "0001", "0002");
            var link = new LinkInfo(LinkKind.Bluetooth, "paired+connected", true, true, false, now);
            var btKey = new ReadingKey("bluetooth", "aaa", "battery");
            var btDevice = new ProviderDevice(identity, link, btKey, "键盘", DeviceCategory.Keyboard, BatterySource.BluetoothGatt,
                77, null, PercentUnavailableReason.None, now, string.Empty);
            bluetooth.Emit(new ProviderUpdate(UpdateKind.ReadingUpdated, btKey, btDevice, ConnectionState.Online));

            var hidLink = new LinkInfo(LinkKind.UsbWired, "wired", false, true, false, now);
            var hidKey = new ReadingKey("hid", "aaa", "battery");
            var hidDevice = new ProviderDevice(identity, hidLink, hidKey, "键盘", DeviceCategory.Keyboard, BatterySource.Hid,
                null, null, PercentUnavailableReason.NoStandardBatteryUsage, now, string.Empty);
            bluetooth.Emit(new ProviderUpdate(UpdateKind.ReadingUpdated, hidKey, hidDevice, ConnectionState.Online));

            var snapshot = service.PublishNow();
            Assert.Single(snapshot);
            Assert.Equal(77, snapshot[0].Percent);
            Assert.Equal(BatterySource.BluetoothGatt, snapshot[0].AuthoritativeSource);
            service.Dispose();
        }
    }
}
