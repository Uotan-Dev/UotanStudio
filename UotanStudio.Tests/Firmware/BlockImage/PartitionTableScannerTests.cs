using System.Buffers.Binary;
using UotanStudio.Firmware.BlockImage;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.BlockImage.Mbr;
using UotanStudio.Hash.Crc32Ieee;
using Xunit;

namespace UotanStudio.Tests.Firmware.BlockImage;

public class PartitionTableScannerTests
{
    private const int SectorSize = 512;
    private const long Megabyte = 1024 * 1024;

    [Fact]
    public void TryFind_MbrAtWindowStart_ReportsZeroOffset()
    {
        var window = new byte[4 * SectorSize];
        WriteMbr(window.AsSpan(0, SectorSize), 0x83, 2048, 20480);

        var found = PartitionTableScanner.TryFind(window, 0, 4 * Megabyte, out var tableOffset, out var kind);

        Assert.True(found);
        Assert.Equal(0, tableOffset);
        Assert.Equal(PartitionTableKind.Mbr, kind);
    }

    [Fact]
    public void TryFind_MbrDeepInsideWindow_ReportsExtentRelativeOffset()
    {
        var window = new byte[1 * Megabyte];
        var baseOffset = 768 * 1024;
        WriteMbr(window.AsSpan(baseOffset, SectorSize), 0x05, 1024, 12288);

        var found = PartitionTableScanner.TryFind(window, 0, 8 * Megabyte, out var tableOffset, out var kind);

        Assert.True(found);
        Assert.Equal(baseOffset, tableOffset);
        Assert.Equal(PartitionTableKind.Mbr, kind);
    }

    [Fact]
    public void TryFind_MbrWithNonZeroWindowOffset_AddsWindowOriginToReportedOffset()
    {
        var window = new byte[4 * SectorSize];
        WriteMbr(window.AsSpan(SectorSize, SectorSize), 0x83, 4096, 8192);

        var found = PartitionTableScanner.TryFind(window, 1 * Megabyte, 16 * Megabyte, out var tableOffset,
            out var kind);

        Assert.True(found);
        Assert.Equal(Megabyte + SectorSize, tableOffset);
        Assert.Equal(PartitionTableKind.Mbr, kind);
    }

    [Fact]
    public void TryFind_ValidGptHeaderAfterProtectiveMbr_ReportsGptAndItsBaseSector()
    {
        var baseOffset = 1024 * 1024L;
        var window = new byte[8 * SectorSize];
        WriteMbr(window.AsSpan(0, SectorSize), GptLayout.ProtectiveMbrType, 1, 16 * 2048);
        WriteGptHeader(window.AsSpan(SectorSize, SectorSize), alternateLba: 4095);

        var found = PartitionTableScanner.TryFind(window, baseOffset, 64 * Megabyte, out var tableOffset,
            out var kind);

        Assert.True(found);
        Assert.Equal(baseOffset, tableOffset);
        Assert.Equal(PartitionTableKind.Gpt, kind);
    }

