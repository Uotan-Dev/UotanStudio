using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AllwinnerImageWtyTests : IDisposable
{
    private const int NewTableStart = 0x400;
    private const int NewLayoutItemCountOffset = 60;
    private const int NewLayoutItemTableOffset = 64;
    private const int NewItemDataLengthOffset = 292;
    private const int NewItemFileLengthOffset = 300;
    private const int NewItemPayloadOffset = 308;
    private const int ClassicTableStart = 0x400;
    private const int ClassicRecordSize = 84;
    private const int ClassicTotalHeaderSizeOffset = 4;
    private const int ClassicOriginalLengthOffset = 36;
    private const int ClassicV1CountOffset = 56;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-imagewty-" + Guid.NewGuid().ToString("N"));

    public AllwinnerImageWtyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] BuildNewImage(uint headerVersion, (string Name, byte[] Payload)[] files,
        uint pid = 0x1234, uint vid = 0x5678, uint attributes = 0)
    {
        var dataStart = NewTableStart + files.Length * AllwinnerImageWtyFormat.EntryRecordSize;
        var totalSize = dataStart + files.Sum(file => (long)file.Payload.Length);
        var image = new byte[totalSize];
        "IMAGEWTY"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), headerVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), (uint)Math.Min(totalSize, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(36), pid);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(40), vid);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(44), 0x1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(48), 0x1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(NewLayoutItemCountOffset), (uint)files.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(NewLayoutItemTableOffset), NewTableStart);
        var offset = (long)dataStart;
        for (var index = 0; index < files.Length; index++)
        {
            var record = NewTableStart + index * AllwinnerImageWtyFormat.EntryRecordSize;
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(record), 0x100);
            Encoding.ASCII.GetBytes("SYS").CopyTo(image, record + 8);
            Encoding.ASCII.GetBytes(files[index].Name).CopyTo(image, record + 16);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(record + 32), attributes);
            Encoding.ASCII.GetBytes(files[index].Name).CopyTo(image, record + 36);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(record + NewItemDataLengthOffset), (ulong)files[index].Payload.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(record + NewItemFileLengthOffset), (ulong)files[index].Payload.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(record + NewItemPayloadOffset), (ulong)offset);
            files[index].Payload.CopyTo(image, offset);
            offset += files[index].Payload.Length;
        }

        return image;
    }

    private static byte[] BuildClassicImage(uint headerVersion, (string Name, byte[] Payload)[] files,
        uint pid = 0x1234, uint vid = 0x5678)
    {
        var dataStart = ClassicTableStart + files.Length * ClassicRecordSize;
        var totalSize = dataStart + files.Sum(file => (long)file.Payload.Length);
        var image = new byte[totalSize];
        "IMAGEWTY"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), headerVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), (uint)Math.Min(totalSize, uint.MaxValue));
        var shift = headerVersion == AllwinnerImageWtyFormat.HeaderVersionV3 ? 4 : 0;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32 + shift), pid);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(36 + shift), vid);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(56 + shift), (uint)files.Length);
        var offset = (long)dataStart;
        for (var index = 0; index < files.Length; index++)
        {
            var record = ClassicTableStart + index * ClassicRecordSize;
            var nameBytes = Encoding.ASCII.GetBytes(files[index].Name);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(record), (uint)nameBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(record + ClassicTotalHeaderSizeOffset), ClassicRecordSize);
            Encoding.ASCII.GetBytes("FEX").CopyTo(image, record + 8);
            Encoding.ASCII.GetBytes(files[index].Name).CopyTo(image, record + 16);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(record + 32), (uint)files[index].Payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(record + ClassicOriginalLengthOffset), (uint)files[index].Payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(record + 40), (uint)offset);
            nameBytes.CopyTo(image, record + 44);
            files[index].Payload.CopyTo(image, offset);
            offset += files[index].Payload.Length;
        }

        return image;
    }

    private static byte[] PatchWord(byte[] image, int offset, uint value)
    {
        var patched = (byte[])image.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(offset), value);
        return patched;
    }

    private static byte[] PatchQuadWord(byte[] image, int offset, ulong value)
    {
        var patched = (byte[])image.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(patched.AsSpan(offset), value);
        return patched;
    }

    [Fact]
    public void Read_NewFormatV1Container_ReturnsIdsEntriesAndLayout()
    {
        using var stream = new MemoryStream(BuildNewImage(AllwinnerImageWtyFormat.HeaderVersionV1,
            [("boot0", new byte[100]), ("u-boot.bin", new byte[50])]));

        var header = AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken);

        Assert.Equal(AllwinnerImageWtyFormat.HeaderVersionV1, header.HeaderVersion);
        Assert.Equal(ImageWtyLayout.NewFormat, header.Layout);
        Assert.Equal(0x5678u, header.Vid);
        Assert.Equal(0x1234u, header.Pid);
        Assert.Equal(2, header.Entries.Count);
        Assert.Equal("boot0", header.Entries[0].Name);
        Assert.Equal("SYS", header.Entries[0].MainType);
        Assert.Equal("boot0", header.Entries[0].SubType);
        Assert.Equal(ImageWtyLayout.NewFormat, header.Entries[0].Layout);
        Assert.Equal(NewTableStart + 2 * AllwinnerImageWtyFormat.EntryRecordSize, header.Entries[0].Offset);
        Assert.Equal(100, header.Entries[0].Length);
        Assert.Equal(100, header.Entries[0].FileLength);
        Assert.Equal(100, header.Entries[0].DataLength);
        Assert.Equal(NewTableStart + 2 * AllwinnerImageWtyFormat.EntryRecordSize + 100, header.Entries[1].Offset);
        Assert.Equal(50, header.Entries[1].Length);
    }

    [Fact]
    public void Read_NewFormatV3Container_ReadsLargeDeclaredFileLength()
    {
        var image = PatchQuadWord(BuildNewImage(AllwinnerImageWtyFormat.HeaderVersionV3, [("boot0", new byte[16])]),
            NewTableStart + NewItemFileLengthOffset, 0x1_0000_0005UL);
        using var stream = new MemoryStream(image);

        var header = AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken);

        Assert.Equal(AllwinnerImageWtyFormat.HeaderVersionV3, header.HeaderVersion);
        Assert.Single(header.Entries);
        Assert.Equal(16, header.Entries[0].Length);
        Assert.Equal(16, header.Entries[0].DataLength);
        Assert.Equal(0x1_0000_0005L, header.Entries[0].FileLength);
    }

    [Fact]
    public void Read_ClassicV1Container_ParsesFileHeaderChain()
    {
        var image = PatchWord(BuildClassicImage(AllwinnerImageWtyFormat.HeaderVersionV1,
            [("boot0", new byte[100]), ("u-boot.bin", new byte[50])]),
            ClassicTableStart + ClassicOriginalLengthOffset, 124);
        using var stream = new MemoryStream(image);

        var header = AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken);

        Assert.Equal(AllwinnerImageWtyFormat.HeaderVersionV1, header.HeaderVersion);
        Assert.Equal(ImageWtyLayout.Classic, header.Layout);
        Assert.Equal(0x1234u, header.Pid);
        Assert.Equal(0x5678u, header.Vid);
        Assert.Equal(2, header.Entries.Count);
        Assert.Equal("boot0", header.Entries[0].Name);
        Assert.Equal("FEX", header.Entries[0].MainType);
        Assert.Equal("boot0", header.Entries[0].SubType);
        Assert.Equal(ImageWtyLayout.Classic, header.Entries[0].Layout);
        Assert.Equal(ClassicTableStart + 2 * ClassicRecordSize, header.Entries[0].Offset);
        Assert.Equal(100, header.Entries[0].Length);
        Assert.Equal(100, header.Entries[0].DataLength);
        Assert.Equal(124, header.Entries[0].FileLength);
        Assert.Equal(ClassicTableStart + 2 * ClassicRecordSize + 100, header.Entries[1].Offset);
        Assert.Equal(50, header.Entries[1].Length);
    }

    [Fact]
    public void Read_ClassicV3Container_UsesShiftedBodyMetadata()
    {
        using var stream = new MemoryStream(BuildClassicImage(AllwinnerImageWtyFormat.HeaderVersionV3,
            [("opensbi.bin", new byte[64])], pid: 0xE896, vid: 0x1F25));

        var header = AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken);

        Assert.Equal(AllwinnerImageWtyFormat.HeaderVersionV3, header.HeaderVersion);
        Assert.Equal(ImageWtyLayout.Classic, header.Layout);
        Assert.Equal(0xE896u, header.Pid);
        Assert.Equal(0x1F25u, header.Vid);
        Assert.Single(header.Entries);
        Assert.Equal(64, header.Entries[0].Length);
    }

    [Theory]
    [InlineData("encrypted")]
    [InlineData("unknown-version")]
    [InlineData("new-itemcount-over-limit")]
    [InlineData("new-itemtable-past-end")]
    [InlineData("new-entry-payload-past-end")]
    [InlineData("new-entry-offset-past-end")]
    [InlineData("classic-header-size-out-of-range")]
    [InlineData("classic-record-past-end")]
    [InlineData("classic-count-over-limit")]
    public void Read_MalformedVariants_RejectsWithInvalidData(string failure)
    {
        var newImage = BuildNewImage(AllwinnerImageWtyFormat.HeaderVersionV3, [("boot0", new byte[8])]);
        var classicImage = BuildClassicImage(AllwinnerImageWtyFormat.HeaderVersionV1, [("boot0", new byte[8])]);
        var image = failure switch
        {
            "encrypted" => newImage,
            "unknown-version" => BuildNewImage(0x200, [("boot0", new byte[8])]),
            "new-itemcount-over-limit" => PatchWord(newImage, NewLayoutItemCountOffset,
                (uint)(AllwinnerImageWtyFormat.MaximumEntries + 1)),
            "new-itemtable-past-end" => PatchWord(newImage, NewLayoutItemTableOffset, (uint)newImage.Length + 1),
            "new-entry-payload-past-end" => PatchQuadWord(newImage, NewTableStart + NewItemDataLengthOffset,
                (ulong)newImage.Length + 1),
            "new-entry-offset-past-end" => PatchQuadWord(newImage, NewTableStart + NewItemPayloadOffset,
                (ulong)newImage.Length + 1),
            "classic-header-size-out-of-range" => PatchWord(classicImage,
                ClassicTableStart + ClassicTotalHeaderSizeOffset, 9999),
            "classic-record-past-end" => PatchWord(classicImage,
                ClassicTableStart + ClassicTotalHeaderSizeOffset, 4096),
            "classic-count-over-limit" => PatchWord(classicImage, ClassicV1CountOffset,
                (uint)(AllwinnerImageWtyFormat.MaximumEntries + 1)),
            _ => throw new InvalidOperationException($"Unknown failure variant {failure}.")
        };
        if (failure == "encrypted")
        {
            image[0] = (byte)'X';
        }

        using var stream = new MemoryStream(image);

        Assert.Throws<InvalidDataException>(() =>
            AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CanHandle_PlainAndEncryptedMagicOnlyFiles_DecidesByMagic()
    {
        var handler = new AllwinnerImageWtyFormatHandler();
        var plain = Path.Combine(_root, "plain.img");
        await File.WriteAllBytesAsync(plain,
            BuildNewImage(AllwinnerImageWtyFormat.HeaderVersionV1, [("boot0", [1])]),
            TestContext.Current.CancellationToken);
        var encrypted = Path.Combine(_root, "crypto.img");
        var bytes = BuildNewImage(AllwinnerImageWtyFormat.HeaderVersionV1, [("boot0", [1])]);
        bytes[7] = (byte)'X';
        await File.WriteAllBytesAsync(encrypted, bytes, TestContext.Current.CancellationToken);

        Assert.True(handler.CanHandle(plain));
        Assert.False(handler.CanHandle(encrypted));
    }

    [Fact]
    public async Task UnpackAsync_EntriesWithZeroLengthPlaceholder_WritesOnlyRealPayloads()
    {
        var payload = new byte[512];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index ^ 0x5A);
        }

        var image = BuildNewImage(AllwinnerImageWtyFormat.HeaderVersionV1,
            [("u-boot.bin", payload), ("empty.fex", [])]);
        var path = Path.Combine(_root, "unpack.img");
        await File.WriteAllBytesAsync(path, image, TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "out");

        var result = await new AllwinnerImageWtyFormatHandler().UnpackAsync(path, output,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(output, "u-boot.bin"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(output, "empty.fex")));
    }

    [Fact]
    public async Task ReadMetadataAsync_V3Container_ReportsHeaderWords()
    {
        var path = Path.Combine(_root, "meta.img");
        await File.WriteAllBytesAsync(path,
            BuildNewImage(AllwinnerImageWtyFormat.HeaderVersionV3, [("boot0", new byte[16])], pid: 0xE896, vid: 0x1F25),
            TestContext.Current.CancellationToken);

        var result = await new AllwinnerImageWtyFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("0x300", result.Metadata["HeaderVersion"]);
        Assert.Equal("0x1F25", result.Metadata["Vid"]);
        Assert.Equal("0xE896", result.Metadata["Pid"]);
    }

    [Fact]
    public void ExtractEntry_NewFormatContainer_CopiesExactPayload()
    {
        var payload = new byte[300];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index ^ 0xA5);
        }

        using var stream = new MemoryStream(BuildNewImage(AllwinnerImageWtyFormat.HeaderVersionV3,
            [("boot0", payload), ("u-boot.bin", [1, 2, 3])]));
        var header = AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken);
        using var output = new MemoryStream();

        AllwinnerImageWtyFormat.ExtractEntry(stream, header.Entries[0], output, TestContext.Current.CancellationToken);

        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public void ExtractEntry_ClassicContainer_CopiesExactPayload()
    {
        var first = new byte[200];
        for (var index = 0; index < first.Length; index++)
        {
            first[index] = (byte)(index ^ 0x3C);
        }

        var second = new byte[64];
        for (var index = 0; index < second.Length; index++)
        {
            second[index] = (byte)(index ^ 0xC3);
        }

        using var stream = new MemoryStream(BuildClassicImage(AllwinnerImageWtyFormat.HeaderVersionV1,
            [("boot0", first), ("u-boot.bin", second)]));
        var header = AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken);
        using var firstOutput = new MemoryStream();
        using var secondOutput = new MemoryStream();

        AllwinnerImageWtyFormat.ExtractEntry(stream, header.Entries[0], firstOutput, TestContext.Current.CancellationToken);
        AllwinnerImageWtyFormat.ExtractEntry(stream, header.Entries[1], secondOutput, TestContext.Current.CancellationToken);

        Assert.Equal(first, firstOutput.ToArray());
        Assert.Equal(second, secondOutput.ToArray());
    }

    [Fact]
    public void Read_CanceledDuringEntryLoop_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancelingStream(
            new MemoryStream(BuildNewImage(AllwinnerImageWtyFormat.HeaderVersionV3, [("boot0", new byte[32])])),
            cancellation,
            50);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            AllwinnerImageWtyFormat.Read(stream, cancellation.Token));
    }

    [Fact]
    public async Task UnpackAsync_PreCanceledToken_ThrowsBeforePublishing()
    {
        var path = Path.Combine(_root, "cancel.img");
        await File.WriteAllBytesAsync(path,
            BuildNewImage(AllwinnerImageWtyFormat.HeaderVersionV1, [("boot0", [1])]),
            TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "cancel-out");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AllwinnerImageWtyFormatHandler().UnpackAsync(path, output, cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(output) && Directory.EnumerateFiles(output).Any());
    }

    private sealed class CancelingStream(Stream inner, CancellationTokenSource source, int triggerBytes) : Stream
    {
        private long _served;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            _served += read;
            if (_served >= triggerBytes)
            {
                source.Cancel();
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                source.Dispose();
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}