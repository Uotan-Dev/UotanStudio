namespace UotanStudio.Comm.Devices.Contracts;

/// <summary>An exact route to one discovered transport endpoint, not a physical-device guess.</summary>
public sealed record DeviceEndpoint
{
    public required string Source { get; init; }
    public required string Transport { get; init; }
    public required string Address { get; init; }
    public string? DisplayName { get; init; }

    public DeviceEndpointId Id => new(Source, Transport, Address);
}

/// <summary>Identity includes the discovery source and transport namespace.</summary>
public readonly record struct DeviceEndpointId(string Source, string Transport, string Address);
