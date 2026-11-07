# PowerDock

PowerDock（BatteryManagement）是面向 Windows 的桌面电量中心设计。计划离线汇总蓝牙、2.4G/USB HID、本机电池及受支持的厂商设备，通过桌面挂件、主窗口和系统托盘展示电量。

当前状态（2026-11-08）：**.NET 10 + WPF 工程基线已在本地建立**——尚未合并进 `origin/main`，也尚未通过 S0 验收。
`src/PowerDock.Core`（`net10.0`）、`src/PowerDock.App`（`net10.0-windows`，WPF + WinForms 托盘）、
`tests/PowerDock.Core.Tests`（xunit）、`build.ps1`（`dotnet build`）与 `pack.ps1`（自包含未裁剪单文件）
在装有 .NET 10 SDK 的机器上可跑通；采集与界面功能尚未开始。三态记录见 [AGENTS.md §2](AGENTS.md)，
工程基线的包体与解包度量见 [验证记录](docs/验证记录-v0.1-S0-NET10工程基线.md)。
UI 方案可打开 [HTML 原型](docs/mockup/index.html) 查看（使用演示设备数据）。

## 构建与验证（需要 .NET 10 SDK，仅装运行时不够）

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\pack.ps1 -Version 0.1.0
dotnet test tests\PowerDock.Core.Tests\PowerDock.Core.Tests.csproj
.\dist\PowerDock-v0.1.0-win-x64\PowerDock.exe --self-test
```

- `build.ps1` / `pack.ps1` 自己探测 SDK（`PATH` → `DOTNET_ROOT` → 常规安装目录 → 就地部署目录），
  找不到时打印中文安装指引；仓库**不提交 `global.json`**，避免锁死补丁版本。
- 分发包是 win-x64 **未裁剪自包含单文件**（D16）。单文件首次运行会把原生库解包到
  `%TEMP%\.net\PowerDock\<hash>\`，因此首次启动略慢于后续启动。
- 分发一律用 `pack.ps1` 的产物；`build.ps1` 的输出是框架依赖版本，要求目标机装有 .NET 10 运行时。

- [项目协作契约与实施路线](AGENTS.md)
- [工程设计与采集方案](docs/设计方案-v0.1.md)
- [原型运行与规格说明](docs/mockup/README.md)
- [MIT 许可证](LICENSE)
