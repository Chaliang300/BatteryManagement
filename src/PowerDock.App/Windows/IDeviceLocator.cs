using PowerDock.Core.Common;

namespace PowerDock.App.Windows
{
    /// <summary>
    /// 设备页跨页定位契约（AGENTS.md §6.4.2 与 §6.5）。
    /// 概览页或其他视图请求定位设备时，设备页实现此接口以选中目标并滚动到可视区。
    /// </summary>
    public interface IDeviceLocator
    {
        /// <summary>选中对应设备卡片并将其滚动到可视区。</summary>
        /// <returns>找到并定位成功返回 true；未找到对应设备返回 false。</returns>
        bool SelectAndScrollToDevice(DeviceKey key);

        /// <summary>按字符串形式的 DeviceKey 定位。</summary>
        bool SelectAndScrollToDevice(string key);
    }
}
