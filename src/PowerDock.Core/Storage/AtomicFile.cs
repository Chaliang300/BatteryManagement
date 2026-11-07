using System;
using System.IO;
using System.Text;
using PowerDock.Core.Diagnostics;

namespace PowerDock.Core.Storage
{
    /// <summary>
    /// 提供基于临时文件与原子替换机制的文件安全写入工具。
    /// 约束规范：
    /// 1. 先写 targetPath + ".tmp"，回读验证完整性；
    /// 2. 若目标文件已存在，使用 File.Replace(tmp, target, backup) 并保留 .bak 备份；
    /// 3. 若目标文件不存在，使用 File.Move(tmp, target)；
    /// 4. 出现异常时清理临时文件并记入日志。
    /// </summary>
    public static class AtomicFile
    {
        private static readonly object SyncLock = new object();

        /// <summary>
        /// 无 BOM 的 UTF-8。BCL 的 Encoding.UTF8 在用 File.WriteAllText 写文件时会带 3 字节 BOM，
        /// 这里显式去掉，保证 settings.json 等文本产物是纯 UTF-8；读取侧仍兼容带 BOM 的旧文件。
        /// </summary>
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// 使用 UTF-8 编码原子写入文本内容到指定路径。
        /// </summary>
        /// <param name="targetPath">目标文件路径，不能为 null 或空字符串。</param>
        /// <param name="content">写入的文本内容，不能为 null。</param>
        public static void AtomicWrite(string targetPath, string content)
        {
            AtomicWrite(targetPath, content, Utf8NoBom);
        }

        /// <summary>
        /// 使用指定编码原子写入文本内容到指定路径。
        /// </summary>
        /// <param name="targetPath">目标文件路径，不能为 null 或空字符串。</param>
        /// <param name="content">写入的文本内容，不能为 null。</param>
        /// <param name="encoding">文本字符编码，若为 null 则默认使用 UTF-8。</param>
        public static void AtomicWrite(string targetPath, string content, Encoding encoding)
        {
            if (string.IsNullOrEmpty(targetPath))
            {
                throw new ArgumentNullException("targetPath", "目标文件路径不能为 null 或空。");
            }

            if (content == null)
            {
                throw new ArgumentNullException("content", "写入内容不能为 null。");
            }

            if (encoding == null)
            {
                encoding = Utf8NoBom;
            }

            lock (SyncLock)
            {
                string fullTargetPath = Path.GetFullPath(targetPath);
                string directory = Path.GetDirectoryName(fullTargetPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string tempPath = fullTargetPath + ".tmp";
                string backupPath = fullTargetPath + ".bak";

                // 清理可能遗留的历史临时文件
                CleanTempFile(tempPath);

                try
                {
                    // 1. 写入临时文件
                    File.WriteAllText(tempPath, content, encoding);

                    // 2. 回读验证完整性
                    string readBack = File.ReadAllText(tempPath, encoding);
                    if (!string.Equals(readBack, content, StringComparison.Ordinal))
                    {
                        throw new IOException(string.Format("临时文件回读完整性校验失败: '{0}' 内容与预期不一致。", tempPath));
                    }

                    // 确保若存在只读属性时予以移除，避免替换失败
                    RemoveReadOnlyAttributeIfPresent(fullTargetPath);
                    RemoveReadOnlyAttributeIfPresent(backupPath);

                    // 3. 根据目标是否存在，执行原子替换或移动
                    if (File.Exists(fullTargetPath))
                    {
                        File.Replace(tempPath, fullTargetPath, backupPath);
                    }
                    else
                    {
                        File.Move(tempPath, fullTargetPath);
                    }
                }
                catch (Exception ex)
                {
                    // 4. 清理异常临时文件
                    CleanTempFile(tempPath);
                    AppLogger.Error(string.Format("AtomicWrite 写入文件失败: '{0}'", fullTargetPath), ex);
                    throw;
                }
            }
        }

        private static void CleanTempFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    RemoveReadOnlyAttributeIfPresent(path);
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void RemoveReadOnlyAttributeIfPresent(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    {
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    }
                }
            }
            catch
            {
            }
        }
    }
}
