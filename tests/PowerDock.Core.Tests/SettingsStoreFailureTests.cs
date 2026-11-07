using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PowerDock.Core.Diagnostics;
using PowerDock.Core.Storage;
using Xunit;

namespace PowerDock.Core.Tests
{
    /// <summary>
    /// 失败与恢复路径（矩阵 S0-STORE-02）。这些用例会改写进程级日志路径（AppLogger 是静态类），
    /// 因此单独成一个禁用并行化的集合，避免与其他测试类互相污染日志断言。
    ///
    /// 取值约定：用例里的 v1 / v2 一律取**非默认值**（默认是 dark / 20 / medium）。
    /// 否则「回滚成功」与「回落到默认配置」在断言上无法区分，用例会假通过
    /// —— 这一点已由变异测试实测确认（把 v1 设成 dark/20 时，删掉恢复分支用例仍然绿）。
    /// </summary>
    [CollectionDefinition("SettingsStoreFailure", DisableParallelization = true)]
    public sealed class SettingsStoreFailureCollection
    {
    }

    [Collection("SettingsStoreFailure")]
    public sealed class SettingsStoreFailureTests
    {
        // ---------- 1. 目标文件被独占占用：File.Replace 阶段失败 ----------

        [Fact]
        public void Save_目标文件被独占占用_抛可捕获异常且内存回滚()
        {
            string dir = TestPaths.NewTempDir("store-fail-lock");
            string logDir = TestPaths.NewTempDir("store-fail-lock-log");
            try
            {
                AppLogger.Initialize(logDir);

                string path = Path.Combine(dir, "settings.json");
                var store = new SettingsStore(path);
                store.Save(V1());
                AssertNoDefaultDrift(store.Current, "v1");

                using (var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // 失败以「可捕获异常」表达：Save 的文档契约就是「回滚并抛出异常」，
                    // 断言点是能被调用方捕获、不产生进程级未处理异常。
                    var ex = Record.Exception(() => store.Save(V2()));
                    Assert.NotNull(ex);
                    if (ex != null)
                    {
                        Assert.True(ex is IOException || ex is UnauthorizedAccessException,
                            "期望文件系统异常，实际: " + ex.GetType().FullName);
                    }
                }

                // 内存回滚到上一次有效值（v1，且必须是非默认值才有区分度）
                AssertNoDefaultDrift(store.Current, "失败后的 Current");

                // 磁盘仍是上一版有效内容
                var probe = new SettingsStore(path);
                probe.Load();
                AssertNoDefaultDrift(probe.Current, "失败后的磁盘内容");

                // 日志含原因与路径
                string log = ReadLog(logDir);
                Assert.Contains("保存设置失败，正在执行回滚", log);
                Assert.Contains(path, log);
            }
            finally
            {
                TestPaths.TryDelete(dir);
                TestPaths.TryDelete(logDir);
            }
        }

        // ---------- 2. 目标目录拒绝写入（ACL）：写入阶段失败 ----------

        [Fact]
        public void Save_目标目录拒绝写入_抛可捕获异常且内存回滚()
        {
            string dir = TestPaths.NewTempDir("store-fail-acl");
            string logDir = TestPaths.NewTempDir("store-fail-acl-log");
            try
            {
                AppLogger.Initialize(logDir);

                string path = Path.Combine(dir, "settings.json");
                var store = new SettingsStore(path);
                store.Save(V1());

                var deny = new FileSystemAccessRule(
                    WindowsIdentity.GetCurrent().User,
                    FileSystemRights.CreateFiles | FileSystemRights.WriteData | FileSystemRights.Delete,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Deny);

                ApplyDirectoryAcl(dir, deny, add: true);
                try
                {
                    var ex = Record.Exception(() => store.Save(V2()));
                    Assert.NotNull(ex);
                    if (ex != null)
                    {
                        Assert.True(ex is UnauthorizedAccessException || ex is IOException,
                            "期望文件系统异常，实际: " + ex.GetType().FullName);
                    }
                }
                finally
                {
                    ApplyDirectoryAcl(dir, deny, add: false);
                }

                AssertNoDefaultDrift(store.Current, "失败后的 Current");

                var probe = new SettingsStore(path);
                probe.Load();
                AssertNoDefaultDrift(probe.Current, "失败后的磁盘内容");

                string log = ReadLog(logDir);
                Assert.Contains("保存设置失败，正在执行回滚", log);
                Assert.Contains("Access to the path", log);
            }
            finally
            {
                TestPaths.TryDelete(dir);
                TestPaths.TryDelete(logDir);
            }
        }

