using System.Globalization;
using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Gets validated XML geometry after discovery, or null if unavailable or the session faulted.</summary>
    /// <remarks>Device-reported geometry is not proof of authentication, successful I/O or identity.</remarks>
    public MtkXmlStorageInfo? XmlStorage { get; private set; }

    /// <summary>Queries bounded eMMC/UFS geometry after DA2 hardware initialization.</summary>
    /// <remarks>Accepts only the selected in-memory destination, never a target-supplied host path.
    /// No storage data is read or written. NAND, absent storage and unknown schemas are rejected.
    /// Geometry is published only after the complete END/START exchange, without granting authorization.</remarks>
    /// <param name="timeoutMs">Cooperative whole-operation deadline, from one millisecond through 24 hours.</param>
    /// <param name="cancellationToken">Cancellation for waiting, capability negotiation and discovery.</param>
    /// <returns>Validated immutable geometry reported by the XML agent.</returns>
    public async Task<MtkXmlStorageInfo> DiscoverXmlStorageAsync(int timeoutMs = 120000,
        CancellationToken cancellationToken = default)
    {
        ValidateXmlSetupTimeout(timeoutMs);
        const int maximumLength = MtkXmlStorageInfo.MaximumDocumentLength;
        const string destination = "MEM://0x0:0x10000";
        var command = new XElement("da", new XElement("version", "1.0"), new XElement("command", "CMD:GET-HW-INFO"),
            new XElement("arg", new XElement("target_file", destination)));
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XmlStageTwoHardwareInitialized);
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
                var text = await ReceiveXmlDocumentOwnedAsync(channel, destination, maximumLength, token).ConfigureAwait(false);
                var storage = MtkXmlStorageInfo.Parse(text);
                await CompleteXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                XmlStorage = storage;
                _state = MtkSessionState.XmlStorageGeometryKnown;
                return storage;
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXml();
                throw new TimeoutException("The XML storage discovery deadline expired; reconnect before continuing.", error);
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

    private static int ValidateXmlUploadRequest(MtkXmlMessage request, string destination, int maximumPacketLength)
    {
        RequireXmlCommand(request, "CMD:UPLOAD-FILE");
        var arguments = request.Arguments;
        if (arguments.Count != 4
            || !arguments.TryGetValue("checksum", out var checksum) || checksum != "CHK_NO"
            || !arguments.TryGetValue("info", out var info) || info.Length == 0
            || !arguments.TryGetValue("target_file", out var target) || target != destination
            || !arguments.TryGetValue("packet_length", out var packet)
            || !TryXmlHexLength(packet, maximumPacketLength, out var packetLength))
        {
            throw new MtkProtocolException("The XML upload callback does not match the bounded destination.");
        }
        return packetLength;
    }

    private static bool TryXmlHexLength(string text, int maximum, out int length)
    {
        length = 0;
        if (text.Length is < 3 or > 10 || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            || value == 0 || value > maximum)
        {
            return false;
        }
        length = (int)value;
        return true;
    }
}
