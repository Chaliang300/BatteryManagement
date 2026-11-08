using System;
using System.Collections.Generic;
using PowerDock.Core.Common;
using PowerDock.Core.State;
using Xunit;

namespace PowerDock.Core.Tests
{
    /// <summary>
    /// 托盘聚合单测（AGENTS P0-2 / D14 / C9）：
    /// 1. 取最低电量设备；
    /// 2. 无有效电量时绝不显示固定 100% 或 0%，呈现未知态（Percent = null）；
    /// 3. Tooltip 格式合规；
    /// 4. 充电与离线语义跟随最低电量设备。
    /// </summary>
    public sealed class TrayAggregatorTests
    {
        private static DeviceState MakeDevice(string name, int? percent, bool? charging, ConnectionState state)
        {
            var ds = new DeviceState(new DeviceKey("dev-" + name), name, DeviceCategory.Mouse)
            {
                Percent = percent,
                Charging = charging,
                State = state
            };
            return ds;
        }

        [Fact]
        public void EmptyList_ReturnsUnknownWithNoDevicesTooltip()
        {
            TrayAggregate agg = TrayAggregator.Aggregate(null);
            Assert.Null(agg.Percent);
            Assert.True(agg.IsOffline);
            Assert.Contains("未发现设备", agg.Tooltip);

            agg = TrayAggregator.Aggregate(new List<DeviceState>());
            Assert.Null(agg.Percent);
            Assert.True(agg.IsOffline);
            Assert.Contains("未发现设备", agg.Tooltip);
        }

        [Fact]
        public void MultipleDevices_PicksLowestPercent()
        {
            var list = new List<DeviceState>
            {
                MakeDevice("鼠标", 85, false, ConnectionState.Online),
                MakeDevice("键盘", 32, false, ConnectionState.Online),
                MakeDevice("耳机", 70, false, ConnectionState.Online)
            };

            TrayAggregate agg = TrayAggregator.Aggregate(list);
            Assert.Equal(32, agg.Percent);
            Assert.False(agg.IsCharging);
            Assert.False(agg.IsOffline);
            Assert.Equal("键盘 - 32%", agg.Tooltip);
        }

        [Fact]
        public void LowestDeviceCharging_SetsChargingFlag()
        {
            var list = new List<DeviceState>
            {
                MakeDevice("鼠标", 85, false, ConnectionState.Online),
                MakeDevice("键盘", 15, true, ConnectionState.Online)
            };

            TrayAggregate agg = TrayAggregator.Aggregate(list);
            Assert.Equal(15, agg.Percent);
            Assert.True(agg.IsCharging);
            Assert.Equal("键盘 - 15% (充电中)", agg.Tooltip);
        }

        [Fact]
        public void AllUnreadableDevices_ReturnsNullPercentNotZeroOrHundred()
        {
            var list = new List<DeviceState>
            {
                MakeDevice("USB接收器", null, null, ConnectionState.Unknown),
                MakeDevice("断开的蓝牙键盘", null, null, ConnectionState.Offline)
            };

            TrayAggregate agg = TrayAggregator.Aggregate(list);
            Assert.Null(agg.Percent);
            Assert.False(agg.IsCharging);
            Assert.False(agg.IsOffline);
            Assert.Contains("2 台设备，电量未知", agg.Tooltip);
        }

        [Fact]
        public void MixOfReadableAndUnreadable_PicksLowestReadable()
        {
            var list = new List<DeviceState>
            {
                MakeDevice("USB接收器", null, null, ConnectionState.Unknown),
                MakeDevice("鼠标", 60, false, ConnectionState.Online)
            };

            TrayAggregate agg = TrayAggregator.Aggregate(list);
            Assert.Equal(60, agg.Percent);
            Assert.Equal("鼠标 - 60%", agg.Tooltip);
        }

        [Fact]
        public void LowestDeviceIsOffline_MarksOffline()
        {
            var list = new List<DeviceState>
            {
                MakeDevice("掉线手柄", 10, false, ConnectionState.Offline),
                MakeDevice("在线鼠标", 80, false, ConnectionState.Online)
            };

            TrayAggregate agg = TrayAggregator.Aggregate(list);
            Assert.Equal(10, agg.Percent);
            Assert.True(agg.IsOffline);
        }

        [Fact]
        public void ZeroPercent_IsHandledAsValidLowestReading()
        {
            var list = new List<DeviceState>
            {
                MakeDevice("空电鼠标", 0, false, ConnectionState.Online),
                MakeDevice("键盘", 50, false, ConnectionState.Online)
            };

            TrayAggregate agg = TrayAggregator.Aggregate(list);
            Assert.Equal(0, agg.Percent);
            Assert.Equal("空电鼠标 - 0%", agg.Tooltip);
        }
    }
}
