using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;

public static class BootconfigTool
{
    public class BootconfigEntry
    {
        public string Key { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;

        public string RawLine { get; set; } = string.Empty;

        public bool IsComment { get; set; }

        public bool IsEmpty { get; set; }
    }

    public class BootconfigResult
    {
        public bool Success { get; set; }

        public List<BootconfigEntry> Entries { get; set; } = [];

        public string RawContent { get; set; } = string.Empty;

        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Parses bootconfig data.
    /// <para>解析 bootconfig 数据。</para>
    /// </summary>
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
    public static bool HasKey(byte[] data, string key) => GetValue(data, key) != null;

    /// <summary>
    /// Sets the value of a key (adding it when missing) and re-serializes the bootconfig.
    /// <para>修改或新增指定键的值并重新序列化为 bootconfig。</para>
    /// </summary>
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
