using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Components;

/// <summary>
/// Parses, queries and rewrites Android bootconfig blocks stored as NUL-separated <c>key=value</c> lines.
/// <para>解析、查询并改写 Android bootconfig 块，其内容以 NUL 分隔的 <c>key=value</c> 行存储。</para>
/// </summary>
public static class BootconfigTool
{
    /// <summary>
    /// A single decoded bootconfig line: a key/value pair, a comment, or a blank entry kept for round-tripping.
    /// <para>一条解码后的 bootconfig 行：键值对、注释，或为往返保留的空条目。</para>
    /// </summary>
    public class BootconfigEntry
    {
        /// <summary>Key text before the first <c>=</c>, trimmed. <para>首个 <c>=</c> 之前、已去除空白的键文本。</para></summary>
        /// <value>The key; empty for comment and blank entries. <para>键；注释与空条目时为空。</para></value>
        public string Key { get; set; } = string.Empty;

        /// <summary>Value text after the first <c>=</c>, trimmed. <para>首个 <c>=</c> 之后、已去除空白的值文本。</para></summary>
        /// <value>The value; empty for comment and blank entries. <para>值；注释与空条目时为空。</para></value>
        public string Value { get; set; } = string.Empty;

        /// <summary>Original line text as read. <para>读取时的原始行文本。</para></summary>
        /// <value>The line reproduced verbatim on build for comments and blanks. <para>构建时用于逐字重现注释和空条目的行。</para></value>
        public string RawLine { get; set; } = string.Empty;

        /// <summary>Comment-line flag. <para>注释行标志。</para></summary>
        /// <value><see langword="true"/> when the entry is a <c>#</c> comment line. <para>条目为 <c>#</c> 注释行时为 <see langword="true"/>。</para></value>
        public bool IsComment { get; set; }

        /// <summary>Blank-entry flag. <para>空条目标志。</para></summary>
        /// <value><see langword="true"/> when the entry is blank or carries no <c>=</c> separator. <para>条目为空行或不含 <c>=</c> 分隔符时为 <see langword="true"/>。</para></value>
        public bool IsEmpty { get; set; }
    }

    /// <summary>
    /// Outcome of a bootconfig parse, carrying the decoded entries, the raw text, and any failure message.
    /// <para>bootconfig 解析的结果，包含解码条目、原始文本以及失败信息。</para>
    /// </summary>
    public class BootconfigResult
    {
        /// <summary>Parse-completion flag. <para>解析完成标志。</para></summary>
        /// <value><see langword="true"/> when parsing completed and <see cref="Entries"/> is usable. <para>解析完成且 <see cref="Entries"/> 可用时为 <see langword="true"/>。</para></value>
        public bool Success { get; set; }

        /// <summary>Decoded entry list. <para>解码条目列表。</para></summary>
        /// <value>Entries in source order; empty until a successful parse fills them. <para>按源顺序排列的条目；成功解析填充前为空。</para></value>
        public List<BootconfigEntry> Entries { get; set; } = [];

        /// <summary>Raw decoded text. <para>原始解码文本。</para></summary>
        /// <value>ASCII text up to the first double-NUL terminator, or empty when parsing never reached that stage. <para>截至首个双 NUL 终止符之前的 ASCII 文本；解析未进行到该阶段时为空。</para></value>
        public string RawContent { get; set; } = string.Empty;

        /// <summary>Failure reason. <para>失败原因。</para></summary>
        /// <value>The error message, or <see langword="null"/> when parsing succeeded. <para>错误信息；解析成功时为 <see langword="null"/>。</para></value>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Parses bootconfig data.
    /// <para>解析 bootconfig 数据。</para>
    /// </summary>
    /// <param name="data">
    /// Raw bootconfig bytes decoded as ASCII and truncated at the first double-NUL terminator; <see langword="null"/> or empty yields a failure result.
    /// <para>以 ASCII 解码、并在首个双 NUL 终止符处截断的原始 bootconfig 字节；为 <see langword="null"/> 或空时返回失败结果。</para>
    /// </param>
    /// <returns>The parse result; on <see cref="BootconfigResult.Success"/> the entries hold every decoded line, otherwise <see cref="BootconfigResult.ErrorMessage"/> explains the failure.
    /// <para>解析结果；<see cref="BootconfigResult.Success"/> 为真时条目包含每条解码行，否则 <see cref="BootconfigResult.ErrorMessage"/> 说明失败原因。</para>
    /// </returns>
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

