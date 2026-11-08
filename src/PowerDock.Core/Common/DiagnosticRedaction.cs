using System;

namespace PowerDock.Core.Common
{
    /// <summary>
    /// 诊断输出的脱敏工具。对外提交的诊断证据不得包含设备序列号、蓝牙地址等标识；
    /// 这里只保留「同一标识可比对」的哈希或短尾，便于复现问题而不泄露硬件身份。
    /// </summary>
    public static class DiagnosticRedaction
    {
        /// <summary>
        /// HID 设备接口路径形如 \\?\hid#vid_xxxx&amp;pid_xxxx#&lt;serial&gt;#{guid}，
        /// 第三段是序列号，必须整体替换；其余段保留以便定位型号与接口。
        /// </summary>
        public static string HidInterfacePath(string interfacePath)
        {
            if (string.IsNullOrEmpty(interfacePath)) return string.Empty;
            string[] parts = interfacePath.Split('#');
            if (parts.Length < 3) return interfacePath;
            parts[2] = "***";
            return string.Join("#", parts);
        }

        /// <summary>
        /// 蓝牙设备 ID 形如 BluetoothLE\Device\Bluetooth#Bluetooth&lt;mac&gt;-&lt;name&gt;，
        /// 只保留末尾最多 4 个字符用于区分同批设备，其余丢弃。
        /// </summary>
        public static string BluetoothId(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            int separator = value.LastIndexOf('-');
            string tail = separator >= 0 && value.Length - separator - 1 <= 4
                ? value.Substring(separator + 1)
                : string.Empty;
            return "***" + tail;
        }

        /// <summary>短哈希：用于把「同一个未知标识」在诊断里关联起来，同时不暴露原值。</summary>
        public static string ShortHash(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return "h" + (uint)value.GetHashCode();
        }
    }
}
