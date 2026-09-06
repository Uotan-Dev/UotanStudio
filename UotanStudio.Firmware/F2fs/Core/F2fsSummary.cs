using System.Buffers.Binary;

namespace UotanStudio.Firmware.F2fs;

internal sealed record F2fsSummary
{
    public const int Size = 7;


    private const int OffsetNid = 0;
    private const int OffsetVersion = 4;
    private const int OffsetOfsInNode = 5;

    public uint Nid { get; init; }

    public byte Version { get; init; }

    public ushort OfsInNode { get; init; }

    /// <summary>
    /// Parses a summary entry from 7 bytes (f2fs-tools layout).
    /// <para>从 7 字节解析摘要条目（f2fs-tools 布局）。</para>
    /// </summary>
    public static F2fsSummary FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new FormatException($"F2FS summary region too short: {data.Length} bytes.");
        }

        return new F2fsSummary
        {
            Nid = BinaryPrimitives.ReadUInt32LittleEndian(data[OffsetNid..]),
            Version = data[OffsetVersion],
            OfsInNode = BinaryPrimitives.ReadUInt16LittleEndian(data[OffsetOfsInNode..]),
        };
    }

    /// <summary>
    /// Serializes the summary entry into a buffer of at least 7 bytes (f2fs-tools layout).
    /// <para>将摘要条目序列化到缓冲区（至少 7 字节，f2fs-tools 布局）。</para>
    /// </summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("Summary destination buffer is too small.", nameof(destination));
        }

        destination.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination[OffsetNid..], Nid);
        destination[OffsetVersion] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[OffsetOfsInNode..], OfsInNode);
    }
}
