using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using PowerDock.Core.Common;
using PowerDock.Core.Diagnostics;

namespace PowerDock.Core.Providers.Hid
{
    /// <summary>
    /// 一个在场 HID 设备接口的只读快照。InstanceId 只用于定位与分组，不能当持久身份。
    /// </summary>
    public sealed class HidInterfaceInfo
    {
        public string InterfacePath { get; set; }
        public string InstanceId { get; set; }
        public string ContainerId { get; set; }
        public string ParentInstanceId { get; set; }
        public string VendorId { get; set; }
        public string ProductId { get; set; }
        public bool IsUsbBus { get; set; }
        public string FriendlyName { get; set; }

        /// <summary>PnP 设备描述（SPDRP_DEVICEDESC），友好名缺失时的可读回退。</summary>
        public string DeviceDescription { get; set; }

        public HidInterfaceInfo()
        {
            InterfacePath = string.Empty;
            InstanceId = string.Empty;
            ContainerId = string.Empty;
            ParentInstanceId = string.Empty;
            VendorId = string.Empty;
            ProductId = string.Empty;
            FriendlyName = string.Empty;
            DeviceDescription = string.Empty;
        }
    }

    /// <summary>
    /// 用 SetupAPI 枚举在场 HID 接口。只读、只看 DIGCF_PRESENT：
    /// 1. 一律用设备接口路径打开句柄，不用 PnP 实例 ID（后者不能 CreateFile）；
    /// 2. 容器 ID 取失败时如实留空，调用方必须降级为「身份未确认」；
    /// 3. 父节点与 VID/PID 只作总线证据，不能单独用来判定有线或 2.4G。
    /// </summary>
    public sealed class HidDeviceEnumerator
    {
        public IList<HidInterfaceInfo> EnumeratePresent()
        {
            var list = new List<HidInterfaceInfo>();
            Guid guid = HidInterop.GUID_DEVINTERFACE_HID;
            IntPtr deviceInfoSet = HidInterop.SetupDiGetClassDevs(
                ref guid, IntPtr.Zero, IntPtr.Zero,
                HidInterop.DIGCF_PRESENT | HidInterop.DIGCF_DEVICEINTERFACE);

            if (deviceInfoSet == new IntPtr(-1))
            {
                AppLogger.Warn("SetupDiGetClassDevs(HID) failed, win32=" + Marshal.GetLastWin32Error());
                return list;
            }

            try
            {
                for (int index = 0; ; index++)
                {
                    var interfaceData = new HidInterop.SP_DEVICE_INTERFACE_DATA();
                    interfaceData.cbSize = Marshal.SizeOf(typeof(HidInterop.SP_DEVICE_INTERFACE_DATA));
                    if (!HidInterop.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref guid, index, ref interfaceData))
                    {
                        break;
                    }

                    HidInterfaceInfo info = ReadInterface(deviceInfoSet, ref interfaceData);
                    if (info != null) list.Add(info);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("HID enumeration aborted", ex);
            }
            finally
            {
                HidInterop.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return list;
        }

        private static HidInterfaceInfo ReadInterface(IntPtr deviceInfoSet, ref HidInterop.SP_DEVICE_INTERFACE_DATA interfaceData)
        {
            int requiredSize;
            var devInfo = new HidInterop.SP_DEVINFO_DATA();
            devInfo.cbSize = Marshal.SizeOf(typeof(HidInterop.SP_DEVINFO_DATA));

            // 第一次调用拿所需长度；detail 结构中 cbSize 字段在 x64 下占 8 字节（含 4 字节对齐填充）。
            HidInterop.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out requiredSize, ref devInfo);
            if (requiredSize <= 0) return null;

            IntPtr detailBuffer = Marshal.AllocHGlobal(requiredSize);
            try
            {
                Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                if (!HidInterop.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailBuffer, requiredSize, out requiredSize, ref devInfo))
                {
                    return null;
                }

                IntPtr pathPtr = IntPtr.Add(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                var info = new HidInterfaceInfo();
                info.InterfacePath = Marshal.PtrToStringUni(pathPtr) ?? string.Empty;

                var instanceBuffer = new char[512];
                int instanceLength;
                if (HidInterop.SetupDiGetDeviceInstanceId(deviceInfoSet, ref devInfo, instanceBuffer, instanceBuffer.Length, out instanceLength))
                {
                    info.InstanceId = new string(instanceBuffer, 0, Math.Max(0, instanceLength - 1));
                }

                info.ContainerId = ReadStringProperty(deviceInfoSet, ref devInfo, HidInterop.DEVPKEY_Device_ContainerId);
                info.ParentInstanceId = ReadStringProperty(deviceInfoSet, ref devInfo, HidInterop.DEVPKEY_Device_Parent);
                info.FriendlyName = ReadRegistryString(deviceInfoSet, ref devInfo, HidInterop.SPDRP_FRIENDLYNAME);
                if (string.IsNullOrEmpty(info.FriendlyName))
                {
                    info.DeviceDescription = ReadRegistryString(deviceInfoSet, ref devInfo, HidInterop.SPDRP_DEVICEDESC);
                }

                FillVidPid(info);
                info.IsUsbBus = info.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
                                info.InterfacePath.StartsWith("\\\\?\\hid#", StringComparison.OrdinalIgnoreCase) && info.ParentInstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);
                return info;
            }
            finally
            {
                Marshal.FreeHGlobal(detailBuffer);
            }
        }

