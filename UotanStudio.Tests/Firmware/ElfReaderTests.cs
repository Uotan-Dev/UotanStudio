using System.Buffers.Binary;
using UotanStudio.Firmware.Executable;
using UotanStudio.Firmware.Executable.Elf;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ElfReaderTests
{
    public static bool FirehoseSampleAvailable => RealSampleIndex.IsAvailable(RealSampleIndex.ElfFirehoseVariable);

    [Fact(Skip = "Set UOTAN_ELF_FIREHOSE_SAMPLE to the SM7150 DDR Firehose sample.", SkipUnless = nameof(FirehoseSampleAvailable))]
    public async Task ReadAsync_Sm7150Firehose_MatchesReadelfMetadata()
    {
        await RealSampleIndex.VerifyIdentityAsync(RealSampleIndex.GetPath(RealSampleIndex.ElfFirehoseVariable),
            RealSampleIndex.ElfFirehoseVariable, RealSampleIndex.ElfFirehoseSha256,
            TestContext.Current.CancellationToken);

        var info = await ElfReader.ReadAsync(RealSampleIndex.GetPath(RealSampleIndex.ElfFirehoseVariable),
            TestContext.Current.CancellationToken);

        Assert.True(info.Header.Is64Bit);
        Assert.True(info.Header.IsLittleEndian);
        Assert.Equal(183, info.Header.Machine);
        Assert.Equal(0x1482d2a8UL, info.Header.EntryPoint);
        Assert.Equal(18, info.Segments.Count);
        Assert.Empty(info.Sections);
        Assert.Equal(0x80080UL, info.Segments[2].Offset);
        Assert.Equal(0UL, info.Segments[2].FileSize);
        Assert.Equal(0x14680000UL, info.Segments[2].VirtualAddress);
        Assert.Equal(0x1000UL, info.Segments[2].Alignment);
        Assert.Equal(0x5c250UL, info.Segments[3].Offset);
        Assert.Equal(0x1dccUL, info.Segments[3].FileSize);
        Assert.Equal(0x14699000UL, info.Segments[3].VirtualAddress);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Read_NonCongruentLoadSegment_PreservesMetadata(bool is64, bool little)
    {
        using var stream = new MemoryStream(ElfFixture.Create(is64, little, segmentOffset: 481));
        stream.Position = 7;

        var info = ElfReader.Read(stream, TestContext.Current.CancellationToken);

        var segment = Assert.Single(info.Segments);
        Assert.Equal(481UL, segment.Offset);
        Assert.Equal(0x1000UL, segment.VirtualAddress);
        Assert.Equal(16UL, segment.Alignment);
        Assert.Equal(4UL, segment.FileSize);
        Assert.Equal(7, stream.Position);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Read_ClassesAndByteOrders_DecodesHeadersAndNames(bool is64, bool little)
    {
        using var stream = new MemoryStream(ElfFixture.Create(is64, little));
        stream.Position = 7;

        var info = ElfReader.Read(stream, TestContext.Current.CancellationToken);

        Assert.Equal(is64, info.Header.Is64Bit);
        Assert.Equal(little, info.Header.IsLittleEndian);
        Assert.Equal(is64 ? 0x123456789abcdef0UL : 0x12345678UL, info.Header.EntryPoint);
        Assert.Equal(243, info.Header.Machine);
        Assert.Equal(4u, info.Header.SectionHeaderCount);
        var segment = Assert.Single(info.Segments);
        Assert.Equal(0x1000UL, segment.VirtualAddress);
        Assert.Equal(4UL, segment.FileSize);
        Assert.Equal(8UL, segment.MemorySize);
        Assert.Equal(".text", info.Sections[2].Name);
        Assert.Equal(".bss", info.Sections[3].Name);
        Assert.Equal(8u, info.Sections[3].Type);
        Assert.Equal(0x100000UL, info.Sections[3].Size);
        Assert.Equal(7, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Read_ExtendedNumbering_ResolvesSectionZero(bool is64, bool little)
    {
        using var stream = new MemoryStream(ElfFixture.Create(is64, little, extended: true));

        var info = ElfReader.Read(stream, TestContext.Current.CancellationToken);

        Assert.Equal(1u, info.Header.ProgramHeaderCount);
        Assert.Equal(4u, info.Header.SectionHeaderCount);
        Assert.Equal(1u, info.Header.SectionNameTableIndex);
        Assert.Equal(".text", info.Sections[2].Name);
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(4, 3)]
    [InlineData(5, 0)]
    [InlineData(5, 3)]
    [InlineData(6, 0)]
    [InlineData(20, 0)]
    [InlineData(52, 63)]
    [InlineData(54, 55)]
    [InlineData(58, 63)]
    [InlineData(62, 5)]
    public void TryReadHeader_InvalidIdentificationOrLayout_Rejects(int offset, byte value)
    {
        var data = ElfFixture.Create();
        data[offset] = value;

        Assert.False(ElfReader.TryReadHeader(data, out var header));
        Assert.Null(header);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(51)]
    [InlineData(63)]
    public void TryReadHeader_TruncatedElf64_Rejects(int length)
    {
        Assert.False(ElfReader.TryReadHeader(ElfFixture.Create().AsSpan(0, length), out _));
    }

    [Fact]
    public void Read_MinimalElf32_DoesNotRequire64BytesOrTables()
    {
        var data = new byte[52];
        "\u007fELF"u8.CopyTo(data);
        data[4] = data[5] = data[6] = data[20] = 1;
        data[40] = 52;
        using var stream = new MemoryStream(data);

        var info = ElfReader.Read(stream, TestContext.Current.CancellationToken);

        Assert.False(info.Header.Is64Bit);
        Assert.Empty(info.Segments);
        Assert.Empty(info.Sections);
    }

    [Theory]
    [InlineData(32, ulong.MaxValue)]
    [InlineData(40, ulong.MaxValue)]
    [InlineData(72, ulong.MaxValue)]
    [InlineData(96, ulong.MaxValue)]
    [InlineData(280, ulong.MaxValue)]
    [InlineData(288, ulong.MaxValue)]
    public void Read_OutOfRangeTableOrSectionRange_RejectsViaSharedBoundsValidator(int offset, ulong value)
    {
        var data = ElfFixture.Create();
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);
        using var stream = new MemoryStream(data);

        Assert.Throws<InvalidDataException>(() => ElfReader.Read(stream, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(104, 3UL, "ELF load-segment file size exceeds its memory size.")]
    [InlineData(112, 3UL, "ELF alignment is not a power of two.")]
    public void Read_CorruptSegmentFields_ReportTypedCorruption(int offset, ulong value, string message)
    {
        var data = ElfFixture.Create();
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => ElfReader.Read(stream, TestContext.Current.CancellationToken));
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void Read_InvalidIdentification_ReportsTypedCorruption()
    {
        var data = ElfFixture.Create();
        data[6] = 2;
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => ElfReader.Read(stream, TestContext.Current.CancellationToken));
        Assert.Equal("Invalid or truncated ELF header.", error.Message);
    }

    [Fact]
    public void Read_NonNullSectionZero_ReportsTypedCorruption()
    {
        var data = ElfFixture.Create(extended: true);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(132), 1);
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => ElfReader.Read(stream, TestContext.Current.CancellationToken));
        Assert.Equal("ELF section zero must be SHT_NULL.", error.Message);
    }

    [Fact]
    public void Read_ExtendedNameTableIndexOutOfRange_ReportsTypedCorruption()
    {
        var data = ElfFixture.Create(extended: true);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(168), 99);
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => ElfReader.Read(stream, TestContext.Current.CancellationToken));
        Assert.Equal("ELF section-name table index is out of range.", error.Message);
    }

    [Fact]
    public void Read_SectionLinkOutOfRange_ReportsTypedCorruption()
    {
        var data = ElfFixture.Create();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(296), 99);
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => ElfReader.Read(stream, TestContext.Current.CancellationToken));
        Assert.Equal("Invalid ELF section link or entry size.", error.Message);
    }

    [Fact]
    public void Read_NameStringTableWrongType_ReportsTypedCorruption()
    {
        var data = ElfFixture.Create();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(196), 1);
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => ElfReader.Read(stream, TestContext.Current.CancellationToken));
        Assert.Equal("Invalid or excessive ELF section-name string table.", error.Message);
    }

    [Fact]
    public void Read_ExtendedSegmentCountAboveEntryLimit_RejectsBeforeAllocatingTable()
    {
        var data = ElfFixture.Create(extended: true);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(172), 200000);
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => ElfReader.Read(stream, TestContext.Current.CancellationToken));
        Assert.Equal("ELF table exceeds the metadata entry limit.", error.Message);
    }

    [Fact]
    public void Read_ExcessiveExtendedCount_RejectsBeforeAllocatingTable()
    {
        var data = ElfFixture.Create(extended: true);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(160), uint.MaxValue);
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => ElfReader.Read(stream, TestContext.Current.CancellationToken));
        Assert.Equal("Invalid or excessive ELF extended section count.", error.Message);
    }

    [Fact]
    public void Read_InvalidNameOffset_Rejects()
    {
        var data = ElfFixture.Create();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(256), 100);
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => ElfReader.Read(stream, TestContext.Current.CancellationToken));
        Assert.Equal("ELF section name is outside the string table.", error.Message);
    }

    [Fact]
    public void Read_UnterminatedStringTable_Rejects()
    {
        var data = ElfFixture.Create();
        data[438] = 1;
        using var stream = new MemoryStream(data);

        var error = Assert.Throws<ExecutableCorruptedException>(
            () => ElfReader.Read(stream, TestContext.Current.CancellationToken));
        Assert.Equal("ELF string table must start and end with NUL.", error.Message);
    }

    [Fact]
    public void Read_HugeFile_ReadsOnlyBoundedMetadata()
    {
        using var stream = new LargeStream(ElfFixture.Create());

        var info = ElfReader.Read(stream, TestContext.Current.CancellationToken);

        Assert.Equal(8L * 1024 * 1024 * 1024, info.FileSize);
        Assert.InRange(stream.BytesRead, 1, 512);
    }

    [Fact]
    public void Read_Cancelled_RestoresStreamPosition()
    {
        using var stream = new MemoryStream(ElfFixture.Create());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stream.Position = 17;
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ElfReader.Read(stream, cancellation.Token));
        Assert.Equal(17, stream.Position);
    }

    private sealed class LargeStream(byte[] data) : MemoryStream(data)
    {
        public int BytesRead { get; private set; }
        public override long Length => 8L * 1024 * 1024 * 1024;

        public override int Read(Span<byte> buffer)
        {
            var count = base.Read(buffer);
            BytesRead += count;
            return count;
        }
    }
}