            var text = Encoding.ASCII.GetString(data);
            var doubleNull = text.IndexOf("\0\0", StringComparison.Ordinal);
            if (doubleNull >= 0)
            {
                text = text[..doubleNull];
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
    /// Reads the value of the given key.
    /// <para>读取指定键的值。</para>
    /// </summary>
    /// <param name="data">
    /// Raw bootconfig bytes; parsed on each call.
    /// <para>原始 bootconfig 字节；每次调用都会解析。</para>
    /// </param>
    /// <param name="key">
    /// Key to match case-sensitively against non-comment, non-empty entries.
    /// <para>区分大小写地匹配非注释、非空条目的键。</para>
    /// </param>
    /// <returns>The trimmed value of the first matching key, or <see langword="null"/> when parsing fails or the key is absent.
    /// <para>首个匹配键的去空白值；解析失败或键不存在时为 <see langword="null"/>。</para>
    /// </returns>
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
    /// Checks whether the bootconfig contains the given key.
    /// <para>检查 bootconfig 是否包含指定键。</para>
    /// </summary>
    /// <param name="data">
    /// Raw bootconfig bytes; parsed on each call.
    /// <para>原始 bootconfig 字节；每次调用都会解析。</para>
    /// </param>
    /// <param name="key">
    /// Key to look up case-sensitively.
    /// <para>区分大小写查找的键。</para>
    /// </param>
    /// <returns><see langword="true"/> when a matching non-comment, non-empty key exists and parsing succeeded.
    /// <para>存在匹配的非注释、非空键且解析成功时为 <see langword="true"/>。</para>
    /// </returns>
    public static bool HasKey(byte[] data, string key) => GetValue(data, key) != null;

    /// <summary>
    /// Sets the value of a key (adding it when missing) and re-serializes the bootconfig.
    /// <para>修改或新增指定键的值并重新序列化为 bootconfig。</para>
    /// </summary>
    /// <param name="data">
    /// Raw bootconfig bytes to parse before editing.
    /// <para>编辑前先解析的原始 bootconfig 字节。</para>
    /// </param>
    /// <param name="key">
    /// Key to update case-sensitively, or to append when no matching entry exists.
    /// <para>区分大小写更新的键；无匹配条目时追加。</para>
    /// </param>
    /// <param name="value">
    /// New value stored verbatim for the key.
    /// <para>为该键逐字存储的新值。</para>
    /// </param>
    /// <returns>The re-serialized bootconfig bytes produced by <see cref="Build"/>.
    /// <para>由 <see cref="Build"/> 生成的重新序列化 bootconfig 字节。</para>
    /// </returns>
    /// <exception cref="FormatException">
    /// The input could not be parsed as bootconfig.
    /// <para>输入无法作为 bootconfig 解析。</para>
    /// </exception>
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
    /// Builds bootconfig bytes from the entry list.
    /// <para>从条目列表构建 bootconfig 字节。</para>
    /// </summary>
    /// <param name="entries">
    /// Entries emitted in list order: comments and blanks reuse their <see cref="BootconfigEntry.RawLine"/>, others are written as <c>Key=Value</c>, each terminated by a NUL and closed by a trailing NUL.
    /// <para>按列表顺序输出：注释与空条目使用其 <see cref="BootconfigEntry.RawLine"/>，其余写为 <c>Key=Value</c>，每条以 NUL 结尾并再追加一个终止 NUL。</para>
    /// </param>
    /// <returns>The ASCII-encoded bootconfig block terminated by a double NUL.
    /// <para>以双 NUL 结尾的 ASCII 编码 bootconfig 块。</para>
    /// </returns>
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