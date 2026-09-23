namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>External, explicitly authorized Boot ROM SLA response generation; no keys are provided by the library.</summary>
public interface IMtkBootRomSlaResponder
{
    /// <summary>Produces the wire-ready response to one bounded challenge, without fallback key attempts.</summary>
    /// <param name="device">Queried target identity, not cryptographic proof of device identity.</param>
    /// <param name="wireChallenge">Exact target bytes, valid only until this task completes; do not retain.
    /// The provider owns any required word swapping, cryptographic encoding and signing.</param>
    /// <param name="cancellationToken">Cooperative cancellation including the exchange deadline.</param>
    /// <returns>A newly allocated response of 1..4096 bytes. Ownership transfers to the session,
    /// which clears it on exit. Do not return shared/cached key or signature buffers.</returns>
    Task<byte[]> CreateResponseAsync(MtkDeviceInfo device, ReadOnlyMemory<byte> wireChallenge,
        CancellationToken cancellationToken);
}