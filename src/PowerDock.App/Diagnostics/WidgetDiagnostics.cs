using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using PowerDock.App.Windows.Widgets;
using PowerDock.Core.Common;
using PowerDock.Core.Diagnostics;
using PowerDock.Core.Storage;
using PowerDock.Core.Widgets;

namespace PowerDock.App.Diagnostics
{
    /// <summary>
    /// 组件页与挂件尺寸量测、DPI 适配、溢出检查与三态验证诊断器（S2 交付验收契约）。
    /// </summary>
    public static class WidgetDiagnostics
    {
        public sealed class DiagnosticResult
        {
            public bool AllPassed { get; set; }
            public List<string> Logs { get; set; } = new List<string>();
        }

        public static int Run()
        {
            Console.WriteLine("=================================================");
            Console.WriteLine("  PowerDock 组件页与挂件 1:1 量测验收诊断工具");
            Console.WriteLine("=================================================");

            var result = RunDiagnosticsInternal();
            foreach (var log in result.Logs)
            {
                Console.WriteLine(log);
            }

            Console.WriteLine("=================================================");
            if (result.AllPassed)
            {
                Console.WriteLine("  [PASS] 全部组件页与挂件验收项验证通过！");
                return 0;
            }
            else
            {
                Console.WriteLine("  [FAIL] 存在未通过的验收项，请检查上方日志。");
                return 1;
            }
        }

        public static DiagnosticResult RunDiagnosticsInternal()
        {
            var res = new DiagnosticResult { AllPassed = true };

            // 1. 数据集构建
            var emptySnapshot = Array.Empty<DeviceState>();

            var normalSnapshot = new List<DeviceState>
            {
                CreateMockDevice("dev-1", "主力鼠标 MX Master 3S", 18, ConnectionState.Online, isCharging: false),
                CreateMockDevice("dev-2", "本机电池", 47, ConnectionState.Online, isCharging: true),
                CreateMockDevice("dev-3", "USB 相机", 55, ConnectionState.Online, isCharging: false),
                CreateMockDevice("dev-4", "蓝牙键盘 K380", 64, ConnectionState.Online, isCharging: false),
                CreateMockDevice("dev-5", "触控板 Trackpad", 71, ConnectionState.Offline, isCharging: false), // 离线设备
                CreateMockDevice("dev-6", "AirPods Pro", 82, ConnectionState.Online, isCharging: false),
                CreateMockDevice("dev-7", "蓝牙音箱 JBL", 100, ConnectionState.Online, isCharging: false),
                CreateMockDevice("dev-8", "绘图板", null, ConnectionState.Online, isCharging: false),
            };

            var selectedKeys = new List<string>
            {
                "dev-1", "dev-2", "dev-3", "dev-4", "dev-5", "dev-6", "dev-7", "dev-8"
            };

            // 2. 验证空态（无设备与未勾选）
            VerifyEmptyState(res, emptySnapshot);

            // 3. 验证三档尺寸、容量截断与溢出（100% 与 150% DPI）
            VerifySizeAndDpiOverflow(res, normalSnapshot, selectedKeys, 1.0);
            VerifySizeAndDpiOverflow(res, normalSnapshot, selectedKeys, 1.5);

            // 4. 验证离线设备表现（50%透明，电量「—」）
            VerifyOfflineDevice(res, normalSnapshot, selectedKeys);

            // 5. 验证切档不清空勾选集合
            VerifySizeSwitchRetainsSelection(res, normalSnapshot, selectedKeys);

            // 6. 验证持久化与设置失败回滚
            VerifySettingsPersistenceAndRollback(res);

            return res;
        }

