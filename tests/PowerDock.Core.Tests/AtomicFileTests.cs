using System;
using System.IO;
using System.Text;
using PowerDock.Core.Storage;
using Xunit;

namespace PowerDock.Core.Tests
{
    /// <summary>
    /// AtomicFile 契约（AGENTS.md §5.4）：临时文件 → 原子替换 → 回读一致；
    /// 文本产物为无 BOM UTF-8、单行内容不含 CRLF。
    /// </summary>
    public sealed class AtomicFileTests
    {
        [Fact]
        public void AtomicWrite_写入后回读内容一致且无BOM()
        {
            string dir = TestPaths.NewTempDir("atomic-roundtrip");
            try
            {
                string path = Path.Combine(dir, "settings.json");
                string content = "{\"Theme\":\"dark\",\"DeviceAliases\":{\"bt-1\":\"耳机\"}}";

                AtomicFile.AtomicWrite(path, content);

                Assert.Equal(content, File.ReadAllText(path, Encoding.UTF8));
                byte[] bytes = File.ReadAllBytes(path);
                Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                    "AtomicWrite 默认应写无 BOM 的 UTF-8");
                Assert.DoesNotContain("\r\n", Encoding.UTF8.GetString(bytes));
                Assert.False(File.Exists(path + ".tmp"), "写入完成后不应残留临时文件");
            }
            finally
            {
                TestPaths.TryDelete(dir);
            }
        }

        [Fact]
        public void AtomicWrite_覆盖写保留上一版备份并清理临时文件()
        {
            string dir = TestPaths.NewTempDir("atomic-replace");
            try
            {
                string path = Path.Combine(dir, "settings.json");

                AtomicFile.AtomicWrite(path, "{\"v\":1}");
                AtomicFile.AtomicWrite(path, "{\"v\":2}");

                Assert.Equal("{\"v\":2}", File.ReadAllText(path, Encoding.UTF8));
                Assert.True(File.Exists(path + ".bak"));
                Assert.Equal("{\"v\":1}", File.ReadAllText(path + ".bak", Encoding.UTF8));
                Assert.False(File.Exists(path + ".tmp"));
            }
            finally
            {
                TestPaths.TryDelete(dir);
            }
        }

        [Fact]
        public void AtomicWrite_自动创建缺失目录()
        {
            string dir = TestPaths.NewTempDir("atomic-mkdir");
            try
            {
                string path = Path.Combine(dir, "nested", "deep", "settings.json");

                AtomicFile.AtomicWrite(path, "{}");

                Assert.True(File.Exists(path));
            }
            finally
            {
                TestPaths.TryDelete(dir);
            }
        }

        [Fact]
        public void AtomicWrite_参数为空时抛出参数异常()
        {
            Assert.Throws<ArgumentNullException>(() => AtomicFile.AtomicWrite(null, "{}"));
            Assert.Throws<ArgumentNullException>(() => AtomicFile.AtomicWrite("x.json", null));
        }
    }

    internal static class TestPaths
    {
        internal static string NewTempDir(string prefix)
        {
            string dir = Path.Combine(Path.GetTempPath(), "PowerDockTests", prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        internal static void TryDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
            }
        }
    }
}
