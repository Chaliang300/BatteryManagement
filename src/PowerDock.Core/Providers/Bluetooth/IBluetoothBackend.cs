using System;
using PowerDock.Core.Common;

namespace PowerDock.Core.Providers.Bluetooth
{
    /// <summary>
    /// 一次 BLE 电量读取的结果。百分比为 null 时必须给出原因，调用方据此显示「—」。
    /// SupportsNotify 只表示特征支持订阅，不代表本轮已经读到值。
    /// </summary>
    public sealed class BleBatteryReadResult
    {
        public int? Percent { get; set; }
        public PercentUnavailableReason Reason { get; set; }
        public string Detail { get; set; }
        public bool SupportsNotify { get; set; }

        public BleBatteryReadResult()
        {
            Reason = PercentUnavailableReason.NotProbed;
            Detail = string.Empty;
        }

        public static BleBatteryReadResult Unavailable(PercentUnavailableReason reason, string detail)
        {
            return new BleBatteryReadResult { Reason = reason, Detail = detail ?? string.Empty };
        }
    }

    /// <summary>已配对 BLE 设备的最小状态快照。配对与连接分开表达，不得互相推导。</summary>
    public sealed class BluetoothDeviceSnapshot
    {
        public string DeviceId { get; set; }
        public string Name { get; set; }
        public bool IsPaired { get; set; }
        public bool IsConnected { get; set; }
        public string ContainerId { get; set; }
        public string BluetoothAddress { get; set; }

        public BluetoothDeviceSnapshot()
        {
            DeviceId = string.Empty;
            Name = string.Empty;
            ContainerId = string.Empty;
            BluetoothAddress = string.Empty;
        }
    }

    /// <summary>
    /// 蓝牙后端抽象。把 WinRT 细节挡在 Provider 之外，使设备状态机可以在无蓝牙环境里单测。
    /// 实现约束：
    /// 1. 事件可能在任意线程触发，Provider 自行封送；
    /// 2. 读失败必须映射成原因而不是抛异常；
    /// 3. 适配器不可用时发 <see cref="AvailabilityChanged"/>，不得清空已有设备。
    /// </summary>
    public interface IBluetoothBackend : IDisposable
    {
        /// <summary>已配对设备清单发生变化（新增/更新/移除）。</summary>
        event EventHandler<BluetoothDevicesChangedEventArgs> DevicesChanged;

        /// <summary>适配器或服务不可用/恢复。payload 是离线原因，恢复时为 null。</summary>
        event EventHandler<BluetoothAvailabilityChangedEventArgs> AvailabilityChanged;

        /// <summary>某设备订阅的 0x2A19 通知到达。</summary>
        event EventHandler<BluetoothBatteryNotificationEventArgs> BatteryNotified;

        /// <summary>开始枚举已配对设备并监听变化。重复调用应为幂等。</summary>
        void Start();

        /// <summary>读取指定设备 0x180F/0x2A19。实现必须串行化同一设备并释放 GATT 会话。</summary>
        BleBatteryReadResult ReadBattery(string deviceId, TimeSpan timeout);
    }

    public sealed class BluetoothDevicesChangedEventArgs : EventArgs
    {
        public System.Collections.Generic.IList<BluetoothDeviceSnapshot> Devices { get; private set; }
        public bool IsComplete { get; private set; }

        public BluetoothDevicesChangedEventArgs(System.Collections.Generic.IList<BluetoothDeviceSnapshot> devices, bool isComplete)
        {
            Devices = devices;
            IsComplete = isComplete;
        }
    }

    public sealed class BluetoothAvailabilityChangedEventArgs : EventArgs
    {
        public bool IsAvailable { get; private set; }
        public string Reason { get; private set; }

        public BluetoothAvailabilityChangedEventArgs(bool isAvailable, string reason)
        {
            IsAvailable = isAvailable;
            Reason = reason ?? string.Empty;
        }
    }

    public sealed class BluetoothBatteryNotificationEventArgs : EventArgs
    {
        public string DeviceId { get; private set; }
        public int Percent { get; private set; }

        public BluetoothBatteryNotificationEventArgs(string deviceId, int percent)
        {
            DeviceId = deviceId;
            Percent = percent;
        }
    }
}
