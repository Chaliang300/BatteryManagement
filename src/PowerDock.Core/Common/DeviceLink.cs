using System;

namespace PowerDock.Core.Common
{
    /// <summary>
    /// 设备接入方式。只描述「这个端点实际走哪条链路」，不由 VID/PID、名称或型号推导。
    /// 无法确认时用 <see cref="Unknown"/>，界面按「USB 设备，连接方式待确认」呈现，不得猜成有线或 2.4G。
    /// </summary>
    public enum LinkKind
    {
        Unknown = 0,
        Bluetooth = 1,
        Wireless24G = 2,
        UsbWired = 3,
        Internal = 4
    }

    /// <summary>
    /// 电量不可用的原因。只要百分比为 null 就必须给出原因，界面据此显示「—」并注明为何读不到。
    /// </summary>
    public enum PercentUnavailableReason
    {
        None = 0,
        NotProbed = 1,
        NoStandardBatteryUsage = 2,
        NoBatteryService = 3,
        AccessDenied = 4,
        Timeout = 5,
        SourceDisabled = 6,
        VendorUnsupported = 7
    }

    /// <summary>
    /// 物理设备身份。Key 一旦确定必须稳定：优先 Windows 容器 ID，其次可验证的厂商稳定 ID，
    /// 最后才退回「按端点派生」的临时键。临时键必须在 <see cref="IsContainerVerified"/> 上如实标 false，
    /// 因为端点路径会随换口/重连变化，不能作为持久身份。
    /// 名称、型号、VID/PID 相同均不构成合并依据（D17）。
    /// </summary>
    public sealed class DeviceIdentity
    {
        public DeviceKey Key { get; private set; }
        public bool IsContainerVerified { get; private set; }
        public string ContainerId { get; private set; }
        public string InstancePath { get; private set; }
        public string VendorId { get; private set; }
        public string ProductId { get; private set; }

        private DeviceIdentity(DeviceKey key, bool verified, string containerId, string instancePath, string vendorId, string productId)
        {
            Key = key;
            IsContainerVerified = verified;
            ContainerId = containerId ?? string.Empty;
            InstancePath = instancePath ?? string.Empty;
            VendorId = vendorId ?? string.Empty;
            ProductId = productId ?? string.Empty;
        }

        /// <summary>用容器 ID 建立可信身份；containerId 为空时抛异常，调用方应改用 <see cref="FromFallback"/>。</summary>
        public static DeviceIdentity FromContainer(string containerId, string instancePath, string vendorId, string productId)
        {
            if (string.IsNullOrWhiteSpace(containerId))
            {
                throw new ArgumentException("容器 ID 为空时不能建立可信身份，请使用 FromFallback。", "containerId");
            }

            string normalized = NormalizeContainer(containerId);
            return new DeviceIdentity(new DeviceKey("container:" + normalized), true, containerId, instancePath, vendorId, productId);
        }

        /// <summary>
        /// 无容器 ID 时的临时身份。键由采集方命名空间 + 稳定端点片段组成，保证同一端点重扫不漂移，
        /// 但换口后可能变成另一台；界面与诊断必须显示「身份未确认」。
        /// </summary>
        public static DeviceIdentity FromFallback(string providerNamespace, string endpointId, string instancePath, string vendorId, string productId)
        {
            if (string.IsNullOrWhiteSpace(providerNamespace))
            {
                throw new ArgumentException("providerNamespace 不能为空。", "providerNamespace");
            }
            if (string.IsNullOrWhiteSpace(endpointId))
            {
                throw new ArgumentException("endpointId 不能为空。", "endpointId");
            }

            string value = "endpoint:" + providerNamespace.Trim().ToLowerInvariant() + ":" + NormalizeEndpoint(endpointId);
            return new DeviceIdentity(new DeviceKey(value), false, null, instancePath, vendorId, productId);
        }

        private static string NormalizeContainer(string containerId)
        {
            return containerId.Trim().Trim('{', '}').ToLowerInvariant();
        }

        private static string NormalizeEndpoint(string endpointId)
        {
            return endpointId.Trim().ToLowerInvariant();
        }

        public override string ToString()
        {
            return Key.ToString();
        }
    }

    /// <summary>
    /// 链路状态。四个判断彼此独立、不得互相推导：
    /// 配对状态（<see cref="IsPaired"/>）、接收器/接口在场（<see cref="IsConnected"/> 的「在场」部分）、
    /// 外设是否在线（由三态与在线证据表达）、电量是否可读（读数上的 <see cref="PercentUnavailableReason"/>）。
    /// 任意一个为真都不代表其余为真。
    /// </summary>
    public sealed class LinkInfo
    {
        public LinkKind Kind { get; private set; }
        public string Evidence { get; private set; }
        public bool IsPaired { get; private set; }
        public bool IsConnected { get; private set; }
        public bool IsReceiverOnly { get; private set; }
        public DateTime? LastSeenUtc { get; private set; }

        public LinkInfo(LinkKind kind, string evidence, bool isPaired, bool isConnected, bool isReceiverOnly, DateTime? lastSeenUtc)
        {
            Kind = kind;
            Evidence = evidence ?? string.Empty;
            IsPaired = isPaired;
            IsConnected = isConnected;
            IsReceiverOnly = isReceiverOnly;
            LastSeenUtc = lastSeenUtc;
        }

