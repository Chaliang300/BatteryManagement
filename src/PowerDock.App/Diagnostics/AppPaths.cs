using System;
using System.IO;

namespace PowerDock.App.Diagnostics
{
    /// <summary>诊断输出的落盘位置。与 <see cref="AppDomain.CurrentDomain.BaseDirectory"/> 下的 data/ 保持一致。</summary>
    public static class AppPaths
    {
        public static string DataDirectory()
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>诊断证据目录：data\diagnostics\。按 commit 隔离由调用方或 QA 脚本负责。</summary>
        public static string DiagnoseDirectory()
        {
            string dir = Path.Combine(DataDirectory(), "diagnostics");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