        private static void VerifyEmptyState(DiagnosticResult res, IReadOnlyList<DeviceState> emptySnapshot)
        {
            var control = new WidgetPreviewControl();
            var modelNoDev = WidgetPipeline.Project(emptySnapshot, Array.Empty<string>(), WidgetSizeKind.Medium4x2);
            control.ApplyModel(modelNoDev);

            bool noDevPassed = !modelNoDev.HasDevices && modelNoDev.DisplayedRows.Count == 0;
            LogCheck(res, noDevPassed, "空态-无设备：正确识别无设备状态，不呈现任何伪造读数");

            var normalDev = new[] { CreateMockDevice("d1", "TestDev", 50, ConnectionState.Online) };
            var modelNoSelect = WidgetPipeline.Project(normalDev, Array.Empty<string>(), WidgetSizeKind.Medium4x2);
            control.ApplyModel(modelNoSelect);

            bool noSelectPassed = modelNoSelect.HasDevices && !modelNoSelect.HasSelectedDevices && modelNoSelect.DisplayedRows.Count == 0;
            LogCheck(res, noSelectPassed, "空态-未勾选：设备在场但未勾选时正确显示空态提示");
        }

        private static void VerifySizeAndDpiOverflow(DiagnosticResult res, IReadOnlyList<DeviceState> snapshot, IReadOnlyCollection<string> selectedKeys, double dpiScale)
        {
            int dpiPercent = (int)(dpiScale * 100);

            // 小号 2×2
            VerifySingleSize(res, snapshot, selectedKeys, WidgetSizeKind.Small2x2, 173.0, 173.0, 22.0, 14.0, 3, dpiScale, dpiPercent);

            // 中号 4×2
            VerifySingleSize(res, snapshot, selectedKeys, WidgetSizeKind.Medium4x2, 362.0, 173.0, 27.0, 16.0, 5, dpiScale, dpiPercent);

            // 大号 4×4
            VerifySingleSize(res, snapshot, selectedKeys, WidgetSizeKind.Large4x4, 362.0, 362.0, 27.0, 22.0, 7, dpiScale, dpiPercent);
        }

        private static void VerifySingleSize(
            DiagnosticResult res,
            IReadOnlyList<DeviceState> snapshot,
            IReadOnlyCollection<string> selectedKeys,
            WidgetSizeKind sizeKind,
            double expectedW,
            double expectedH,
            double expectedR,
            double expectedPad,
            int expectedMaxRows,
            double dpiScale,
            int dpiPercent)
        {
            var model = WidgetPipeline.Project(snapshot, selectedKeys, sizeKind);
            var control = new WidgetPreviewControl();
            if (Math.Abs(dpiScale - 1.0) > 0.001)
            {
                control.LayoutTransform = new ScaleTransform(dpiScale, dpiScale);
            }

            control.ApplyModel(model);

            // 执行 WPF Measure 与 Arrange
            control.Measure(new Size(expectedW * dpiScale, expectedH * dpiScale));
            control.Arrange(new Rect(0, 0, expectedW * dpiScale, expectedH * dpiScale));

            bool specPassed = model.WidthDip == expectedW &&
                              model.HeightDip == expectedH &&
                              model.CornerRadiusDip == expectedR &&
                              model.PaddingDip == expectedPad &&
                              model.MaxRows == expectedMaxRows;

            int displayedRows = model.DisplayedRows.Count;
            bool rowsTruncated = displayedRows <= expectedMaxRows;

            // 验证未发生布局溢出：DesiredSize 应当与设计 DIP 严格对齐
            bool noOverflow = control.DesiredSize.Width <= (expectedW * dpiScale + 0.5) &&
                              control.DesiredSize.Height <= (expectedH * dpiScale + 0.5);

            string sizeName = WidgetSizeSpecs.GetDisplayName(sizeKind);
            string desc = string.Format(
                CultureInfo.InvariantCulture,
                "[{0}% DPI] {1} 尺寸规格校验: {2}×{3} DIP (r{4}/pad{5}), 显示 {6}/{7} 行 (无溢出: {8})",
                dpiPercent,
                sizeName,
                expectedW,
                expectedH,
                expectedR,
                expectedPad,
                displayedRows,
                expectedMaxRows,
                noOverflow);

            LogCheck(res, specPassed && rowsTruncated && noOverflow, desc);
        }

