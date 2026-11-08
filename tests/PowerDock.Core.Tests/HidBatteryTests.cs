using System;
using System.Collections.Generic;
using PowerDock.Core.Common;
using PowerDock.Core.Providers.Hid;
using Xunit;

namespace PowerDock.Core.Tests
{
    /// <summary>
    /// 标准 HID 电量解析单测。判据来自 AGENTS §2.1 C8：必须在 Input/Feature 的**值能力**里查
    /// UsagePage=0x06 / Usage=0x20，顶层集合未命中不构成「无电量」。
    /// </summary>
    public sealed class HidBatterySelectorTests
    {
        private static HidBatteryCapability Cap(
            bool feature, byte reportId, ushort usagePage, ushort usage,
            uint logicalMin, uint logicalMax, ushort bitSize = 8, ushort linkCollection = 0)
        {
            return new HidBatteryCapability(feature, reportId, usagePage, usage, linkCollection, 0, 0, bitSize,
                logicalMin, logicalMax, true, false);
        }

        [Fact]
        public void SelectsBatteryUsageRegardlessOfTopLevelCollection()
        {
            // 顶层集合未命中 0x06:0x20，但值能力里有 0x06:0x20 —— 必须能选中（C8）。
            var capabilities = new List<HidBatteryCapability>
            {
                Cap(false, 0x03, 0x0001, 0x0030, 0, 1),
                Cap(true, 0x05, 0x0006, 0x0020, 0, 100)
            };

            IList<HidBatteryCapability> selected = HidBatterySelector.SelectBatteryCapabilities(capabilities);

            Assert.Single(selected);
            Assert.True(selected[0].IsFeatureReport);
            Assert.Equal(0x05, selected[0].ReportId);
        }

        [Fact]
        public void FeatureReportIsPreferredOverInput()
        {
            var capabilities = new List<HidBatteryCapability>
            {
                Cap(false, 0x01, 0x0006, 0x0020, 0, 100),
                Cap(true, 0x02, 0x0006, 0x0020, 0, 100)
            };

            IList<HidBatteryCapability> selected = HidBatterySelector.SelectBatteryCapabilities(capabilities);

            Assert.Equal(2, selected.Count);
            Assert.True(selected[0].IsFeatureReport);
        }

        [Fact]
        public void NonBatteryUsagesAreIgnored()
        {
            var capabilities = new List<HidBatteryCapability>
            {
                Cap(true, 0x01, 0x0006, 0x0021, 0, 100),
                Cap(true, 0x01, 0x000C, 0x0020, 0, 100)
            };

            Assert.Empty(HidBatterySelector.SelectBatteryCapabilities(capabilities));
        }

        [Fact]
        public void NormalizeHonoursLogicalRangeNotAssumedZeroToHundred()
        {
            // 逻辑范围 10–90：raw=50 应映射到 50%，而不是 50/90。
            HidBatteryCapability capability = Cap(true, 0x04, 0x0006, 0x0020, 10, 90);

            Assert.Equal(50, HidBatterySelector.NormalizePercent(capability, 50));
            Assert.Equal(0, HidBatterySelector.NormalizePercent(capability, 10));
            Assert.Equal(100, HidBatterySelector.NormalizePercent(capability, 90));
        }

        [Fact]
        public void OutOfRangeRawValuesAreClampedNotWrapped()
        {
            HidBatteryCapability capability = Cap(true, 0x04, 0x0006, 0x0020, 0, 100);

            Assert.Equal(0, HidBatterySelector.NormalizePercent(capability, 0));
            Assert.Equal(100, HidBatterySelector.NormalizePercent(capability, 255));
        }

        [Fact]
        public void ZeroRawValueIsValidZeroPercent()
        {
            HidBatteryCapability capability = Cap(true, 0x04, 0x0006, 0x0020, 0, 100);
            byte[] report = { 0x04, 0x00 };

            int percent;
            Assert.True(HidBatterySelector.TryResolvePercent(capability, report, out percent));
            Assert.Equal(0, percent);
        }

