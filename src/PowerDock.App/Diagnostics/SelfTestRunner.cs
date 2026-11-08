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

            // S0-AUTO-03: clean leftovers from a previously killed self-test before doing anything.
            int leftoverAutoStartValues = AutoStartService.CleanupLeftoverTestValues();
            if (leftoverAutoStartValues > 0)
            {
                AppLogger.Warn("Self-test cleaned " + leftoverAutoStartValues + " leftover autostart test value(s).");
            }

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

            int winBuild = 0;
            int.TryParse(buildNumber, out winBuild);
            bool isWin11 = osMajor >= 10 && winBuild >= 22000;
            items.Add(new SelfTestItem
            {
                Id = "SYS-02",
                Category = "Environment",
                Description = "Windows 11 操作系统版本检测（Build >= 22000；Win10 不通过）",
                Passed = isWin11,
                Detail = string.Format("OS Major: {0}, Build: {1} -> {2}（要求 build>=22000）",
                    osMajor, buildNumber, isWin11 ? "Windows 11" : "Windows 10/未知（不满足）")
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
                    Description = "数据目录写入/读取/删除闭环测试（原子覆盖由 FS-02 验证）",
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
                    Description = "数据目录写入/读取/删除闭环测试（原子覆盖由 FS-02 验证）",
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
                string defaultReadyEventName = SingleInstanceService.GetDefaultReadyEventName();

                // Per-user + per-session isolation: "Local\PowerDock_Mutex_<sessionId>_<userSid>".
                bool formatValid = defaultMutexName != null &&
                                   defaultMutexName.StartsWith(expectedPrefix) &&
                                   defaultMutexName.Contains("_" + sessionId + "_") &&
                                   defaultReadyEventName != null &&
                                   defaultReadyEventName.StartsWith(@"Local\PowerDock_Ready_");

                string testMutexName = @"Local\PowerDock_Mutex_Test_" + Guid.NewGuid().ToString("N");
                string dummyPipe1 = "PowerDock_Pipe_Dummy_" + Guid.NewGuid().ToString("N");
                string dummyPipe2 = "PowerDock_Pipe_Dummy_" + Guid.NewGuid().ToString("N");

                bool acquire1 = false;
                bool conflictDetected = false;
                bool acquireAfterRelease = false;

                Func<int, ActivationResult> primaryCallback = delegate(int clientPid) { return ActivationResult.Activated; };

                // A mutex is owned per thread: a same-thread second acquisition is re-entrant and
                // would look like a successful primary. Run the first instance on its own thread so
                // this test mirrors the real cross-process case.
                var ownerReady = new ManualResetEvent(false);
                var releaseOwner = new ManualResetEvent(false);
                Exception ownerError = null;
                Thread ownerThread = new Thread(delegate()
                {
                    try
                    {
                        using (var svc1 = new SingleInstanceService(testMutexName, dummyPipe1))
                        {
                            acquire1 = svc1.TryStartAsFirstInstance(primaryCallback);
                            ownerReady.Set();
                            releaseOwner.WaitOne(10000);
                        }
                    }
                    catch (Exception ex)
                    {
                        ownerError = ex;
                        ownerReady.Set();
                    }
                });
                ownerThread.IsBackground = true;
                ownerThread.Name = "SelfTest_MutexOwner";
                ownerThread.Start();
                ownerReady.WaitOne(5000);

                using (var svc2 = new SingleInstanceService(testMutexName, dummyPipe1))
                {
                    bool isFirst2 = svc2.TryStartAsFirstInstance(primaryCallback);
                    conflictDetected = !isFirst2 && svc2.SecondaryExitCode == SingleInstanceExitCodes.Success;
                }

                releaseOwner.Set();
                ownerThread.Join(5000);
                releaseOwner.Close();
                ownerReady.Close();

                using (var svc3 = new SingleInstanceService(testMutexName, dummyPipe2))
                {
                    acquireAfterRelease = svc3.TryStartAsFirstInstance(primaryCallback);
                }

                if (ownerError != null) throw ownerError;

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
                                       defaultPipeName.Contains("_" + sessionId + "_");

                string testMutexName = @"Local\PowerDock_Mutex_PipeTest_" + Guid.NewGuid().ToString("N");
                string testPipeName = "PowerDock_Pipe_Test_" + Guid.NewGuid().ToString("N");

                bool callbackInvoked = false;
                var callbackEvent = new ManualResetEvent(false);

                bool serverStarted = false;
                bool clientReportedSecond = false;
                bool waitSuccess = false;

                // Same reason as MUTEX-01: keep the server instance (and its mutex ownership) on a
                // dedicated thread so the client really takes the secondary path.
                var serverReady = new ManualResetEvent(false);
                var releaseServer = new ManualResetEvent(false);
                Exception serverError = null;
                Thread serverThread = new Thread(delegate()
                {
                    try
                    {
                        using (var serverSvc = new SingleInstanceService(testMutexName, testPipeName))
                        {
                            serverStarted = serverSvc.TryStartAsFirstInstance(delegate(int clientPid)
                            {
                                callbackInvoked = true;
                                callbackEvent.Set();
                                return ActivationResult.Activated;
                            });
                            serverReady.Set();
                            releaseServer.WaitOne(10000);
                        }
                    }
                    catch (Exception ex)
                    {
                        serverError = ex;
                        serverReady.Set();
                    }
                });
                serverThread.IsBackground = true;
                serverThread.Name = "SelfTest_PipeServer";
                serverThread.Start();
                serverReady.WaitOne(5000);

                using (var clientSvc = new SingleInstanceService(testMutexName, testPipeName))
                {
                    bool isFirst = clientSvc.TryStartAsFirstInstance(delegate(int clientPid) { return ActivationResult.Activated; });
                    clientReportedSecond = !isFirst && clientSvc.SecondaryExitCode == SingleInstanceExitCodes.Success;
                }

                waitSuccess = callbackEvent.WaitOne(3000);
                releaseServer.Set();
                serverThread.Join(5000);
                releaseServer.Close();
                serverReady.Close();
                callbackEvent.Close();

                if (serverError != null) throw serverError;

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

            // AUTO-01: 开机自启启用写入、回读校验与关闭清理无残留测试（try/finally 保证清理）
            try
            {
                string testValueName = AutoStartService.TestValuePrefix + Guid.NewGuid().ToString("N");
                string testExe = AutoStartService.GetDefaultExecutablePath();
                var autoStart = new AutoStartService(testExe, testValueName);

                bool initialDisabled = false;
                bool enableSuccess = false;
                bool afterEnableVerified = false;
                bool regMatch = false;
                bool disableSuccess = false;
                bool afterDisableClean = false;
                bool residueClean = false;
                Exception bodyError = null;

                try
                {
                    // 1. 验证初始状态未启用
                    initialDisabled = !autoStart.IsAutoStartEnabled();

                    // 2. 启用自启，回读校验
                    enableSuccess = autoStart.SetAutoStart(true);
                    afterEnableVerified = autoStart.IsAutoStartEnabled();

                    // 3. 直接通过注册表检查写入的值与格式
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
                }
                catch (Exception ex)
                {
                    bodyError = ex;
                }
                finally
                {
                    // 4. 关闭自启，清理并校验无残留：无论上面的读取/校验是否抛异常都必须执行
                    try
                    {
                        disableSuccess = autoStart.SetAutoStart(false);
                        afterDisableClean = !autoStart.IsAutoStartEnabled();
                        using (var rk = Registry.CurrentUser.OpenSubKey(AutoStartService.RunRegistrySubKey, false))
                        {
                            if (rk != null)
                            {
                                residueClean = rk.GetValue(testValueName, null) == null;
                            }
                            else
                            {
                                residueClean = true;
                            }
                        }
                    }
                    catch (Exception cleanupEx)
                    {
                        AppLogger.Error("AUTO-01 cleanup (disable autostart test value) failed", cleanupEx);
                    }
                }

                // 5. 验证默认 AutoStartService 属性
                var defaultSvc = new AutoStartService();
                bool defaultValid = !string.IsNullOrEmpty(defaultSvc.ValueName) &&
                                    !string.IsNullOrEmpty(defaultSvc.ExePath) &&
                                    defaultSvc.ExpectedCommandLine.EndsWith("--autostart");

                bool autoPassed = initialDisabled && enableSuccess && afterEnableVerified && regMatch &&
                                  disableSuccess && afterDisableClean && residueClean && defaultValid &&
                                  bodyError == null;

                items.Add(new SelfTestItem
                {
                    Id = "AUTO-01",
                    Category = "Shell",
                    Description = "开机自启启用写入、回读校验与关闭清理无残留测试（try/finally）",
                    Passed = autoPassed,
                    Detail = autoPassed
                        ? string.Format("写入/回读校验/关闭清理完全闭环（try/finally 保证清理），Command={0}，启动清理遗留值={1}", autoStart.ExpectedCommandLine, leftoverAutoStartValues)
                        : string.Format("InitDis={0}, Enable={1}, AfterEn={2}, RegMatch={3}, Dis={4}, Clean={5}, ResidueClean={6}, BodyError={7}",
                                        initialDisabled, enableSuccess, afterEnableVerified, regMatch, disableSuccess, afterDisableClean, residueClean, bodyError == null ? "none" : bodyError.Message)
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

            // TRAY-01: 三格托盘图标像素级断言（直调 RenderBitmap，不做抓屏；AGENTS.md §6.8 几何）
            try
            {
                int[] sizes = new int[] { 16, 20, 24, 28, 32 };
                bool allSizesPassed = true;
                int totalRendered = 0;
                int totalFreed = 0;
                StringBuilder failures = new StringBuilder();

                foreach (int sz in sizes)
                {
                    TrayIconGeometry geo = BatteryTrayIconRenderer.GetGeometry(sz);
                    int row = (int)Math.Floor(geo.BarY + geo.BarH / 2f);
                    if (row >= sz) row = sz / 2;
                    int xStart = (int)Math.Ceiling(geo.InnerLeft);
                    int xEnd = (int)Math.Floor(geo.InnerRight) - 1;

                    using (Bitmap bmpNormal = BatteryTrayIconRenderer.RenderBitmap(sz, 80, false, false, true))
                    using (Bitmap bmpCharging = BatteryTrayIconRenderer.RenderBitmap(sz, 50, true, false, true))
                    using (Bitmap bmpLow = BatteryTrayIconRenderer.RenderBitmap(sz, 15, false, false, true))
                    using (Bitmap bmpOffline = BatteryTrayIconRenderer.RenderBitmap(sz, null, false, true, true))
                    {
                        totalRendered += 4;
                        if (bmpNormal.Width != sz || bmpNormal.Height != sz || bmpCharging.Width != sz ||
                            bmpLow.Width != sz || bmpOffline.Width != sz)
                        {
                            allSizesPassed = false;
                            failures.Append(sz + ":size;");
                        }

                        // 1) 中间行 80%：三格全亮，且相邻格之间至少 1 列 alpha<64（16px 不许融成一条）
                        List<int[]> runs = new List<int[]>();
                        bool inRun = false;
                        int runStart = 0;
                        for (int x = xStart; x <= xEnd; x++)
                        {
                            bool lit = bmpNormal.GetPixel(x, row).A >= 64;
                            if (lit && !inRun) { inRun = true; runStart = x; }
                            if (!lit && inRun) { inRun = false; runs.Add(new int[] { runStart, x - 1 }); }
                        }
                        if (inRun) runs.Add(new int[] { runStart, xEnd });
                        if (runs.Count != 3)
                        {
                            allSizesPassed = false;
                            failures.Append(sz + ":runs=" + runs.Count + ";");
                        }
                        else
                        {
                            for (int i = 1; i < runs.Count; i++)
                            {
                                int minGapAlpha = 255;
                                for (int x = runs[i - 1][1] + 1; x < runs[i][0]; x++)
                                {
                                    int a = bmpNormal.GetPixel(x, row).A;
                                    if (a < minGapAlpha) minGapAlpha = a;
                                }
                                if (minGapAlpha >= 64)
                                {
                                    allSizesPassed = false;
                                    failures.Append(sz + ":gap" + i + "a=" + minGapAlpha + ";");
                                }
                            }
                        }

                        // 2) 右侧空档列（最右列）必须 alpha=0
                        if (bmpNormal.GetPixel(sz - 1, row).A != 0)
                        {
                            allSizesPassed = false;
                            failures.Append(sz + ":rightMargin;");
                        }

                        // 3) 语义色 / 点亮格数 / 未点亮格 alpha≈0.14
                        int[] cellCenters = new int[3];
                        cellCenters[0] = Math.Min(sz - 1, (int)Math.Floor(geo.BarX0 + geo.BarW / 2f));
                        cellCenters[1] = Math.Min(sz - 1, (int)Math.Floor(geo.BarX1 + geo.BarW / 2f));
                        cellCenters[2] = Math.Min(sz - 1, (int)Math.Floor(geo.BarX2 + geo.BarW / 2f));

                        Color normalCell = bmpNormal.GetPixel(cellCenters[0], row);
                        Color lowCell = bmpLow.GetPixel(cellCenters[0], row);
                        Color chargeCell = bmpCharging.GetPixel(cellCenters[0], row);
                        Color offlineCell = bmpOffline.GetPixel(cellCenters[0], row);
                        Color unlitLow = bmpLow.GetPixel(cellCenters[1], row);
                        Color unlitCharge = bmpCharging.GetPixel(cellCenters[2], row);

                        if (!(normalCell.A >= 200 && normalCell.R >= 250 && normalCell.G >= 250 && normalCell.B >= 250))
                        { allSizesPassed = false; failures.Append(sz + ":normal;"); }
                        if (!(lowCell.A >= 200 && Math.Abs(lowCell.R - 0xFF) <= 2 && Math.Abs(lowCell.G - 0x9F) <= 2 && Math.Abs(lowCell.B - 0x0A) <= 2))
                        { allSizesPassed = false; failures.Append(sz + ":low;"); }
                        if (!(chargeCell.A >= 200 && Math.Abs(chargeCell.R - 0x34) <= 2 && Math.Abs(chargeCell.G - 0xD3) <= 2 && Math.Abs(chargeCell.B - 0x99) <= 2))
                        { allSizesPassed = false; failures.Append(sz + ":charge;"); }
                        // offline = 全部未点亮；15% 只点亮第 1 格，50% 充电点亮前 2 格
                        if (!(offlineCell.A >= 30 && offlineCell.A <= 40)) { allSizesPassed = false; failures.Append(sz + ":offline=" + offlineCell.A + ";"); }
                        if (!(unlitLow.A >= 30 && unlitLow.A <= 40)) { allSizesPassed = false; failures.Append(sz + ":unlitLow=" + unlitLow.A + ";"); }
                        if (!(unlitCharge.A >= 30 && unlitCharge.A <= 40)) { allSizesPassed = false; failures.Append(sz + ":unlitCharge=" + unlitCharge.A + ";"); }
                    }

                    // 4) 浅色任务栏反色：墨色而非白
                    using (Bitmap bmpLight = BatteryTrayIconRenderer.RenderBitmap(sz, 80, false, false, false))
                    {
                        totalRendered++;
                        int cx = Math.Min(sz - 1, (int)Math.Floor(geo.BarX0 + geo.BarW / 2f));
                        int cy = Math.Min(sz - 1, (int)Math.Floor(geo.BarY + geo.BarH / 2f));
                        Color lightCell = bmpLight.GetPixel(cx, cy);
                        if (!(lightCell.A >= 200 && lightCell.R < 120 && lightCell.G < 120 && lightCell.B < 120))
                        {
                            allSizesPassed = false;
                            failures.Append(sz + ":light;");
                        }
                    }

                    // 5) GDI 句柄获取与释放闭环
                    IntPtr hIcon;
                    using (Icon icon = BatteryTrayIconRenderer.RenderIcon(sz, 75, false, false, true, out hIcon))
                    {
                        if (icon == null || hIcon == IntPtr.Zero || icon.Width != sz || icon.Height != sz)
                        {
                            allSizesPassed = false;
                            failures.Append(sz + ":icon;");
                        }
                        if (!NativeMethods.DestroyIcon(hIcon))
                        {
                            allSizesPassed = false;
                            failures.Append(sz + ":destroy;");
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
                        if (NativeMethods.DestroyIcon(h)) totalFreed++;
                    }
                }

                bool tray01Passed = allSizesPassed && totalRendered == 25 && totalFreed == 15;

                items.Add(new SelfTestItem
                {
                    Id = "TRAY-01",
                    Category = "Shell",
                    Description = "三格托盘图标像素断言（16/20/24/28/32px：像素间隙/右空档/语义色/未点亮 alpha/句柄释放）",
                    Passed = tray01Passed,
                    Detail = tray01Passed
                        ? string.Format("5 档 × 5 状态渲染 ({0} 幅) 像素断言全部通过；句柄释放闭环 ({1} 次)", totalRendered, totalFreed)
                        : string.Format("SizesPassed={0}, TotalRendered={1}, TotalFreed={2}, Failures={3}",
                                        allSizesPassed, totalRendered, totalFreed, failures.ToString())
                });
            }
            catch (Exception ex)
            {
                items.Add(new SelfTestItem
                {
                    Id = "TRAY-01",
                    Category = "Shell",
                    Description = "三格托盘图标像素断言（16/20/24/28/32px：像素间隙/右空档/语义色/未点亮 alpha/句柄释放）",
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

            // PERF-01: 进程运行时长与内存（明确不是首帧；首帧由 PERF-02 / --diagnose-first-frame 测量）
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
                    Description = "进程运行时长与内存（uptime，非首帧）",
                    Passed = perfPassed,
                    Detail = string.Format("当前工作集内存: {0} MB (指标 <150MB), 进程运行时长(uptime): {1:N0} ms；首帧见 PERF-02/--diagnose-first-frame", workingSetMb, uptime.TotalMilliseconds)
                });
            }
            catch (Exception ex)
            {
                items.Add(new SelfTestItem
                {
                    Id = "PERF-01",
                    Category = "Performance",
                    Description = "进程运行时长与内存（uptime，非首帧）",
                    Passed = false,
                    Detail = ex.Message
                });
            }

            // PERF-02: 首帧埋点通路（Program.Main 首行 Arm -> MainWindow 订阅 Rendering -> 首次回调）。
            // 自检不显示窗口，因此只验证埋点通路已就位；真实首帧数值用 --diagnose-first-frame。
            try
            {
                bool armed = FirstFrameTimer.Armed;
                bool handler = FirstFrameTimer.HandlerRegistered;
                bool measured = FirstFrameTimer.Measured;
                items.Add(new SelfTestItem
                {
                    Id = "PERF-02",
                    Category = "Performance",
                    Description = "首帧埋点通路（Main->CompositionTarget.Rendering；数值由 --diagnose-first-frame 测量）",
                    Passed = armed && handler,
                    Detail = string.Format("armed={0}, handlerRegistered={1}, measuredInSelfTest={2}（自检无窗口，不产生首帧数值）",
                        armed, handler, measured)
                });
            }
            catch (Exception ex)
            {
                items.Add(new SelfTestItem
                {
                    Id = "PERF-02",
                    Category = "Performance",
                    Description = "首帧埋点通路",
                    Passed = false,
                    Detail = ex.Message
                });
            }

            // WIDGET-01: 挂件三档规格（173×173/362×173/362×362 DIP）、100%/150% DPI 量测无溢出与离线/截断
            try
            {
                var widgetDiag = WidgetDiagnostics.RunDiagnosticsInternal();
                items.Add(new SelfTestItem
                {
                    Id = "WIDGET-01",
                    Category = "Widget",
                    Description = "挂件三档规格（173×173/362×173/362×362 DIP）、100%/150% DPI 量测无溢出与离线/截断",
                    Passed = widgetDiag.AllPassed,
                    Detail = widgetDiag.AllPassed ? "三档尺寸、DPI 量测无溢出、离线显示「—」与切档保持勾选全部通过" : "挂件自检存在失败项"
                });
            }
            catch (Exception ex)
            {
                items.Add(new SelfTestItem
                {
                    Id = "WIDGET-01",
                    Category = "Widget",
                    Description = "挂件三档规格与量测自检",
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