        private static string ReadStringProperty(IntPtr deviceInfoSet, ref HidInterop.SP_DEVINFO_DATA devInfo, HidInterop.DEVPROPKEY key)
        {
            int propertyType;
            int requiredSize;
            var buffer = new byte[512];
            if (!HidInterop.SetupDiGetDevicePropertyW(deviceInfoSet, ref devInfo, ref key, out propertyType, buffer, buffer.Length, out requiredSize, 0))
            {
                return string.Empty;
            }
            if (requiredSize < 4) return string.Empty;

            // DEVPROP_TYPE_GUID = 13：容器 ID 是 GUID，必须按 16 字节解析而不是当字符串读。
            if (propertyType == 13 && requiredSize >= 16)
            {
                var bytes = new byte[16];
                Array.Copy(buffer, bytes, 16);
                string guid = new Guid(bytes).ToString("D");
                // 「无容器」哨兵：Windows 用它表示该设备没有真实容器，不能作为合并依据。
                if (!DeviceIdentity.IsUsableContainer(guid))
                {
                    return string.Empty;
                }
                return guid;
            }

            string text = System.Text.Encoding.Unicode.GetString(buffer, 0, (requiredSize / 2) * 2).Trim('\0');
            if (key.pid == HidInterop.DEVPKEY_Device_ContainerId.pid && !DeviceIdentity.IsUsableContainer(text))
            {
                return string.Empty;
            }
            return text;
        }

        private static string ReadRegistryString(IntPtr deviceInfoSet, ref HidInterop.SP_DEVINFO_DATA devInfo, int property)
        {
            var buffer = new byte[1024];
            int regType;
            int requiredSize;
            if (!HidInterop.SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devInfo, property, out regType, buffer, buffer.Length, out requiredSize))
            {
                return string.Empty;
            }
            if (requiredSize < 4) return string.Empty;
            return System.Text.Encoding.Unicode.GetString(buffer, 0, requiredSize - 2).Trim('\0');
        }

        private static void FillVidPid(HidInterfaceInfo info)
        {
            string[] sources = { info.InstanceId, info.ParentInstanceId };
            foreach (string source in sources)
            {
                string vid = ExtractToken(source, "VID_", 4);
                string pid = ExtractToken(source, "PID_", 4);
                if (!string.IsNullOrEmpty(vid) && !string.IsNullOrEmpty(pid))
                {
                    info.VendorId = vid;
                    info.ProductId = pid;
                    return;
                }
            }
        }

        private static string ExtractToken(string source, string prefix, int length)
        {
            if (string.IsNullOrEmpty(source)) return string.Empty;
            int start = source.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return string.Empty;
            start += prefix.Length;
            if (source.Length < start + length) return string.Empty;
            string token = source.Substring(start, length);
            for (int i = 0; i < token.Length; i++)
            {
                if (!Uri.IsHexDigit(token[i])) return string.Empty;
            }
            return token.ToUpperInvariant();
        }

        /// <summary>
        /// 设备接口到达/移除通知。回调来自系统线程，只做「打标记 + 唤醒」，不做枚举本身；
        /// 返回时必须尽快，避免阻塞配置管理器。
        /// </summary>
        public sealed class ChangeMonitor : IDisposable
        {
            private readonly Action _onChanged;
            private readonly HidInterop.CM_NOTIFY_CALLBACK _callback;
            private IntPtr _notify;
            private bool _disposed;

            public ChangeMonitor(Action onChanged)
            {
                if (onChanged == null) throw new ArgumentNullException("onChanged");
                _onChanged = onChanged;
                _callback = OnNotify;
            }

            public bool Start()
            {
                var filter = new HidInterop.CM_NOTIFY_FILTER();
                filter.cbSize = Marshal.SizeOf(typeof(HidInterop.CM_NOTIFY_FILTER));
                filter.FilterType = HidInterop.CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE;
                filter.ClassGuid = HidInterop.GUID_DEVINTERFACE_HID;

                int result = HidInterop.CM_Register_Notification(ref filter, IntPtr.Zero, _callback, out _notify);
                if (result != 0)
                {
                    AppLogger.Warn("CM_Register_Notification(HID) failed, cm=" + result.ToString(CultureInfo.InvariantCulture));
                    _notify = IntPtr.Zero;
                    return false;
                }
                return true;
            }

            private int OnNotify(IntPtr hNotify, IntPtr context, int action, IntPtr eventData, int eventDataSize)
            {
                if (_disposed) return 0;
                try
                {
                    _onChanged();
                }
                catch (Exception ex)
                {
                    AppLogger.Error("HID change notification handler failed", ex);
                }
                return 0;
            }

            public void Dispose()
            {
                _disposed = true;
                if (_notify != IntPtr.Zero)
                {
                    try { HidInterop.CM_Unregister_Notification(_notify); } catch { }
                    _notify = IntPtr.Zero;
                }
            }
        }
    }
}
