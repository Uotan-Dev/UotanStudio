using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Explicit XML security-resource commands; these are not boot-ROM E0/E2 resources.</summary>
public enum MtkXmlSecurityResourceKind
{
    /// <summary>A caller-supplied flash-policy response, including any required device-specific signature.</summary>
    FlashPolicy,
    /// <summary>A caller-supplied all-in-one signature resource.</summary>
    AllInOneSignature
}

public sealed partial class MtkSession
{
    /// <summary>Uploads an explicitly selected XML security resource without generating or guessing credentials.</summary>
    /// <remarks>
    /// Only initialized DA1 or DA2 hardware-control stages are accepted, before storage discovery.
    /// The caller must select resources for the exact device/session and keep their source stable.
    /// Completion means END/OK and START were acknowledged; it does not authenticate the device,
    /// prove SLA is disabled or authorize subsequent storage operations. No key lookup, patching,
    /// fallback credentials, target-provided host paths or automatic replay is performed.
    /// </remarks>
    /// <param name="kind">The explicitly selected security command.</param>
    /// <param name="resource">Readable stream at the selected resource bytes; remains caller-owned.</param>
    /// <param name="length">Positive selected byte count, limited to 1 MiB.</param>
    /// <param name="timeoutMs">Cooperative whole-operation deadline, from one millisecond through 24 hours.</param>
    /// <param name="progress">Cumulative acknowledged bytes; full progress is not final command completion.</param>
    /// <param name="cancellationToken">Cancellation for waiting and the complete resource exchange.</param>
    public async Task UploadXmlSecurityResourceAsync(MtkXmlSecurityResourceKind kind, Stream resource, int length,
        int timeoutMs = 120000, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var name = kind switch
        {
            MtkXmlSecurityResourceKind.FlashPolicy => "CMD:SECURITY-SET-FLASH-POLICY",
            MtkXmlSecurityResourceKind.AllInOneSignature => "CMD:SECURITY-SET-ALLINONE-SIGNATURE",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 1024 * 1024);
        MtkXFlashChannel.ValidateSource(resource, length);
        ValidateXmlSetupTimeout(timeoutMs);
        var sourceId = $"MEM://0x0:0x{length:x}";
        var command = new XElement("da", new XElement("version", "1.0"), new XElement("command", name),
            new XElement("arg", new XElement("source_file", sourceId)));
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!Transport.IsConnected || _state is not (MtkSessionState.XmlHardwareInitialized
                or MtkSessionState.XmlStageTwoHardwareInitialized))
            {
                throw new InvalidOperationException("XML security resources require initialized DA1 or DA2 hardware before storage discovery.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            MtkXFlashChannel.ValidateSource(resource, length);
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
                var packetLength = ValidateXmlDownloadRequest(request, sourceId);
                await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                await SendXmlSourceOwnedAsync(channel, resource, length, packetLength, progress, token).ConfigureAwait(false);
                await CompleteXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXml();
                throw new TimeoutException("The XML security-resource deadline expired; reconnect rather than replaying the exchange.", error);
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
}