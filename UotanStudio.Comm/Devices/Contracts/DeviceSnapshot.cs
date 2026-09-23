using System.Collections.Immutable;

namespace UotanStudio.Comm.Devices.Contracts;

/// <summary>A point-in-time discovery result. A later refresh invalidates its generation.</summary>
public sealed record DeviceSnapshot(long Generation, ImmutableArray<DeviceEndpoint> Endpoints);
