using System.Buffers.Binary;

namespace UotanStudio.Firmware.FileSystem.F2fs.Models;

internal sealed record F2fsCheckpoint
{
    public const uint FlagUnmount = 0x0000_0001;

    public const uint FlagCompactSum = 0x0000_0004;

    public const int DefaultChecksumOffset = F2fsNode.NodeSize - 4;


    private const int OffsetCheckpointVer = 0;
    private const int OffsetUserBlockCount = 8;
    private const int OffsetValidBlockCount = 16;
    private const int OffsetRsvdSegmentCount = 24;
    private const int OffsetOverprovSegmentCount = 28;
    private const int OffsetFreeSegmentCount = 32;
    private const int OffsetCurNodeSegno = 36;
    private const int OffsetCurNodeBlkoff = 68;
    private const int OffsetCurDataSegno = 84;
    private const int OffsetCurDataBlkoff = 116;
    private const int OffsetCkptFlags = 132;
    private const int OffsetCpPackTotalBlockCount = 136;
    private const int OffsetCpPackStartSum = 140;
    private const int OffsetValidNodeCount = 144;
    private const int OffsetValidInodeCount = 148;
    private const int OffsetNextFreeNid = 152;
    private const int OffsetSitVerBitmapBytesize = 156;
    private const int OffsetNatVerBitmapBytesize = 160;
    private const int OffsetChecksumOffset = 164;
    private const int OffsetElapsedTime = 168;
    private const int OffsetAllocType = 176;
    private const int OffsetVersionBitmap = 192;

    public ulong CheckpointVer { get; init; }

    public ulong UserBlockCount { get; init; }

    public ulong ValidBlockCount { get; init; }

    public uint RsvdSegmentCount { get; init; }

    public uint OverprovSegmentCount { get; init; }

    public uint FreeSegmentCount { get; init; }

    public uint[] CurNodeSegno { get; init; } = [];

    public ushort[] CurNodeBlkoff { get; init; } = [];

    public uint[] CurDataSegno { get; init; } = [];

    public ushort[] CurDataBlkoff { get; init; } = [];

    public uint CkptFlags { get; init; }

    public uint CpPackTotalBlockCount { get; init; }

    public uint CpPackStartSum { get; init; }

    public uint ValidNodeCount { get; init; }

    public uint ValidInodeCount { get; init; }

    public uint NextFreeNid { get; init; }

    public uint SitVerBitmapBytesize { get; init; }

    public uint NatVerBitmapBytesize { get; init; }

    public uint ChecksumOffset { get; init; }

    public ulong ElapsedTime { get; init; }

    public int VersionBitmapOffset => OffsetVersionBitmap;