        // ---------- 3. 写入阶段失败：临时文件路径被同名目录占位 ----------

        [Fact]
        public void Save_临时文件路径被同名目录占位_写入失败且内存回滚()
        {
            string dir = TestPaths.NewTempDir("store-fail-temp");
            string logDir = TestPaths.NewTempDir("store-fail-temp-log");
            try
            {
                AppLogger.Initialize(logDir);

                string path = Path.Combine(dir, "settings.json");
                var store = new SettingsStore(path);
                store.Save(V1());

                // AtomicWrite 第一步 File.WriteAllText(<target>.tmp) 会因同名目录而失败
                string tempPath = path + ".tmp";
                Directory.CreateDirectory(tempPath);
                try
                {
                    Assert.NotNull(Record.Exception(() => store.Save(V2())));
                    AssertNoDefaultDrift(store.Current, "失败后的 Current");

                    string log = ReadLog(logDir);
                    Assert.Contains("AtomicWrite 写入文件失败", log);
                    Assert.Contains("保存设置失败，正在执行回滚", log);
                }
                finally
                {
                    Directory.Delete(tempPath);
                }

                // 条件恢复后可写，且不残留临时文件
                store.Save(V2());
                AssertV2(store.Current, "恢复后的 Current");
                Assert.False(File.Exists(path + ".tmp"));
            }
            finally
            {
                TestPaths.TryDelete(dir);
                TestPaths.TryDelete(logDir);
            }
        }

        // ---------- 4. 只读属性文件：本实现下不构成失败（文档化行为） ----------

        [Fact]
        public void Save_目标文件带只读属性_实现会清除只读并写入成功()
        {
            string dir = TestPaths.NewTempDir("store-readonly-attr");
            try
            {
                string path = Path.Combine(dir, "settings.json");
                var store = new SettingsStore(path);
                store.Save(V1());

                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);

                // AtomicFile 在替换前主动 RemoveReadOnlyAttributeIfPresent，
                // 因此「文件只读」不是本实现的失败条件；这里断言真实行为而不是期望行为。
                store.Save(V2());

                AssertV2(store.Current, "只读文件写入后的 Current");
                Assert.False((File.GetAttributes(path) & FileAttributes.ReadOnly) == FileAttributes.ReadOnly);

                var probe = new SettingsStore(path);
                probe.Load();
                AssertV2(probe.Current, "只读文件写入后的磁盘内容");
            }
            finally
            {
                TestPaths.TryDelete(dir);
            }
        }

        // ---------- 5. 损坏 settings.json → 从 .bak 恢复 ----------

        [Fact]
        public void Load_目标文件为非法JSON_从有效备份恢复()
        {
            string dir = TestPaths.NewTempDir("store-recover-bak");
            string logDir = TestPaths.NewTempDir("store-recover-bak-log");
            try
            {
                AppLogger.Initialize(logDir);

                string path = Path.Combine(dir, "settings.json");
                var store = new SettingsStore(path);
                store.Save(V1());   // 首次写入：无 .bak
                store.Save(V2());   // 覆盖写：.bak = v1
                Assert.True(File.Exists(path + ".bak"));

                // 真造一个损坏文件（非法 JSON）
                File.WriteAllText(path, "{\"Theme\":\"light\",", new UTF8Encoding(false));

                var reloaded = new SettingsStore(path);
                reloaded.Load();

                // 恢复的是 .bak 的内容（v1）。若恢复分支失效，本断言会因回落到默认配置而失败
                AssertNoDefaultDrift(reloaded.Current, "从备份恢复后的 Current");

                string log = ReadLog(logDir);
                Assert.Contains("加载 settings.json 失败或文件损坏", log);
                Assert.Contains("已从备份文件恢复设置", log);
            }
            finally
            {
                TestPaths.TryDelete(dir);
                TestPaths.TryDelete(logDir);
            }
        }

