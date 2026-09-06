namespace UotanStudio.Firmware.Avb;

public sealed class AvbHashDescriptor
{
    public string PartitionName { get; init; } = string.Empty;

    public ulong ImageSize { get; init; }

    public string HashAlgorithm { get; init; } = "sha256";

    public uint Flags { get; init; }

    public byte[] Salt { get; init; } = [];

    public byte[] Digest { get; init; } = [];
}

public sealed class AvbSignResult
{
    public bool Success { get; init; }

    public string? ErrorMessage { get; init; }

    public string? OutputPath { get; init; }

    public uint AlgorithmType { get; init; }

    public string AlgorithmName { get; init; } = string.Empty;

    public int DescriptorCount { get; init; }

    public long OutputSize { get; init; }
}