    /// <summary>
    /// Parses a checkpoint from its block.
    /// <para>从 checkpoint 块解析。</para>
    /// </summary>
    public static F2fsCheckpoint FromBytes(ReadOnlySpan<byte> block)
    {
        if (block.Length < F2fsNode.NodeSize)
        {
            throw new FormatException($"F2FS checkpoint block too short: {block.Length} bytes.");
        }

        return new F2fsCheckpoint
        {
            CheckpointVer = BinaryPrimitives.ReadUInt64LittleEndian(block[OffsetCheckpointVer..]),
            UserBlockCount = BinaryPrimitives.ReadUInt64LittleEndian(block[OffsetUserBlockCount..]),
            ValidBlockCount = BinaryPrimitives.ReadUInt64LittleEndian(block[OffsetValidBlockCount..]),
            RsvdSegmentCount = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetRsvdSegmentCount..]),
            OverprovSegmentCount = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetOverprovSegmentCount..]),
            FreeSegmentCount = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetFreeSegmentCount..]),
            CurNodeSegno = ReadUInt32Array(block, OffsetCurNodeSegno, 8),
            CurNodeBlkoff = ReadUInt16Array(block, OffsetCurNodeBlkoff, 8),
            CurDataSegno = ReadUInt32Array(block, OffsetCurDataSegno, 8),
            CurDataBlkoff = ReadUInt16Array(block, OffsetCurDataBlkoff, 8),
            CkptFlags = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetCkptFlags..]),
            CpPackTotalBlockCount = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetCpPackTotalBlockCount..]),
            CpPackStartSum = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetCpPackStartSum..]),
            ValidNodeCount = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetValidNodeCount..]),
            ValidInodeCount = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetValidInodeCount..]),
            NextFreeNid = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetNextFreeNid..]),
            SitVerBitmapBytesize = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetSitVerBitmapBytesize..]),
            NatVerBitmapBytesize = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetNatVerBitmapBytesize..]),
            ChecksumOffset = BinaryPrimitives.ReadUInt32LittleEndian(block[OffsetChecksumOffset..]),
            ElapsedTime = BinaryPrimitives.ReadUInt64LittleEndian(block[OffsetElapsedTime..]),
        };
    }

    /// <summary>
    /// Writes the checkpoint key fields into a block (the version bitmap area and the checksum are written as well).
    /// <para>将 checkpoint 关键字段写入块（版本位图区域与 checksum 一并写入）。</para>
    /// </summary>
    public void WriteTo(Span<byte> block, ReadOnlySpan<byte> versionBitmap)
    {
        if (block.Length < F2fsNode.NodeSize)
        {
            throw new ArgumentException("Checkpoint destination buffer is too small.", nameof(block));
        }

        block.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(block[OffsetCheckpointVer..], CheckpointVer);
        BinaryPrimitives.WriteUInt64LittleEndian(block[OffsetUserBlockCount..], UserBlockCount);
        BinaryPrimitives.WriteUInt64LittleEndian(block[OffsetValidBlockCount..], ValidBlockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetRsvdSegmentCount..], RsvdSegmentCount);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetOverprovSegmentCount..], OverprovSegmentCount);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetFreeSegmentCount..], FreeSegmentCount);
        for (var i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(OffsetCurNodeSegno + i * 4, 4), i < CurNodeSegno.Length ? CurNodeSegno[i] : uint.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(block.Slice(OffsetCurNodeBlkoff + i * 2, 2), i < CurNodeBlkoff.Length ? CurNodeBlkoff[i] : (ushort)0);
            BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(OffsetCurDataSegno + i * 4, 4), i < CurDataSegno.Length ? CurDataSegno[i] : uint.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(block.Slice(OffsetCurDataBlkoff + i * 2, 2), i < CurDataBlkoff.Length ? CurDataBlkoff[i] : (ushort)0);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetCkptFlags..], CkptFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetCpPackTotalBlockCount..], CpPackTotalBlockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetCpPackStartSum..], CpPackStartSum);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetValidNodeCount..], ValidNodeCount);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetValidInodeCount..], ValidInodeCount);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetNextFreeNid..], NextFreeNid);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetSitVerBitmapBytesize..], SitVerBitmapBytesize);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetNatVerBitmapBytesize..], NatVerBitmapBytesize);
        BinaryPrimitives.WriteUInt32LittleEndian(block[OffsetChecksumOffset..], ChecksumOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(block[OffsetElapsedTime..], ElapsedTime);
        var bitmapLength = Math.Min(versionBitmap.Length, F2fsNode.NodeSize - OffsetVersionBitmap);
        versionBitmap.Slice(0, bitmapLength).CopyTo(block.Slice(OffsetVersionBitmap, bitmapLength));
    }

    private static uint[] ReadUInt32Array(ReadOnlySpan<byte> block, int offset, int count)
    {
        var result = new uint[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(offset + i * 4, 4));
        }

        return result;
    }

    private static ushort[] ReadUInt16Array(ReadOnlySpan<byte> block, int offset, int count)
    {
        var result = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(offset + i * 2, 2));
        }

        return result;
    }
}