        public static LinkInfo Internal(string evidence, bool connected)
        {
            return new LinkInfo(LinkKind.Internal, evidence, false, connected, false, DateTime.UtcNow);
        }

        /// <summary>
        /// 面向界面的链路文案。只根据已有证据说话：只识别到接收器时不得宣称外设在线，
        /// 只知道在 USB 总线上时不得宣称有线。
        /// </summary>
        public string Describe()
        {
            switch (Kind)
            {
                case LinkKind.Bluetooth:
                    if (!IsPaired) return "蓝牙设备（未配对）";
                    return IsConnected ? "蓝牙 GATT" : "蓝牙（已配对，未连接）";
                case LinkKind.Wireless24G:
                    return IsReceiverOnly ? "接收器已连接，外设状态未知" : "2.4G";
                case LinkKind.UsbWired:
                    return IsConnected ? "USB 有线" : "USB 有线（已断开）";
                case LinkKind.Internal:
                    return "本机电池";
                default:
                    return IsConnected ? "USB 设备，连接方式待确认" : "连接方式未知";
            }
        }
    }

    /// <summary>
    /// 采集端上报的一次设备视图：身份 + 链路 + 可选读数。百分比为 null 时 <see cref="PercentReason"/> 必须非 None，
    /// 真实 0% 是有效读数，不得当成缺失。Service 层不猜链路、不猜电量来源。
    /// </summary>
    public sealed class ProviderDevice
    {
        public DeviceIdentity Identity { get; private set; }
        public LinkInfo Link { get; private set; }
        public ReadingKey Key { get; private set; }
        public string DisplayName { get; private set; }
        public DeviceCategory Category { get; private set; }
        public BatterySource Source { get; private set; }
        public int? Percent { get; private set; }
        public bool? Charging { get; private set; }
        public PercentUnavailableReason PercentReason { get; private set; }
        public DateTime TsUtc { get; private set; }
        public string Detail { get; private set; }

        public ProviderDevice(
            DeviceIdentity identity,
            LinkInfo link,
            ReadingKey key,
            string displayName,
            DeviceCategory category,
            BatterySource source,
            int? percent,
            bool? charging,
            PercentUnavailableReason percentReason,
            DateTime tsUtc,
            string detail)
        {
            if (identity == null) throw new ArgumentNullException("identity");
            if (link == null) throw new ArgumentNullException("link");
            if (key == null) throw new ArgumentNullException("key");
            if (percent.HasValue && (percent.Value < 0 || percent.Value > 100))
            {
                throw new ArgumentOutOfRangeException("percent", "电量只接受 0–100 或 null。");
            }
            if (!percent.HasValue && percentReason == PercentUnavailableReason.None)
            {
                throw new ArgumentException("百分比为 null 时必须给出不可用原因。", "percentReason");
            }

            Identity = identity;
            Link = link;
            Key = key;
            DisplayName = displayName ?? "未知设备";
            Category = category;
            Source = source;
            Percent = percent;
            Charging = charging;
            PercentReason = percentReason;
            TsUtc = tsUtc;
            Detail = detail ?? string.Empty;
        }

        public static ProviderDevice Unreadable(
            DeviceIdentity identity,
            LinkInfo link,
            ReadingKey key,
            string displayName,
            DeviceCategory category,
            BatterySource source,
            PercentUnavailableReason reason,
            string detail)
        {
            return new ProviderDevice(identity, link, key, displayName, category, source, null, null, reason, DateTime.UtcNow, detail);
        }
    }

    /// <summary>把枚举转成稳定的英文诊断键，日志与 --diagnose-read 输出不得依赖本地化文案。</summary>
    public static class ContractText
    {
        public static string Key(LinkKind kind)
        {
            switch (kind)
            {
                case LinkKind.Bluetooth: return "bluetooth";
                case LinkKind.Wireless24G: return "wireless_2_4g";
                case LinkKind.UsbWired: return "usb_wired";
                case LinkKind.Internal: return "internal";
                default: return "unknown";
            }
        }

        public static string Key(PercentUnavailableReason reason)
        {
            switch (reason)
            {
                case PercentUnavailableReason.None: return "none";
                case PercentUnavailableReason.NotProbed: return "not_probed";
                case PercentUnavailableReason.NoStandardBatteryUsage: return "no_standard_battery_usage";
                case PercentUnavailableReason.NoBatteryService: return "no_battery_service";
                case PercentUnavailableReason.AccessDenied: return "access_denied";
                case PercentUnavailableReason.Timeout: return "timeout";
                case PercentUnavailableReason.SourceDisabled: return "source_disabled";
                case PercentUnavailableReason.VendorUnsupported: return "vendor_unsupported";
                default: return "unknown";
            }
        }

        public static string Key(ConnectionState state)
        {
            switch (state)
            {
                case ConnectionState.Online: return "online";
                case ConnectionState.Offline: return "offline";
                default: return "unknown";
            }
        }
    }
}
