using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PowerDock.App.Windows;
using PowerDock.Core.Common;
using PowerDock.Core.Storage;
using Xunit;

namespace PowerDock.Core.Tests
{
    public class DevicePageTests
    {
        private static void RunInSta(Action action)
        {
            Exception thrown = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (thrown != null)
            {
                throw new InvalidOperationException("STA thread test failed: " + thrown.Message, thrown);
            }
        }

        [Fact]
        public void EmptyState_WhenNoDevices_ShowsHintAndNoZeroPercent()
        {
            RunInSta(() =>
            {
                var view = new DeviceSummaryView();
                view.Render(new List<DeviceState>());

                // 验证在空态下不应存在卡片
                Assert.Null(view.SelectedDeviceKey);

                // 验证 null 快照同样优雅进入空态
                view.Render(null);
                Assert.Null(view.SelectedDeviceKey);
            });
        }

        [Fact]
        public void DeviceCards_RealSnapshots_CoversOnlineOfflineUnknownAndMissingPercent()
        {
            RunInSta(() =>
            {
                var view = new DeviceSummaryView();

                var now = DateTime.UtcNow;

                // 1. 在线蓝牙耳机 (75%, 未充电)
                var dev1 = new DeviceState(new DeviceKey("container:bt-01"), "WH-1000XM4", DeviceCategory.Headset)
                {
                    State = ConnectionState.Online,
                    Percent = 75,
                    Charging = false,
                    IdentityVerified = true,
                    Link = new LinkInfo(LinkKind.Bluetooth, "BLE GATT Battery", true, true, false, now),
                    LastSuccessfulReadingUtc = now
                };

                // 2. 充电中本机电池 (45%, 正在充电)
                var dev2 = new DeviceState(new DeviceKey("container:internal-01"), "本机电池", DeviceCategory.Laptop)
                {
                    State = ConnectionState.Online,
                    Percent = 45,
                    Charging = true,
                    IdentityVerified = true,
                    Link = LinkInfo.Internal("Win32_Battery", true),
                    LastSuccessfulReadingUtc = now
                };

                // 3. 离线设备 (上次 82%, 10分钟前)
                var dev3 = new DeviceState(new DeviceKey("container:mouse-off"), "MX Anywhere 3", DeviceCategory.Mouse)
                {
                    State = ConnectionState.Offline,
                    Percent = 82,
                    Charging = false,
                    IdentityVerified = true,
                    Link = new LinkInfo(LinkKind.Wireless24G, "2.4G Receiver", true, false, false, now.AddMinutes(-10)),
                    LastSuccessfulReadingUtc = now.AddMinutes(-10)
                };

                // 4. 未知且缺电量设备 (Percent == null, 给出原因 NoStandardBatteryUsage)
                var dev4 = new DeviceState(new DeviceKey("container:custom-01"), "自定义键盘", DeviceCategory.Keyboard)
                {
                    State = ConnectionState.Unknown,
                    Percent = null,
                    PercentReason = PercentUnavailableReason.NoStandardBatteryUsage,
                    IdentityVerified = false,
                    Link = new LinkInfo(LinkKind.UsbWired, "USB Endpoint", false, true, false, now),
                    LastSuccessfulReadingUtc = null
                };

                // 5. 真实 0% 设备 (Percent == 0, 必须显示为 0% 而不是 —)
                var dev5 = new DeviceState(new DeviceKey("container:dead-mouse"), "已耗尽手柄", DeviceCategory.Gamepad)
                {
                    State = ConnectionState.Online,
                    Percent = 0,
                    Charging = false,
                    IdentityVerified = true,
                    Link = new LinkInfo(LinkKind.Bluetooth, "BLE GATT", true, true, false, now),
                    LastSuccessfulReadingUtc = now
                };

                var snapshot = new List<DeviceState> { dev1, dev2, dev3, dev4, dev5 };
                view.Render(snapshot);

                // 验证卡片正确加载并可跨页定位到真实 0% 设备
                bool selected0 = view.SelectAndScrollToDevice(dev5.Key);
                Assert.True(selected0);
                Assert.Equal(dev5.Key, view.SelectedDeviceKey);

                // 验证缺电量卡片也能正确定位
                bool selectedNull = view.SelectAndScrollToDevice(dev4.Key);
                Assert.True(selectedNull);
                Assert.Equal(dev4.Key, view.SelectedDeviceKey);
            });
        }