        [Fact]
        public void ReportIdIsHonouredWhenExtracting()
        {
            HidBatteryCapability capability = Cap(true, 0x07, 0x0006, 0x0020, 0, 100);
            byte[] matching = { 0x07, 0x4B };
            byte[] mismatched = { 0x06, 0x4B };

            int percent;
            Assert.True(HidBatterySelector.TryResolvePercent(capability, matching, out percent));
            Assert.Equal(75, percent);
            Assert.False(HidBatterySelector.TryResolvePercent(capability, mismatched, out percent));
        }

        [Fact]
        public void ReportIdZeroUsesWholeBuffer()
        {
            HidBatteryCapability capability = Cap(false, 0x00, 0x0006, 0x0020, 0, 100);
            byte[] report = { 0x2A };

            int percent;
            Assert.True(HidBatterySelector.TryResolvePercent(capability, report, out percent));
            Assert.Equal(42, percent);
        }

        [Fact]
        public void ShortReportIsRejectedNotGuessed()
        {
            HidBatteryCapability capability = Cap(true, 0x07, 0x0006, 0x0020, 0, 100);

            int percent;
            Assert.False(HidBatterySelector.TryResolvePercent(capability, new byte[] { 0x07 }, out percent));
        }

        [Fact]
        public void MultiByteValueIsLittleEndian()
        {
            HidBatteryCapability capability = Cap(true, 0x02, 0x0006, 0x0020, 0, 1000, 16);
            byte[] report = { 0x02, 0xE8, 0x03 };

            uint raw;
            Assert.True(HidBatterySelector.TryExtractRawValue(capability, report, out raw));
            Assert.Equal(1000u, raw);
            Assert.Equal(100, HidBatterySelector.NormalizePercent(capability, raw));
        }

        [Fact]
        public void SignatureDistinguishesEndpointsOfSameDevice()
        {
            HidBatteryCapability input = Cap(false, 0x01, 0x0006, 0x0020, 0, 100);
            HidBatteryCapability feature = Cap(true, 0x01, 0x0006, 0x0020, 0, 100);

            Assert.NotEqual(input.Signature(), feature.Signature());
        }

        [Fact]
        public void InvalidLogicalRangeIsRejectedAtConstruction()
        {
            Assert.Throws<ArgumentException>(delegate { Cap(true, 0x01, 0x0006, 0x0020, 100, 100); });
        }
    }

    /// <summary>HID 归并与链路判定单测：同一复合设备多接口不重复计数，总线证据不得升级成有线/2.4G。</summary>
    public sealed class HidGroupingTests
    {
        private static HidInterfaceInfo Info(string instanceId, string containerId, string parent)
        {
            return new HidInterfaceInfo
            {
                InterfacePath = "\\\\?\\hid#" + instanceId.Replace('\\', '#'),
                InstanceId = instanceId,
                ContainerId = containerId,
                ParentInstanceId = parent,
                VendorId = "046D",
                ProductId = "C52B",
                FriendlyName = "测试设备"
            };
        }

        [Fact]
        public void SameContainerMeansSameGroup()
        {
            HidInterfaceInfo a = Info("HID\\VID_046D&PID_C52B\\1", "{9C2A1B77-1111-2222-3333-444455556666}", "USB\\VID_046D&PID_C52B\\A");
            HidInterfaceInfo b = Info("HID\\VID_046D&PID_C52B\\2", "9c2a1b77-1111-2222-3333-444455556666", "USB\\VID_046D&PID_C52B\\A");

            Assert.Equal(HidDeviceProvider.GroupKey(a), HidDeviceProvider.GroupKey(b));
        }

        [Fact]
        public void TwoDevicesSameModelAreNotMergedWithoutContainer()
        {
            HidInterfaceInfo a = Info("HID\\VID_046D&PID_C52B\\DEV1", null, "USB\\VID_046D&PID_C52B\\SERIAL-A");
            HidInterfaceInfo b = Info("HID\\VID_046D&PID_C52B\\DEV2", null, "USB\\VID_046D&PID_C52B\\SERIAL-B");

            Assert.NotEqual(HidDeviceProvider.GroupKey(a), HidDeviceProvider.GroupKey(b));
        }

