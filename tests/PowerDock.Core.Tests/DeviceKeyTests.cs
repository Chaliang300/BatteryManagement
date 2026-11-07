using System;
using PowerDock.Core.Common;
using Xunit;

namespace PowerDock.Core.Tests
{
    /// <summary>
    /// 聚合键语义：D17 要求物理设备键与来源键在比较与哈希上都大小写不敏感，
    /// 否则同一设备从不同来源上报时会被拆成两台，或去重时漏判。
    /// </summary>
    public sealed class DeviceKeyTests
    {
        [Fact]
        public void DeviceKey_大小写不同但相等且哈希一致()
        {
            var lower = new DeviceKey("dev-001");
            var upper = new DeviceKey("DEV-001");

            Assert.True(lower.Equals(upper));
            Assert.True(upper.Equals(lower));
            Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
            // 引用类型未重载 operator ==，判等一律走 Equals
            Assert.True(lower.Equals(new DeviceKey("dev-001")));
        }

        [Fact]
        public void DeviceKey_不同物理键不相等()
        {
            Assert.NotEqual(new DeviceKey("dev-001"), new DeviceKey("dev-002"));
            Assert.False(new DeviceKey("dev-001").Equals(null));
        }

        [Fact]
        public void ReadingKey_三段各自大小写不敏感()
        {
            var a = new ReadingKey("wmi", "endpoint-1", "bat-0");
            var b = new ReadingKey("WMI", "ENDPOINT-1", "BAT-0");
            var c = new ReadingKey("WMI", "ENDPOINT-1", "BAT-1");

            Assert.True(a.Equals(b));
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.False(a.Equals(c));
        }

        [Fact]
        public void ReadingKey_空值归一为空字符串且ToString稳定()
        {
            var key = new ReadingKey(null, null, null);

            Assert.Equal(string.Empty, key.ProviderId);
            Assert.Equal("::", key.ToString());
        }
    }
}
