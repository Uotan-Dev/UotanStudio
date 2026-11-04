using System.Text;

namespace UotanStudio.Services.Logging;

/// <summary>
/// Renders the optional scope and structured fields of an entry into one text shape, so the log window, exports
/// and the session file never disagree about how metadata is displayed.
/// <para>把条目的可选作用域与结构化字段渲染成统一的文本形态，使日志窗口、导出与会话文件对元数据的展示保持一致。</para>
/// </summary>
internal static class LogMetadataFormat
{
    /// <summary>
    /// Renders a correlation scope as a trailing parenthesised suffix.
    /// <para>把关联作用域渲染为尾随的括号后缀。</para>
    /// </summary>
    /// <param name="scope">The scope text, or null when the entry carries none.
    /// <para>作用域文本；条目不携带时为 null。</para></param>
    /// <returns>The suffix, or an empty string when there is nothing to render.
    /// <para>后缀；没有可渲染内容时为空字符串。</para></returns>
    internal static string ScopeSuffix(string? scope)
        => string.IsNullOrEmpty(scope) ? string.Empty : $" ({scope})";

    /// <summary>
    /// Renders structured fields as a bracketed <c>key=value</c> list.
    /// <para>把结构化字段渲染为方括号包裹的 <c>key=value</c> 列表。</para>
    /// </summary>
    /// <param name="fields">The fields to render, or null when the entry carries none.
    /// <para>待渲染的字段；条目不携带时为 null。</para></param>
    /// <returns>The suffix, or an empty string when there is nothing to render.
    /// <para>后缀；没有可渲染内容时为空字符串。</para></returns>
    internal static string FieldSuffix(IReadOnlyList<LogField>? fields)
    {
        if (fields is not { Count: > 0 })
        {
            return string.Empty;
        }

        var builder = new StringBuilder(" [");
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(fields[i].Key).Append('=').Append(fields[i].Value);
        }

        return builder.Append(']').ToString();
    }
}