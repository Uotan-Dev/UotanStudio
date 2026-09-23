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

    public DeviceEndpointId Id => new(Source, Transport, Address);
}

/// <summary>Identity includes the discovery source and transport namespace.</summary>
public readonly record struct DeviceEndpointId(string Source, string Transport, string Address);