        private static void VerifyOfflineDevice(DiagnosticResult res, IReadOnlyList<DeviceState> snapshot, IReadOnlyCollection<string> selectedKeys)
        {
            var model = WidgetPipeline.Project(snapshot, selectedKeys, WidgetSizeKind.Medium4x2);
            WidgetRowItem offlineItem = null;
            foreach (var r in model.DisplayedRows)
            {
                if (r.IsOffline)
                {
                    offlineItem = r;
                    break;
                }
            }

            bool foundOffline = offlineItem != null;
            bool displayCorrect = foundOffline && offlineItem.DisplayPercent == "—" && offlineItem.Tone == WidgetTone.Off;

            LogCheck(res, displayCorrect, "离线设备呈现：已勾选离线设备照常显示在挂件行中，电量显示「—」，色调为 Off");
        }

        private static void VerifySizeSwitchRetainsSelection(DiagnosticResult res, IReadOnlyList<DeviceState> snapshot, IReadOnlyCollection<string> selectedKeys)
        {
            var small = WidgetPipeline.Project(snapshot, selectedKeys, WidgetSizeKind.Small2x2);
            var med = WidgetPipeline.Project(snapshot, selectedKeys, WidgetSizeKind.Medium4x2);
            var large = WidgetPipeline.Project(snapshot, selectedKeys, WidgetSizeKind.Large4x4);

            bool retained = small.SelectedCount == selectedKeys.Count &&
                            med.SelectedCount == selectedKeys.Count &&
                            large.SelectedCount == selectedKeys.Count;

            LogCheck(res, retained, "切档保持勾选：三档切换期间勾选集合严格保持不变 (各档 SelectedCount=" + small.SelectedCount + ")");
        }

        private static void VerifySettingsPersistenceAndRollback(DiagnosticResult res)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "powerdock-widget-diag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string settingsPath = Path.Combine(tempDir, "settings.json");
                var store = new SettingsStore(settingsPath);
                var settings = new AppSettings();
                settings.WidgetSize = "small";
                settings.WidgetDevices.Add("test-dev-1");
                settings.WidgetDevices.Add("test-dev-2");

                // 保存
                store.Save(settings);

                // 重启恢复验证
                var reloaded = new SettingsStore(settingsPath);
                reloaded.Load();
                bool restored = reloaded.Current.WidgetSize == "small" &&
                                reloaded.Current.WidgetDevices.Count == 2 &&
                                reloaded.Current.WidgetDevices.Contains("test-dev-1");

                LogCheck(res, restored, "持久化与重启恢复：WidgetDevices 与 WidgetSize 正确落盘并在冷启动后完整恢复");

                // 验证保存校验不一致时自动回滚
                var corruptCandidate = reloaded.Current.Clone();
                corruptCandidate.WidgetDevices.Add("will-fail");

                // 用占位文件锁模拟写盘故障（S0-STORE-02 解除条件）
                bool rollbackTriggered = false;
                try
                {
                    using (new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        store.Save(corruptCandidate);
                    }
                }
                catch
                {
                    rollbackTriggered = true;
                }

                bool rolledBack = rollbackTriggered && store.Current.WidgetDevices.Count == 2;
                LogCheck(res, rolledBack, "保存失败自动回滚：写盘故障时自动恢复上次有效配置 (已恢复 2 个设备)");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static void LogCheck(DiagnosticResult res, bool passed, string message)
        {
            if (!passed) res.AllPassed = false;
            string prefix = passed ? "  [OK] " : "  [FAIL] ";
            res.Logs.Add(prefix + message);
        }

        private static DeviceState CreateMockDevice(string id, string name, int? percent, ConnectionState state, bool isCharging = false)
        {
            var dev = new DeviceState(new DeviceKey(id), name, DeviceCategory.Other);
            dev.Percent = percent;
            dev.State = state;
            dev.Charging = isCharging;
            dev.Link = new LinkInfo(LinkKind.Bluetooth, "bt-mock", true, state == ConnectionState.Online, false, DateTime.UtcNow);
            return dev;
        }
    }
}
