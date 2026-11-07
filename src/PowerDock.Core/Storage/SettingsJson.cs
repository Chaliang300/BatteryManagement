using System;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PowerDock.Core.Storage
{
    /// <summary>
    /// settings.json 的序列化适配层，取代 .NET Framework 时期的 JavaScriptSerializer。
    /// 约束规范：
    /// 1. 字段名保持 PascalCase（与 JavaScriptSerializer 时期写出的 settings.json 完全一致，旧文件可直接读）；
    /// 2. 反序列化大小写不敏感，兼容手工编辑成小写字段名的文件；
    /// 3. 输出紧凑（无缩进），与旧版行为一致；
    /// 4. 中文别名不转义成 \uXXXX，保持文件可读。
    /// </summary>
    internal static class SettingsJson
    {
        private static readonly JsonSerializerOptions Options = CreateOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = false,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            return options;
        }

        internal static string Serialize(AppSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings", "待序列化的设置不能为 null。");
            }

            return JsonSerializer.Serialize(settings, Options);
        }

        internal static AppSettings Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new InvalidOperationException("settings.json 内容为空，无法反序列化。");
            }

            return JsonSerializer.Deserialize<AppSettings>(json, Options);
        }
    }
}