    [Fact]
    public void TryFind_EfiPartTextWithoutValidHeaderCrc_IsRejected()
    {
        var window = new byte[8 * SectorSize];
        var sector = window.AsSpan(SectorSize, SectorSize);
        GptLayout.Signature.CopyTo(sector);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[8..], GptLayout.Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[12..], GptLayout.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(sector[24..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(sector[32..], 4095);
        BinaryPrimitives.WriteUInt64LittleEndian(sector[40..], 34);
        BinaryPrimitives.WriteUInt64LittleEndian(sector[48..], 4094);
        BinaryPrimitives.WriteUInt64LittleEndian(sector[72..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[80..], 128);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[84..], 128);

        var found = PartitionTableScanner.TryFind(window, 0, 64 * Megabyte, out var tableOffset, out var kind);

        Assert.False(found);
        Assert.Equal(-1, tableOffset);
        Assert.Equal(PartitionTableKind.Unknown, kind);
    }

    [Fact]
    public void TryFind_GptHeaderWithForeignMyLba_FallsBackToProtectiveMbr()
    {
        var window = new byte[8 * SectorSize];
        WriteMbr(window.AsSpan(0, SectorSize), GptLayout.ProtectiveMbrType, 1, 16 * 2048);
        WriteGptHeader(window.AsSpan(SectorSize, SectorSize), alternateLba: 4095, myLba: 7);

        var found = PartitionTableScanner.TryFind(window, 0, 64 * Megabyte, out var tableOffset, out var kind);

        Assert.True(found);
        Assert.Equal(0, tableOffset);
        Assert.Equal(PartitionTableKind.Mbr, kind);
    }

    [Fact]
    public void TryFind_FatVolumeBootRecord_IsRejectedAsMbr()
    {
        var window = new byte[4 * SectorSize];
        var sector = window.AsSpan(0, SectorSize);
        sector[0] = 0xEB;
        sector[2] = 0x90;
        "MSDOS5.0"u8.CopyTo(sector[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(sector[11..], SectorSize);
        sector[13] = 8;
        BinaryPrimitives.WriteUInt16LittleEndian(sector[14..], 32);
        sector[16] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(sector[17..], 512);
        BinaryPrimitives.WriteUInt16LittleEndian(sector[19..], 40960);
        sector[21] = 0xF8;
        BinaryPrimitives.WriteUInt16LittleEndian(sector[22..], 32);
        sector[510] = MbrTable.SignatureLo;
        sector[511] = MbrTable.SignatureHi;

        var found = PartitionTableScanner.TryFind(window, 0, 4 * Megabyte, out _, out _);

        Assert.False(found);
    }

    [Fact]
    public void TryFind_WindowWithoutAnyTable_ReturnsFalse()
    {
        var window = new byte[16 * SectorSize];
        window.AsSpan().Fill(0xA5);

        var found = PartitionTableScanner.TryFind(window, 0, 4 * Megabyte, out var tableOffset, out var kind);

        Assert.False(found);
        Assert.Equal(-1, tableOffset);
        Assert.Equal(PartitionTableKind.Unknown, kind);
    }

    [Fact]
    public void TryFind_EmptyWindow_ReturnsFalse()
    {
        var found = PartitionTableScanner.TryFind(ReadOnlySpan<byte>.Empty, 0, 4 * Megabyte, out var tableOffset,
            out var kind);

        Assert.False(found);
        Assert.Equal(-1, tableOffset);
        Assert.Equal(PartitionTableKind.Unknown, kind);
    }

    [Fact]
    public void TryFind_MbrWhoseExtendedEntrySpansToDiskEnd_IsAcceptedOverItsEbr()
    {
        var mainBase = 512 * 1024;
        var ebrBase = 2 * 1024 * 1024;
        var window = new byte[3 * Megabyte];
        WriteExtendedMbr(window.AsSpan(mainBase, SectorSize));
        WriteMbr(window.AsSpan(ebrBase, SectorSize), 0x83, 123904, 1638400);

        var found = PartitionTableScanner.TryFind(window, 0, 8 * 1024 * Megabyte, out var tableOffset, out var kind);

        Assert.True(found);
        Assert.Equal(mainBase, tableOffset);
        Assert.Equal(PartitionTableKind.Mbr, kind);
    }

    [Fact]
    public void HasPartitionTable_ExtendedEntrySpanningToDiskEnd_IsAccepted()
    {
        var sector = new byte[SectorSize];
        WriteExtendedMbr(sector);

        var isTable = MbrTable.HasPartitionTable(sector);

        Assert.True(isTable);
    }

    [Fact]
    public void TryFind_WindowOffsetBeyondExtent_ReturnsFalse()
    {
        var window = new byte[SectorSize];
        WriteMbr(window, 0x83, 2048, 20480);

        var found = PartitionTableScanner.TryFind(window, 8 * Megabyte, 4 * Megabyte, out _, out _);

        Assert.False(found);
    }

    [Fact]
    public void TryFind_NegativeWindowOffset_Throws()
    {
        var window = new byte[SectorSize];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PartitionTableScanner.TryFind(window, -1, 4 * Megabyte, out _, out _));
    }

    [Fact]
    public void TryFind_NonPositiveExtent_Throws()
    {
        var window = new byte[SectorSize];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PartitionTableScanner.TryFind(window, 0, 0, out _, out _));
    }

    private static void WriteMbr(Span<byte> sector, byte partitionType, uint startLba, uint sectorCount)
    {
        sector.Clear();
        var entry = sector.Slice(446, 16);
        entry[0] = 0;
        entry[4] = partitionType;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], startLba);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], sectorCount);
        sector[510] = MbrTable.SignatureLo;
        sector[511] = MbrTable.SignatureHi;
    }

    private static void WriteExtendedMbr(Span<byte> sector)
    {
        sector.Clear();
        WriteEntry(sector, 0, 0x00, 0x05, 1024, uint.MaxValue);
        WriteEntry(sector, 1, 0x00, 0x83, 18432, 20480);
        WriteEntry(sector, 2, 0x00, 0x83, 38912, 20480);
        WriteEntry(sector, 3, 0x00, 0x83, 84992, 12288);
        sector[510] = MbrTable.SignatureLo;
        sector[511] = MbrTable.SignatureHi;
    }

    private static void WriteEntry(Span<byte> sector, int index, byte boot, byte type, uint startLba, uint sectorCount)
    {
        var entry = sector.Slice(446 + (index * 16), 16);
        entry[0] = boot;
        entry[4] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], startLba);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], sectorCount);
    }

    private static void WriteGptHeader(Span<byte> sector, ulong alternateLba, ulong myLba = 1)
    {
        sector.Clear();
        GptLayout.Signature.CopyTo(sector);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[8..], GptLayout.Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[12..], GptLayout.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(sector[24..], myLba);
        BinaryPrimitives.WriteUInt64LittleEndian(sector[32..], alternateLba);
        BinaryPrimitives.WriteUInt64LittleEndian(sector[40..], 34);
        BinaryPrimitives.WriteUInt64LittleEndian(sector[48..], alternateLba - 1);
        sector[56] = 0x11;
        BinaryPrimitives.WriteUInt64LittleEndian(sector[72..], GptLayout.EntriesStartLba);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[80..], 128);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[84..], GptLayout.EntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[GptLayout.HeaderCrcOffset..],
            Crc32Ieee.Compute(sector[..GptLayout.HeaderSize]));
    }
}