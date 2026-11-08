using System;
using System.Collections.Generic;
using PowerDock.Core.Common;
using PowerDock.Core.State;
using PowerDock.Core.Storage;
using Xunit;

namespace PowerDock.Core.Tests
{
    public class OverviewAggregatorTests
    {
        [Fact]
        public void EmptySnapshot_ReturnsEmptyStateWithZeroCounts()
        {
            var result = OverviewAggregator.Aggregate(new List<DeviceState>(), new AppSettings());

            Assert.True(result.IsEmpty);
            Assert.Equal(0, result.TotalCount);
            Assert.Equal(0, result.OnlineCount);
            Assert.Equal(0, result.NeedChargingCount);
            Assert.Null(result.NeedChargingLowestPercent);
            Assert.False(result.HasTrendHistory);
            Assert.Equal(7, result.FocusDailyTrend.Count);
        }

        [Fact]
        public void UnknownPercentages_DoNotParticipateInLowestCalculation()
        {
            var dev1 = new DeviceState(new DeviceKey("dev1"), "设备1", DeviceCategory.Mouse)
            {
                Percent = null,
                State = ConnectionState.Online
            };
            var dev2 = new DeviceState(new DeviceKey("dev2"), "设备2", DeviceCategory.Keyboard)
            {
                Percent = 75,
                State = ConnectionState.Online
            };
            var dev3 = new DeviceState(new DeviceKey("dev3"), "设备3", DeviceCategory.Headset)
            {
                Percent = null,
                State = ConnectionState.Online
            };

            var snapshot = new List<DeviceState> { dev1, dev2, dev3 };
            var result = OverviewAggregator.Aggregate(snapshot, new AppSettings());

            Assert.False(result.IsEmpty);
            Assert.Equal(3, result.TotalCount);
            Assert.Equal(0, result.NeedChargingCount);
            Assert.Equal("设备2", result.FocusDevice.DisplayName);
            Assert.Equal(75, result.FocusPercent);
        }

        [Fact]
        public void BelowThreshold_CountsAsNeedChargingAndAlerts()
        {
            var dev1 = new DeviceState(new DeviceKey("dev1"), "正常鼠标", DeviceCategory.Mouse)
            {
                Percent = 50,
                State = ConnectionState.Online
            };
            var dev2 = new DeviceState(new DeviceKey("dev2"), "低电键盘", DeviceCategory.Keyboard)
            {
                Percent = 15,
                State = ConnectionState.Online
            };
            var dev3 = new DeviceState(new DeviceKey("dev3"), "极低耳机", DeviceCategory.Headset)
            {
                Percent = 8,
                State = ConnectionState.Online
            };

            var snapshot = new List<DeviceState> { dev1, dev2, dev3 };
            var settings = new AppSettings { LowBatteryThreshold = 20 };
            var result = OverviewAggregator.Aggregate(snapshot, settings);

            Assert.Equal(2, result.NeedChargingCount);
            Assert.Equal(8, result.NeedChargingLowestPercent);
            Assert.Equal("极低耳机", result.NeedChargingDevice.DisplayName);
            Assert.Equal("极低耳机", result.FocusDevice.DisplayName);
            Assert.Equal(8, result.FocusPercent);
        }

        [Fact]
        public void ChargingDevice_CountedAndSurfaced()
        {
            var dev1 = new DeviceState(new DeviceKey("dev1"), "笔记本", DeviceCategory.Laptop)
            {
                Percent = 47,
                Charging = true,
                State = ConnectionState.Online
            };
            var dev2 = new DeviceState(new DeviceKey("dev2"), "普通鼠标", DeviceCategory.Mouse)
            {
                Percent = 80,
                Charging = false,
                State = ConnectionState.Online
            };

            var snapshot = new List<DeviceState> { dev1, dev2 };
            var result = OverviewAggregator.Aggregate(snapshot, new AppSettings());

            Assert.Equal(1, result.ChargingCount);
            Assert.Equal("笔记本", result.PrimaryChargingDevice.DisplayName);
        }

        [Fact]
        public void OnlineAndOfflineSummary_Accurate()
        {
            var dev1 = new DeviceState(new DeviceKey("dev1"), "在线鼠标", DeviceCategory.Mouse)
            {
                State = ConnectionState.Online,
                Percent = 90
            };
            var dev2 = new DeviceState(new DeviceKey("dev2"), "离线耳机", DeviceCategory.Headset)
            {
                State = ConnectionState.Offline,
                Percent = 30
            };

            var snapshot = new List<DeviceState> { dev1, dev2 };
            var result = OverviewAggregator.Aggregate(snapshot, new AppSettings());

            Assert.Equal(1, result.OnlineCount);
            Assert.Equal(2, result.TotalCount);
            Assert.Contains("离线耳机 离线", result.OfflineSummary);
        }

        [Fact]
        public void TrendEmptyState_IdentifiedWhenNoHistory()
        {
            var dev = new DeviceState(new DeviceKey("dev1"), "设备", DeviceCategory.Mouse)
            {
                Percent = 60,
                State = ConnectionState.Online
            };

            var result = OverviewAggregator.Aggregate(new List<DeviceState> { dev }, new AppSettings(), focusHistory: null);

            Assert.False(result.HasTrendHistory);
            Assert.Equal(7, result.FocusDailyTrend.Count);
            foreach (var point in result.FocusDailyTrend)
            {
                Assert.True(point.IsGap);
                Assert.Null(point.Percent);
            }
        }

        [Fact]
        public void TrendHistory_MapsDailyBucketsProperly()
        {
            var key = new DeviceKey("dev1");
            var dev = new DeviceState(key, "设备", DeviceCategory.Mouse)
            {
                Percent = 18,
                State = ConnectionState.Online
            };

            DateTime now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
            var samples = new List<BatterySample>
            {
                new BatterySample(key, now.AddDays(-2), 45, false, BatterySource.Hid),
                new BatterySample(key, now.AddDays(-1), 27, false, BatterySource.Hid),
                new BatterySample(key, now, 18, false, BatterySource.Hid)
            };

            var result = OverviewAggregator.Aggregate(new List<DeviceState> { dev }, new AppSettings(), samples, now);

            Assert.True(result.HasTrendHistory);
            Assert.Equal(7, result.FocusDailyTrend.Count);

            // Today is index 6
            var today = result.FocusDailyTrend[6];
            Assert.True(today.IsToday);
            Assert.False(today.IsGap);
            Assert.Equal(18, today.Percent);
            Assert.Equal(TrendTone.Danger, today.Tone);

            // Yesterday is index 5
            var yesterday = result.FocusDailyTrend[5];
            Assert.False(yesterday.IsGap);
            Assert.Equal(27, yesterday.Percent);
            Assert.Equal(TrendTone.Warn, yesterday.Tone);

            // Day -2 is index 4
            var dayMinus2 = result.FocusDailyTrend[4];
            Assert.False(dayMinus2.IsGap);
            Assert.Equal(45, dayMinus2.Percent);
            Assert.Equal(TrendTone.Neutral, dayMinus2.Tone);

            // Day -3 is index 3 (gap)
            var dayMinus3 = result.FocusDailyTrend[3];
            Assert.True(dayMinus3.IsGap);
            Assert.Null(dayMinus3.Percent);
        }
    }
}
