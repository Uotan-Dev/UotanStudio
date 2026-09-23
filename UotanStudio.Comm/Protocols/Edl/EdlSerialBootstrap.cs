using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>Starts EDL through one explicitly selected serial port without enumeration, retries or loader fallback.</summary>
public static class EdlSerialBootstrap
{
    /// <summary>Retains an optional programmer file before opening the port, then completes Sahara/Firehose initialization.</summary>
    /// <param name="serial">Explicit serial connection settings.</param>
    /// <param name="programmerPath">Selected programmer file, or null when no upload is intended; no automatic selection occurs.</param>
    /// <param name="configuration">Firehose configuration; the caller must not mutate it during startup.</param>
    /// <param name="cancellationToken">The startup cancellation token.</param>
    /// <returns>A caller-owned initialized session. Loader execution may have device-side effects.</returns>
    public static Task<EdlSession> ConnectAsync(SerialConnectionOptions serial, string? programmerPath,
        FirehoseConfiguration? configuration = null, CancellationToken cancellationToken = default) =>
        ConnectCoreAsync(serial, programmerPath, configuration, static async (settings, token) =>
            await settings.ConnectAsync(token).ConfigureAwait(false), cancellationToken);

    internal static async Task<EdlSession> ConnectCoreAsync(SerialConnectionOptions serial, string? programmerPath,
        FirehoseConfiguration? configuration, Func<SerialConnectionOptions, CancellationToken, Task<ICommTransport>> connect,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(connect);
        serial.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        using var programmer = programmerPath is null ? null
            : new FileStream(programmerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (programmer is { Length: 0 })
        {
            throw new InvalidDataException("The selected programmer file is empty.");
        }
        ICommTransport? transport = null;
        EdlSession? session = null;
        try
        {
            transport = await connect(serial, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The serial connector returned no transport.");
            cancellationToken.ThrowIfCancellationRequested();
            if (!transport.IsConnected)
            {
                throw new InvalidOperationException("The selected transport is not connected.");
            }
            session = new EdlSession(transport, configuration);
            await session.InitializeAsync(programmer, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return session;
        }
        catch
        {
            if (session is not null)
            {
                session.Dispose();
            }
            else
            {
                transport?.Dispose();
            }
            throw;
        }
    }
}