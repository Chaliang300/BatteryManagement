using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using PowerDock.Core.Common;
using PowerDock.Core.Diagnostics;
using PowerDock.Core.Providers.Bluetooth;

namespace PowerDock.Providers.Bluetooth
{
    /// <summary>
    /// 真实 WinRT 蓝牙后端。只读约束：
    /// 1. 只读 Battery Service 0x180F 的 0x2A19，不写特征、不配对、不改适配器；
    /// 2. 每次读取短连接，读取完立即释放 GATT 会话与设备句柄，避免会话数随时间增长；
    /// 3. 适配器不可用时只上报可用性变化，不清空已缓存读数；
    /// 4. 订阅 Notify 后保存监听器引用，Dispose 时必须解绑并释放。
    /// </summary>
    public sealed class WinRtBluetoothBackend : IBluetoothBackend
    {
        private static readonly Guid BatteryServiceUuid = new Guid("0000180F-0000-1000-8000-00805F9B34FB");
        private static readonly Guid BatteryLevelUuid = new Guid("00002A19-0000-1000-8000-00805F9B34FB");

        private readonly object _sync = new object();
        private readonly Dictionary<string, GattSession> _sessions = new Dictionary<string, GattSession>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GattCharacteristic> _subscribed = new Dictionary<string, GattCharacteristic>(StringComparer.OrdinalIgnoreCase);
        private DeviceWatcher _watcher;
        private bool _started;
        private bool _disposed;

        public event EventHandler<BluetoothDevicesChangedEventArgs> DevicesChanged;
        public event EventHandler<BluetoothAvailabilityChangedEventArgs> AvailabilityChanged;
        public event EventHandler<BluetoothBatteryNotificationEventArgs> BatteryNotified;

        private static readonly string[] RequestedProperties =
        {
            "System.Devices.Aep.IsPaired",
            "System.Devices.ContainerId"
        };

