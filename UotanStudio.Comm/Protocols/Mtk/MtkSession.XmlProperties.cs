using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Queries one XML system property using a bounded, source-bound document transfer.</summary>
    /// <remarks>The generic envelope name is preserved, not guessed or treated as a trusted vendor schema.
    /// The accepted shape contains one direct item with an exact key and an optional scalar version.
    /// The result is returned only after END/START; no session authorization state is changed.</remarks>
    /// <param name="key">Exact nonblank property key, at most 128 XML-compatible characters.</param>
    /// <param name="timeoutMs">Cooperative whole-operation deadline, from one millisecond through 24 hours.</param>
    /// <param name="cancellationToken">Cancellation for waiting and the complete query.</param>
    /// <returns>An immutable, non-authoritative property report.</returns>
    public Task<MtkXmlSystemProperty> QueryXmlSystemPropertyAsync(string key, int timeoutMs = 120000,
        CancellationToken cancellationToken = default)
        => QueryXmlSystemPropertyCoreAsync(key, null, timeoutMs, cancellationToken);

    /// <summary>Queries DA.SLA against an explicitly selected property-envelope profile.</summary>
    /// <remarks>No envelope is inferred from the reply. Only exact ENABLED and DISABLED values are
    /// classified; other nonempty values remain Unknown. Failure never returns a disabled indication.
    /// Even DisabledReported is an agent statement, not proof of authorization or device authenticity.</remarks>
    /// <param name="expectedEnvelopeName">Exact unqualified outer element name selected from the caller's loader profile.</param>
    /// <param name="timeoutMs">Cooperative whole-operation deadline, from one millisecond through 24 hours.</param>
    /// <param name="cancellationToken">Cancellation for waiting and the complete query.</param>
    /// <returns>The property and its conservative textual SLA indication.</returns>
    public async Task<MtkXmlSlaReport> QueryXmlSlaStatusAsync(string expectedEnvelopeName, int timeoutMs = 120000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedEnvelopeName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(expectedEnvelopeName.Length, 128);
        XmlConvert.VerifyNCName(expectedEnvelopeName);
        var property = await QueryXmlSystemPropertyCoreAsync("DA.SLA", expectedEnvelopeName, timeoutMs, cancellationToken).ConfigureAwait(false);
        return new MtkXmlSlaReport(property);
    }

    private async Task<MtkXmlSystemProperty> QueryXmlSystemPropertyCoreAsync(string key, string? expectedEnvelope,
        int timeoutMs, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, 128);
        XmlConvert.VerifyXmlChars(key);
        ValidateXmlSetupTimeout(timeoutMs);
        const string destination = "MEM://0x0:0x10000";
        var command = new XElement("da", new XElement("version", "1.0"),
            new XElement("command", "CMD:GET-SYS-PROPERTY"), new XElement("arg",
                new XElement("key", key), new XElement("target_file", destination)));
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!Transport.IsConnected || _state is not (MtkSessionState.XmlStageTwoHardwareInitialized
                or MtkSessionState.XmlStorageGeometryKnown))
            {
                throw new InvalidOperationException("XML property queries require initialized DA2 hardware.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            try
            {
                await SendXmlControlCommandOwnedAsync(channel, XmlHostCapabilities(download: false, upload: true), token).ConfigureAwait(false);
                await channel.SendCommandAsync(command, token).ConfigureAwait(false);
                await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
                var text = await ReceiveXmlDocumentOwnedAsync(channel, destination,
                    MtkXmlSystemProperty.MaximumDocumentLength, token).ConfigureAwait(false);
                var property = MtkXmlSystemProperty.Parse(text, key, expectedEnvelope);
                await CompleteXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                return property;
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXml();
                throw new TimeoutException("The XML property query deadline expired; no security state can be inferred.", error);
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