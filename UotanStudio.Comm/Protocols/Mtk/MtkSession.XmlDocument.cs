using System.Text;
using UotanStudio.Firmware.Core.Text;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Strict UTF-8 decoder shared by every XML document read; throwing on invalid sequences is part of the wire
    /// contract, so the instance is cached instead of being rebuilt per document.
    /// <para>所有 XML 文档读取共享的严格 UTF-8 解码器；对无效序列抛错属于线上契约的一部分，因此缓存该实例而
    /// 不逐文档重建。</para>
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private async Task<string> ReceiveXmlDocumentOwnedAsync(MtkXmlChannel channel, string destination,
        int maximumLength, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumLength, MtkXmlChannel.MaximumPayload);
        var request = await ReceiveXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
        var packetLength = ValidateXmlUploadRequest(request, destination, MtkXmlChannel.MaximumPayload);
        await channel.AcknowledgeAsync(token).ConfigureAwait(false);
        var lengthText = await channel.ReceiveTextAsync(token).ConfigureAwait(false);
        if (!lengthText.StartsWith("OK@", StringComparison.Ordinal)
            || !TryXmlHexLength(lengthText[3..], maximumLength, out var length))
        {
            throw new MtkProtocolException("The XML document length is invalid or exceeds its destination.");
        }
        await channel.AcknowledgeAsync(token).ConfigureAwait(false);
        await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
        await channel.AcknowledgeAsync(token).ConfigureAwait(false);
        var bytes = new byte[length];
        var received = 0;
        while (received < length)
        {
            var payload = await channel.ReceivePayloadAsync(token).ConfigureAwait(false);
            if (payload.Length == 0 || payload.Length > packetLength || payload.Length > length - received)
            {
                throw new MtkProtocolException("The XML document frame exceeds its announced bounds.");
            }
            payload.CopyTo(bytes, received);
            received += payload.Length;
        }
        await channel.AcknowledgeAsync(token).ConfigureAwait(false);
        return StrictUtf8.GetStringTrimNul(bytes);
    }
}