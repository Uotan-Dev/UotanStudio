using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class AllwinnerImageWtyTests : IDisposable
{
    private const int HeaderRecordSize = 1024;
    private const int FileHeaderSize = 1024;
    private const int TableOffset = 0x400;
    private const int VersionOne = 0x100;
    private const int VersionThree = 0x300;

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

    private readonly record struct EntrySpec(string Name, string MainType, string SubType, byte[] Payload,
        int OriginalLength = -1);

    private static byte[] BuildImage(int headerVersion, params EntrySpec[] files)
    {
        var tableEnd = TableOffset + (files.Length * FileHeaderSize);
        var total = tableEnd + files.Sum(file => (long)file.Payload.Length);
        var image = new byte[total];
        "IMAGEWTY"u8.CopyTo(image);
        Write32(image, 8, (uint)headerVersion);
        Write32(image, 12, HeaderRecordSize);
        Write32(image, 16, 0x2);
        Write32(image, 20, 0x100234);
        Write32(image, 24, (uint)(total & 0xFFFFFFFF));
        if (headerVersion == VersionThree)
        {
            Write32(image, 28, (uint)((ulong)total >> 32));
            Write32(image, 32, 0x100);
            Write32(image, 36, 0xE896);
            Write32(image, 40, 0x1F25);
            Write32(image, 44, 0x100);
            Write32(image, 48, 0x200);
            Write32(image, 60, (uint)files.Length);
            Write32(image, 64, TableOffset);
        }
        else
        {
            Write32(image, 28, 0);
            Write32(image, 32, 0xE896);
            Write32(image, 36, 0x1F25);
            Write32(image, 40, 0x100);
            Write32(image, 44, 0x200);
            Write32(image, 56, (uint)files.Length);
            Write32(image, 60, TableOffset);
        }

        var payloadOffset = (long)tableEnd;
        for (var index = 0; index < files.Length; index++)
        {
            var record = TableOffset + (index * FileHeaderSize);
            var file = files[index];
            var nameBytes = Encoding.ASCII.GetBytes(file.Name);
            Write32(image, record, (uint)nameBytes.Length);
            Write32(image, record + 4, FileHeaderSize);
            Encoding.ASCII.GetBytes(file.MainType.PadRight(8)).CopyTo(image, record + 8);
            Encoding.ASCII.GetBytes(file.SubType.PadRight(16)).CopyTo(image, record + 16);
            Write32(image, record + 32, 0x1);
            var original = file.OriginalLength < 0 ? file.Payload.Length : file.OriginalLength;
            if (headerVersion == VersionThree)
            {
                nameBytes.CopyTo(image, record + 36);
                Write64(image, record + 292, (ulong)file.Payload.Length);
                Write64(image, record + 300, (ulong)original);
                Write64(image, record + 308, (ulong)payloadOffset);
                Write32(image, record + 380, 0xDEADBEEF);
            }
            else
            {
                nameBytes.CopyTo(image, record + 52);
                Write32(image, record + 36, (uint)file.Payload.Length);
                Write32(image, record + 40, (uint)original);
                Write32(image, record + 44, (uint)payloadOffset);
                Write32(image, record + 48, 0xDEADBEEF);
            }

            file.Payload.CopyTo(image, payloadOffset);
            payloadOffset += file.Payload.Length;
        }

        return image;
    }

    private static byte[] Encrypt(byte[] plain, int headerVersion, IReadOnlyList<EntrySpec> files)
    {
        var image = (byte[])plain.Clone();
        var headerCipher = BuildCipher(0x00, (byte)'i');
        var recordCipher = BuildCipher(0x01, (byte)'m');
        var contentCipher = BuildCipher(0x02, (byte)'g');
        for (var offset = 0; offset < HeaderRecordSize; offset += Rc6.BlockSize)
        {
            headerCipher.EncryptBlock(image.AsSpan(offset, Rc6.BlockSize));
        }

        for (var index = 0; index < files.Count; index++)
        {
            var record = TableOffset + (index * FileHeaderSize);
            var payloadOffset = GetPayloadOffset(plain, headerVersion, index, files.Count);
            for (var offset = 0; offset < FileHeaderSize; offset += Rc6.BlockSize)
            {
                recordCipher.EncryptBlock(image.AsSpan(record + offset, Rc6.BlockSize));
            }

            var length = files[index].Payload.Length;
            for (var offset = 0; offset + Rc6.BlockSize <= length; offset += Rc6.BlockSize)
            {
                contentCipher.EncryptBlock(image.AsSpan(payloadOffset + offset, Rc6.BlockSize));
            }
        }

        return image;
    }

    private static int GetPayloadOffset(byte[] image, int headerVersion, int index, int fileCount)
    {
        var record = TableOffset + (index * FileHeaderSize);
        return headerVersion == VersionThree
            ? (int)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(record + 308))
            : (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(record + 44));
    }

    private static Rc6 BuildCipher(byte filler, byte terminator)
    {
        var key = new byte[32];
        key.AsSpan().Fill(filler);
        key[^1] = terminator;
        return Rc6.Create(key);
    }

    private static void Write32(byte[] image, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), value);

    private static void Write64(byte[] image, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset), value);

    private static byte[] CreatePayload(int length, byte seed)
    {
        var payload = new byte[length];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index ^ seed);
        }

        return payload;
    }

    [Fact]
    public void Read_VersionThreeVendorGeometry_ReturnsIdsSizesAndRecordRoles()
    {
        var boot0 = CreatePayload(100, 0x5A);
        var uboot = CreatePayload(50, 0x3C);
        var image = BuildImage(VersionThree,
            new EntrySpec("boot0_sdcard.fex", "12345678", "1234567890BOOT_0", boot0),
            new EntrySpec("u-boot-efex.fex", "12345678", "UBOOT_0000000000", uboot));
        using var stream = new MemoryStream(image);

        var header = AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken);

        Assert.Equal(ImageWtyVersion.V3, header.Version);
        Assert.Equal(0xE896u, header.Pid);
        Assert.Equal(0x1F25u, header.Vid);
        Assert.Equal(0x100u, header.HardwareId);
        Assert.Equal(0x200u, header.FirmwareId);
        Assert.Equal(2u, header.FileCount);
        Assert.Equal(TableOffset, header.TableOffset);
        Assert.Equal(image.Length, header.ImageSize);
        Assert.Equal(0x100234u, header.ContainerVersion);
        Assert.False(header.IsEncrypted);
        Assert.Collection(header.Entries,
            entry =>
            {
                Assert.Equal("boot0_sdcard.fex", entry.Name);
                Assert.Equal("12345678", entry.MainType);
                Assert.Equal("1234567890BOOT_0", entry.SubType);
                Assert.Equal(AllwinnerImageItem.Boot0Card, entry.Item);
                Assert.Equal(100, entry.StoredLength);
                Assert.Equal(100, entry.OriginalLength);
                Assert.Equal(0xDEADBEEFu, entry.Checksum);
                Assert.Equal(TableOffset + (2 * FileHeaderSize), entry.Offset);
            },
            entry =>
            {
                Assert.Equal("u-boot-efex.fex", entry.Name);
                Assert.Equal(AllwinnerImageItem.UBoot, entry.Item);
                Assert.Equal(TableOffset + (2 * FileHeaderSize) + 100, entry.Offset);
            });
    }

    [Fact]
    public void Read_VersionOneVendorGeometry_UsesVersionOneHeaderAndRecordOffsets()
    {
        var mbr = CreatePayload(64, 0x77);
        var fes1 = CreatePayload(32, 0x11);
        var image = BuildImage(VersionOne,
            new EntrySpec("sunxi_mbr.fex", "12345678", "1234567890___MBR", mbr),
            new EntrySpec("fes1.fex", "FES", "FES_1-0000000000", fes1));
        using var stream = new MemoryStream(image);

        var header = AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken);

        Assert.Equal(ImageWtyVersion.V1, header.Version);
        Assert.Equal(0xE896u, header.Pid);
        Assert.Equal(0x1F25u, header.Vid);
        Assert.Equal(0x100u, header.HardwareId);
        Assert.Equal(0x200u, header.FirmwareId);
        Assert.Equal(2u, header.FileCount);
        Assert.Equal(TableOffset, header.TableOffset);
        Assert.Equal(image.Length, header.ImageSize);
        Assert.Collection(header.Entries,
            entry =>
            {
                Assert.Equal("sunxi_mbr.fex", entry.Name);
                Assert.Equal(AllwinnerImageItem.Mbr, entry.Item);
                Assert.Equal(64, entry.StoredLength);
                Assert.Equal(64, entry.OriginalLength);
                Assert.Equal(TableOffset + (2 * FileHeaderSize), entry.Offset);
            },
            entry =>
            {
                Assert.Equal("fes1.fex", entry.Name);
                Assert.Equal(AllwinnerImageItem.Fes1, entry.Item);
                Assert.Equal(TableOffset + (2 * FileHeaderSize) + 64, entry.Offset);
            });
    }

    [Fact]
    public void Read_VersionThreeRecord_KeepsSixtyFourBitLengths()
    {
        var image = BuildImage(VersionThree,
            new EntrySpec("system.img", "RFSFAT16", "SYSTEM_IMG000000", CreatePayload(16, 0x21)));
        Write64(image, TableOffset + 300, 0x1_0000_0005UL);
        using var stream = new MemoryStream(image);

        var header = AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken);

        var entry = Assert.Single(header.Entries);
        Assert.Equal(16, entry.StoredLength);
        Assert.Equal(0x1_0000_0005L, entry.OriginalLength);
        Assert.Equal(16, AllwinnerImageWtyFormat.EffectiveLength(entry));
    }

    [Fact]
    public void Read_VersionThreeRecord_CombinesImageSizeWords()
    {
        var image = BuildImage(VersionThree,
            new EntrySpec("boot0_nand.fex", "BOOT", "BOOT0_0000000000", CreatePayload(8, 0x01)));
        Write32(image, 24, 0x0000_1000);
        Write32(image, 28, 0x0000_0001);
        using var stream = new MemoryStream(image);

        var header = AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken);

        Assert.Equal(0x1_0000_1000L, header.ImageSize);
    }

    [Theory]
    [InlineData(VersionOne)]
    [InlineData(VersionThree)]
    public void Read_EncryptedContainer_DecryptsHeaderRecordsAndPayloads(int headerVersion)
    {
        var payload = CreatePayload(256, 0x9D);
        var entries = new[]
        {
            new EntrySpec("u-boot-efex.fex", "12345678", "UBOOT_0000000000", payload)
        };
        var plain = BuildImage(headerVersion, entries);
        var encrypted = Encrypt(plain, headerVersion, entries);

        Assert.NotEqual("IMAGEWTY", Encoding.ASCII.GetString(encrypted, 0, 8));

        using var stream = new MemoryStream(encrypted);
        var header = AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken);

        Assert.True(header.IsEncrypted);
        Assert.Equal((uint)headerVersion, (uint)header.Version);
        Assert.Equal(0xE896u, header.Pid);
        var entry = Assert.Single(header.Entries);
        Assert.Equal("u-boot-efex.fex", entry.Name);
        Assert.Equal(AllwinnerImageItem.UBoot, entry.Item);
        Assert.Equal(256, entry.StoredLength);

        using var output = new MemoryStream();
        var written = AllwinnerImageWtyFormat.ExtractEntry(stream, entry, output, decrypt: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(256, written);
        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public void ExtractEntry_PlainContainer_CopiesExactPayload()
    {
        var first = CreatePayload(300, 0xA5);
        var second = CreatePayload(7, 0x33);
        using var stream = new MemoryStream(BuildImage(VersionThree,
            new EntrySpec("boot0_sdcard.fex", "12345678", "1234567890BOOT_0", first),
            new EntrySpec("board.fex", "COMMON", "BOARD_CONFIG_BIN", second)));
        var header = AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken);
        using var firstOutput = new MemoryStream();
        using var secondOutput = new MemoryStream();

        AllwinnerImageWtyFormat.ExtractEntry(stream, header.Entries[0], firstOutput, decrypt: false,
            TestContext.Current.CancellationToken);
        AllwinnerImageWtyFormat.ExtractEntry(stream, header.Entries[1], secondOutput, decrypt: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(first, firstOutput.ToArray());
        Assert.Equal(second, secondOutput.ToArray());
        Assert.Equal(AllwinnerImageItem.BoardConfig, header.Entries[1].Item);
    }

    [Fact]
    public void ExtractEntry_DeclaredOriginalLengthSmaller_TruncatesWrittenBytes()
    {
        var payload = CreatePayload(96, 0x6B);
        using var stream = new MemoryStream(BuildImage(VersionThree,
            new EntrySpec("config.fex", "COMMON", "SYS_CONFIG_BIN00", payload, OriginalLength: 64)));
        var header = AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken);
        var entry = Assert.Single(header.Entries);
        using var output = new MemoryStream();

        var written = AllwinnerImageWtyFormat.ExtractEntry(stream, entry, output, decrypt: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(64, written);
        Assert.Equal(payload[..64], output.ToArray());
    }

    [Theory]
    [InlineData("encrypted")]
    [InlineData("unknown-version")]
    [InlineData("file-count-over-limit")]
    [InlineData("table-past-end")]
    [InlineData("entry-payload-past-end")]
    [InlineData("entry-length-past-end")]
    [InlineData("sixty-four-bit-length-overflow")]
    public void Read_MalformedVariants_RejectsWithInvalidData(string failure)
    {
        var image = BuildImage(VersionThree,
            new EntrySpec("boot0_sdcard.fex", "12345678", "1234567890BOOT_0", CreatePayload(8, 0x01)));
        switch (failure)
        {
            case "encrypted":
                image[0] = (byte)'X';
                break;
            case "unknown-version":
                Write32(image, 8, 0x200);
                break;
            case "file-count-over-limit":
                Write32(image, 60, 4097);
                break;
            case "table-past-end":
                Write32(image, 64, 0x7FFFFFFF);
                break;
            case "entry-payload-past-end":
                Write64(image, TableOffset + 308, (ulong)image.Length + 1);
                break;
            case "entry-length-past-end":
                Write64(image, TableOffset + 292, (ulong)image.Length);
                break;
            case "sixty-four-bit-length-overflow":
                Write64(image, TableOffset + 292, ulong.MaxValue);
                break;
            default:
                throw new InvalidOperationException($"Unknown failure variant {failure}.");
        }

        using var stream = new MemoryStream(image);

        Assert.Throws<InvalidDataException>(() =>
            AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Read_TruncatedHeader_RejectsWithInvalidData()
    {
        using var stream = new MemoryStream(new byte[8]);

        Assert.Throws<InvalidDataException>(() =>
            AllwinnerImageWtyFormat.Read(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CanHandle_PlainAndEncryptedMagicOnlyFiles_DecidesByMagic()
    {
        var handler = new AllwinnerImageWtyFormatHandler();
        var plain = Path.Combine(_root, "plain.img");
        await File.WriteAllBytesAsync(plain,
            BuildImage(VersionThree, new EntrySpec("boot0_sdcard.fex", "12345678", "1234567890BOOT_0", [1])),
            TestContext.Current.CancellationToken);
        var encryptedPath = Path.Combine(_root, "crypto.img");
        var bytes = BuildImage(VersionThree, new EntrySpec("boot0_sdcard.fex", "12345678", "1234567890BOOT_0", [1]));
        bytes[7] = (byte)'X';
        await File.WriteAllBytesAsync(encryptedPath, bytes, TestContext.Current.CancellationToken);

        Assert.True(handler.CanHandle(plain));
        Assert.False(handler.CanHandle(encryptedPath));
    }

    [Fact]
    public async Task UnpackAsync_EncryptedContainer_WritesDecryptedPayloadsAndSkipsEmptyEntries()
    {
        var payload = CreatePayload(512, 0x5A);
        var entries = new[]
        {
            new EntrySpec("u-boot-efex.fex", "12345678", "UBOOT_0000000000", payload),
            new EntrySpec("empty.fex", "COMMON", "BOARD_CONFIG_BIN", [])
        };
        var image = Encrypt(BuildImage(VersionThree, entries), VersionThree, entries);
        var path = Path.Combine(_root, "unpack.img");
        await File.WriteAllBytesAsync(path, image, TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "out");

        var result = await new AllwinnerImageWtyFormatHandler().UnpackAsync(path, output,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal(payload,
            await File.ReadAllBytesAsync(Path.Combine(output, "u-boot-efex.fex"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(output, "empty.fex")));
        var component = Assert.Single(result.Components!);
        Assert.Equal(512, component.SizeBytes);
    }

    [Fact]
    public async Task UnpackAsync_ExistingOutputWithoutOverwrite_FailsBeforeReplacing()
    {
        var path = Path.Combine(_root, "collide.img");
        await File.WriteAllBytesAsync(path,
            BuildImage(VersionOne, new EntrySpec("sys_config.fex", "COMMON", "SYS_CONFIG100000", [1, 2, 3])),
            TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "collide-out");
        Directory.CreateDirectory(output);
        var existing = Path.Combine(output, "sys_config.fex");
        await File.WriteAllBytesAsync(existing, [9], TestContext.Current.CancellationToken);

        var result = await new AllwinnerImageWtyFormatHandler().UnpackAsync(path, output,
            new UnpackOptions { Overwrite = false }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal([9], await File.ReadAllBytesAsync(existing, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadMetadataAsync_VersionThreeContainer_ReportsSizeRolesAndEncryption()
    {
        var path = Path.Combine(_root, "meta.img");
        await File.WriteAllBytesAsync(path,
            BuildImage(VersionThree,
                new EntrySpec("sunxi_mbr.fex", "12345678", "1234567890___MBR", CreatePayload(16, 0x42)),
                new EntrySpec("unknown.bin", "CUSTOM", "CUSTOM_000000000", CreatePayload(4, 0x02))),
            TestContext.Current.CancellationToken);

        var result = await new AllwinnerImageWtyFormatHandler().ReadMetadataAsync(path,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Summary);
        Assert.Equal("300", result.Metadata!["Version"]);
        Assert.Equal("false", result.Metadata["Encrypted"]);
        Assert.Equal("0xE896", result.Metadata["Pid"]);
        Assert.Contains(result.Entries!, entry => entry.Contains("sunxi_mbr.fex", StringComparison.Ordinal)
            && entry.Contains("Mbr", StringComparison.Ordinal));
        Assert.Contains(result.Entries!, entry => entry.Contains("unknown.bin", StringComparison.Ordinal)
            && !entry.Contains("Unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_CanceledDuringEntryLoop_ThrowsOperationCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancelingStream(
            new MemoryStream(BuildImage(VersionThree,
                new EntrySpec("boot0_sdcard.fex", "12345678", "1234567890BOOT_0", CreatePayload(32, 0x01)))),
            cancellation,
            TableOffset);
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            AllwinnerImageWtyFormat.Read(stream, cancellation.Token));
    }

    [Fact]
    public async Task UnpackAsync_PreCanceledToken_ThrowsBeforePublishing()
    {
        var path = Path.Combine(_root, "cancel.img");
        await File.WriteAllBytesAsync(path,
            BuildImage(VersionOne, new EntrySpec("sys_config.fex", "COMMON", "SYS_CONFIG100000", [1])),
            TestContext.Current.CancellationToken);
        var output = Path.Combine(_root, "cancel-out");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AllwinnerImageWtyFormatHandler().UnpackAsync(path, output, cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(output) && Directory.EnumerateFiles(output).Any());
    }

    [Fact]
    public void Resolve_VendorFileListCoversEveryFlashingRole()
    {
        var expected = new Dictionary<AllwinnerImageItem, (string MainType, string SubType)>
        {
            [AllwinnerImageItem.Fes1] = ("FES", "FES_1-0000000000"),
            [AllwinnerImageItem.UBoot] = ("12345678", "UBOOT_0000000000"),
            [AllwinnerImageItem.UBootCrash] = ("12345678", "UBOOT_CRASH_0000"),
            [AllwinnerImageItem.Mbr] = ("12345678", "1234567890___MBR"),
            [AllwinnerImageItem.Gpt] = ("12345678", "1234567890___GPT"),
            [AllwinnerImageItem.SysConfig] = ("COMMON", "SYS_CONFIG100000"),
            [AllwinnerImageItem.SysConfigBinary] = ("COMMON", "SYS_CONFIG_BIN00"),
            [AllwinnerImageItem.SysPartition] = ("COMMON", "SYS_CONFIG000000"),
            [AllwinnerImageItem.BoardConfig] = ("COMMON", "BOARD_CONFIG_BIN"),
            [AllwinnerImageItem.DeviceTree] = ("COMMON", "DTB_CONFIG000000"),
            [AllwinnerImageItem.Boot0Nand] = ("BOOT", "BOOT0_0000000000"),
            [AllwinnerImageItem.Boot0Card] = ("12345678", "1234567890BOOT_0"),
            [AllwinnerImageItem.BootPackage] = ("12345678", "BOOTPKG-00000000")
        };

        foreach (var (item, signature) in expected)
        {
            Assert.Equal(item, AllwinnerImageItemTable.Resolve(signature.MainType, signature.SubType));
            Assert.NotNull(AllwinnerImageItemTable.GetFileName(item));
        }

        Assert.Equal(AllwinnerImageItem.Unknown,
            AllwinnerImageItemTable.Resolve("12345678", "UBOOT_0000000001"));
        Assert.Null(AllwinnerImageItemTable.GetFileName(AllwinnerImageItem.Unknown));
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