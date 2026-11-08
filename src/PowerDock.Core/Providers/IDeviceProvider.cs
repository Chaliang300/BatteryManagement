using System;
using System.Threading;
using System.Threading.Tasks;
using PowerDock.Core.Common;

namespace PowerDock.Core.Providers
{
    public enum UpdateKind
    {
        Discovered,
        ConnectionChanged,
        ReadingUpdated,
        ReadingFailed,
        SourceUnavailable,
        Removed
    }

    /// <summary>
    /// 采集事件。约定：
    /// 1. Device 是本次事件的完整设备视图（身份、链路、读数、不可用原因），Service 不做二次推断；
    /// 2. 发现事件允许 Percent=null 且 reason=NotProbed，表示「设备在、还没探测」；
    /// 3. 同一来源同一端点的刷新不得重叠，调用方需在会话失效后丢弃迟到回调；
    /// 4. Removed 只表示设备已离开总线或取消配对，不得用来表达「读失败」。
    /// </summary>
    public sealed class ProviderUpdate : EventArgs
    {
        public UpdateKind Kind { get; private set; }
        public ReadingKey Key { get; private set; }
        public ProviderDevice Device { get; private set; }
        public ConnectionState Connection { get; private set; }
        public string Message { get; private set; }

        public ProviderUpdate(UpdateKind kind, ReadingKey key, ProviderDevice device, ConnectionState connection, string message)
        {
            if (key == null) throw new ArgumentNullException("key");
            Kind = kind;
            Key = key;
            Device = device;
            Connection = connection;
            Message = message ?? string.Empty;
        }

        public ProviderUpdate(UpdateKind kind, ReadingKey key, ProviderDevice device, ConnectionState connection)
            : this(kind, key, device, connection, null)
        {
        }
    }

    public interface IDeviceProvider : IDisposable
    {
        string ProviderId { get; }

        /// <summary>
        /// 枚举当前可达设备（含「已配对但未连接」）。实现必须可重复调用：重复发现不得产生第二条设备条目。
        /// </summary>
        Task DiscoverAsync(CancellationToken ct);

        /// <summary>
        /// 刷新单个读端点。实现必须串行化同一 Key 的调用；超时与取消必须映射为 ReadingFailed 而不是静默丢弃。
        /// </summary>
        Task RefreshAsync(ReadingKey key, CancellationToken ct);

        event EventHandler<ProviderUpdate> Updated;
    }
}
