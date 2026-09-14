using System.Text;

namespace UotanStudio.Services.Devices;

internal sealed class AdbLogcatLineDecoder(Action<string> onLine)
{
    internal const int MaximumLineLength = 16384;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();

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
                if (c == '\n') Emit();
                else if (_line.Length < MaximumLineLength) _line.Append(c);
            }
        } while (!completed);
        if (flush && _line.Length > 0) Emit();
    }

    private void Emit()
    {
        onLine(_line.ToString().TrimEnd('\r'));
        _line.Clear();
    }
}