        public void Start()
        {
            lock (_sync)
            {
                if (_disposed || _started) return;
                _started = true;
            }

            try
            {
                // 必须显式请求属性：否则 Properties 里没有 Aep.IsPaired 与 ContainerId，
                // 配对状态与容器归并都会落空（实测 FindAllAsync 只传选择器时二者皆缺失）。
                _watcher = DeviceInformation.CreateWatcher(
                    BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                    RequestedProperties,
                    DeviceInformationKind.AssociationEndpoint);
                _watcher.Added += OnDeviceAdded;
                _watcher.Updated += OnDeviceUpdated;
                _watcher.Removed += OnDeviceRemoved;
                _watcher.EnumerationCompleted += OnEnumerationCompleted;
                _watcher.Stopped += OnWatcherStopped;
                _watcher.Start();
                AppLogger.Info("Bluetooth watcher started (paired BLE devices).");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to start Bluetooth watcher", ex);
                RaiseAvailability(false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation info)
        {
            PublishSnapshot(info, null);
        }

        private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            PublishSnapshot(null, update);
        }

        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
        {
            PublishSnapshot(null, update);
        }

        private void OnEnumerationCompleted(DeviceWatcher sender, object args)
        {
            PublishAvailability(true);
        }

        private void OnWatcherStopped(DeviceWatcher sender, object args)
        {
            // Watcher 停止可能是适配器被关闭，也可能是系统节能；两种情况都按「来源暂不可用」处理，
            // 但不清空已缓存读数（由 Service 保留上次值并标过期）。
            PublishAvailability(false);
        }

        /// <summary>
        /// 重新枚举已配对设备并上报。Watcher 事件只带增量信息，统一走一次全量枚举，
        /// 避免增量合并逻辑与真实状态漂移。
        /// </summary>
        private void PublishSnapshot(DeviceInformation single, DeviceInformationUpdate update)
        {
            try
            {
                var devices = new List<BluetoothDeviceSnapshot>();
                IReadOnlyList<DeviceInformation> paired = DeviceInformation.FindAllAsync(
                    BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                    RequestedProperties).AsTask().GetAwaiter().GetResult();

                foreach (DeviceInformation info in paired)
                {
                    devices.Add(ToSnapshot(info));
                }

                var handler = DevicesChanged;
                if (handler != null)
                {
                    handler(this, new BluetoothDevicesChangedEventArgs(devices, true));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Bluetooth enumeration failed", ex);
                PublishAvailability(false);
            }
        }

        private BluetoothDeviceSnapshot ToSnapshot(DeviceInformation info)
        {
            var snapshot = new BluetoothDeviceSnapshot();
            snapshot.DeviceId = info.Id;
            snapshot.Name = info.Name;
            // 实测（2026-11-08，本机 3 台 BLE 键盘/手柄）：AssociationEndpoint 的 DeviceInformation.Pairing
            // 不是有效来源，IsPaired 恒为 false、CanPair 恒为 false。配对状态必须读 AEP 属性；
            // 若属性缺失，则退回「该设备由 paired 选择器枚举出来」这一事实，两者都不成立的才判未配对。
            snapshot.IsPaired = ReadAepPaired(info);

            try
            {
                using (BluetoothLEDevice device = BluetoothLEDevice.FromIdAsync(info.Id).AsTask().GetAwaiter().GetResult())
                {
                    if (device != null)
                    {
                        snapshot.IsConnected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
                        snapshot.BluetoothAddress = device.BluetoothAddress.ToString("X12");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Bluetooth connection status unavailable for " + DiagnosticRedaction.BluetoothId(info.Id) + ": " + ex.Message);
            }

            object container;
            if (info.Properties != null && info.Properties.TryGetValue("System.Devices.ContainerId", out container) && container is Guid)
            {
                snapshot.ContainerId = ((Guid)container).ToString("D");
            }

            return snapshot;
        }

        /// <summary>
        /// 读 AEP 的配对标记。已显式请求 System.Devices.Aep.IsPaired；
        /// 若枚举器仍未提供该属性，则把「由 paired 选择器枚举出来」视为已配对（选择器本身即配对过滤器）。
        /// </summary>
        private static bool ReadAepPaired(DeviceInformation info)
        {
            if (info.Properties != null)
            {
                object paired;
                if (info.Properties.TryGetValue("System.Devices.Aep.IsPaired", out paired) && paired is bool)
                {
                    return (bool)paired;
                }
            }
            return true;
        }

        public BleBatteryReadResult ReadBattery(string deviceId, TimeSpan timeout)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                return BleBatteryReadResult.Unavailable(PercentUnavailableReason.NotProbed, "empty device id");
            }

            try
            {
                using (var cts = new CancellationTokenSource(timeout))
                {
                    BluetoothLEDevice device = BluetoothLEDevice.FromIdAsync(deviceId).AsTask(cts.Token).GetAwaiter().GetResult();
                    if (device == null)
                    {
                        return BleBatteryReadResult.Unavailable(PercentUnavailableReason.AccessDenied, "BluetoothLEDevice.FromIdAsync returned null");
                    }

                    try
                    {
                        GattDeviceServicesResult services = device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(cts.Token).GetAwaiter().GetResult();
                        if (services.Status != GattCommunicationStatus.Success)
                        {
                            return BleBatteryReadResult.Unavailable(MapStatus(services.Status), "GetGattServicesAsync=" + services.Status);
                        }

                        GattDeviceService battery = null;
                        try
                        {
                            foreach (GattDeviceService service in services.Services)
                            {
                                if (service.Uuid == BatteryServiceUuid)
                                {
                                    battery = service;
                                    break;
                                }
                            }
                            if (battery == null)
                            {
                                return BleBatteryReadResult.Unavailable(PercentUnavailableReason.NoBatteryService, "service 0x180F not found");
                            }

                            GattCharacteristicsResult characteristics = battery.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cts.Token).GetAwaiter().GetResult();
                            if (characteristics.Status != GattCommunicationStatus.Success)
                            {
                                return BleBatteryReadResult.Unavailable(MapStatus(characteristics.Status), "GetCharacteristicsAsync=" + characteristics.Status);
                            }

                            GattCharacteristic level = null;
                            foreach (GattCharacteristic characteristic in characteristics.Characteristics)
                            {
                                if (characteristic.Uuid == BatteryLevelUuid)
                                {
                                    level = characteristic;
                                    break;
                                }
                            }
                            if (level == null)
                            {
                                return BleBatteryReadResult.Unavailable(PercentUnavailableReason.NoBatteryService, "characteristic 0x2A19 not found");
                            }

                            bool supportsNotify = (level.CharacteristicProperties & GattCharacteristicProperties.Notify) != 0;
                            var result = new BleBatteryReadResult { SupportsNotify = supportsNotify };

                            if (supportsNotify)
                            {
                                TrySubscribe(deviceId, level);
                            }

                            GattReadResult read = level.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cts.Token).GetAwaiter().GetResult();
                            if (read.Status != GattCommunicationStatus.Success)
                            {
                                result.Reason = MapStatus(read.Status);
                                result.Detail = "ReadValueAsync=" + read.Status;
                                return result;
                            }

                            byte value = ReadByte(read.Value);
                            result.Percent = value > 100 ? 100 : value;
                            result.Reason = PercentUnavailableReason.None;
                            result.Detail = "read 0x2A19=" + value;
                            return result;
                        }
                        finally
                        {
                            // GATT 服务对象持有原生句柄：每次读取后必须释放，否则服务会话数会随轮询增长。
                            foreach (GattDeviceService service in services.Services)
                            {
                                try { service.Dispose(); } catch { }
                            }
                        }
                    }
                    finally
                    {
                        device.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return BleBatteryReadResult.Unavailable(PercentUnavailableReason.Timeout, "GATT read timed out");
            }
            catch (UnauthorizedAccessException ex)
            {
                return BleBatteryReadResult.Unavailable(PercentUnavailableReason.AccessDenied, ex.GetType().Name + ": " + ex.Message);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Bluetooth GATT read failed for " + DiagnosticRedaction.BluetoothId(deviceId), ex);
                return BleBatteryReadResult.Unavailable(PercentUnavailableReason.Timeout, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void TrySubscribe(string deviceId, GattCharacteristic characteristic)
        {
            lock (_sync)
            {
                if (_disposed || _subscribed.ContainsKey(deviceId)) return;
            }

            try
            {
                GattCommunicationStatus status = characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask().GetAwaiter().GetResult();

                if (status != GattCommunicationStatus.Success)
                {
                    AppLogger.Warn("Bluetooth notify subscription failed (" + status + ") for " + DiagnosticRedaction.BluetoothId(deviceId));
                    return;
                }

                characteristic.ValueChanged += OnCharacteristicValueChanged;
                lock (_sync)
                {
                    if (_disposed)
                    {
                        characteristic.ValueChanged -= OnCharacteristicValueChanged;
                        return;
                    }
                    _subscribed[deviceId] = characteristic;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Bluetooth notify subscribe threw for " + DiagnosticRedaction.BluetoothId(deviceId) + ": " + ex.Message);
            }
        }

        private void OnCharacteristicValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            try
            {
                byte value = ReadByte(args.CharacteristicValue);
                string deviceId = FindDeviceId(sender);
                if (deviceId == null) return;

                var handler = BatteryNotified;
                if (handler != null)
                {
                    handler(this, new BluetoothBatteryNotificationEventArgs(deviceId, value > 100 ? 100 : value));
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Bluetooth notification handler failed", ex);
            }
        }

        private string FindDeviceId(GattCharacteristic characteristic)
        {
            lock (_sync)
            {
                foreach (var pair in _subscribed)
                {
                    if (ReferenceEquals(pair.Value, characteristic)) return pair.Key;
                }
            }
            return null;
        }

        private static byte ReadByte(IBuffer buffer)
        {
            if (buffer == null || buffer.Length == 0) return 0;
            var reader = DataReader.FromBuffer(buffer);
            return reader.ReadByte();
        }

        private static PercentUnavailableReason MapStatus(GattCommunicationStatus status)
        {
            switch (status)
            {
                case GattCommunicationStatus.AccessDenied:
                    return PercentUnavailableReason.AccessDenied;
                case GattCommunicationStatus.Unreachable:
                    return PercentUnavailableReason.Timeout;
                case GattCommunicationStatus.ProtocolError:
                    return PercentUnavailableReason.Timeout;
                default:
                    return PercentUnavailableReason.Timeout;
            }
        }

        private void PublishAvailability(bool available)
        {
            var handler = AvailabilityChanged;
            if (handler == null) return;
            try
            {
                handler(this, new BluetoothAvailabilityChangedEventArgs(available, available ? string.Empty : "watcher stopped"));
            }
            catch (Exception ex)
            {
                AppLogger.Error("Bluetooth availability handler threw", ex);
            }
        }

        private void RaiseAvailability(bool available, string reason)
        {
            var handler = AvailabilityChanged;
            if (handler == null) return;
            try
            {
                handler(this, new BluetoothAvailabilityChangedEventArgs(available, reason));
            }
            catch (Exception ex)
            {
                AppLogger.Error("Bluetooth availability handler threw", ex);
            }
        }

        public void Dispose()
        {
            DeviceWatcher watcher;
            var subscribed = new List<KeyValuePair<string, GattCharacteristic>>();
            var sessions = new List<GattSession>();

            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                watcher = _watcher;
                _watcher = null;
                subscribed.AddRange(_subscribed);
                _subscribed.Clear();
                sessions.AddRange(_sessions.Values);
                _sessions.Clear();
            }

            if (watcher != null)
            {
                try
                {
                    watcher.Added -= OnDeviceAdded;
                    watcher.Updated -= OnDeviceUpdated;
                    watcher.Removed -= OnDeviceRemoved;
                    watcher.EnumerationCompleted -= OnEnumerationCompleted;
                    watcher.Stopped -= OnWatcherStopped;
                    if (watcher.Status == DeviceWatcherStatus.Started) watcher.Stop();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("Bluetooth watcher stop failed: " + ex.Message);
                }
            }

            foreach (var pair in subscribed)
            {
                try
                {
                    pair.Value.ValueChanged -= OnCharacteristicValueChanged;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("Bluetooth unsubscribe failed: " + ex.Message);
                }
            }

            foreach (GattSession session in sessions)
            {
                try { session.Dispose(); } catch { }
            }
        }
    }
}