        // ---------- 6. 失败后恢复：修复条件后保存成功，磁盘与内存一致 ----------

        [Fact]
        public void Save_失败后修复条件_再次保存成功且磁盘与内存一致()
        {
            string dir = TestPaths.NewTempDir("store-fail-then-ok");
            try
            {
                string path = Path.Combine(dir, "settings.json");
                var store = new SettingsStore(path);
                store.Save(V1());

                using (var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.NotNull(Record.Exception(() => store.Save(V2())));
                }
                AssertNoDefaultDrift(store.Current, "失败后的 Current");

                // 证明失败没有留下不可恢复状态
                store.Save(V2());
                AssertV2(store.Current, "恢复保存后的 Current");

                var probe = new SettingsStore(path);
                probe.Load();
                AssertV2(probe.Current, "恢复保存后的磁盘内容");
            }
            finally
            {
                TestPaths.TryDelete(dir);
            }
        }

        // ---------- 7. 失败不广播 SettingsChanged ----------

        [Fact]
        public void Save_写盘失败_不触发SettingsChanged事件()
        {
            string dir = TestPaths.NewTempDir("store-fail-event");
            try
            {
                string path = Path.Combine(dir, "settings.json");
                var store = new SettingsStore(path);

                int raised = 0;
                store.SettingsChanged += delegate { raised++; };

                store.Save(V1());
                Assert.Equal(1, raised);

                using (var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.NotNull(Record.Exception(() => store.Save(V2())));
                }

                // 失败不广播，避免 UI 展示一份并未落盘的配置
                Assert.Equal(1, raised);
            }
            finally
            {
                TestPaths.TryDelete(dir);
            }
        }

        // ---------- 8. 文件与备份同时损坏：不抛异常，保留上次有效配置 ----------

        [Fact]
        public void Load_文件与备份同时损坏_不抛异常且保留上次有效配置()
        {
            string dir = TestPaths.NewTempDir("store-both-broken");
            string logDir = TestPaths.NewTempDir("store-both-broken-log");
            try
            {
                AppLogger.Initialize(logDir);

                string path = Path.Combine(dir, "settings.json");
                var store = new SettingsStore(path);
                store.Save(V1());
                store.Save(V2());   // .bak = v1

                store.Load();
                AssertV2(store.Current, "损坏前的 Current");   // 上次有效 = v2

                File.WriteAllText(path, "!! not json !!", new UTF8Encoding(false));
                File.WriteAllText(path + ".bak", "!! also broken !!", new UTF8Encoding(false));

                var ex = Record.Exception(() => store.Load());
                Assert.Null(ex);

                // 两条恢复路径都失败时，保留进程内上一次有效配置（v2）
                AssertV2(store.Current, "双损坏后的 Current");

                string log = ReadLog(logDir);
                Assert.Contains("从备份文件恢复设置亦失败", log);
            }
            finally
            {
                TestPaths.TryDelete(dir);
                TestPaths.TryDelete(logDir);
            }
        }

        // ---------- 9. 回滚自身失败：目标不可写且已有 .bak，回滚复制失败也要被记录 ----------

        [Fact]
        public void Save_回滚过程本身失败_内存仍回滚且记录回滚失败原因()
        {
            string dir = TestPaths.NewTempDir("store-rollback-fails");
            string logDir = TestPaths.NewTempDir("store-rollback-fails-log");
            try
            {
                AppLogger.Initialize(logDir);

                string path = Path.Combine(dir, "settings.json");
                var store = new SettingsStore(path);
                store.Save(V1());
                store.Save(V2());                 // 磁盘 = v2，.bak = v1
                store.Load();
                AssertV2(store.Current, "回滚失败用例的前置 Current");
                Assert.True(File.Exists(path + ".bak"));

                var v3 = new AppSettings();
                v3.Theme = "dark";
                v3.LowBatteryThreshold = 25;
                v3.WidgetSize = "medium";

                using (var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.NotNull(Record.Exception(() => store.Save(v3)));

                    // 回滚里的 File.Copy(.bak -> 目标) 同样因独占而失败，必须走 catch 并记日志
                    string log = ReadLog(logDir);
                    Assert.Contains("回滚磁盘设置文件失败", log);
                }

                // 即使回滚复制失败，内存仍回滚到上一次有效值；磁盘未被破坏
                AssertV2(store.Current, "回滚失败后的 Current");

                var probe = new SettingsStore(path);
                probe.Load();
                AssertV2(probe.Current, "回滚失败后的磁盘内容");
            }
            finally
            {
                TestPaths.TryDelete(dir);
                TestPaths.TryDelete(logDir);
            }
        }

