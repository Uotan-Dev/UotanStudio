namespace UotanStudio.Firmware.Dex;

public sealed class DexHeader
{
    public byte[] Magic { get; init; } = [];

    public string Version { get; init; } = "035";

    public uint Checksum { get; init; }

    public byte[] Signature { get; init; } = new byte[20];

    public uint FileSize { get; init; }

    public uint HeaderSize { get; init; } = 0x70;

    public uint EndianTag { get; init; } = 0x12345678;

    public uint LinkSize { get; init; }

    public uint LinkOffset { get; init; }

    public uint MapOffset { get; init; }

    public uint StringIdsSize { get; init; }

    public uint StringIdsOffset { get; init; }

    public uint TypeIdsSize { get; init; }

    public uint TypeIdsOffset { get; init; }

    public uint ProtoIdsSize { get; init; }

    public uint ProtoIdsOffset { get; init; }

    public uint FieldIdsSize { get; init; }

    public uint FieldIdsOffset { get; init; }

    public uint MethodIdsSize { get; init; }

    public uint MethodIdsOffset { get; init; }

    public uint ClassDefsSize { get; init; }

    public uint ClassDefsOffset { get; init; }

    public uint DataSize { get; init; }

    public uint DataOffset { get; init; }
}
