using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Uploads one explicitly selected XML DA2 image and requires the final END/START handoff.</summary>
    /// <remarks>
    /// The only accepted download request names this operation's opaque MEM source exactly,
    /// identifies 2nd-DA and requests CHK_NO. No target-provided path is opened. Packet
    /// acceptance is not a checksum/readback guarantee. Failure requires reconnect, not retry.
    /// The caller owns the source; signature inclusion is an explicit caller/profile choice.
    /// The successful state does not imply DA2 hardware setup, authorization or storage readiness.
    /// </remarks>
    /// <param name="image">Readable source positioned at the selected DA2 bytes.</param>
    /// <param name="loadAddress">Aligned target address used for both load and jump.</param>
    /// <param name="length">Positive selected byte count, at most 64 MiB.</param>
    /// <param name="timeoutMs">Cooperative whole-operation deadline, one millisecond through 24 hours.</param>
    /// <param name="progress">Cumulative packet-acknowledged bytes; full progress is not final handoff success.</param>
    /// <param name="cancellationToken">Cancellation for waiting, capability negotiation and upload.</param>
    public async Task UploadXmlStageTwoAsync(Stream image, ulong loadAddress, int length, int timeoutMs = 120000,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        MtkXFlashChannel.ValidateSource(image, length);
        ValidateXmlSetupTimeout(timeoutMs);
        if ((loadAddress & 3) != 0 || loadAddress > ulong.MaxValue - (uint)length)
        {
            throw new ArgumentOutOfRangeException(nameof(loadAddress), "The DA2 address must be aligned and its range must not overflow.");
        }
        // The MEM identifier is an opaque wire token, never a pointer to host memory.
        var sourceId = $"MEM://0x0:0x{length:x}";
        var command = new XElement("da", new XElement("version", "1.0"), new XElement("command", "CMD:BOOT-TO"),
            new XElement("arg", new XElement("at_address", $"0x{loadAddress:x}"),
                new XElement("jmp_address", $"0x{loadAddress:x}"), new XElement("source_file", sourceId)));
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XmlHardwareInitialized);
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            try
            {
                await SendXmlControlCommandOwnedAsync(channel, XmlHostCapabilities(download: true), token).ConfigureAwait(false);
                await channel.SendCommandAsync(command, token).ConfigureAwait(false);
                await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
                var request = await ReceiveXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
                var packetLength = ValidateXmlStageTwoRequest(request, sourceId, length);
                await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                await SendXmlSourceOwnedAsync(channel, image, length, packetLength, progress, token).ConfigureAwait(false);
                await CompleteXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                _state = MtkSessionState.XmlStageTwoStarted;
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXml();
                throw new TimeoutException("The XML DA2 exchange deadline expired; do not replay the upload.", error);
            }
            catch
            {
                FaultXml();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private static int ValidateXmlStageTwoRequest(MtkXmlMessage request, string sourceId, int length)
    {
        var packetLength = ValidateXmlDownloadRequest(request, sourceId, "2nd-DA");
        if ((length - 1) / packetLength + 1 > 65536)
        {
            throw new MtkProtocolException("The XML DA2 request exceeds the bounded packet count.");
        }
        return packetLength;
    }

    private static int ValidateXmlDownloadRequest(MtkXmlMessage request, string sourceId, string? expectedInfo = null)
    {
        RequireXmlCommand(request, "CMD:DOWNLOAD-FILE");
        var arguments = request.Arguments;
        if (arguments.Count != 4
            || !arguments.TryGetValue("checksum", out var checksum) || checksum != "CHK_NO"
            || !arguments.TryGetValue("info", out var info) || info.Length == 0 || (expectedInfo is not null && info != expectedInfo)
            || !arguments.TryGetValue("source_file", out var source) || source != sourceId
            || !arguments.TryGetValue("packet_length", out var packet)
            || !TryXmlHexLength(packet, MtkXmlChannel.MaximumBinaryPayload, out var packetLength))
        {
            throw new MtkProtocolException("The XML download request does not match the selected source or bounded transfer profile.");
        }
        return packetLength;
    }

    private static async Task RequireXmlOkAsync(MtkXmlChannel channel, CancellationToken token)
    {
        if (await channel.ReceiveTextAsync(token).ConfigureAwait(false) != "OK")
        {
            throw new MtkProtocolException("The XML DA did not acknowledge the transfer phase.");
        }
    }
}