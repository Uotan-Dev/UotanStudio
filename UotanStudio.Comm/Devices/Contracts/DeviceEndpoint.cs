namespace UotanStudio.Comm.Devices.Contracts;

/// <summary>An exact route to one discovered transport endpoint, not a physical-device guess.</summary>
public sealed record DeviceEndpoint
{
    public required string Source { get; init; }
    public required string Transport { get; init; }
    public required string Address { get; init; }
    public string? DisplayName { get; init; }
    /// <summary>Protocol observed by the source, when known.</summary>
    public string? Protocol { get; init; }
    /// <summary>Server or direct USB ownership; never inferred from a matching serial.</summary>
    public string? Route { get; init; }
    public string? State { get; init; }
    public string? Serial { get; init; }
    /// <summary>Server-reported target transport (for example USB or TCP), not an opening route.</summary>
    public string? TargetTransport { get; init; }
    public UsbEndpointDetails? Usb { get; init; }
    /// <summary>A defensive copy for host projection; mutations never alter the snapshot.</summary>
    public UotanStudio.Comm.Transports.Usb.Abstractions.UsbDeviceInfo? UsbInfo
    {
        get => field is null ? null : UsbInfoCopy.Copy(field);
        init => field = value is null ? null : UsbInfoCopy.Copy(value);
    }

    public DeviceEndpointId Id => new(Source, Transport, Address);
}

/// <summary>Identity includes the discovery source and transport namespace.</summary>
public readonly record struct DeviceEndpointId(string Source, string Transport, string Address);
