namespace UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Models;

public static class FdtConstants
{
    public const uint FdtMagic = 0xD00DFEED;

    public const int HeaderSize = 40;

    public const uint FdtVersion = 17;

    public const uint FdtLastCompVersion = 16;

    public const int Alignment = 4;

    public const int MemoryReservationAlignment = 8;

    public const int MemoryReservationEntrySize = 16;

    public const uint FdtBeginNode = 0x00000001;

    public const uint FdtEndNode = 0x00000002;

    public const uint FdtProp = 0x00000003;

    public const uint FdtNop = 0x00000004;

    public const uint FdtEnd = 0x00000009;

    public const int PropertyHeaderSize = 8;

    public const int TokenSize = 4;

    public const uint DtboMagic = 0xD7B7AB1E;

    public const int DtboHeaderSize = 32;

    public const int DtboEntrySize = 32;

    public const uint DtboDefaultPageSize = 2048;
}

public sealed class FdtHeader
{
    public uint Magic { get; set; }

    public uint TotalSize { get; set; }

    public uint OffsetDtStruct { get; set; }

    public uint OffsetDtStrings { get; set; }

    public uint OffsetMemRsvmap { get; set; }

    public uint Version { get; set; }

    public uint LastCompVersion { get; set; }

    public uint BootCpuidPhys { get; set; }

    public uint SizeDtStrings { get; set; }

    public uint SizeDtStruct { get; set; }

    public bool IsValidMagic => Magic == FdtConstants.FdtMagic;

    /// <summary>
    /// Reads an FDT header from a byte span (big-endian).
    /// <para>从字节跨度读取 FDT 头部(大端)。</para>
    /// </summary>
    public static FdtHeader ReadFrom(ReadOnlySpan<byte> data)
    {
        if (data.Length < 28)
        {
            throw new InvalidDataException($"Data too small for FDT header: {data.Length} bytes.");
        }

        var header = new FdtHeader
        {
            Magic = ReadU32BE(data),
            TotalSize = ReadU32BE(data[4..]),
            OffsetDtStruct = ReadU32BE(data[8..]),
            OffsetDtStrings = ReadU32BE(data[12..]),
            OffsetMemRsvmap = ReadU32BE(data[16..]),
            Version = ReadU32BE(data[20..]),
            LastCompVersion = ReadU32BE(data[24..])
        };

        if (header.Version >= 2 && data.Length >= 32)
        {
            header.BootCpuidPhys = ReadU32BE(data[28..]);
        }
        if (header.Version >= 3 && data.Length >= 36)
        {
            header.SizeDtStrings = ReadU32BE(data[32..]);
        }
        if (header.Version >= 17 && data.Length >= 40)
        {
            header.SizeDtStruct = ReadU32BE(data[36..]);
        }

        return header;
    }

    /// <summary>
    /// Serializes the header to a byte array (big-endian).
    /// <para>将头部序列化为字节数组(大端)。</para>
    /// </summary>
    public byte[] ToBytes()
    {
        var data = new byte[FdtConstants.HeaderSize];
        WriteU32BE(data, 0, Magic);
        WriteU32BE(data, 4, TotalSize);
        WriteU32BE(data, 8, OffsetDtStruct);
        WriteU32BE(data, 12, OffsetDtStrings);
        WriteU32BE(data, 16, OffsetMemRsvmap);
        WriteU32BE(data, 20, Version);
        WriteU32BE(data, 24, LastCompVersion);
        WriteU32BE(data, 28, BootCpuidPhys);
        WriteU32BE(data, 32, SizeDtStrings);
        WriteU32BE(data, 36, SizeDtStruct);
        return data;
    }

    internal static uint ReadU32BE(ReadOnlySpan<byte> data) =>
        (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);

    internal static void WriteU32BE(Span<byte> data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}

public readonly struct MemoryReservation
{
    public ulong Address { get; init; }

    public ulong Size { get; init; }
}