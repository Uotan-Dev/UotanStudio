using System.Text;

namespace UotanStudio.Services.Devices;

internal sealed class AdbLogcatLineDecoder(Action<string> onLine)
{
    internal const int MaximumLineLength = 16384;

    /// <summary>
    /// Visible marker appended to a line that exceeds <see cref="MaximumLineLength"/>; it counts
    /// against that bound, so an emitted line never exceeds the maximum length.
    /// <para>追加到超过 <see cref="MaximumLineLength"/> 的行尾的可见标记；它计入该上限，因此输出行不会超过最大长度。</para>
    /// </summary>
    internal const string TruncationMarker = " …[truncated]";
    private static readonly int ContentCapacity = MaximumLineLength - TruncationMarker.Length;

    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private bool _truncated;

    internal void Feed(ReadOnlySpan<byte> bytes, bool flush = false)
    {
        Span<char> chars = stackalloc char[1024];
        bool completed;
        do
        {
            _decoder.Convert(bytes, chars, flush, out var used, out var written, out completed);
            bytes = bytes[used..];
            foreach (var c in chars[..written])
            {
                if (c == '\n')
                {
                    Emit();
                }
                else if (_line.Length < MaximumLineLength)
                {
                    _line.Append(c);
                }
                else
                {
                    _truncated = true;
                }
            }
        } while (!completed);
        if (flush && _line.Length > 0)
        {
            Emit();
        }
    }

    private void Emit()
    {
        if (_truncated)
        {
            var content = _line.ToString();
            var length = Math.Min(content.Length, ContentCapacity);
            if (length > 0 && char.IsHighSurrogate(content[length - 1]))
            {
                length--;
            }
            onLine(string.Concat(content.AsSpan(0, length).TrimEnd('\r'), TruncationMarker));
        }
        else
        {
            onLine(_line.ToString().TrimEnd('\r'));
        }
        _line.Clear();
        _truncated = false;
    }
}