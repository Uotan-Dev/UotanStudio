using System.IO.Ports;

namespace UotanStudio.Comm.Transports.Serial;

/// <summary>Explicit serial connection settings; no port enumeration, profile guessing or automatic retries.</summary>
public sealed record SerialConnectionOptions
{
    /// <summary>Gets the exact caller-selected COM/TTY name.</summary>
    public required string PortName { get; init; }
    /// <summary>Gets the explicitly selected positive baud rate.</summary>
    public required int BaudRate { get; init; }
    /// <summary>Gets the explicitly selected flow-control policy.</summary>
    public required Handshake Handshake { get; init; }
    /// <summary>Gets the parity policy.</summary>
    public Parity Parity { get; init; } = Parity.None;
    /// <summary>Gets the number of data bits, from five through eight.</summary>
    public int DataBits { get; init; } = 8;
    /// <summary>Gets the stop-bit policy.</summary>
    public StopBits StopBits { get; init; } = StopBits.One;
    /// <summary>Gets the initial finite read/write timeout in milliseconds.</summary>
    public int TimeoutMs { get; init; } = 5000;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PortName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BaudRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TimeoutMs);
        if (!Enum.IsDefined(Handshake) || !Enum.IsDefined(Parity) || !Enum.IsDefined(StopBits)
            || StopBits == StopBits.None || DataBits is < 5 or > 8)
        {
            throw new ArgumentException("The serial framing or flow-control settings are invalid.");
        }
    }

    /// <summary>Opens only the selected port and transfers ownership to the caller on success.</summary>
    /// <remarks>Native opening is not cancellable mid-call; cancellation is checked again before publishing the transport.</remarks>
    public async Task<SerialTransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var transport = new SerialTransport(PortName, BaudRate, Parity, DataBits, StopBits, Handshake)
        {
            DefaultTimeoutMs = TimeoutMs
        };
        try
        {
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return transport;
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }
}