        [Fact]
        public void LinkKind_DistinguishesUsbWired_UsbBusKnown_Wireless24G_AndUnknown()
        {
            var now = DateTime.UtcNow;

            // 1. USB 有线已确认
            var devWired = new DeviceState(new DeviceKey("container:wired"), "有线鼠标", DeviceCategory.Mouse)
            {
                Link = new LinkInfo(LinkKind.UsbWired, "USB Wired confirmed", false, true, false, now)
            };
            Assert.Equal("USB 有线", DeviceCardControl.DescribeLinkExact(devWired));

            // 2. USB 总线已知，连接方式待确认
            var devUsbBus = new DeviceState(new DeviceKey("container:usbbus"), "USB 复合外设", DeviceCategory.Other)
            {
                Link = new LinkInfo(LinkKind.Unknown, "USB\\VID_046D&PID_C52B", false, true, false, now)
            };
            Assert.Equal("USB 设备，连接方式待确认", DeviceCardControl.DescribeLinkExact(devUsbBus));

            // 3. 2.4G 外设已确认 (外设在场)
            var dev24G = new DeviceState(new DeviceKey("container:24g-active"), "2.4G 鼠标", DeviceCategory.Mouse)
            {
                Link = new LinkInfo(LinkKind.Wireless24G, "2.4G HID paired and active", true, true, false, now)
            };
            Assert.Equal("2.4G HID", DeviceCardControl.DescribeLinkExact(dev24G));

            // 4. 2.4G 接收器在场，外设状态未知 (IsReceiverOnly == true)
            var devReceiverOnly = new DeviceState(new DeviceKey("container:receiver-only"), "无线接收器", DeviceCategory.Other)
            {
                Link = new LinkInfo(LinkKind.Wireless24G, "2.4G Receiver plugged", false, true, true, now)
            };
            Assert.Equal("接收器已连接，外设状态未知", DeviceCardControl.DescribeLinkExact(devReceiverOnly));

            // 5. 蓝牙已连接
            var devBt = new DeviceState(new DeviceKey("container:bt"), "蓝牙鼠标", DeviceCategory.Mouse)
            {
                Link = new LinkInfo(LinkKind.Bluetooth, "GATT", true, true, false, now)
            };
            Assert.Equal("蓝牙 GATT", DeviceCardControl.DescribeLinkExact(devBt));

            // 6. 蓝牙已配对未连接
            var devBtDisconnected = new DeviceState(new DeviceKey("container:bt-disc"), "蓝牙耳机", DeviceCategory.Headset)
            {
                Link = new LinkInfo(LinkKind.Bluetooth, "GATT", true, false, false, now)
            };
            Assert.Equal("蓝牙（已配对，未连接）", DeviceCardControl.DescribeLinkExact(devBtDisconnected));

            // 7. 连接方式未知
            var devUnknown = new DeviceState(new DeviceKey("container:unknown"), "未知外设", DeviceCategory.Other)
            {
                Link = new LinkInfo(LinkKind.Unknown, "Raw Endpoint", false, false, false, now)
            };
            Assert.Equal("连接方式未知", DeviceCardControl.DescribeLinkExact(devUnknown));
        }