        // ---------- 10. 首次保存即失败：覆盖 Rollback 的「目标此前不存在」分支 ----------

        [Fact]
        public void Save_首次保存就失败_保持默认配置且不留下半成品文件()
        {
            string dir = TestPaths.NewTempDir("store-first-save-fail");
            string logDir = TestPaths.NewTempDir("store-first-save-fail-log");
            try
            {
                AppLogger.Initialize(logDir);

                string path = Path.Combine(dir, "settings.json");
                var store = new SettingsStore(path);
                var defaults = store.Current;          // 构造时为默认配置
                Assert.Equal(20, defaults.LowBatteryThreshold);

                var deny = new FileSystemAccessRule(
                    WindowsIdentity.GetCurrent().User,
                    FileSystemRights.CreateFiles | FileSystemRights.WriteData | FileSystemRights.Delete,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Deny);

                ApplyDirectoryAcl(dir, deny, add: true);
                try
                {
                    Assert.NotNull(Record.Exception(() => store.Save(V1())));
                }
                finally
                {
                    ApplyDirectoryAcl(dir, deny, add: false);
                }

                // 目标此前不存在 → Rollback 走删除分支，Current 回到默认配置（不是 v1）
                Assert.NotNull(store.Current);
                Assert.Equal(20, store.Current.LowBatteryThreshold);
                Assert.Equal("medium", store.Current.WidgetSize);

                // 不应留下 settings.json / .tmp / .bak 半成品
                Assert.False(File.Exists(path));
                Assert.False(File.Exists(path + ".tmp"));
                Assert.False(File.Exists(path + ".bak"));
            }
            finally
            {
                TestPaths.TryDelete(dir);
                TestPaths.TryDelete(logDir);
            }
        }

        // ---------- 取值与断言 helpers ----------

        // v1 = 非默认：dark / 15 / large（默认是 dark / 20 / medium，主题相同但其余两项可区分）
        private static AppSettings V1()
        {
            var settings = new AppSettings();
            settings.Theme = "dark";
            settings.LowBatteryThreshold = 15;
            settings.WidgetSize = "large";
            return settings;
        }

        // v2 = 非默认且与 v1 全不同
        private static AppSettings V2()
        {
            var settings = new AppSettings();
            settings.Theme = "light";
            settings.LowBatteryThreshold = 12;
            settings.WidgetSize = "small";
            return settings;
        }

        private static void AssertNoDefaultDrift(AppSettings actual, string what)
        {
            Assert.NotNull(actual);
            Assert.Equal("dark", actual.Theme);
            Assert.Equal(15, actual.LowBatteryThreshold);
            Assert.Equal("large", actual.WidgetSize);
            Assert.True(actual.WidgetSize != new AppSettings().WidgetSize, what + " 落回了默认 WidgetSize");
        }

        private static void AssertV2(AppSettings actual, string what)
        {
            Assert.NotNull(actual);
            Assert.Equal("light", actual.Theme);
            Assert.Equal(12, actual.LowBatteryThreshold);
            Assert.Equal("small", actual.WidgetSize);
        }

        private static string ReadLog(string logDir)
        {
            string logPath = Path.Combine(logDir, "powerdock.log");
            if (!File.Exists(logPath))
            {
                return string.Empty;
            }
            return File.ReadAllText(logPath, Encoding.UTF8);
        }

        private static void ApplyDirectoryAcl(string dir, FileSystemAccessRule rule, bool add)
        {
            var info = new DirectoryInfo(dir);
            DirectorySecurity security = info.GetAccessControl();
            if (add)
            {
                security.AddAccessRule(rule);
            }
            else
            {
                security.RemoveAccessRule(rule);
            }
            info.SetAccessControl(security);
        }
    }
}
