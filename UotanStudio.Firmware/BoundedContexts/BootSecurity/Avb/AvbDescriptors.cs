namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb;

public sealed class AvbHashtreeDescriptor
{
    public uint DmVerityVersion { get; init; } = 1;

    public ulong ImageSize { get; init; }

    public ulong TreeOffset { get; init; }

    public ulong TreeSize { get; init; }

    public uint DataBlockSize { get; init; } = 4096;

    public uint HashBlockSize { get; init; } = 4096;

    public uint FecNumRoots { get; init; }

    public ulong FecOffset { get; init; }

    public ulong FecSize { get; init; }

    public string HashAlgorithm { get; init; } = "sha256";

    public string PartitionName { get; init; } = string.Empty;

    public byte[] Salt { get; init; } = [];

    public byte[] RootDigest { get; init; } = [];

    public uint Flags { get; init; }
}

public sealed class AvbChainPartitionDescriptor
{
    public uint RollbackIndexLocation { get; init; }

    public string PartitionName { get; init; } = string.Empty;

    public byte[] PublicKey { get; init; } = [];
}

public sealed class AvbKernelCmdlineDescriptor
{
    public uint Flags { get; init; }

    public string KernelCmdline { get; init; } = string.Empty;
}

public sealed class AvbPropertyDescriptor
{
    public string Key { get; init; } = string.Empty;

    public byte[] Value { get; init; } = [];
}
