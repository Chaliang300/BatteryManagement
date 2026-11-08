using System;
using System.Collections.Generic;
using PowerDock.Core.Common;
using PowerDock.Core.Widgets;
using Xunit;

namespace PowerDock.Core.Tests
{
    public sealed class WidgetPipelineTests
    {
        private static DeviceState CreateDevice(string id, string name, int? percent, ConnectionState state, bool isCharging = false)
        {
            var dev = new DeviceState(new DeviceKey(id), name, DeviceCategory.Other);
            dev.Percent = percent;
            dev.State = state;
            dev.Charging = isCharging;
            dev.Link = new LinkInfo(LinkKind.Bluetooth, "bt-mock", true, state == ConnectionState.Online, false, DateTime.UtcNow);
            return dev;
        }

        [Fact]
        public void Project_根据尺寸规格正确截断行数()
        {
            var snapshot = new List<DeviceState>();
            var selectedKeys = new List<string>();

            for (int i = 1; i <= 10; i++)
            {
                string id = "dev-" + i;
                snapshot.Add(CreateDevice(id, "Device " + i, 10 + i, ConnectionState.Online));
                selectedKeys.Add(id);
            }

            var small = WidgetPipeline.Project(snapshot, selectedKeys, WidgetSizeKind.Small2x2);
            Assert.Equal(3, small.DisplayedRows.Count);
            Assert.Equal(3, small.MaxRows);
            Assert.Equal(173.0, small.WidthDip);
            Assert.Equal(173.0, small.HeightDip);
            Assert.Equal(22.0, small.CornerRadiusDip);
            Assert.Equal(14.0, small.PaddingDip);

            var medium = WidgetPipeline.Project(snapshot, selectedKeys, WidgetSizeKind.Medium4x2);
            Assert.Equal(5, medium.DisplayedRows.Count);
            Assert.Equal(5, medium.MaxRows);
            Assert.Equal(362.0, medium.WidthDip);
            Assert.Equal(173.0, medium.HeightDip);
            Assert.Equal(27.0, medium.CornerRadiusDip);
            Assert.Equal(16.0, medium.PaddingDip);

            var large = WidgetPipeline.Project(snapshot, selectedKeys, WidgetSizeKind.Large4x4);
            Assert.Equal(7, large.DisplayedRows.Count);
            Assert.Equal(7, large.MaxRows);
            Assert.Equal(362.0, large.WidthDip);
            Assert.Equal(362.0, large.HeightDip);
            Assert.Equal(27.0, large.CornerRadiusDip);
            Assert.Equal(22.0, large.PaddingDip);
        }

        [Fact]
        public void Project_排序按电量升序且无电量排末尾()
        {
            var snapshot = new List<DeviceState>
            {
                CreateDevice("d1", "Mouse", 80, ConnectionState.Online),
                CreateDevice("d2", "Keyboard", 15, ConnectionState.Online),
                CreateDevice("d3", "UnknownDev", null, ConnectionState.Online),
                CreateDevice("d4", "Headset", 45, ConnectionState.Online),
            };
            var selected = new[] { "d1", "d2", "d3", "d4" };

            var model = WidgetPipeline.Project(snapshot, selected, WidgetSizeKind.Medium4x2);
            Assert.Equal(4, model.DisplayedRows.Count);
            Assert.Equal("Keyboard", model.DisplayedRows[0].Name);
            Assert.Equal(15, model.DisplayedRows[0].Percent);
            Assert.Equal(WidgetTone.Danger, model.DisplayedRows[0].Tone);

            Assert.Equal("Headset", model.DisplayedRows[1].Name);
            Assert.Equal(45, model.DisplayedRows[1].Percent);

            Assert.Equal("Mouse", model.DisplayedRows[2].Name);
            Assert.Equal(80, model.DisplayedRows[2].Percent);

            Assert.Equal("UnknownDev", model.DisplayedRows[3].Name);
            Assert.Null(model.DisplayedRows[3].Percent);
            Assert.Equal("—", model.DisplayedRows[3].DisplayPercent);
        }

        [Fact]
        public void Project_已勾选离线设备照常显示且电量为破折号()
        {
            var snapshot = new List<DeviceState>
            {
                CreateDevice("d1", "OnlineMouse", 70, ConnectionState.Online),
                CreateDevice("d2", "OfflinePad", 10, ConnectionState.Offline) // 即使以前有 10%，离线状态下电量显示「—」
            };
            var selected = new[] { "d1", "d2" };

            var model = WidgetPipeline.Project(snapshot, selected, WidgetSizeKind.Medium4x2);
            Assert.Equal(2, model.DisplayedRows.Count);

            var offlineRow = model.DisplayedRows[0]; // 10% < 70%，升序排在第0
            Assert.Equal("OfflinePad", offlineRow.Name);
            Assert.True(offlineRow.IsOffline);
            Assert.Equal("—", offlineRow.DisplayPercent);
            Assert.Equal(WidgetTone.Off, offlineRow.Tone);

            var onlineRow = model.DisplayedRows[1];
            Assert.Equal("OnlineMouse", onlineRow.Name);
            Assert.False(onlineRow.IsOffline);
            Assert.Equal("70%", onlineRow.DisplayPercent);
        }

        [Fact]
        public void Project_切尺寸不清空勾选集合仅改变容量截断()
        {
            var snapshot = new List<DeviceState>
            {
                CreateDevice("d1", "A", 10, ConnectionState.Online),
                CreateDevice("d2", "B", 20, ConnectionState.Online),
                CreateDevice("d3", "C", 30, ConnectionState.Online),
                CreateDevice("d4", "D", 40, ConnectionState.Online),
                CreateDevice("d5", "E", 50, ConnectionState.Online),
                CreateDevice("d6", "F", 60, ConnectionState.Online),
            };
            var selected = new[] { "d1", "d2", "d3", "d4", "d5", "d6" };

            var small = WidgetPipeline.Project(snapshot, selected, WidgetSizeKind.Small2x2);
            var med = WidgetPipeline.Project(snapshot, selected, WidgetSizeKind.Medium4x2);
            var lg = WidgetPipeline.Project(snapshot, selected, WidgetSizeKind.Large4x4);

            Assert.Equal(6, small.SelectedCount);
            Assert.Equal(6, med.SelectedCount);
            Assert.Equal(6, lg.SelectedCount);

            Assert.Equal(3, small.DisplayedRows.Count);
            Assert.Equal(5, med.DisplayedRows.Count);
            Assert.Equal(6, lg.DisplayedRows.Count);
        }

        [Fact]
        public void Project_大号脚注格式符合契约()
        {
            var snapshot = new List<DeviceState>
            {
                CreateDevice("d1", "A", 10, ConnectionState.Online),
                CreateDevice("d2", "B", 20, ConnectionState.Offline),
                CreateDevice("d3", "C", 30, ConnectionState.Online),
            };
            var selected = new[] { "d1", "d2" };

            var model = WidgetPipeline.Project(snapshot, selected, WidgetSizeKind.Large4x4);
            Assert.Equal("共 3 台 · 2 在线 · 按电量升序 · 空闲 60s 刷新", model.Footnote);
        }
    }
}
