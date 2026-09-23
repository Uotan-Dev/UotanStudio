using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Requests XML device-side erase of an explicitly selected, aligned physical range.</summary>
    /// <remarks>
    /// The caller must authorize this destructive operation. This profile requires initial OK,
    /// bounded progress handling, END/OK, START and the trailing OK consumed by the reference erase
    /// wrapper. No timeout probing, retry, rollback, zero-fill or secure-erasure guarantee is provided.
    /// Cancellation cannot stop an erase already accepted by the agent.
    /// </remarks>
    /// <param name="regionName">An exact XML physical-region token from the discovered geometry, excluding RPMB.</param>
    /// <param name="offset">Nonnegative block-aligned byte offset.</param>
    /// <param name="length">Positive block-aligned byte count bounded by reported capacity.</param>
    /// <param name="timeoutMs">Cooperative whole-operation deadline, from one millisecond through 24 hours.</param>
    /// <param name="cancellationToken">Cancellation for waiting and the exchange; cannot undo device-side erase.</param>
    public async Task EraseXmlStorageAsync(string regionName, long offset, long length,
        int timeoutMs = 120000, CancellationToken cancellationToken = default)
    {
        ValidateXmlSetupTimeout(timeoutMs);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XmlStorageGeometryKnown);
            cancellationToken.ThrowIfCancellationRequested();
            var storage = XmlStorage ?? throw new InvalidOperationException("XML storage has not been discovered.");
            storage.ValidateRange(regionName, offset, length);
            var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
            var command = new XElement("da", new XElement("version", "1.0"), new XElement("command", "CMD:ERASE-FLASH"),
                new XElement("arg", new XElement("partition", regionName), new XElement("offset", $"0x{offset:x}"),
                    new XElement("length", $"0x{length:x}")));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs);
            var token = deadline.Token;
            try
            {
                await SendXmlControlCommandOwnedAsync(channel, XmlHostCapabilities(download: false), token).ConfigureAwait(false);
                await SendXmlControlCommandOwnedAsync(channel, command, token).ConfigureAwait(false);
                await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXml();
                throw new TimeoutException("The XML erase deadline expired; the device may still be erasing and must not be retried blindly.", error);
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