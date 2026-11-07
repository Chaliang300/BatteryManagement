using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PowerDock.Core.Storage;
using Xunit;

namespace PowerDock.Core.Tests
{
    /// <summary>
    /// settings.json 契约：默认值、PascalCase 字段名、大小写不敏感读取、
    /// 以及「写 → 回读 → 校验」链路（AGENTS.md §5.4 / §8）。
    /// </summary>
    public sealed class SettingsTests
    {
        [Fact]
        public void AppSettings_默认值符合设计基线()
        {
            var settings = new AppSettings();

            // 挂件中号档对应 §6.3 的 362×173 DIP；模型层只持久化档位标识
            Assert.Equal("medium", settings.WidgetSize);
            Assert.Equal("dark", settings.Theme);
            Assert.Equal(20, settings.LowBatteryThreshold);
            Assert.Equal(30, settings.AlertCooldownMinutes);
            Assert.True(settings.WidgetTopMost);
            Assert.True(settings.MinimizeToTrayOnClose);
            Assert.True(settings.ShowStatusDot);
            Assert.True(settings.ShowBatteryBar);
            Assert.True(settings.ShowPercentage);
            Assert.Equal(-1, settings.WidgetLeft);
            Assert.Equal(-1, settings.WidgetTop);
            Assert.False(settings.AutoStart);
            Assert.Empty(settings.DeviceAliases);
            Assert.Empty(settings.IgnoredDevices);
            Assert.Empty(settings.PinnedDevices);
        }

        [Fact]
        public void AppSettings_Clone为深拷贝且保留大小写不敏感别名表()
        {
            var settings = new AppSettings();
            settings.DeviceAliases["Mouse-01"] = "主力鼠标";
            settings.IgnoredDevices.Add("dev-x");

            var clone = settings.Clone();
            clone.DeviceAliases["mouse-01"] = "改名后";
            clone.IgnoredDevices.Add("dev-y");

            Assert.Equal("主力鼠标", settings.DeviceAliases["Mouse-01"]);
            Assert.Single(settings.IgnoredDevices);
            // 别名表写入后仍应大小写不敏感
            Assert.True(clone.DeviceAliases.ContainsKey("MOUSE-01"));
        }

        [Fact]
        public void SettingsStore_保存后回读一致并保留中文字段内容()
        {
            string dir = TestPaths.NewTempDir("store-roundtrip");
            try
            {
                string path = Path.Combine(dir, "settings.json");
                var store = new SettingsStore(path);
                var settings = new AppSettings();
                settings.Theme = "light";
                settings.WidgetSize = "large";
                settings.LowBatteryThreshold = 15;
                settings.AlertCooldownMinutes = 45;
                settings.DeviceAliases["bt-endpoint-1"] = "耳机";

                store.Save(settings);

                Assert.True(File.Exists(path));
                string json = File.ReadAllText(path, Encoding.UTF8);
                Assert.Contains("\"Theme\":\"light\"", json);   // PascalCase 字段名保持兼容
                Assert.Contains("\"WidgetSize\":\"large\"", json);
                Assert.Contains("耳机", json);                  // 中文不转义为 \uXXXX

                var reloaded = new SettingsStore(path);
                reloaded.Load();
                Assert.Equal("light", reloaded.Current.Theme);
                Assert.Equal("large", reloaded.Current.WidgetSize);
                Assert.Equal(15, reloaded.Current.LowBatteryThreshold);
                Assert.Equal(45, reloaded.Current.AlertCooldownMinutes);
                Assert.Equal("耳机", reloaded.Current.DeviceAliases["BT-ENDPOINT-1"]);
            }
            finally
            {
                TestPaths.TryDelete(dir);
            }
        }

        [Fact]
        public void SettingsStore_可读取旧版带BOM且字段名小写的设置文件()
        {
            string dir = TestPaths.NewTempDir("store-legacy");
            try
            {
                string path = Path.Combine(dir, "settings.json");
                // 模拟 .NET Framework + JavaScriptSerializer 时期写出、且被手工改成小写字段名的文件
                string legacyJson = "{\"theme\":\"light\",\"widgetsize\":\"small\",\"lowbatterythreshold\":12,"
                                    + "\"alertcooldownminutes\":25,\"showstatusdot\":false}";
                File.WriteAllText(path, legacyJson, new UTF8Encoding(false));

                var store = new SettingsStore(path);
                store.Load();

                Assert.Equal("light", store.Current.Theme);
                Assert.Equal("small", store.Current.WidgetSize);
                Assert.Equal(12, store.Current.LowBatteryThreshold);
                Assert.Equal(25, store.Current.AlertCooldownMinutes);
                Assert.False(store.Current.ShowStatusDot);
            }
            finally
            {
                TestPaths.TryDelete(dir);
            }
        }
    }
}