internal static class ElfFixture
{
    internal static byte[] Create(bool is64 = true, bool little = true, bool extended = false, ulong segmentOffset = 480)
    {
        var data = new byte[512];
        "\u007fELF"u8.CopyTo(data);
        data[4] = is64 ? (byte)2 : (byte)1;
        data[5] = little ? (byte)1 : (byte)2;
        data[6] = 1;
        Write(16, 2, 2);
        Write(18, 243, 2);
        Write(20, 1, 4);
        Word(24, is64 ? 0x123456789abcdef0UL : 0x12345678UL);
        Word(is64 ? 32 : 28, 64);
        Word(is64 ? 40 : 32, 128);
        var tail = is64 ? 52 : 40;
        Write(tail, is64 ? 64UL : 52UL, 2);
        Write(tail + 2, is64 ? 56UL : 32UL, 2);
        Write(tail + 4, extended ? 0xffffUL : 1UL, 2);
        Write(tail + 6, is64 ? 64UL : 40UL, 2);
        Write(tail + 8, extended ? 0UL : 4UL, 2);
        Write(tail + 10, extended ? 0xffffUL : 1UL, 2);
        Write(64, 1, 4);
        Write(is64 ? 68 : 88, 5, 4);
        Word(is64 ? 72 : 68, segmentOffset);
        Word(is64 ? 80 : 72, 0x1000);
        Word(is64 ? 88 : 76, 0x2000);
        Word(is64 ? 96 : 80, 4);
        Word(is64 ? 104 : 84, 8);
        Word(is64 ? 112 : 92, 16);
        Section(0, 0, 0, 0, extended ? 4UL : 0UL);
        if (extended)
        {
            Write(128 + (is64 ? 40 : 24), 1, 4);
            Write(128 + (is64 ? 44 : 28), 1, 4);
        }

        Section(1, 1, 3, 416, 23);
        Section(2, 11, 1, 480, 4);
        Section(3, 17, 8, 512, 0x100000);
        "\0.shstrtab\0.text\0.bss\0\0\0"u8.CopyTo(data.AsSpan(416));
        return data;

        void Section(int index, uint name, uint type, ulong offset, ulong size)
        {
            var start = 128 + index * (is64 ? 64 : 40);
            Write(start, name, 4);
            Write(start + 4, type, 4);
            Word(start + (is64 ? 24 : 16), offset);
            Word(start + (is64 ? 32 : 20), size);
            Word(start + (is64 ? 48 : 32), 1);
        }

        void Word(int offset, ulong value) => Write(offset, value, is64 ? 8 : 4);

        void Write(int offset, ulong value, int size)
        {
            for (var i = 0; i < size; i++)
            {
                data[offset + (little ? i : size - 1 - i)] = (byte)(value >> (8 * i));
            }
        }
    }
}