using System.Text;

namespace UotanStudio.Comm.Diagnostics;

/// <summary>
/// Formats a bounded, human-readable preview of a protocol payload for debug logs: the byte count, the leading
/// bytes as hexadecimal and the same bytes as printable ASCII. The preview never exceeds the requested bound, so a
/// file transfer or a device response can never dump its whole content into the log.
/// <para>为调试日志格式化有界的协议负载预览：字节数、前若干字节的十六进制，以及同样的字节按可打印 ASCII 呈现。
/// 预览绝不会超过给定上限，因此文件传输或设备应答不会把全部内容倒进日志。</para>
/// </summary>
public static class PayloadPreview
{
    /// <summary>Default preview bound in bytes. <para>默认预览上限（字节）。</para></summary>
    public const int DefaultMaxBytes = 64;

    /// <summary>Formats one bounded payload preview. <para>格式化一条有界负载预览。</para></summary>
    /// <param name="payload">Payload bytes; empty input yields a zero-length preview.
    /// <para>负载字节；空输入产生零长度预览。</para></param>
    /// <param name="maxBytes">Maximum bytes to render; values below one are treated as one.
    /// <para>最多渲染的字节数；小于 1 的取值按 1 处理。</para></param>
    /// <returns>A single-line preview such as <c>132 B: 41 42 … |AB…| (truncated)</c>.
    /// <para>单行预览，例如 <c>132 B: 41 42 … |AB…| (truncated)</c>。</para></returns>
    public static string Format(ReadOnlySpan<byte> payload, int maxBytes = DefaultMaxBytes)
    {
        var limit = Math.Max(1, maxBytes);
        var shown = Math.Min(payload.Length, limit);
        var builder = new StringBuilder(payload.Length == 0 ? 8 : (shown * 4) + 32);
        builder.Append(payload.Length).Append(" B");
        if (payload.Length == 0)
        {
            return builder.ToString();
        }

        builder.Append(": ");
        for (var index = 0; index < shown; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(payload[index].ToString("X2"));
        }

        builder.Append(" |");
        for (var index = 0; index < shown; index++)
        {
            var value = payload[index];
            builder.Append(value is >= 0x20 and <= 0x7E ? (char)value : '.');
        }

        builder.Append('|');
        if (shown < payload.Length)
        {
            builder.Append(" (truncated)");
        }

        return builder.ToString();
    }
}