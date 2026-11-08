using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using PowerDock.Core.Common;
using PowerDock.Core.State;

namespace PowerDock.Core.Diagnostics
{
    /// <summary>
    /// --diagnose-read 的采集证据输出。约束：
    /// 1. 逐事件 JSONL，字段名稳定（英文），供脚本判读，不依赖本地化文案；
    /// 2. 摘要含发现数、链路依据、连接状态、读数来源、失败类别、首读耗时与状态变更次数；
    /// 3. 设备标识一律脱敏（HID 实例路径、蓝牙地址），对外可直接提交。
    /// </summary>
    public sealed class ReadDiagnostics
    {
        private readonly object _sync = new object();
        private readonly List<string> _events = new List<string>();
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly Dictionary<string, int> _stateChanges = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lastState = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _firstReadMs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private int _readingCount;

        public void Start()
        {
            _clock.Start();
        }

        public long ElapsedMs
        {
            get { return _clock.ElapsedMilliseconds; }
        }

        public int ReadingCount
        {
            get { lock (_sync) { return _readingCount; } }
        }

        /// <summary>记录一条快照变化。同一设备的状态切换会累计到 statusChanges。</summary>
        public void Observe(IReadOnlyList<DeviceState> snapshot)
        {
            if (snapshot == null) return;
            long now = _clock.ElapsedMilliseconds;
            lock (_sync)
            {
                foreach (DeviceState state in snapshot)
                {
                    string key = state.Key.Value;
                    string current = ContractText.Key(state.State);
                    string previous;
                    if (_lastState.TryGetValue(key, out previous) && !string.Equals(previous, current, StringComparison.Ordinal))
                    {
                        int changes;
                        _stateChanges.TryGetValue(key, out changes);
                        _stateChanges[key] = changes + 1;
                    }
                    _lastState[key] = current;

                    if (state.Percent.HasValue && !_firstReadMs.ContainsKey(key))
                    {
                        _firstReadMs[key] = now;
                        _readingCount++;
                    }

                    _events.Add(ToJsonLine(now, state));
                }
            }
        }

        private static string ToJsonLine(long elapsedMs, DeviceState state)
        {
            var sb = new StringBuilder(320);
            sb.Append('{');
            sb.Append("\"t_ms\":").Append(elapsedMs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"device\":\"").Append(Esc(DiagnosticRedaction.ShortHash(state.Key.Value))).Append('"');
            sb.Append(",\"name\":\"").Append(Esc(state.EffectiveName)).Append('"');
            sb.Append(",\"identity_verified\":").Append(state.IdentityVerified ? "true" : "false");
            sb.Append(",\"link\":\"").Append(ContractText.Key(state.Link != null ? state.Link.Kind : LinkKind.Unknown)).Append('"');
            sb.Append(",\"link_text\":\"").Append(Esc(state.Link != null ? state.Link.Describe() : "unknown")).Append('"');
            sb.Append(",\"link_evidence\":\"").Append(Esc(state.Link != null ? state.Link.Evidence : string.Empty)).Append('"');
            sb.Append(",\"paired\":").Append(state.Link != null && state.Link.IsPaired ? "true" : "false");
            sb.Append(",\"connected\":").Append(state.Link != null && state.Link.IsConnected ? "true" : "false");
            sb.Append(",\"receiver_only\":").Append(state.Link != null && state.Link.IsReceiverOnly ? "true" : "false");
            sb.Append(",\"online\":\"").Append(ContractText.Key(state.State)).Append('"');
            sb.Append(",\"percent\":").Append(state.Percent.HasValue ? state.Percent.Value.ToString(CultureInfo.InvariantCulture) : "null");
            sb.Append(",\"percent_reason\":\"").Append(ContractText.Key(state.PercentReason)).Append('"');
            sb.Append(",\"source\":\"").Append(state.AuthoritativeSource.HasValue ? state.AuthoritativeSource.Value.ToString() : "none").Append('"');
            sb.Append(",\"stale\":").Append(state.IsPercentStale ? "true" : "false");
            sb.Append(",\"conflict\":").Append(state.HasConflict ? "true" : "false");
            if (state.LastSuccessfulReadingUtc.HasValue)
            {
                sb.Append(",\"last_success_utc\":\"").Append(state.LastSuccessfulReadingUtc.Value.ToString("o", CultureInfo.InvariantCulture)).Append('"');
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string Esc(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }

        /// <summary>写出事件文件与摘要；返回摘要 JSON 的路径。</summary>
        public string Write(string directory, string fileNamePrefix)
        {
            if (string.IsNullOrEmpty(directory)) directory = AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(directory);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string eventsPath = Path.Combine(directory, fileNamePrefix + "-" + stamp + "-events.jsonl");
            string summaryPath = Path.Combine(directory, fileNamePrefix + "-" + stamp + "-summary.json");

            string[] lines;
            Dictionary<string, int> changes;
            Dictionary<string, long> firstReads;
            int readings;
            lock (_sync)
            {
                lines = _events.ToArray();
                changes = new Dictionary<string, int>(_stateChanges, StringComparer.OrdinalIgnoreCase);
                firstReads = new Dictionary<string, long>(_firstReadMs, StringComparer.OrdinalIgnoreCase);
                readings = _readingCount;
            }

            File.WriteAllLines(eventsPath, lines, new UTF8Encoding(false));

            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"generated_at\":\"").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append('"');
            sb.Append(",\"duration_ms\":").Append(_clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"event_count\":").Append(lines.Length.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"devices_with_reading\":").Append(readings.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"first_read_ms\":{");
            bool first = true;
            foreach (var pair in firstReads)
            {
                if (!first) sb.Append(',');
                sb.Append('"').Append(Esc(DiagnosticRedaction.ShortHash(pair.Key))).Append("\":").Append(pair.Value.ToString(CultureInfo.InvariantCulture));
                first = false;
            }
            sb.Append("},\"status_changes\":{");
            first = true;
            foreach (var pair in changes)
            {
                if (!first) sb.Append(',');
                sb.Append('"').Append(Esc(DiagnosticRedaction.ShortHash(pair.Key))).Append("\":").Append(pair.Value.ToString(CultureInfo.InvariantCulture));
                first = false;
            }
            sb.Append("},\"events_file\":\"").Append(Esc(Path.GetFileName(eventsPath))).Append('"');
            sb.Append('}');

            File.WriteAllText(summaryPath, sb.ToString(), new UTF8Encoding(false));
            return summaryPath;
        }
    }
}
