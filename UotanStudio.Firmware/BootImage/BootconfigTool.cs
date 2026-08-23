using System.Text;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// Android Bootconfig 工具。
/// <para>
/// 提供 bootconfig 参数的解析、读取与修改功能。</para>
/// <para>
/// Provides parsing, reading and modification of Android bootconfig parameters.</para>
/// <para>
/// bootconfig 是内核命令行之外由 init 消费的键值对块，格式为 NUL 分隔的 "key=value" 文本。</para>
/// <para>
/// Bootconfig is a NUL-separated "key=value" text block consumed by init in addition to the kernel cmdline.</para>
/// </summary>
public static class BootconfigTool
{
    /// <summary>
    /// bootconfig 条目。
    /// <para>A bootconfig entry.</para>
    /// </summary>
    public class BootconfigEntry
    {
        /// <summary>
        /// 键名。
        /// <para>The key name.</para>
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// 值。
        /// <para>The value.</para>
        /// </summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// 原始行。
        /// <para>The raw line.</para>
        /// </summary>
        public string RawLine { get; set; } = string.Empty;

        /// <summary>
        /// 是否为注释。
        /// <para>Whether it is a comment.</para>
        /// </summary>
        public bool IsComment { get; set; }

        /// <summary>
        /// 是否为空行。
        /// <para>Whether it is an empty line.</para>
        /// </summary>
        public bool IsEmpty { get; set; }
    }

    /// <summary>
    /// bootconfig 解析结果。
    /// <para>The bootconfig parsing result.</para>
    /// </summary>
    public class BootconfigResult
    {
        /// <summary>
        /// 是否成功。
        /// <para>Whether parsing succeeded.</para>
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 解析出的条目列表。
        /// <para>The parsed entry list.</para>
        /// </summary>
        public List<BootconfigEntry> Entries { get; set; } = [];

        /// <summary>
        /// 原始文本内容。
        /// <para>The raw text content.</para>
        /// </summary>
        public string RawContent { get; set; } = string.Empty;

        /// <summary>
        /// 错误消息。
        /// <para>The error message.</para>
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// 解析 bootconfig 数据。
    /// <para>Parses bootconfig data.</para>
    /// </summary>
    /// <param name="data">bootconfig 字节数据。<para>The bootconfig bytes.</para></param>
    /// <returns>解析结果。<para>The parsing result.</para></returns>
    public static BootconfigResult Parse(byte[] data)
    {
        var result = new BootconfigResult { Success = false };

        if (data == null || data.Length == 0)
        {
            result.ErrorMessage = "Empty bootconfig data";
            return result;
        }

        try
        {
            // 截断尾部 NUL 与可能存在的 "#\n + 8 字节大小" 尾注
            var text = Encoding.ASCII.GetString(data);
            var end = text.IndexOf('\0');
            if (end >= 0)
            {
                text = text[..end];
            }

            result.RawContent = text;

            foreach (var rawLine in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    result.Entries.Add(new BootconfigEntry { IsEmpty = true, RawLine = rawLine });
                    continue;
                }

                if (line.StartsWith('#'))
                {
                    result.Entries.Add(new BootconfigEntry { IsComment = true, RawLine = line });
                    continue;
                }

                var eq = line.IndexOf('=');
                if (eq < 0)
                {
                    result.Entries.Add(new BootconfigEntry { IsEmpty = true, RawLine = line });
                    continue;
                }

                result.Entries.Add(new BootconfigEntry
                {
                    Key = line[..eq].Trim(),
                    Value = line[(eq + 1)..].Trim(),
                    RawLine = line
                });
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// 读取指定键的值。
    /// <para>Reads the value of the given key.</para>
    /// </summary>
    /// <param name="data">bootconfig 数据。<para>The bootconfig data.</para></param>
    /// <param name="key">键名。<para>The key name.</para></param>
    /// <returns>值；键不存在时返回 null。<para>The value, or null when the key is absent.</para></returns>
    public static string? GetValue(byte[] data, string key)
    {
        var result = Parse(data);
        return !result.Success
            ? null
            : (result.Entries
            .FirstOrDefault(e => !e.IsComment && !e.IsEmpty && string.Equals(e.Key, key, StringComparison.Ordinal))
            ?.Value);
    }

    /// <summary>
    /// 检查 bootconfig 是否包含指定键。
    /// <para>Checks whether the bootconfig contains the given key.</para>
    /// </summary>
    /// <param name="data">bootconfig 数据。<para>The bootconfig data.</para></param>
    /// <param name="key">键名。<para>The key name.</para></param>
    /// <returns>如果包含该键则为 true。<para>True if the key exists.</para></returns>
    public static bool HasKey(byte[] data, string key) => GetValue(data, key) != null;

    /// <summary>
    /// 修改或新增指定键的值并重新序列化为 bootconfig。
    /// <para>Sets the value of a key (adding it when missing) and re-serializes the bootconfig.</para>
    /// </summary>
    /// <param name="data">bootconfig 数据。<para>The bootconfig data.</para></param>
    /// <param name="key">键名。<para>The key name.</para></param>
    /// <param name="value">新值。<para>The new value.</para></param>
    /// <returns>新的 bootconfig 字节。<para>The new bootconfig bytes.</para></returns>
    public static byte[] SetValue(byte[] data, string key, string value)
    {
        var result = Parse(data);
        if (!result.Success)
        {
            throw new FormatException(result.ErrorMessage ?? "Invalid bootconfig data");
        }

        var target = result.Entries.FirstOrDefault(e =>
            !e.IsComment && !e.IsEmpty && string.Equals(e.Key, key, StringComparison.Ordinal));

        if (target != null)
        {
            target.Value = value;
            target.RawLine = $"{key}={value}";
        }
        else
        {
            result.Entries.Add(new BootconfigEntry { Key = key, Value = value, RawLine = $"{key}={value}" });
        }

        return Build(result.Entries);
    }

    /// <summary>
    /// 从条目列表构建 bootconfig 字节。
    /// <para>Builds bootconfig bytes from the entry list.</para>
    /// </summary>
    /// <param name="entries">条目列表。<para>The entry list.</para></param>
    /// <returns>bootconfig 字节数据（以 NUL 结尾）。<para>The bootconfig bytes (NUL-terminated).</para></returns>
    public static byte[] Build(List<BootconfigEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            if (entry.IsComment || entry.IsEmpty)
            {
                sb.Append(entry.RawLine);
            }
            else
            {
                sb.Append(entry.Key).Append('=').Append(entry.Value);
            }
            sb.Append('\0');
        }
        sb.Append('\0');
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}