        [Fact]
        public void IdentityWithoutContainerIsMarkedUnverified()
        {
            HidInterfaceInfo a = Info("HID\\VID_046D&PID_C52B\\DEV1", null, "USB\\VID_046D&PID_C52B\\SERIAL-A");
            var group = new HidGroup(HidDeviceProvider.GroupKey(a), a);
            DeviceIdentity identity = group.BuildIdentity();

            Assert.False(identity.IsContainerVerified);
        }

        [Fact]
        public void IdentityWithContainerIsVerified()
        {
            HidInterfaceInfo a = Info("HID\\VID_046D&PID_C52B\\DEV1", "{9C2A1B77-1111-2222-3333-444455556666}", "USB\\VID_046D&PID_C52B\\A");
            var group = new HidGroup(HidDeviceProvider.GroupKey(a), a);
            DeviceIdentity identity = group.BuildIdentity();

            Assert.True(identity.IsContainerVerified);
            Assert.StartsWith("container:", identity.Key.Value);
        }

        [Fact]
        public void UsbInterfaceIsClassifiedWiredWithEvidence()
        {
            HidInterfaceInfo a = Info("USB\\VID_046D&PID_C52B\\A", "{9C2A1B77-1111-2222-3333-444455556666}", "USB\\ROOT_HUB30\\4");
            var group = new HidGroup(HidDeviceProvider.GroupKey(a), a);
            group.Add(a);
            group.AttachUnreadable(a, HidBatteryProbeResult.Unavailable(PercentUnavailableReason.NoStandardBatteryUsage, "none"));

            ProviderDevice device = group.ToProviderDevice(group.Endpoints[0], PercentUnavailableReason.NoStandardBatteryUsage, "none");

            Assert.Equal(LinkKind.UsbWired, device.Link.Kind);
            Assert.Contains("vid=046D", device.Link.Evidence);
            Assert.Contains("container={9C2A1B77", device.Link.Evidence);
            Assert.Null(device.Percent);
            Assert.Equal(PercentUnavailableReason.NoStandardBatteryUsage, device.PercentReason);
        }

        [Fact]
        public void NonUsbInstanceIsBusOnlyNotWired()
        {
            HidInterfaceInfo a = Info("HID\\VID_046D&PID_C52B\\DEV1", null, "USB\\VID_046D&PID_C52B\\A");
            var group = new HidGroup(HidDeviceProvider.GroupKey(a), a);
            group.Add(a);
            group.AttachUnreadable(a, HidBatteryProbeResult.Unavailable(PercentUnavailableReason.NoStandardBatteryUsage, "none"));

            ProviderDevice device = group.ToProviderDevice(group.Endpoints[0], PercentUnavailableReason.NoStandardBatteryUsage, "none");

            Assert.Equal(LinkKind.Unknown, device.Link.Kind);
            Assert.Equal("USB 设备，连接方式待确认", device.Link.Describe());
        }

        [Fact]
        public void MultipleInterfacesOfOneDeviceProduceOneIdentityButSeparateReadingKeys()
        {
            HidInterfaceInfo a = Info("HID\\VID_046D&PID_C52B\\1", "{9C2A1B77-1111-2222-3333-444455556666}", "USB\\VID_046D&PID_C52B\\A");
            HidInterfaceInfo b = Info("HID\\VID_046D&PID_C52B\\2", "{9C2A1B77-1111-2222-3333-444455556666}", "USB\\VID_046D&PID_C52B\\A");
            var group = new HidGroup(HidDeviceProvider.GroupKey(a), a);
            group.Add(a);
            group.Add(b);

            var capability = new HidBatteryCapability(true, 0x05, 0x0006, 0x0020, 0, 0, 0, 8, 0, 100, true, false);
            var result = new HidBatteryProbeResult
            {
                Percent = 66,
                Reason = PercentUnavailableReason.None,
                Detail = "read",
                Capabilities = new List<HidBatteryCapability> { capability }
            };
            group.AttachProbe(a, result);
            group.AttachProbe(b, result);

            Assert.Equal(2, group.Endpoints.Count);
            Assert.NotEqual(group.Endpoints[0].Key, group.Endpoints[1].Key);
            Assert.Equal(group.BuildIdentity().Key, group.BuildIdentity().Key);
        }
    }
}
