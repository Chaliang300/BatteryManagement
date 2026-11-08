using System;
using PowerDock.Core.Common;
using PowerDock.Core.State;
using Xunit;

namespace PowerDock.Core.Tests
{
    /// <summary>
    /// 契约层单测：身份、链路、三态与电量原因必须分离，任何「看起来一样」的字段都不得成为合并依据。
    /// </summary>
    public sealed class DeviceContractTests
    {
        [Fact]
        public void ContainerIdentity_NormalizesBracesAndCase()
        {
            var a = DeviceIdentity.FromContainer("{AABBCCDD-1122-3344-5566-77889900AABB}", "HID\\VID_1&PID_2\\1", "0001", "0002");
            var b = DeviceIdentity.FromContainer("aabbccdd-1122-3344-5566-77889900aabb", "HID\\VID_1&PID_2\\3", "0001", "0002");

            Assert.True(a.IsContainerVerified);
            Assert.Equal(a.Key, b.Key);
            Assert.Equal(a.Key.GetHashCode(), b.Key.GetHashCode());
        }

        [Fact]
        public void FallbackIdentity_IsNotVerified_AndDiffersPerEndpoint()
        {
            var a = DeviceIdentity.FromFallback("hid", "HID\\VID_1&PID_2\\1", "HID\\VID_1&PID_2\\1", "0001", "0002");
            var b = DeviceIdentity.FromFallback("hid", "HID\\VID_1&PID_2\\2", "HID\\VID_1&PID_2\\2", "0001", "0002");

            Assert.False(a.IsContainerVerified);
            Assert.NotEqual(a.Key, b.Key);
        }

        [Fact]
        public void SameModelSameVidPid_WithoutContainer_StaysTwoDevices()
        {
            var first = DeviceIdentity.FromFallback("hid", "endpoint-1", "USB\\VID_0001&PID_0002\\A", "0001", "0002");
            var second = DeviceIdentity.FromFallback("hid", "endpoint-2", "USB\\VID_0001&PID_0002\\B", "0001", "0002");

            Assert.NotEqual(first.Key, second.Key);
        }

        [Fact]
        public void UnknownLink_IsDescribedAsPending_NotWired()
        {
            var link = new LinkInfo(LinkKind.Unknown, "usb parent only", false, true, false, DateTime.UtcNow);

            Assert.Equal("USB 设备，连接方式待确认", link.Describe());
        }

        [Fact]
        public void ReceiverOnly_DoesNotClaimPeripheralOnline()
        {
            var link = new LinkInfo(LinkKind.Wireless24G, "receiver pid 0x1849", false, true, true, DateTime.UtcNow);

            Assert.Equal("接收器已连接，外设状态未知", link.Describe());
        }

        [Fact]
        public void PairedButDisconnected_StillDescribesBluetooth()
        {
            var link = new LinkInfo(LinkKind.Bluetooth, "paired=true; connection=Disconnected", true, false, false, DateTime.UtcNow);

            Assert.Equal("蓝牙（已配对，未连接）", link.Describe());
            Assert.True(link.IsPaired);
            Assert.False(link.IsConnected);
        }

        [Fact]
        public void ProviderDevice_NullPercentWithoutReason_IsRejected()
        {
            var identity = DeviceIdentity.FromFallback("hid", "e1", "p", "0001", "0002");
            var link = new LinkInfo(LinkKind.UsbWired, "ev", false, true, false, DateTime.UtcNow);
            var key = new ReadingKey("hid", "e1", "battery");

            Assert.Throws<ArgumentException>(delegate
            {
                new ProviderDevice(identity, link, key, "d", DeviceCategory.Mouse, BatterySource.Hid, null, null,
                    PercentUnavailableReason.None, DateTime.UtcNow, string.Empty);
            });
        }

        [Fact]
        public void ProviderDevice_ZeroPercent_IsAValidReading()
        {
            var identity = DeviceIdentity.FromFallback("hid", "e1", "p", "0001", "0002");
            var link = new LinkInfo(LinkKind.UsbWired, "ev", false, true, false, DateTime.UtcNow);
            var key = new ReadingKey("hid", "e1", "battery");

            var device = new ProviderDevice(identity, link, key, "d", DeviceCategory.Mouse, BatterySource.Hid, 0, false,
                PercentUnavailableReason.None, DateTime.UtcNow, string.Empty);

            Assert.Equal(0, device.Percent);
            Assert.Equal(PercentUnavailableReason.None, device.PercentReason);
        }

        [Fact]
        public void ProviderDevice_PercentOutOfRange_IsRejected()
        {
            var identity = DeviceIdentity.FromFallback("hid", "e1", "p", "0001", "0002");
            var link = new LinkInfo(LinkKind.UsbWired, "ev", false, true, false, DateTime.UtcNow);
            var key = new ReadingKey("hid", "e1", "battery");

            Assert.Throws<ArgumentOutOfRangeException>(delegate
            {
                new ProviderDevice(identity, link, key, "d", DeviceCategory.Mouse, BatterySource.Hid, 101, false,
                    PercentUnavailableReason.None, DateTime.UtcNow, string.Empty);
            });
        }

        [Fact]
        public void DiagnosticKeys_AreStableEnglish()
        {
            Assert.Equal("wireless_2_4g", ContractText.Key(LinkKind.Wireless24G));
            Assert.Equal("no_battery_service", ContractText.Key(PercentUnavailableReason.NoBatteryService));
            Assert.Equal("unknown", ContractText.Key(ConnectionState.Unknown));
        }
    }
}
