using System;
using System.Runtime.InteropServices;

namespace PowerDock.Core.Providers.Hid
{
    /// <summary>
    /// 仅只读的 HID/SetupAPI/配置管理器互操作。约束：
    /// 1. 不写设备、不发输出报告，只用 HidD_GetFeature / HidD_GetInputReport 读取；
    /// 2. 句柄一律显式释放，避免句柄随发现次数增长；
    /// 3. 枚举用 DIGCF_PRESENT 只看在场设备，幽灵设备不参与电量探测。
    /// </summary>
    internal static class HidInterop
    {
        internal const int DIGCF_PRESENT = 0x00000002;
        internal const int DIGCF_DEVICEINTERFACE = 0x00000010;

        internal const int SPDRP_FRIENDLYNAME = 0x0000000C;
        internal const int SPDRP_DEVICEDESC = 0x00000000;

        internal const uint GENERIC_READ = 0x80000000;
        internal const uint FILE_SHARE_READ = 0x00000001;
        internal const uint FILE_SHARE_WRITE = 0x00000002;
        internal const uint OPEN_EXISTING = 3;
        internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        internal const int HIDP_INPUT = 0;
        internal const int HIDP_FEATURE = 2;

        internal const int HIDP_STATUS_SUCCESS = 0x00110000;
        internal const int HIDP_STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0110007);

        internal const ushort USAGE_PAGE_GENERIC_DEVICE = 0x06;
        internal const ushort USAGE_BATTERY_STRENGTH = 0x20;

        internal static readonly Guid GUID_DEVINTERFACE_HID = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

        internal static readonly DEVPROPKEY DEVPKEY_Device_ContainerId =
            new DEVPROPKEY { fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), pid = 2 };

        internal static readonly DEVPROPKEY DEVPKEY_Device_Parent =
            new DEVPROPKEY { fmtid = new Guid("4340A6C5-93FA-4706-972C-7B648008A5A7"), pid = 8 };

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DEVPROPKEY
        {
            public Guid fmtid;
            public int pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        /// <summary>
        /// HIDP_VALUE_CAPS 布局必须与 hidpi.h 逐字段一致，且顺序不可调整：
        /// UsagePage..HasNull、Reserved、BitSize、ReportCount、Reserved2、UnitsExp、Units，
        /// 之后才是 LogicalMin/LogicalMax/PhysicalMin/PhysicalMax 与末尾的 Range/NotRange 联合体。
        /// 若把联合体写在逻辑范围之前，或漏掉 UnitsExp/Units，LogicalMin/Max 会读到错位数据，
        /// 归一化结果全错（这是 S1 必须避开的静默错误）。
        /// BOOLEAN 一律显式 U1，否则 .NET 默认按 4 字节布尔编组，整个结构错位。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsAlias;
            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsRange;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsStringRange;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsDesignatorRange;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsAbsolute;
            [MarshalAs(UnmanagedType.U1)]
            public bool HasNull;
            public byte Reserved;
            public ushort BitSize;
            public ushort ReportCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
            public ushort[] Reserved2;
            public uint UnitsExp;
            public uint Units;
            public int LogicalMin;
            public int LogicalMax;
            public int PhysicalMin;
            public int PhysicalMax;
            public ushort UsageMin;
            public ushort UsageMax;
            public ushort StringMin;
            public ushort StringMax;
            public ushort DesignatorMin;
            public ushort DesignatorMax;
            public ushort DataIndexMin;
            public ushort DataIndexMax;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CM_NOTIFY_FILTER
        {
            public int cbSize;
            public int Flags;
            public int FilterType;
            public int Reserved;
            public Guid ClassGuid;
            public IntPtr TargetInstance;
            public IntPtr TargetPort;
            public IntPtr TargetDevice;
        }

        internal const int CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate int CM_NOTIFY_CALLBACK(IntPtr hNotify, IntPtr context, int action, IntPtr eventData, int eventDataSize);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetValueCaps(int reportType, [In, Out] HIDP_VALUE_CAPS[] valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetUsageValue(
            int reportType, ushort usagePage, ushort linkCollection, ushort usage,
            out uint usageValue, IntPtr preparsedData, byte[] report, int reportLength);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetFeature(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetInputReport(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFile(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
            IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize,
            out int requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetupDiGetDeviceInstanceId(
            IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
            [Out] char[] deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetupDiGetDeviceRegistryProperty(
            IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, int property,
            out int propertyRegDataType, byte[] propertyBuffer, int propertyBufferSize, out int requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetupDiGetDevicePropertyW(
            IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref DEVPROPKEY propertyKey,
            out int propertyType, byte[] propertyBuffer, int propertyBufferSize, out int requiredSize, int flags);

        [DllImport("cfgmgr32.dll", SetLastError = true)]
        internal static extern int CM_Register_Notification(ref CM_NOTIFY_FILTER filter, IntPtr context, CM_NOTIFY_CALLBACK callback, out IntPtr notify);

        [DllImport("cfgmgr32.dll")]
        internal static extern int CM_Unregister_Notification(IntPtr notify);
    }
}
