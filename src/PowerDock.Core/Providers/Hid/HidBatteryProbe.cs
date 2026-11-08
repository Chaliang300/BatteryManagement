using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using PowerDock.Core.Common;
using PowerDock.Core.Diagnostics;

namespace PowerDock.Core.Providers.Hid
{
    /// <summary>一次探测结果：百分比或不可用原因，绝不把 null 写成 0。</summary>
    public sealed class HidBatteryProbeResult
    {
        public int? Percent { get; set; }
        public PercentUnavailableReason Reason { get; set; }
        public string Detail { get; set; }
        public IList<HidBatteryCapability> Capabilities { get; set; }

        public HidBatteryProbeResult()
        {
            Reason = PercentUnavailableReason.NotProbed;
            Detail = string.Empty;
            Capabilities = new List<HidBatteryCapability>();
        }

        public static HidBatteryProbeResult Unavailable(PercentUnavailableReason reason, string detail)
        {
            return new HidBatteryProbeResult { Reason = reason, Detail = detail ?? string.Empty };
        }
    }

    /// <summary>
    /// 标准 HID 电量探测。只读，且严格遵守 C8：
    /// 1. 必须枚举 Input 与 Feature 两边的 HIDP_VALUE_CAPS 找 0x06:0x20，顶层集合未命中不算缺电量；
    /// 2. Feature 优先读取，读不到再退回 Input 报告；
    /// 3. 每次调用都在内部串行化（同一接口同时只允许一个读取会话）；
    /// 4. 打开/读取失败按 AccessDenied / Timeout 分类，设备条目保留。
    /// </summary>
    public sealed class HidBatteryProbe
    {
        private readonly object _sync = new object();

