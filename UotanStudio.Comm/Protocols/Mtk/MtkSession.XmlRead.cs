using System.Globalization;
using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Reads an explicitly selected, aligned XML physical-storage range to a caller-owned stream.</summary>
    /// <remarks>
    /// The destination may contain partial bytes after failure. No padding, clamping, automatic
    /// authentication or retry is performed. Device authorization remains enforced by the agent.
    /// Full byte progress does not imply the final START handshake or durable file publication.
    /// CHK_NO callbacks do not provide cryptographic integrity or independently verified storage identity.
    /// </remarks>
    /// <param name="regionName">An exact XML physical-region token from the discovered geometry, excluding RPMB.</param>
    /// <param name="offset">Nonnegative block-aligned byte offset.</param>
    /// <param name="length">Positive block-aligned byte count, bounded by reported capacity.</param>
    /// <param name="destination">Writable output positioned at the intended location; remains caller-owned.</param>
    /// <param name="timeoutMs">Cooperative whole-operation deadline, from one millisecond through 24 hours.</param>
    /// <param name="progress">Cumulative bytes delivered to the destination and acknowledged in the exchange.</param>
    /// <param name="cancellationToken">Cancellation for waiting, negotiation, reception and completion.</param>
    public async Task ReadXmlStorageAsync(string regionName, long offset, long length, Stream destination,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The XML storage destination is not writable.", nameof(destination));
        }
        ValidateXmlSetupTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XmlStorageGeometryKnown);
            cancellationToken.ThrowIfCancellationRequested();
            var storage = XmlStorage ?? throw new InvalidOperationException("XML storage has not been discovered.");
            storage.ValidateRange(regionName, offset, length);
            var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            // ROM_0 is an opaque callback identifier, never a path opened on the host.
            var command = new XElement("da", new XElement("version", "1.0"), new XElement("command", "CMD:READ-FLASH"),
                new XElement("arg", new XElement("partition", regionName), new XElement("offset", $"0x{offset:x}"),
                    new XElement("length", $"0x{length:x}"), new XElement("target_file", "ROM_0")));
            try
            {
                await SendXmlControlCommandOwnedAsync(channel, XmlHostCapabilities(download: false, upload: true), token).ConfigureAwait(false);
                await channel.SendCommandAsync(command, token).ConfigureAwait(false);
                await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
                var request = await ReceiveXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
                var packetLength = ValidateXmlUploadRequest(request, "ROM_0", MtkXmlChannel.MaximumBinaryPayload);
                await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                RequireXmlReadLength(await channel.ReceiveTextAsync(token).ConfigureAwait(false), length);
                await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
                await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                long transferred = 0;
                while (transferred < length)
                {
                    var limit = (int)Math.Min(packetLength, length - transferred);
                    var payload = await channel.ReceivePayloadAsync(limit, token).ConfigureAwait(false);
                    if (payload.Length == 0)
                    {
                        throw new MtkProtocolException("The XML storage read returned an empty data frame.");
                    }
                    ThrowIfDisposed();
                    await destination.WriteAsync(payload, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                    var status = await channel.ReceiveTextAsync(token).ConfigureAwait(false);
                    transferred += payload.Length;
                    if (status == "OK")
                    {
                        await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                    }
                    else if (transferred == length && !status.StartsWith("ERR!", StringComparison.Ordinal))
                    {
                        // The last raw-read status can carry END/OK instead of a plain OK.
                        var end = MtkXmlMessage.Parse(status);
                        await AcknowledgeXmlEndOwnedAsync(channel, end, token).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new MtkProtocolException("The XML storage read ended or failed before all selected bytes were received.");
                    }
                    progress?.Report(transferred);
                }
                await AcknowledgeXmlStartOwnedAsync(channel, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXml();
                throw new TimeoutException("The XML storage read deadline expired; reconnect before continuing.", error);
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

    private static void RequireXmlReadLength(string text, long expected)
    {
        if (text.Length is < 6 or > 21 || !text.StartsWith("OK@", StringComparison.Ordinal)
            || !text.AsSpan(3, 2).Equals("0x", StringComparison.OrdinalIgnoreCase)
            || !ulong.TryParse(text.AsSpan(5), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            || value != (ulong)expected)
        {
            throw new MtkProtocolException("The XML storage read length does not match the selected range.");
        }
    }
}