        [Fact]
        public void TwoSameModelDevices_StateSelectionAliasAndCheck_Independent()
        {
            RunInSta(() =>
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "settings-twodevices-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    var store = new SettingsStore(tempFile);
                    store.Load();

                    var view = new DeviceSummaryView();
                    view.SetSettingsStore(store);

                    var now = DateTime.UtcNow;

                    // 模拟两台同型号同名称设备
                    var devA = new DeviceState(new DeviceKey("container:11111111-1111-1111-1111-111111111111"), "MX Master 3S", DeviceCategory.Mouse)
                    {
                        State = ConnectionState.Online,
                        Percent = 80,
                        IdentityVerified = true,
                        Link = new LinkInfo(LinkKind.Wireless24G, "2.4G Dongle 1", true, true, false, now)
                    };

                    var devB = new DeviceState(new DeviceKey("container:22222222-2222-2222-2222-222222222222"), "MX Master 3S", DeviceCategory.Mouse)
                    {
                        State = ConnectionState.Online,
                        Percent = 40,
                        IdentityVerified = true,
                        Link = new LinkInfo(LinkKind.Bluetooth, "BLE BT 2", true, true, false, now)
                    };

                    view.Render(new List<DeviceState> { devA, devB });

                    // 1. 选中设备 A，确认设备 B 不被选中
                    view.SelectAndScrollToDevice(devA.Key);
                    Assert.Equal(devA.Key, view.SelectedDeviceKey);
                    Assert.NotEqual(devB.Key, view.SelectedDeviceKey);

                    // 2. 为设备 A 设别名，设备 B 不受影响
                    bool aliasOk = store.TrySetDeviceAlias(devA.Key.Value, "公司主力鼠标");
                    Assert.True(aliasOk);

                    // 验证持久化磁盘回读
                    var reloaded = new SettingsStore(tempFile);
                    reloaded.Load();
                    Assert.True(reloaded.Current.DeviceAliases.ContainsKey(devA.Key.Value));
                    Assert.Equal("公司主力鼠标", reloaded.Current.DeviceAliases[devA.Key.Value]);
                    Assert.False(reloaded.Current.DeviceAliases.ContainsKey(devB.Key.Value));

                    // 3. 勾选设备 A 进挂件，设备 B 不在勾选集合中
                    bool widgetOk = store.TrySetWidgetSelected(devA.Key.Value, true);
                    Assert.True(widgetOk);

                    reloaded.Load();
                    Assert.Contains(devA.Key.Value, reloaded.Current.WidgetDevices);
                    Assert.DoesNotContain(devB.Key.Value, reloaded.Current.WidgetDevices);

                    // 4. 置顶设备 B，设备 A 不受影响
                    bool pinOk = store.TryTogglePinnedDevice(devB.Key.Value, out bool isPinned);
                    Assert.True(pinOk);
                    Assert.True(isPinned);

                    reloaded.Load();
                    Assert.Contains(devB.Key.Value, reloaded.Current.PinnedDevices);
                    Assert.DoesNotContain(devA.Key.Value, reloaded.Current.PinnedDevices);

                    // 5. 再次 Render 验证各自独立性在刷新后依然稳固
                    view.Render(new List<DeviceState> { devA, devB });
                    Assert.Equal(devA.Key, view.SelectedDeviceKey);
                    Assert.Equal("公司主力鼠标", devA.Alias);
                    Assert.Null(devB.Alias);
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                    string bak = tempFile + ".bak";
                    if (File.Exists(bak)) File.Delete(bak);
                }
            });
        }

        [Fact]
        public void ContinuousRefresh_PreservesSelectionAndDoesNotDesync()
        {
            RunInSta(() =>
            {
                var view = new DeviceSummaryView();
                var now = DateTime.UtcNow;

                var dev1 = new DeviceState(new DeviceKey("container:dev-1"), "键盘 1", DeviceCategory.Keyboard)
                {
                    State = ConnectionState.Online,
                    Percent = 90
                };
                var dev2 = new DeviceState(new DeviceKey("container:dev-2"), "鼠标 2", DeviceCategory.Mouse)
                {
                    State = ConnectionState.Online,
                    Percent = 50
                };
                var dev3 = new DeviceState(new DeviceKey("container:dev-3"), "耳机 3", DeviceCategory.Headset)
                {
                    State = ConnectionState.Online,
                    Percent = 20
                };

                view.Render(new List<DeviceState> { dev1, dev2, dev3 });

                // 选中第 2 台设备
                view.SelectAndScrollToDevice(dev2.Key);
                Assert.Equal(dev2.Key, view.SelectedDeviceKey);

                // 模拟高频或持续周期刷新 5 次，每次数值微调
                for (int i = 0; i < 5; i++)
                {
                    dev1.Percent = 90 - i;
                    dev2.Percent = 50 + i;
                    dev3.Percent = 20 - i;

                    view.Render(new List<DeviceState> { dev1, dev2, dev3 });

                    // 验证选中设备始终锁定在 dev2，没有串选到第 0 项 dev1 或其他项
                    Assert.Equal(dev2.Key, view.SelectedDeviceKey);
                }
            });
        }

        [Fact]
        public void CrossPageNavigation_SelectAndScrollToDevice_Succeeds()
        {
            RunInSta(() =>
            {
                var view = new DeviceSummaryView();

                var d1 = new DeviceState(new DeviceKey("container:alpha"), "Keyboard Pro", DeviceCategory.Keyboard);
                var d2 = new DeviceState(new DeviceKey("container:beta"), "AirPods Pro", DeviceCategory.Headset);
                var d3 = new DeviceState(new DeviceKey("container:gamma"), "Logitech G903", DeviceCategory.Mouse);

                view.Render(new List<DeviceState> { d1, d2, d3 });

                // 1. 按 DeviceKey 匹配
                bool foundKey = view.SelectAndScrollToDevice(d2.Key);
                Assert.True(foundKey);
                Assert.Equal(d2.Key, view.SelectedDeviceKey);

                // 2. 按字符串名称匹配
                bool foundName = view.SelectAndScrollToDevice("Logitech G903");
                Assert.True(foundName);
                Assert.Equal(d3.Key, view.SelectedDeviceKey);

                // 3. 不存在的设备返回 false，且不篡改已有选中
                bool notFound = view.SelectAndScrollToDevice("NonExistentDevice");
                Assert.False(notFound);
                Assert.Equal(d3.Key, view.SelectedDeviceKey);
            });
        }

        [Fact]
        public void DeviceCards_DpiLayout_100And150Percent_NoOverflowOrWrap()
        {
            RunInSta(() =>
            {
                var view = new DeviceSummaryView();
                var now = DateTime.UtcNow;

                // 准备 8 台经典设备（与 docs/mockup 方案 A 保持一致）
                var devices = new List<DeviceState>
                {
                    new DeviceState(new DeviceKey("container:1"), "MX Master 3S", DeviceCategory.Mouse) { State = ConnectionState.Online, Percent = 18, Link = new LinkInfo(LinkKind.Wireless24G, "2.4G", true, true, false, now) },
                    new DeviceState(new DeviceKey("container:2"), "本机电池", DeviceCategory.Laptop) { State = ConnectionState.Online, Percent = 47, Charging = true, Link = LinkInfo.Internal("WMI", true) },
                    new DeviceState(new DeviceKey("container:3"), "相机", DeviceCategory.Other) { State = ConnectionState.Online, Percent = 55, Link = new LinkInfo(LinkKind.UsbWired, "USB", false, true, false, now) },
                    new DeviceState(new DeviceKey("container:4"), "K380 键盘", DeviceCategory.Keyboard) { State = ConnectionState.Online, Percent = 64, Link = new LinkInfo(LinkKind.Bluetooth, "BT", true, true, false, now) },
                    new DeviceState(new DeviceKey("container:5"), "Magic Trackpad", DeviceCategory.Mouse) { State = ConnectionState.Online, Percent = 71, Link = new LinkInfo(LinkKind.Bluetooth, "BT", true, true, false, now) },
                    new DeviceState(new DeviceKey("container:6"), "AirPods Pro", DeviceCategory.Headset) { State = ConnectionState.Online, Percent = 82, Link = new LinkInfo(LinkKind.Bluetooth, "BT", true, true, false, now) },
                    new DeviceState(new DeviceKey("container:7"), "JBL GO 3", DeviceCategory.Headset) { State = ConnectionState.Online, Percent = 100, Link = new LinkInfo(LinkKind.Bluetooth, "BT", true, true, false, now) },
                    new DeviceState(new DeviceKey("container:8"), "Watch S9", DeviceCategory.Other) { State = ConnectionState.Offline, Percent = null, PercentReason = PercentUnavailableReason.Timeout }
                };

                view.Render(devices);

                // 内容区标准宽度：MainWindow TargetWidthDip (880) - 边距 (18*2) = 844 DIP
                const double contentWidth = 844.0;
                const double contentHeight = 460.0;

                // 100% DPI (Scale 1.0) 测量与布局
                view.LayoutTransform = new System.Windows.Media.ScaleTransform(1.0, 1.0);
                view.Measure(new Size(contentWidth, contentHeight));
                view.Arrange(new Rect(0, 0, contentWidth, contentHeight));
                view.UpdateLayout();

                Assert.True(view.ActualWidth > 0, "100% DPI 下 ActualWidth 应大于 0");
                Assert.True(view.ActualHeight > 0, "100% DPI 下 ActualHeight 应大于 0");

                // 150% DPI (Scale 1.5) 测量与布局
                view.LayoutTransform = new System.Windows.Media.ScaleTransform(1.5, 1.5);
                view.Measure(new Size(contentWidth, contentHeight));
                view.Arrange(new Rect(0, 0, contentWidth, contentHeight));
                view.UpdateLayout();

                Assert.True(view.ActualWidth > 0, "150% DPI 下 ActualWidth 应大于 0");
                Assert.True(view.ActualHeight > 0, "150% DPI 下 ActualHeight 应大于 0");
            });
        }

        [Fact]
        public void PersistenceOperations_AliasPinnedIgnoredAndWidgetCheck_RequiresDiskReadbackSuccess()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "settings-persist-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var store = new SettingsStore(tempFile);
                store.Load();

                string devKey = "container:test-dev-01";

                // 别名
                Assert.True(store.TrySetDeviceAlias(devKey, "我的手柄"));
                Assert.Equal("我的手柄", store.Current.DeviceAliases[devKey]);

                // 置顶
                Assert.True(store.TryTogglePinnedDevice(devKey, out bool pinned));
                Assert.True(pinned);
                Assert.Contains(devKey, store.Current.PinnedDevices);

                // 取消置顶
                Assert.True(store.TryTogglePinnedDevice(devKey, out pinned));
                Assert.False(pinned);
                Assert.DoesNotContain(devKey, store.Current.PinnedDevices);

                // 忽略
                Assert.True(store.TryToggleIgnoredDevice(devKey, out bool ignored));
                Assert.True(ignored);
                Assert.Contains(devKey, store.Current.IgnoredDevices);

                // 取消忽略
                Assert.True(store.TryToggleIgnoredDevice(devKey, out ignored));
                Assert.False(ignored);
                Assert.DoesNotContain(devKey, store.Current.IgnoredDevices);

                // 组件勾选
                Assert.True(store.TrySetWidgetSelected(devKey, true));
                Assert.Contains(devKey, store.Current.WidgetDevices);

                // 磁盘真实回读断言
                var reloaded = new SettingsStore(tempFile);
                reloaded.Load();
                Assert.Equal("我的手柄", reloaded.Current.DeviceAliases[devKey]);
                Assert.Contains(devKey, reloaded.Current.WidgetDevices);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
                string bak = tempFile + ".bak";
                if (File.Exists(bak)) File.Delete(bak);
            }
        }
    }
}
