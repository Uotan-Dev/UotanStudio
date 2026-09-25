using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Bounded XML DA text/log messages over the shared MTK frame envelope.</summary>
internal sealed class MtkXmlChannel(ICommTransport transport, int timeoutMs)
{
    internal const int MaximumPayload = 256 * 1024;
    internal const int MaximumBinaryPayload = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly MtkXFlashChannel _frames = new(transport, timeoutMs, MaximumBinaryPayload);
    private bool _faulted;

    internal bool IsFaulted => _faulted || _frames.IsFaulted;

    internal Task AcknowledgeAsync(CancellationToken cancellationToken)
    {
        RequireReady(cancellationToken);
        // mtkclient xsend appends exactly one NUL; a second NUL changes the declared length
        // and strict DA parsers would read the extra byte as payload.
        return _frames.SendAsync(1, "OK\0"u8.ToArray(), cancellationToken);
    }

    internal Task AcknowledgeValueAsync(ulong value, CancellationToken cancellationToken)
    {
        RequireReady(cancellationToken);
        return _frames.SendAsync(1, Utf8.GetBytes($"OK@0x{value:x}\0"), cancellationToken);
    }

    internal Task SendStreamAsync(Stream source, int length, CancellationToken cancellationToken)
    {
        RequireReady(cancellationToken);
        return _frames.SendStreamAsync(1, source, length, null, cancellationToken);
    }

    internal Task SendCommandAsync(XElement command, CancellationToken cancellationToken)
    {
        RequireReady(cancellationToken);
        var text = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + command.ToString(SaveOptions.DisableFormatting);
        var length = Utf8.GetByteCount(text);
        if (length >= MaximumPayload)
        {
            throw new ArgumentException("The XML DA command exceeds the frame limit.", nameof(command));
        }
        var payload = new byte[length + 1];
        Utf8.GetBytes(text, payload);
        return _frames.SendAsync(1, payload, cancellationToken);
    }

    internal async Task<string> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        try
        {
            return DecodeText(await ReceivePayloadAsync(cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    internal Task<byte[]> ReceivePayloadAsync(CancellationToken cancellationToken)
        => ReceivePayloadAsync(MaximumPayload, cancellationToken);

    internal async Task<byte[]> ReceivePayloadAsync(int maximumPayload, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayload);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumPayload, MaximumBinaryPayload);
        RequireReady(cancellationToken);
        try
        {
            for (var logs = 0; logs <= 64; logs++)
            {
                // Logs keep their own bound even when the remaining binary payload is smaller.
                var (type, payload) = await _frames.ReceiveAsync(Math.Max(maximumPayload, 65540), cancellationToken).ConfigureAwait(false);
                if (type == 1)
                {
                    if (payload.Length > maximumPayload)
                    {
                        throw new MtkProtocolException("The XML DA data frame exceeds the selected receive limit.");
                    }
                    return payload;
                }
                if (type != 2 || payload.Length is < 4 or > 65540 || logs == 64)
                {
                    throw new MtkProtocolException("Unsupported or excessive XML DA log framing.");
                }
                // A log payload starts with its four-byte priority; it is not protocol text.
                _ = DecodeText(payload.AsSpan(4));
            }
            throw new MtkProtocolException("XML DA protocol text was not received.");
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private static string DecodeText(ReadOnlySpan<byte> payload)
    {
        var content = payload.TrimEnd((byte)0);
        if (content.Contains((byte)0))
        {
            throw new MtkProtocolException("XML DA text contains an embedded NUL.");
        }
        return Utf8.GetString(content);
    }

    private void RequireReady(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsFaulted)
        {
            throw new MtkProtocolException("The XML DA channel is faulted; reconnect before continuing.");
        }
    }
}