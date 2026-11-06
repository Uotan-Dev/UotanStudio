using System.Text;

namespace UotanStudio.DD.Writing;

/// <summary>
/// Escaping shared by the text formats the host and its helper processes exchange.
/// <para>宿主与其助手进程交换的文本格式共用的转义规则。</para>
/// </summary>
/// <remarks>
/// Records are space separated, so every space inside a free-form value is escaped; a backslash escapes the
/// next character. An empty value is written as <see cref="EmptyValue"/> so a blank field never shifts the
/// fields after it.
/// <para>
/// 记录以空格分隔，因此自由文本中的空格都要转义；反斜杠用于转义下一个字符。空值写成
/// <see cref="EmptyValue"/>，使空白字段不会让后面的字段错位。
/// </para>
/// </remarks>
internal static class FlashTextCodec
{
    /// <summary>
    /// The token written for an empty value.
    /// <para>空值写入的记号。</para>
    /// </summary>
    internal const string EmptyValue = "-";

    /// <summary>
    /// Escapes one value for a space-separated record.
    /// <para>为空格分隔的记录转义一个值。</para>
    /// </summary>
    /// <param name="value">Value to escape; <see langword="null"/> or empty becomes <see cref="EmptyValue"/>.
    /// <para>待转义的值；<see langword="null"/> 或空串写成 <see cref="EmptyValue"/>。</para></param>
    /// <returns>The escaped field.<para>转义后的字段。</para></returns>
    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return EmptyValue;
        }

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(" ", "\\s", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reverses <see cref="Escape"/>.
    /// <para>逆转 <see cref="Escape"/>。</para>
    /// </summary>
    /// <param name="value">Escaped field.<para>转义后的字段。</para></param>
    /// <returns>The original value.<para>原始值。</para></returns>
    internal static string Unescape(string value)
    {
        if (value.Length == 0 || string.Equals(value, EmptyValue, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current != '\\' || index + 1 >= value.Length)
            {
                builder.Append(current);
                continue;
            }

            index++;
            builder.Append(value[index] switch
            {
                'n' => '\n',
                'r' => '\r',
                's' => ' ',
                '\\' => '\\',
                var other => other,
            });
        }

        return builder.ToString();
    }
}