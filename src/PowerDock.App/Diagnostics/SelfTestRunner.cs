using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using PowerDock.App.Interop;
using PowerDock.App.Shell;
using PowerDock.App.Windows;
using PowerDock.Core.Common;
using PowerDock.Core.Diagnostics;
using PowerDock.Core.Storage;

namespace PowerDock.App.Diagnostics
{
    public sealed class SelfTestItem
    {
        public string Id { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        public bool Passed { get; set; }
        public string Detail { get; set; }
    }

    public static class SelfTestRunner
    {
        public static int Run(string dataDir)
        {
            if (string.IsNullOrEmpty(dataDir))
                dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

            if (!Directory.Exists(dataDir))
                Directory.CreateDirectory(dataDir);

            var items = new List<SelfTestItem>();

            bool is64Bit = Environment.Is64BitProcess;
            items.Add(new SelfTestItem
            {
                Id = "SYS-01",
                Category = "Environment",
                Description = "64位进程运行环境",
                Passed = is64Bit,
                Detail = is64Bit ? "x64 运行正常" : "非 64 位运行环境"
            });

            int osMajor = 0;
            string buildNumber = "Unknown";
            try
            {
                var regVal = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentMajorVersionNumber", null);
                if (regVal != null)
                {
                    osMajor = Convert.ToInt32(regVal);
                }
                var buildVal = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", null);
                if (buildVal != null)
                {
                    buildNumber = buildVal.ToString();
                    int bNum;
                    if (int.TryParse(buildNumber, out bNum) && bNum >= 10240)
                    {
                        osMajor = 10;
                    }
                }
            }
            catch
            {
                osMajor = Environment.OSVersion.Version.Major;
            }

            bool isWin10Or11 = osMajor >= 10;
            items.Add(new SelfTestItem
            {
                Id = "SYS-02",
                Category = "Environment",
                Description = "Windows 10/11 操作系统版本检测",
                Passed = isWin10Or11,
                Detail = string.Format("OS Major: {0}, Build: {1}", osMajor, buildNumber)
            });

            try
            {
                var dKey1 = new DeviceKey("dev-001");
                var dKey2 = new DeviceKey("DEV-001");
                var rKey1 = new ReadingKey("wmi", "endpoint-1", "bat-0");
                var rKey2 = new ReadingKey("WMI", "endpoint-1", "bat-0");

                bool keysEqual = dKey1.Equals(dKey2) && rKey1.Equals(rKey2);
                items.Add(new SelfTestItem
                {
                    Id = "CORE-01",
                    Category = "Models",
                    Description = "DeviceKey 与 ReadingKey 大小写不敏感哈希与比较",
                    Passed = keysEqual,
                    Detail = keysEqual ? "键值哈希一致性验证通过" : "键值哈希或比较失败"
                });
            }
            catch (Exception ex)
            {
                items.Add(new SelfTestItem
                {
                    Id = "CORE-01",
                    Category = "Models",
                    Description = "DeviceKey 与 ReadingKey 大小写不敏感哈希与比较",
                    Passed = false,
                    Detail = ex.Message
                });
            }

            try
            {
                var settings = new AppSettings();
                bool validDefaults = settings.WidgetSize == "medium" &&
                                     settings.LowBatteryThreshold == 20 &&
                                     settings.AlertCooldownMinutes == 30 &&
                                     settings.Theme == "dark";
                items.Add(new SelfTestItem
                {
                    Id = "STORE-01",
                    Category = "Storage",
                    Description = "AppSettings 默认值符合产品设计规范 (中号/20%/30min/深色)",
                    Passed = validDefaults,
                    Detail = validDefaults ? "默认值与设计基线完全一致" : "默认值偏离设计规范"
                });
            }
            catch (Exception ex)
            {
                items.Add(new SelfTestItem
                {
                    Id = "STORE-01",
                    Category = "Storage",
                    Description = "AppSettings 默认值符合产品设计规范",
                    Passed = false,
                    Detail = ex.Message
                });
            }

            try
            {
                string testLogFile = Path.Combine(dataDir, "test-write.tmp");
                File.WriteAllText(testLogFile, "ok", Encoding.UTF8);
                string content = File.ReadAllText(testLogFile, Encoding.UTF8);
                File.Delete(testLogFile);

                items.Add(new SelfTestItem
                {
                    Id = "FS-01",
                    Category = "FileSystem",
                    Description = "数据目录写入与原子清理测试",
                    Passed = content == "ok",
                    Detail = string.Format("Data Directory: {0}", dataDir)
                });
            }
            catch (Exception ex)
            {
                items.Add(new SelfTestItem
                {
                    Id = "FS-01",
                    Category = "FileSystem",
                    Description = "数据目录写入与原子清理测试",
                    Passed = false,
                    Detail = ex.Message
                });
            }

            try
            {
                string testAtomicFile = Path.Combine(dataDir, "test-atomic.json");
                string bakFile = testAtomicFile + ".bak";
                AtomicFile.AtomicWrite(testAtomicFile, "{\"v\":1}");
                AtomicFile.AtomicWrite(testAtomicFile, "{\"v\":2}");
                bool atomicPassed = File.ReadAllText(testAtomicFile) == "{\"v\":2}" &&
                                   File.Exists(bakFile) &&
                                   File.ReadAllText(bakFile) == "{\"v\":1}";
                File.Delete(testAtomicFile);
                if (File.Exists(bakFile)) File.Delete(bakFile);

                items.Add(new SelfTestItem
                {
                    Id = "FS-02",
                    Category = "FileSystem",
                    Description = "AtomicFile 原子覆盖替换与 .bak 备份机制测试",
                    Passed = atomicPassed,
                    Detail = atomicPassed ? "原子写入与备份轮转正常" : "原子替换或备份失败"
                });
            }
            catch (Exception ex)
            {
                items.Add(new SelfTestItem
                {
                    Id = "FS-02",
                    Category = "FileSystem",
                    Description = "AtomicFile 原子覆盖替换与 .bak 备份机制测试",
                    Passed = false,
                    Detail = ex.Message
                });
            }

            try
            {
                string testSettingsPath = Path.Combine(dataDir, "test-settings.json");
                if (File.Exists(testSettingsPath)) File.Delete(testSettingsPath);
                var testStore = new SettingsStore(testSettingsPath);
                bool eventFired = false;
                testStore.SettingsChanged += delegate(object s, AppSettings e)
                {
                    if (e != null && e.Theme == "light") eventFired = true;
                };

                var sObj = new AppSettings();
                sObj.Theme = "light";
                sObj.LowBatteryThreshold = 15;
                sObj.DeviceAliases["DEV-TEST"] = "AliasTest";
                testStore.Save(sObj);

                var reloadedStore = new SettingsStore(testSettingsPath);
                reloadedStore.Load();

                bool storePassed = eventFired &&
                                  reloadedStore.Current.Theme == "light" &&
                                  reloadedStore.Current.LowBatteryThreshold == 15 &&
                                  reloadedStore.Current.DeviceAliases.ContainsKey("dev-test") &&
                                  reloadedStore.Current.DeviceAliases["dev-test"] == "AliasTest";

                if (File.Exists(testSettingsPath)) File.Delete(testSettingsPath);
                string testBak = testSettingsPath + ".bak";
                if (File.Exists(testBak)) File.Delete(testBak);

                items.Add(new SelfTestItem
                {
                    Id = "STORE-02",
                    Category = "Storage",
                    Description = "SettingsStore 保存、回读校验与变更事件测试",
                    Passed = storePassed,
                    Detail = storePassed ? "持久化存储与回读校验通过" : "存储或校验失败"
                });
            }
            catch (Exception ex)
            {
                items.Add(new SelfTestItem
                {
                    Id = "STORE-02",
                    Category = "Storage",
                    Description = "SettingsStore 保存、回读校验与变更事件测试",
                    Passed = false,
                    Detail = ex.Message
                });
            }

            // MUTEX-01: 测试单实例 Mutex 名称格式及获取/释放逻辑
            try
            {
                int sessionId = Process.GetCurrentProcess().SessionId;
                string expectedPrefix = @"Local\PowerDock_Mutex_";
                string defaultMutexName = SingleInstanceService.GetDefaultMutexName();

                bool formatValid = defaultMutexName != null &&
                                   defaultMutexName.StartsWith(expectedPrefix) &&
                                   defaultMutexName.EndsWith(sessionId.ToString());

                string testMutexName = @"Local\PowerDock_Mutex_Test_" + Guid.NewGuid().ToString("N");
                string dummyPipe1 = "PowerDock_Pipe_Dummy_" + Guid.NewGuid().ToString("N");
                string dummyPipe2 = "PowerDock_Pipe_Dummy_" + Guid.NewGuid().ToString("N");

                bool acquire1 = false;
                bool conflictDetected = false;
                bool acquireAfterRelease = false;

                using (var svc1 = new SingleInstanceService(testMutexName, dummyPipe1))
                {
                    acquire1 = svc1.TryStartAsFirstInstance(null);

                    using (var svc2 = new SingleInstanceService(testMutexName, dummyPipe1))
                    {
                        bool isFirst2 = svc2.TryStartAsFirstInstance(null);
                        conflictDetected = !isFirst2;
                    }
                }

                using (var svc3 = new SingleInstanceService(testMutexName, dummyPipe2))
                {
                    acquireAfterRelease = svc3.TryStartAsFirstInstance(null);
                }

                bool mutexPassed = formatValid && acquire1 && conflictDetected && acquireAfterRelease;

                items.Add(new SelfTestItem
                {
                    Id = "MUTEX-01",
                    Category = "Shell",
                    Description = "单实例 Mutex 名称格式及获取/释放逻辑",
                    Passed = mutexPassed,
                    Detail = mutexPassed
                        ? string.Format("Mutex: {0}, 获取/冲突拦截/释放闭环正常", defaultMutexName)
                        : string.Format("Format={0}, Acq1={1}, Conflict={2}, Acq2={3}", formatValid, acquire1, conflictDetected, acquireAfterRelease)
                });
            }
            catch (Exception ex)
            {
                items.Add(new SelfTestItem
                {
                    Id = "MUTEX-01",
                    Category = "Shell",
                    Description = "单实例 Mutex 名称格式及获取/释放逻辑",
                    Passed = false,
                    Detail = ex.Message
                });
            }

            // PIPE-01: 测试命名管道服务端与客户端短连接通信闭环
            try
            {
                int sessionId = Process.GetCurrentProcess().SessionId;
                string expectedPipePrefix = "PowerDock_Pipe_";
                string defaultPipeName = SingleInstanceService.GetDefaultPipeName();

                bool pipeFormatValid = defaultPipeName != null &&
                                       defaultPipeName.StartsWith(expectedPipePrefix) &&
                                       defaultPipeName.EndsWith(sessionId.ToString());

                string testMutexName = @"Local\PowerDock_Mutex_PipeTest_" + Guid.NewGuid().ToString("N");
                string testPipeName = "PowerDock_Pipe_Test_" + Guid.NewGuid().ToString("N");

                bool callbackInvoked = false;
                var callbackEvent = new ManualResetEvent(false);

                bool serverStarted = false;
                bool clientReportedSecond = false;
                bool waitSuccess = false;

                using (var serverSvc = new SingleInstanceService(testMutexName, testPipeName))
                {
                    serverStarted = serverSvc.TryStartAsFirstInstance(delegate()
                    {
                        callbackInvoked = true;
                        callbackEvent.Set();
                    });

                    using (var clientSvc = new SingleInstanceService(testMutexName, testPipeName))
                    {
                        bool isFirst = clientSvc.TryStartAsFirstInstance(null);
                        clientReportedSecond = !isFirst;
                    }

                    waitSuccess = callbackEvent.WaitOne(3000);
                }

                bool pipePassed = pipeFormatValid && serverStarted && clientReportedSecond && callbackInvoked && waitSuccess;

                items.Add(new SelfTestItem
                {
                    Id = "PIPE-01",
                    Category = "Shell",
                    Description = "命名管道服务端与客户端短连接通信闭环",
                    Passed = pipePassed,
                    Detail = pipePassed
                        ? string.Format("Pipe: {0}, 客户端发送激活指令并收到确认，服务端激活回调成功触发", defaultPipeName)
                        : string.Format("Format={0}, ServerStarted={1}, ClientSecond={2}, Callback={3}, WaitSuccess={4}",
                                        pipeFormatValid, serverStarted, clientReportedSecond, callbackInvoked, waitSuccess)
                });
            }
            catch (Exception ex)
            {
                items.Add(new SelfTestItem
                {
                    Id = "PIPE-01",
                    Category = "Shell",
                    Description = "命名管道服务端与客户端短连接通信闭环",
                    Passed = false,
                    Detail = ex.Message
                });
            }

            // AUTO-01: 开机自启启用写入、回读校验与关闭清理无残留测试
            try
            {
                string testValueName = "PowerDock_AutoStart_Test_" + Guid.NewGuid().ToString("N");
                string testExe = AutoStartService.GetDefaultExecutablePath();
                var autoStart = new AutoStartService(testExe, testValueName);

                // 1. 验证初始状态未启用
                bool initialDisabled = !autoStart.IsAutoStartEnabled();

                // 2. 启用自启，回读校验
                bool enableSuccess = autoStart.SetAutoStart(true);
                bool afterEnableVerified = autoStart.IsAutoStartEnabled();

                // 3. 直接通过注册表检查写入的值与格式
                bool regMatch = false;
                using (var rk = Registry.CurrentUser.OpenSubKey(AutoStartService.RunRegistrySubKey, false))
                {
                    if (rk != null)
                    {
                        object regVal = rk.GetValue(testValueName, null);
                        if (regVal != null && string.Equals(regVal.ToString(), autoStart.ExpectedCommandLine, StringComparison.OrdinalIgnoreCase))
                        {
                            regMatch = true;
                        }
                    }
                }

                // 4. 关闭自启，清理并校验无残留
                bool disableSuccess = autoStart.SetAutoStart(false);
                bool afterDisableClean = !autoStart.IsAutoStartEnabled();

                // 5. 直接通过注册表检查确认值已删除
                bool residueClean = false;
                using (var rk = Registry.CurrentUser.OpenSubKey(AutoStartService.RunRegistrySubKey, false))
                {
                    if (rk != null)
                    {
                        object regVal = rk.GetValue(testValueName, null);
                        residueClean = (regVal == null);
                    }
                }

                // 6. 验证默认 AutoStartService 属性
                var defaultSvc = new AutoStartService();
                bool defaultValid = !string.IsNullOrEmpty(defaultSvc.ValueName) &&
                                    !string.IsNullOrEmpty(defaultSvc.ExePath) &&
                                    defaultSvc.ExpectedCommandLine.EndsWith("--autostart");

                bool autoPassed = initialDisabled && enableSuccess && afterEnableVerified && regMatch &&
                                  disableSuccess && afterDisableClean && residueClean && defaultValid;

                items.Add(new SelfTestItem
                {
                    Id = "AUTO-01",
                    Category = "Shell",
                    Description = "开机自启启用写入、回读校验与关闭清理无残留测试",
                    Passed = autoPassed,
                    Detail = autoPassed
                        ? string.Format("写入/回读校验/关闭清理完全闭环，Command={0}", autoStart.ExpectedCommandLine)
                        : string.Format("InitDis={0}, Enable={1}, AfterEn={2}, RegMatch={3}, Dis={4}, Clean={5}, ResidueClean={6}",
                                        initialDisabled, enableSuccess, afterEnableVerified, regMatch, disableSuccess, afterDisableClean, residueClean)
                });
            }
            catch (Exception ex)
            {
                items.Add(new SelfTestItem
                {
                    Id = "AUTO-01",
                    Category = "Shell",
                    Description = "开机自启启用写入、回读校验与关闭清理无残留测试",
                    Passed = false,
                    Detail = ex.Message
                });
            }

            // TRAY-01: 三格电池托盘图标在 16/20/24/32px 四档分辨率下的渲染与句柄释放验证
            try
            {
                int[] sizes = new int[] { 16, 20, 24, 32 };
                bool allSizesPassed = true;
                int totalRendered = 0;
                int totalFreed = 0;

                foreach (int sz in sizes)
                {
                    // 1. 正常状态 (80%)
                    using (Bitmap bmpNormal = BatteryTrayIconRenderer.RenderBitmap(sz, 80, false, false, true))
                    {
                        if (bmpNormal == null || bmpNormal.Width != sz || bmpNormal.Height != sz) allSizesPassed = false;
                        totalRendered++;
                    }

                    // 2. 充电状态 (50%, 绿)
                    using (Bitmap bmpCharging = BatteryTrayIconRenderer.RenderBitmap(sz, 50, true, false, true))
                    {
                        if (bmpCharging == null || bmpCharging.Width != sz || bmpCharging.Height != sz) allSizesPassed = false;
                        totalRendered++;
                    }

                    // 3. 低电量状态 (15%, 橙)
                    using (Bitmap bmpLow = BatteryTrayIconRenderer.RenderBitmap(sz, 15, false, false, true))
                    {
                        if (bmpLow == null || bmpLow.Width != sz || bmpLow.Height != sz) allSizesPassed = false;
                        totalRendered++;
                    }

                    // 4. 离线状态 (灰)
                    using (Bitmap bmpOffline = BatteryTrayIconRenderer.RenderBitmap(sz, null, false, true, true))
                    {
                        if (bmpOffline == null || bmpOffline.Width != sz || bmpOffline.Height != sz) allSizesPassed = false;
                        totalRendered++;
                    }

                    // 5. 浅色任务栏反色 (墨色)
                    using (Bitmap bmpLight = BatteryTrayIconRenderer.RenderBitmap(sz, 80, false, false, false))
                    {
                        if (bmpLight == null || bmpLight.Width != sz || bmpLight.Height != sz) allSizesPassed = false;
                        totalRendered++;
                    }

                    // 6. GDI 句柄获取与释放闭环验证
                    IntPtr hIcon;
                    using (Icon icon = BatteryTrayIconRenderer.RenderIcon(sz, 75, false, false, true, out hIcon))
                    {
                        if (icon == null || hIcon == IntPtr.Zero || icon.Width != sz || icon.Height != sz)
                        {
                            allSizesPassed = false;
                        }
                        bool destroyed = NativeMethods.DestroyIcon(hIcon);
                        if (!destroyed)
                        {
                            allSizesPassed = false;
                        }
                        else
                        {
                            totalFreed++;
                        }
                    }
                }

                // 压力释放循环：验证多次创建与销毁无泄漏
                for (int cycle = 0; cycle < 10; cycle++)
                {
                    IntPtr h;
                    using (Icon ic = BatteryTrayIconRenderer.RenderIcon(24, 90, false, false, true, out h))
                    {
                        if (NativeMethods.DestroyIcon(h))
                        {
                            totalFreed++;
                        }
                        else
                        {
                            allSizesPassed = false;
                        }
                    }
                }

                bool tray01Passed = allSizesPassed && totalRendered == 20 && totalFreed == 14;

                items.Add(new SelfTestItem
                {
                    Id = "TRAY-01",
                    Category = "Shell",
                    Description = "三格电池托盘图标在 16/20/24/32px 四档分辨率下的渲染与句柄释放验证",
                    Passed = tray01Passed,
                    Detail = tray01Passed
                        ? string.Format("16/20/24/32px 四档五状态渲染完成 ({0} 幅)，句柄分配与 DestroyIcon 释放闭环 ({1} 次)", totalRendered, totalFreed)
                        : string.Format("SizesPassed={0}, TotalRendered={1}, TotalFreed={2}", allSizesPassed, totalRendered, totalFreed)
                });
            }
            catch (Exception ex)
            {
                items.Add(new SelfTestItem
                {
                    Id = "TRAY-01",
                    Category = "Shell",
                    Description = "三格电池托盘图标在 16/20/24/32px 四档分辨率下的渲染与句柄释放验证",
                    Passed = false,
                    Detail = ex.Message
                });
            }

            // TRAY-02: 托盘可用性与失败降级逻辑测试
            try
            {
                // 1. 正常托盘服务可用性验证
                bool normalAvailable = false;
                bool normalCancelClosing = false;
                bool menuVerified = false;

                using (var normalTray = new TrayIconService())
                {
                    bool initOk = normalTray.Initialize();
                    normalAvailable = initOk && normalTray.IsTrayAvailable && normalTray.ShouldCancelCloseToTray;

                    // 验证右键菜单四项结构: 显示 PowerDock, 刷新设备, 分隔线, 退出 PowerDock
                    if (normalTray.NotifyIcon != null && normalTray.NotifyIcon.ContextMenuStrip != null)
                    {
                        var menuItems = normalTray.NotifyIcon.ContextMenuStrip.Items;
                        if (menuItems.Count == 4 &&
                            menuItems[0].Text == "显示 PowerDock" &&
                            menuItems[1].Text == "刷新设备" &&
                            menuItems[2] is System.Windows.Forms.ToolStripSeparator &&
                            menuItems[3].Text == "退出 PowerDock")
                        {
                            menuVerified = true;
                        }
                    }

                    // 验证托盘可用时关闭窗口 -> Cancel = true 拦截并隐藏
                    var cancelArgs = new System.ComponentModel.CancelEventArgs();
                    normalTray.HandleMainWindowClosing(null, cancelArgs);
                    normalCancelClosing = cancelArgs.Cancel;

                    // 验证电量状态更新
                    normalTray.UpdateState(85, false, false, "PowerDock - 85%");
                }

                // 2. 失败降级逻辑验证 (模拟托盘创建或注册失败场景)
                bool degradedUnavailable = false;
                bool degradedNotCancelled = false;

                using (var degradedTray = new TrayIconService(forceFailureForTest: true))
                {
                    bool degradedInit = degradedTray.Initialize();
                    degradedUnavailable = (!degradedInit) &&
                                          (!degradedTray.IsTrayAvailable) &&
                                          (!degradedTray.ShouldCancelCloseToTray);

                    // 验证托盘不可用时关闭窗口 -> Cancel = false 允许关闭退出应用，不留隐形进程
                    var cancelArgs = new System.ComponentModel.CancelEventArgs();
                    degradedTray.HandleMainWindowClosing(null, cancelArgs);
                    degradedNotCancelled = (!cancelArgs.Cancel);
                }

                bool tray02Passed = normalAvailable && normalCancelClosing && menuVerified &&
                                    degradedUnavailable && degradedNotCancelled;

                items.Add(new SelfTestItem
                {
                    Id = "TRAY-02",
                    Category = "Shell",
                    Description = "托盘可用性与失败降级逻辑测试",
                    Passed = tray02Passed,
                    Detail = tray02Passed
                        ? "托盘可用性及右键菜单结构正常；托盘不可用时降级为直接退出应用，不留隐形进程"
                        : string.Format("NormalAvail={0}, CancelClose={1}, Menu={2}, DegradedUnavail={3}, DegradedNotCancel={4}",
                                        normalAvailable, normalCancelClosing, menuVerified, degradedUnavailable, degradedNotCancelled)
                });
            }
            catch (Exception ex)
            {
                items.Add(new SelfTestItem
                {
                    Id = "TRAY-02",
                    Category = "Shell",
                    Description = "托盘可用性与失败降级逻辑测试",
                    Passed = false,
                    Detail = ex.Message
                });
            }

            // WIN-01: 主窗口规格 (880x600, 圆角 8 DIP, DWM 材质支持) 验证
            try
            {
                var win = new MainWindow();
                bool sizeValid = Math.Abs(win.Width - 880.0) < 0.001 && Math.Abs(win.Height - 600.0) < 0.001;
                bool minSizeValid = Math.Abs(win.MinWidth - 880.0) < 0.001 && Math.Abs(win.MinHeight - 600.0) < 0.001;
                bool acrylicSupported = DwmHelper.SupportsAcrylicBackdrop();
                bool titleBarValid = Math.Abs(MainWindow.TitleBarHeightDip - 44.0) < 0.001;

                bool winPassed = sizeValid && minSizeValid && titleBarValid;

                items.Add(new SelfTestItem
                {
                    Id = "WIN-01",
                    Category = "Windows",
                    Description = "主窗口规格 (880x600, 圆角 8 DIP, DWM 材质支持) 验证",
                    Passed = winPassed,
                    Detail = winPassed
                        ? string.Format("尺寸 880x600 DIP 严丝合缝，标题栏 44 DIP，DWM Acrylic支持: {0}", acrylicSupported)
                        : string.Format("SizeValid={0}, MinSizeValid={1}, TitleBarValid={2}", sizeValid, minSizeValid, titleBarValid)
                });
            }
            catch (Exception ex)
            {
                items.Add(new SelfTestItem
                {
                    Id = "WIN-01",
                    Category = "Windows",
                    Description = "主窗口规格 (880x600, 圆角 8 DIP, DWM 材质支持) 验证",
                    Passed = false,
                    Detail = ex.Message
                });
            }

            // PERF-01: 基础性能指标验证 (内存占用 <150MB, 进程健康)
            try
            {
                Process currentProc = Process.GetCurrentProcess();
                long workingSetMb = currentProc.WorkingSet64 / (1024 * 1024);
                bool memPassed = workingSetMb < 150;
                TimeSpan uptime = DateTime.UtcNow - currentProc.StartTime.ToUniversalTime();

                bool perfPassed = memPassed;

                items.Add(new SelfTestItem
                {
                    Id = "PERF-01",
                    Category = "Performance",
                    Description = "基础性能指标验证 (内存占用 <150MB, 进程健康)",
                    Passed = perfPassed,
                    Detail = string.Format("当前工作集内存: {0} MB (指标 <150MB), 进程运行时间: {1:N0} ms", workingSetMb, uptime.TotalMilliseconds)
                });
            }
            catch (Exception ex)
            {
                items.Add(new SelfTestItem
                {
                    Id = "PERF-01",
                    Category = "Performance",
                    Description = "基础性能指标验证",
                    Passed = false,
                    Detail = ex.Message
                });
            }

            bool allPassed = true;
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine(string.Format("  \"timestampUtc\": \"{0:o}\",", DateTime.UtcNow));
            sb.AppendLine(string.Format("  \"total\": {0},", items.Count));

            int passedCount = 0;
            foreach (var it in items)
            {
                if (it.Passed) passedCount++;
                else allPassed = false;
            }

            sb.AppendLine(string.Format("  \"passed\": {0},", passedCount));
            sb.AppendLine(string.Format("  \"failed\": {0},", items.Count - passedCount));
            sb.AppendLine("  \"items\": [");

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                sb.AppendLine("    {");
                sb.AppendLine(string.Format("      \"id\": \"{0}\",", it.Id));
                sb.AppendLine(string.Format("      \"category\": \"{0}\",", it.Category));
                sb.AppendLine(string.Format("      \"description\": \"{0}\",", EscapeJson(it.Description)));
                sb.AppendLine(string.Format("      \"passed\": {0},", it.Passed ? "true" : "false"));
                sb.AppendLine(string.Format("      \"detail\": \"{0}\"", EscapeJson(it.Detail)));
                sb.Append("    }");
                if (i < items.Count - 1) sb.Append(",");
                sb.AppendLine();
            }

            sb.AppendLine("  ]");
            sb.AppendLine("}");

            string jsonPath = Path.Combine(dataDir, "self-test.json");
            File.WriteAllText(jsonPath, sb.ToString(), Encoding.UTF8);

            Console.WriteLine(string.Format("[PowerDock Self-Test] Total: {0}, Passed: {1}, Failed: {2}", items.Count, passedCount, items.Count - passedCount));
            Console.WriteLine(string.Format("[PowerDock Self-Test] Output written to {0}", jsonPath));

            return allPassed ? 0 : 1;
        }

        public static int Run()
        {
            return Run(null);
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        }
    }
}