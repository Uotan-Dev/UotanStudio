using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    private readonly EdlConnectionCache _edlConnections;

    /// <summary>Observes the EDL protocol while preserving the handshake for the next device operation.</summary>
    public Task<EdlProtocolState> ProbeEdlProtocolAsync(DetectedDevice device,
        CancellationToken cancellationToken = default)
        => _edlConnections.ProbeAsync(device, cancellationToken);
}