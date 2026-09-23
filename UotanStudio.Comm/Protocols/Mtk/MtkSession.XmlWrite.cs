using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Writes exactly a selected aligned XML storage range from a caller-owned stream.</summary>
    /// <remarks>
    /// The caller must authorize this destructive operation and keep the source stable and exclusive.
    /// No padding, clamping, source-path resolution, automatic authentication, retry or rollback is
    /// performed. An interrupted exchange may have changed storage even when no progress was reported.
    /// ACKs and END/START are agent reports, not independent persistence or readback verification.
    /// </remarks>
    /// <param name="regionName">An exact XML physical-region token from the discovered geometry, excluding RPMB.</param>
    /// <param name="offset">Nonnegative block-aligned byte offset.</param>
    /// <param name="length">Positive block-aligned byte count bounded by reported capacity.</param>
    /// <param name="source">Readable source positioned at the selected bytes; remains caller-owned.</param>
    /// <param name="timeoutMs">Cooperative whole-operation deadline, from one millisecond through 24 hours.</param>
    /// <param name="progress">Cumulative packet-acknowledged bytes; full progress is not final command completion.</param>
    /// <param name="cancellationToken">Cancellation for waiting and the exchange; cannot roll back device changes.</param>
    public async Task WriteXmlStorageAsync(string regionName, long offset, long length, Stream source,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateXmlStorageSource(source, length);
        ValidateXmlSetupTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XmlStorageGeometryKnown);
            cancellationToken.ThrowIfCancellationRequested();
            var storage = XmlStorage ?? throw new InvalidOperationException("XML storage has not been discovered.");
            storage.ValidateRange(regionName, offset, length);
            ValidateXmlStorageSource(source, length);
            var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
            var sourceId = $"MEM://0x0:0x{length:x}";
            var command = new XElement("da", new XElement("version", "1.0"), new XElement("command", "CMD:WRITE-FLASH"),
                new XElement("arg", new XElement("partition", regionName), new XElement("offset", $"0x{offset:x}"),
                    new XElement("source_file", sourceId)));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            try
            {
                await SendXmlControlCommandOwnedAsync(channel, XmlHostCapabilities(download: true, fileSize: true), token).ConfigureAwait(false);
                await channel.SendCommandAsync(command, token).ConfigureAwait(false);
                await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
                var sizeRequest = await ReceiveXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
                RequireXmlCommand(sizeRequest, "CMD:FILE-SYS-OPERATION");
                var arguments = sizeRequest.Arguments;
                if (arguments.Count != 2
                    || !arguments.TryGetValue("key", out var key) || key != "FILE-SIZE"
                    || !arguments.TryGetValue("file_path", out var path) || path != sourceId)
                {
                    throw new MtkProtocolException("Only FILE-SIZE for the selected opaque XML source is supported.");
                }
                await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                await channel.AcknowledgeValueAsync((ulong)length, token).ConfigureAwait(false);
                var request = await ReceiveXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
                var packetLength = ValidateXmlDownloadRequest(request, sourceId);
                await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                await SendXmlSourceOwnedAsync(channel, source, length, packetLength, progress, token).ConfigureAwait(false);
                // Raw storage writes require this extra acknowledgement before END/START.
                await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                await CompleteXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXml();
                throw new TimeoutException("The XML write deadline expired; storage may have changed and the write must not be replayed.", error);
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

    private static void ValidateXmlStorageSource(Stream source, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (!source.CanRead || (source.CanSeek && source.Length - source.Position < length))
        {
            throw new ArgumentException("The XML source is unreadable or shorter than the selected range.", nameof(source));
        }
    }

    private static async Task SendXmlSourceOwnedAsync(MtkXmlChannel channel, Stream source, long length,
        int packetLength, IProgress<long>? progress, CancellationToken token)
    {
        await channel.AcknowledgeValueAsync((ulong)length, token).ConfigureAwait(false);
        await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
        long sent = 0;
        while (sent < length)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(packetLength, length - sent);
            await channel.AcknowledgeValueAsync(0, token).ConfigureAwait(false);
            await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
            await channel.SendStreamAsync(source, count, token).ConfigureAwait(false);
            await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
            sent += count;
            progress?.Report(sent);
        }
    }
}