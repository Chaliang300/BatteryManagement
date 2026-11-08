using System;
using PowerDock.Core.Common;

namespace PowerDock.Core.State
{
    /// <summary>
    /// 同一物理设备可能被多条链路枚举（例：BLE 键盘既走 0x180F，又通过 HOGP 暴露 HID 接口）。
    /// 归并到同一 DeviceKey 后，链路信息必须显式裁决，而不是「谁最后上报谁赢」：
    /// 1. 有外设级在线证据的蓝牙优先于 HID-over-GATT 暴露的 USB 总线端点；
    /// 2. 明确的有线端点优先于「仅知在 USB 总线上」；
    /// 3. 只识别到接收器时不得覆盖已知更具体的链路；
    /// 4. 同等级时保留先到者，避免状态抖动。
    /// </summary>
    public static class LinkArbiter
    {
        public static int Rank(LinkInfo link)
        {
            if (link == null) return 0;
            switch (link.Kind)
            {
                case LinkKind.Bluetooth:
                    // 已连接的蓝牙是外设级证据；已配对但断开仍然比「仅知总线」更有信息量。
                    return link.IsConnected ? 60 : 40;
                case LinkKind.Internal:
                    return 55;
                case LinkKind.Wireless24G:
                    return link.IsReceiverOnly ? 20 : 50;
                case LinkKind.UsbWired:
                    return link.IsConnected ? 45 : 10;
                default:
                    return link.IsConnected ? 30 : 5;
            }
        }

        /// <summary>选出应保留的链路。incoming 为 null 时保留 existing。</summary>
        public static LinkInfo Choose(LinkInfo existing, LinkInfo incoming)
        {
            if (incoming == null) return existing;
            if (existing == null) return incoming;
            return Rank(incoming) > Rank(existing) ? incoming : existing;
        }
    }
}
