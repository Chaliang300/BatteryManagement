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
        SourceUnavailable
    }

    public sealed class ProviderUpdate : EventArgs
    {
        public UpdateKind Kind { get; private set; }
        public ReadingKey Key { get; private set; }
        public BatteryReading Reading { get; private set; }
        public ConnectionState Connection { get; private set; }
        public string Message { get; private set; }

        public ProviderUpdate(UpdateKind kind, ReadingKey key, BatteryReading reading, ConnectionState connection, string message)
        {
            Kind = kind;
            Key = key;
            Reading = reading;
            Connection = connection;
            Message = message;
        }

        public ProviderUpdate(UpdateKind kind, ReadingKey key, BatteryReading reading, ConnectionState connection)
            : this(kind, key, reading, connection, null)
        {
        }
    }

    public interface IDeviceProvider : IDisposable
    {
        string ProviderId { get; }
        Task DiscoverAsync(CancellationToken ct);
        Task RefreshAsync(ReadingKey key, CancellationToken ct);
        event EventHandler<ProviderUpdate> Updated;
    }
}
