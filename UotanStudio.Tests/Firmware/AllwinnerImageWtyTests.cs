using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AllwinnerImageWtyTests : IDisposable
{
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

    private static byte[] BuildImage(uint headerVersion, (string Name, byte[] Payload)[] files,
        uint pid = 0x1234, uint vid = 0x5678, bool breakPayloadRange = false)
    {
        var bodyLength = headerVersion == AllwinnerImageWtyFormat.HeaderVersionV3 ? 52 : 48;
        var tableStart = 32 + bodyLength;
        var dataStart = tableStart + files.Length * AllwinnerImageWtyFormat.EntryRecordSize;
        var imageSize = dataStart + files.Sum(f => (long)f.Payload.Length);
        var image = new byte[(int)imageSize];
        "IMAGEWTY"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), headerVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(24), (uint)Math.Min(imageSize, uint.MaxValue));
        var shift = headerVersion == AllwinnerImageWtyFormat.HeaderVersionV3 ? 4 : 0;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32 + shift), pid);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(36 + shift), vid);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32 + shift + 24), (uint)files.Length);
        var offset = dataStart;
        for (var index = 0; index < files.Length; index++)
        {
            var record = tableStart + index * AllwinnerImageWtyFormat.EntryRecordSize;
            Encoding.ASCII.GetBytes(files[index].Name).CopyTo(image, record);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(record + 32), (uint)offset);
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(record + 40), (ulong)files[index].Payload.Length);
            "bootloader"u8.CopyTo(image.AsSpan(record + 48));
            "normal"u8.CopyTo(image.AsSpan(record + 80));
            files[index].Payload.CopyTo(image, offset);
            offset += files[index].Payload.Length;
        }

        if (breakPayloadRange)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(tableStart + 40), (ulong)image.Length + 1);
        }

        return image;
    }

    [Fact]
    public void Read_V1Container_ReturnsIdsAndEntryRanges()
    {
        using var stream = new MemoryStream(BuildImage(AllwinnerImageWtyFormat.HeaderVersionV1,
            [("boot0", new byte[100]), ("u-boot.bin", new byte[50])]));

        var header = AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken);

        Assert.Equal(AllwinnerImageWtyFormat.HeaderVersionV1, header.HeaderVersion);
        Assert.Equal(0x5678u, header.Vid);
        Assert.Equal(0x1234u, header.Pid);
        Assert.Equal(2, header.Entries.Count);
        Assert.Equal("boot0", header.Entries[0].Name);
        Assert.Equal("bootloader", header.Entries[0].MainType);
        Assert.Equal(32 + 48 + 2 * 1024, header.Entries[0].Offset);
        Assert.Equal(100, header.Entries[0].Length);
    }

    [Fact]
    public void Read_V3Container_ReadsCountAtShiftedBodyOffset()
    {
        using var stream = new MemoryStream(BuildImage(AllwinnerImageWtyFormat.HeaderVersionV3,
            [("opensbi.bin", new byte[64])]));

        var header = AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken);

        Assert.Equal(AllwinnerImageWtyFormat.HeaderVersionV3, header.HeaderVersion);
        Assert.Single(header.Entries);
        Assert.Equal(64, header.Entries[0].Length);
    }

    [Theory]
    [InlineData("encrypted")]
    [InlineData("unknown-version")]
    [InlineData("table-past-end")]
    [InlineData("payload-past-end")]
    public void Read_MalformedVariants_RejectsWithInvalidData(string failure)
    {
        byte[] image = failure switch
        {
            "unknown-version" => BuildImage(0x200, [("boot0", new byte[8])]),
            "table-past-end" => [.. BuildImage(AllwinnerImageWtyFormat.HeaderVersionV1, []), 0],
            "payload-past-end" => BuildImage(AllwinnerImageWtyFormat.HeaderVersionV1,
                [("boot0", new byte[8])], breakPayloadRange: true),
            _ => BuildImage(AllwinnerImageWtyFormat.HeaderVersionV1, [("boot0", new byte[8])])
        };
        if (failure == "encrypted")
        {
            image[0] = (byte)'X';
        }
        else if (failure == "table-past-end")
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32 + 24), 9);
            image = image[..(image.Length - 1)];
        }

        using var stream = new MemoryStream(image);

        Assert.Throws<InvalidDataException>(() => AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CanHandle_PlainAndEncryptedMagicOnlyFiles_DecidesByMagic()
    {
        var handler = new AllwinnerImageWtyFormatHandler();
        var plain = Path.Combine(_root, "plain.img");
        await File.WriteAllBytesAsync(plain, BuildImage(AllwinnerImageWtyFormat.HeaderVersionV1, [("boot0", [1])]),
            TestContext.Current.CancellationToken);
        var encrypted = Path.Combine(_root, "crypto.img");
        var bytes = BuildImage(AllwinnerImageWtyFormat.HeaderVersionV1, [("boot0", [1])]);
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

        var image = BuildImage(AllwinnerImageWtyFormat.HeaderVersionV1,
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
        await File.WriteAllBytesAsync(path, BuildImage(AllwinnerImageWtyFormat.HeaderVersionV3,
            [("boot0", new byte[16])], pid: 0xE896, vid: 0x1F25), TestContext.Current.CancellationToken);

        var result = await new AllwinnerImageWtyFormatHandler().ReadMetadataAsync(path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("0x300", result.Metadata["HeaderVersion"]);
        Assert.Equal("0x1F25", result.Metadata["Vid"]);
        Assert.Equal("0xE896", result.Metadata["Pid"]);
    }

    [Fact]
    public async Task UnpackAsync_PreCanceledToken_ThrowsBeforePublishing()
    {
        var path = Path.Combine(_root, "cancel.img");
        await File.WriteAllBytesAsync(path, BuildImage(AllwinnerImageWtyFormat.HeaderVersionV1, [("boot0", [1])]),
            TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "cancel-out");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AllwinnerImageWtyFormatHandler().UnpackAsync(path, output, cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(output) && Directory.EnumerateFiles(output).Any());
    }
}