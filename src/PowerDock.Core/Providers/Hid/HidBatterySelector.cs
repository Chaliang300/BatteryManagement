using System;
using System.Collections.Generic;
using System.Globalization;

namespace PowerDock.Core.Providers.Hid
{
    /// <summary>
    /// 一个 HID 值能力（HIDP_VALUE_CAPS 的可测投影）。只保留判定所需字段，
    /// 便于脱离真实句柄做单测；C8 要求必须在 Input/Feature 的值能力里查 0x06:0x20。
    /// UsagePage/Usage 指的是**该值本身**的用途，LinkUsagePage/LinkUsage 是其所在链接集合的用途，
    /// 两者不可混用：电量常见于 0x06 链接集合下，但也可能挂在别的集合里。
    /// </summary>
    public sealed class HidBatteryCapability
    {
        public bool IsFeatureReport { get; private set; }
        public byte ReportId { get; private set; }
        public ushort UsagePage { get; private set; }
        public ushort Usage { get; private set; }
        public ushort LinkCollection { get; private set; }
        public ushort LinkUsagePage { get; private set; }
        public ushort LinkUsage { get; private set; }
        public ushort BitSize { get; private set; }
        public uint LogicalMin { get; private set; }
        public uint LogicalMax { get; private set; }
        public bool IsAbsolute { get; private set; }
        public bool HasNull { get; private set; }

        public HidBatteryCapability(
            bool isFeatureReport, byte reportId, ushort usagePage, ushort usage,
            ushort linkCollection, ushort linkUsagePage, ushort linkUsage,
            ushort bitSize, uint logicalMin, uint logicalMax, bool isAbsolute, bool hasNull)
        {
            if (logicalMax <= logicalMin)
            {
                throw new ArgumentException("电量逻辑范围必须 max > min，否则无法归一化。", "logicalMax");
            }
            if (bitSize == 0 || bitSize > 32)
            {
                throw new ArgumentOutOfRangeException("bitSize", "电量位宽必须在 1–32 之间。");
            }

            IsFeatureReport = isFeatureReport;
            ReportId = reportId;
            UsagePage = usagePage;
            Usage = usage;
            LinkCollection = linkCollection;
            LinkUsagePage = linkUsagePage;
            LinkUsage = linkUsage;
            BitSize = bitSize;
            LogicalMin = logicalMin;
            LogicalMax = logicalMax;
            IsAbsolute = isAbsolute;
            HasNull = hasNull;
        }

        /// <summary>
        /// ReadingKey.BatteryId 的稳定签名。同一物理设备可以有多个电量集合（复合接口），
        /// 它们是不同采集端点，必须各自成键而不是互相覆盖。
        /// </summary>
        public string Signature()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}:rid{1}:lc{2}:up{3:X4}:u{4:X4}",
                IsFeatureReport ? "feature" : "input",
                ReportId,
                LinkCollection,
                UsagePage,
                Usage);
        }

        public override string ToString()
        {
            return Signature();
        }
    }

    /// <summary>
    /// 标准 HID 电量解析（纯逻辑）。约束：
    /// 1. 只认 UsagePage=0x0006 / Usage=0x0020（Battery Strength）；
    /// 2. 按报告 ID 选择报告体（ReportID 为 0 时整个缓冲区就是报告体）；
    /// 3. 按逻辑范围线性归一化并夹紧到 0–100，不猜厂商怪值；
    /// 4. 真实 0% 是有效读数；取不到值返回 false 并给出原因，绝不用 0 占位。
    /// </summary>
    public static class HidBatterySelector
    {
        public const ushort BatteryUsagePage = 0x0006;
        public const ushort BatteryUsage = 0x0020;

        public static IList<HidBatteryCapability> SelectBatteryCapabilities(IEnumerable<HidBatteryCapability> capabilities)
        {
            var result = new List<HidBatteryCapability>();
            if (capabilities == null) return result;

            foreach (HidBatteryCapability capability in capabilities)
            {
                if (capability.UsagePage == BatteryUsagePage && capability.Usage == BatteryUsage)
                {
                    result.Add(capability);
                }
            }

            // Feature 优先：读 Feature 不消费设备输入流，不干扰真实鼠标/键盘操作。
            result.Sort(delegate(HidBatteryCapability a, HidBatteryCapability b)
            {
                if (a.IsFeatureReport != b.IsFeatureReport) return a.IsFeatureReport ? -1 : 1;
                int byReport = a.ReportId.CompareTo(b.ReportId);
                if (byReport != 0) return byReport;
                return a.LinkCollection.CompareTo(b.LinkCollection);
            });
            return result;
        }

        public static int NormalizePercent(HidBatteryCapability capability, uint rawValue)
        {
            if (capability == null) throw new ArgumentNullException("capability");

            double span = (double)capability.LogicalMax - capability.LogicalMin;
            double value = rawValue;
            if (value < capability.LogicalMin) value = capability.LogicalMin;
            if (value > capability.LogicalMax) value = capability.LogicalMax;

            double normalized = (value - capability.LogicalMin) / span;
            int computed = (int)Math.Round(normalized * 100.0, MidpointRounding.AwayFromZero);
            if (computed < 0) computed = 0;
            if (computed > 100) computed = 100;
            return computed;
        }

        /// <summary>切到目标 ReportID 的报告体；报告长度不足或 ID 不匹配时返回 false。</summary>
        public static bool TryGetReportBody(HidBatteryCapability capability, byte[] report, out int offset, out int length)
        {
            offset = 0;
            length = 0;
            if (capability == null) throw new ArgumentNullException("capability");
            if (report == null || report.Length == 0) return false;

            if (capability.ReportId == 0)
            {
                length = report.Length;
                return true;
            }

            if (report[0] != capability.ReportId || report.Length < 2) return false;
            offset = 1;
            length = report.Length - 1;
            return true;
        }

        /// <summary>按位宽从报告体小端解出无符号原始值。多值共处一份报告时调用方需先按 BitField 偏移切分。</summary>
        public static bool TryExtractRawValue(HidBatteryCapability capability, byte[] report, out uint rawValue)
        {
            rawValue = 0;
            int offset;
            int length;
            if (!TryGetReportBody(capability, report, out offset, out length)) return false;

            int bitSize = capability.BitSize;
            int byteCount = (bitSize + 7) / 8;
            if (length < byteCount) return false;

            uint value = 0;
            for (int i = 0; i < byteCount; i++)
            {
                value |= (uint)report[offset + i] << (8 * i);
            }

            if (bitSize < 32)
            {
                uint mask = (1u << bitSize) - 1u;
                value &= mask;
            }

            rawValue = value;
            return true;
        }

        /// <summary>把一次完整读取（原始值 + 能力）转成百分比；失败时 reason 说明为什么读不到。</summary>
        public static bool TryResolvePercent(HidBatteryCapability capability, byte[] report, out int percent)
        {
            percent = 0;
            uint raw;
            if (!TryExtractRawValue(capability, report, out raw)) return false;
            percent = NormalizePercent(capability, raw);
            return true;
        }
    }
}
