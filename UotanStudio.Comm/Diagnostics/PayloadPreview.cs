namespace UotanStudio.Comm.Diagnostics;

/// <summary>
/// Formats a bounded, human-readable preview of a protocol payload for debug logs: the byte count, the leading
/// bytes as hexadecimal and the same bytes as printable ASCII. The preview never exceeds the requested bound, so a
/// file transfer or a device response can never dump its whole content into the log.
/// <para>为调试日志格式化有界的协议负载预览：字节数、前若干字节的十六进制，以及同样的字节按可打印 ASCII 呈现。
/// 预览绝不会超过给定上限，因此文件传输或设备应答不会把全部内容倒进日志。</para>
/// </summary>
/// <remarks>
/// The line is rendered into a stack buffer (or one pooled character array for oversized bounds) and published
/// with a single string allocation — per-frame trace paths format a preview for every inbound message, so no
/// intermediate hexadecimal strings or builders are produced.
/// <para>整行渲染进栈缓冲（超上限的预览用一次池化的字符数组），并以单次字符串分配发布——逐帧跟踪路径为每条入站
/// 消息格式化一次预览，因此不再产生中间的十六进制字符串或构造器。</para>
/// </remarks>
public static class PayloadPreview
{
    /// <summary>Default preview bound in bytes. <para>默认预览上限（字节）。</para></summary>
    public const int DefaultMaxBytes = 64;

    /// <summary>Largest line rendered on the stack; larger bounds rent one pooled character buffer.
    /// <para>栈上渲染的最大行宽；更大的上限租用一次池化字符缓冲。</para></summary>
    private const int StackCharBudget = 512;

    private const string HexDigits = "0123456789ABCDEF";
    private const string TruncatedSuffix = " (truncated)";

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
        if (payload.Length == 0)
        {
            return $"{payload.Length} B";
        }

        var truncated = shown < payload.Length;
        var digits = DigitCount(payload.Length);
        var capacity = digits + 4 + (shown * 3 - 1) + 2 + shown + 1 + (truncated ? TruncatedSuffix.Length : 0);
        char[]? rented = null;
        var destination = capacity <= StackCharBudget
            ? stackalloc char[StackCharBudget]
            : rented = System.Buffers.ArrayPool<char>.Shared.Rent(capacity);
        try
        {
            var position = 0;
            payload.Length.TryFormat(destination, out var written);
            position += written;
            destination[position++] = ' ';
            destination[position++] = 'B';
            destination[position++] = ':';
            destination[position++] = ' ';
            for (var index = 0; index < shown; index++)
            {
                if (index > 0)
                {
                    destination[position++] = ' ';
                }

                destination[position++] = HexDigits[payload[index] >> 4];
                destination[position++] = HexDigits[payload[index] & 0xF];
            }

            destination[position++] = ' ';
            destination[position++] = '|';
            for (var index = 0; index < shown; index++)
            {
                var value = payload[index];
                destination[position++] = value is >= 0x20 and <= 0x7E ? (char)value : '.';
            }

            destination[position++] = '|';
            if (truncated)
            {
                TruncatedSuffix.CopyTo(destination[position..]);
                position += TruncatedSuffix.Length;
            }

            return new string(destination[..position]);
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    private static int DigitCount(int value)
    {
        var digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }

        return digits;
    }
}