using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PowerDock.App.Acquisition;
using PowerDock.App.Shell;
using PowerDock.Core.Common;
using PowerDock.Core.Diagnostics;
using PowerDock.Core.Running;
using PowerDock.Core.State;

namespace PowerDock.App.Diagnostics
{
    /// <summary>
    /// --diagnose-read &lt;秒数&gt; 无界面采样。输出（脱敏后可直接随 PR 提交）：
    /// 1. 发现：设备数、身份是否可信、链路与识别依据；
    /// 2. 链路依据、配对/连接、在线状态；
    /// 3. 读数来源、失败类别、首读耗时、状态变更次数；
    /// 4. 事件原文写入 events.jsonl，摘要写入 summary.json。
    /// 判据口径与 S1 清单 §1.1 口径 B 一致：首读相对 Main 首行，仅 Percent != null 才算达成。
    /// </summary>
    public static class ReadDiagnosticsRunner
    {
        public static int Run(int seconds)
        {
            if (seconds <= 0) seconds = 20;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var diagnostics = new ReadDiagnostics();
            diagnostics.Start();

            var dispatcher = Dispatcher.CurrentDispatcher;
            using (var host = new AcquisitionHost(dispatcher))
            {
                host.Subscribe(delegate(System.Collections.Generic.IReadOnlyList<DeviceState> snapshot)
                {
                    diagnostics.Observe(snapshot);
                });

                try
                {
                    host.StartAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    AppLogger.Error("diagnose-read acquisition start failed", ex);
                    Console.WriteLine("[diagnose-read] start failed: " + ex.GetType().Name + ": " + ex.Message);
                    return 60;
                }

                // 首轮发现后立刻刷新一次，避免把「发现」误当「首读」。
                try
                {
                    host.RefreshNowAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("diagnose-read initial refresh failed: " + ex.Message);
                }

                var deadline = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < deadline)
                {
                    // 让 Dispatcher 队列里的快照回调有机会执行（本方法运行在 STA 线程上）。
                    DispatcherFrame frame = new DispatcherFrame();
                    var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
                    timer.Interval = TimeSpan.FromMilliseconds(200);
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        frame.Continue = false;
                    };
                    timer.Start();
                    Dispatcher.PushFrame(frame);

                    if (diagnostics.ReadingCount > 0 && seconds > 0 && DateTime.UtcNow >= deadline.AddSeconds(-seconds + 8))
                    {
                        // 已拿到读数且观察窗口足够，提前进入稳定期统计。
                    }
                }

                string directory = AppPaths.DiagnoseDirectory();
                string summaryPath;
                try
                {
                    summaryPath = diagnostics.Write(directory, "diagnose-read");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[diagnose-read] failed to write diagnostics: " + ex.Message);
                    return 61;
                }

                PrintSummary(host.LastSnapshot, diagnostics, stopwatch.ElapsedMilliseconds, summaryPath);
                return diagnostics.ReadingCount > 0 ? 0 : 62;
            }
        }

        private static void PrintSummary(System.Collections.Generic.IReadOnlyList<DeviceState> snapshot, ReadDiagnostics diagnostics, long elapsedMs, string summaryPath)
        {
            Console.WriteLine("[diagnose-read] elapsed=" + elapsedMs + "ms readings=" + diagnostics.ReadingCount);
            if (snapshot != null)
            {
                foreach (DeviceState state in snapshot)
                {
                    Console.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "  device={0} name=\"{1}\" identity_verified={2} link={3} ({4}) paired={5} connected={6} online={7} percent={8} reason={9} source={10} stale={11} conflict={12}",
                        DiagnosticRedaction.ShortHash(state.Key.Value),
                        state.EffectiveName,
                        state.IdentityVerified,
                        ContractText.Key(state.Link != null ? state.Link.Kind : LinkKind.Unknown),
                        state.Link != null ? state.Link.Describe() : "unknown",
                        state.Link != null && state.Link.IsPaired,
                        state.Link != null && state.Link.IsConnected,
                        ContractText.Key(state.State),
                        state.Percent.HasValue ? state.Percent.Value.ToString(CultureInfo.InvariantCulture) : "null",
                        ContractText.Key(state.PercentReason),
                        state.AuthoritativeSource.HasValue ? state.AuthoritativeSource.Value.ToString() : "none",
                        state.IsPercentStale,
                        state.HasConflict));
                }
            }
            Console.WriteLine("[diagnose-read] summary=" + summaryPath);
        }
    }
}