        /// <summary>探测指定接口路径。同一实例的并发调用会排队，不会产生重叠读取。</summary>
        public HidBatteryProbeResult Probe(string interfacePath, TimeSpan timeout)
        {
            if (string.IsNullOrEmpty(interfacePath))
            {
                return HidBatteryProbeResult.Unavailable(PercentUnavailableReason.NotProbed, "empty interface path");
            }

            if (!Monitor.TryEnter(_sync, timeout))
            {
                return HidBatteryProbeResult.Unavailable(PercentUnavailableReason.Timeout, "probe busy");
            }

            IntPtr handle = IntPtr.Zero;
            IntPtr preparsed = IntPtr.Zero;
            try
            {
                handle = HidInterop.CreateFile(
                    interfacePath,
                    HidInterop.GENERIC_READ,
                    HidInterop.FILE_SHARE_READ | HidInterop.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    HidInterop.OPEN_EXISTING,
                    HidInterop.FILE_ATTRIBUTE_NORMAL,
                    IntPtr.Zero);

                if (handle == new IntPtr(-1))
                {
                    int error = Marshal.GetLastWin32Error();
                    return HidBatteryProbeResult.Unavailable(
                        PercentUnavailableReason.AccessDenied,
                        "CreateFile failed win32=" + error);
                }

                if (!HidInterop.HidD_GetPreparsedData(handle, out preparsed) || preparsed == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    return HidBatteryProbeResult.Unavailable(
                        PercentUnavailableReason.AccessDenied,
                        "HidD_GetPreparsedData failed win32=" + error);
                }

                var caps = new HidInterop.HIDP_CAPS();
                if (HidInterop.HidP_GetCaps(preparsed, ref caps) != HidInterop.HIDP_STATUS_SUCCESS)
                {
                    return HidBatteryProbeResult.Unavailable(PercentUnavailableReason.NoStandardBatteryUsage, "HidP_GetCaps failed");
                }

                var capabilities = new List<HidBatteryCapability>();
                CollectValueCaps(preparsed, HidInterop.HIDP_INPUT, false, capabilities);
                CollectValueCaps(preparsed, HidInterop.HIDP_FEATURE, true, capabilities);

                IList<HidBatteryCapability> battery = HidBatterySelector.SelectBatteryCapabilities(capabilities);
                if (battery.Count == 0)
                {
                    return new HidBatteryProbeResult
                    {
                        Reason = PercentUnavailableReason.NoStandardBatteryUsage,
                        Detail = "no value cap with usage 0x06:0x20 (input+feature scanned); top-level caps usage=0x" +
                                 caps.UsagePage.ToString("X4") + ":0x" + caps.Usage.ToString("X4"),
                        Capabilities = capabilities
                    };
                }

                var failures = new List<string>();
                foreach (HidBatteryCapability capability in battery)
                {
                    int percent;
                    string failure;
                    if (TryRead(handle, preparsed, caps, capability, timeout, out percent, out failure))
                    {
                        return new HidBatteryProbeResult
                        {
                            Percent = percent,
                            Reason = PercentUnavailableReason.None,
                            Detail = "read " + capability.Signature() + " -> " + percent + "%",
                            Capabilities = capabilities
                        };
                    }
                    failures.Add(failure);
                }

                PercentUnavailableReason classified = ClassifyFailures(failures);
                return new HidBatteryProbeResult
                {
                    Reason = classified,
                    Detail = string.Join("; ", failures.ToArray()),
                    Capabilities = capabilities
                };
            }
            catch (Exception ex)
            {
                AppLogger.Error("HID battery probe threw for " + Redact(interfacePath), ex);
                return HidBatteryProbeResult.Unavailable(PercentUnavailableReason.Timeout, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (preparsed != IntPtr.Zero)
                {
                    try { HidInterop.HidD_FreePreparsedData(preparsed); } catch { }
                }
                if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                {
                    try { HidInterop.CloseHandle(handle); } catch { }
                }
                Monitor.Exit(_sync);
            }
        }

        private static void CollectValueCaps(IntPtr preparsed, int reportType, bool isFeature, List<HidBatteryCapability> sink)
        {
            ushort length = 0;
            int status = HidInterop.HidP_GetValueCaps(reportType, null, ref length, preparsed);
            if (length == 0) return;
            if (status != HidInterop.HIDP_STATUS_SUCCESS && status != HidInterop.HIDP_STATUS_BUFFER_TOO_SMALL)
            {
                // 没有该类型值能力属于正常情况，不记为设备失败。
                return;
            }

            var buffer = new HidInterop.HIDP_VALUE_CAPS[length];
            ushort actual = length;
            status = HidInterop.HidP_GetValueCaps(reportType, buffer, ref actual, preparsed);
            if (status != HidInterop.HIDP_STATUS_SUCCESS) return;

            for (int i = 0; i < actual; i++)
            {
                HidInterop.HIDP_VALUE_CAPS caps = buffer[i];

                // HIDP_VALUE_CAPS 的末尾联合体里，NotRange 形态下 UsageMin 就是 Usage；
                // Range 形态下的电量几乎不存在，按 UsageMin 记录并在诊断里可见。
                ushort usage = caps.UsageMin;
                ushort usagePage = caps.UsagePage;
                int logicalMin = caps.LogicalMin;
                int logicalMax = caps.LogicalMax;
                if (logicalMax <= logicalMin) continue;
                if (caps.BitSize == 0 || caps.BitSize > 32) continue;

                try
                {
                    sink.Add(new HidBatteryCapability(
                        isFeature,
                        caps.ReportID,
                        usagePage,
                        usage,
                        caps.LinkCollection,
                        caps.LinkUsagePage,
                        caps.LinkUsage,
                        caps.BitSize,
                        (uint)logicalMin,
                        (uint)logicalMax,
                        caps.IsAbsolute,
                        caps.HasNull));
                }
                catch (ArgumentException)
                {
                    // 逻辑范围非法：该集合不可解析，跳过但保留其余集合。
                }
            }
        }

        private static bool TryRead(
            IntPtr handle, IntPtr preparsed, HidInterop.HIDP_CAPS caps,
            HidBatteryCapability capability, TimeSpan timeout,
            out int percent, out string failure)
        {
            percent = 0;
            failure = capability.Signature();

            int reportLength = capability.IsFeatureReport
                ? caps.FeatureReportByteLength
                : caps.InputReportByteLength;
            if (reportLength <= 0)
            {
                failure += ": report length 0";
                return false;
            }

            var buffer = new byte[reportLength];
            if (capability.ReportId != 0) buffer[0] = capability.ReportId;

            bool ok = capability.IsFeatureReport
                ? HidInterop.HidD_GetFeature(handle, buffer, buffer.Length)
                : HidInterop.HidD_GetInputReport(handle, buffer, buffer.Length);

            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                failure += ": read failed win32=" + error;
                return false;
            }

            uint raw;
            int hidStatus = HidInterop.HidP_GetUsageValue(
                capability.IsFeatureReport ? HidInterop.HIDP_FEATURE : HidInterop.HIDP_INPUT,
                capability.UsagePage,
                capability.LinkCollection,
                capability.Usage,
                out raw,
                preparsed,
                buffer,
                buffer.Length);

            if (hidStatus != HidInterop.HIDP_STATUS_SUCCESS)
            {
                // HidP 取不到时退回按位宽自解；两条路径都不成立才算失败。
                if (!HidBatterySelector.TryExtractRawValue(capability, buffer, out raw))
                {
                    failure += ": HidP_GetUsageValue=0x" + hidStatus.ToString("X8");
                    return false;
                }
            }

            percent = HidBatterySelector.NormalizePercent(capability, raw);
            return true;
        }

        private static PercentUnavailableReason ClassifyFailures(List<string> failures)
        {
            foreach (string failure in failures)
            {
                if (failure.IndexOf("win32=5", StringComparison.Ordinal) >= 0 ||
                    failure.IndexOf("win32=32", StringComparison.Ordinal) >= 0)
                {
                    return PercentUnavailableReason.AccessDenied;
                }
            }
            foreach (string failure in failures)
            {
                if (failure.IndexOf("report length 0", StringComparison.Ordinal) >= 0)
                {
                    return PercentUnavailableReason.NoStandardBatteryUsage;
                }
            }
            return PercentUnavailableReason.Timeout;
        }

        /// <summary>诊断与日志一律脱敏实例路径里的序列号段（\\\\?\\hid#vid_xxxx&amp;pid_xxxx#&lt;serial&gt;）。</summary>
        internal static string Redact(string interfacePath)
        {
            if (string.IsNullOrEmpty(interfacePath)) return string.Empty;
            string[] parts = interfacePath.Split('#');
            if (parts.Length < 3) return interfacePath;
            parts[2] = "***";
            return string.Join("#", parts);
        }
    }
}
