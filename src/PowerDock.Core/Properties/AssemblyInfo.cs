using System.Runtime.CompilerServices;

// 读数状态只允许 DeviceStateService 写入；测试需要构造失败/过期场景，故仅对测试程序集开放。
[assembly: InternalsVisibleTo("PowerDock.Core.Tests")]
