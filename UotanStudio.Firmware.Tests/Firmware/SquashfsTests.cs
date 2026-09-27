using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using K4os.Compression.LZ4;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Squashfs;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// SquashFS v4 只读解析测试：合成镜像（存储与 gzip 元数据、LZ4 数据块、大小端）、解包往返与损坏拒绝。
/// <para>SquashFS v4 read-only parsing tests: synthetic images (stored and gzip metadata, LZ4 data blocks,
/// both byte orders), extraction round-trips, and malformed-image rejection.</para>
/// </summary>
public class SquashfsTests
{
    private const int BlockSize = 4096;
    private static readonly byte[] HelloBytes = "hello squash"u8.ToArray();
    private static readonly byte[] InnerBytes = "inner\n"u8.ToArray();
    private static readonly byte[] TargetBytes = "hello.txt"u8.ToArray();
    private static readonly byte[] MultiPayload = BuildMultiPayload();

    private sealed record Fixture(byte[] Image, long InodeBlockAddress);

    [Fact]
    public void EnumerateAndExtract_StoredMetadata_RoundTripsTree()
    {
        var fixture = BuildImage();
        using var ms = new MemoryStream(fixture.Image);
        var parser = new SquashfsParser(ms);

        var paths = parser.EnumerateEntries().Select(entry => entry.Path).ToArray();
        Assert.Equal(["hello.txt", "multi.bin", "sym.lnk", "sub", "sub/inner.txt"], paths);

        using var temp = new TempDirectory();
        var written = parser.ExtractToDirectory(temp.Path, overwrite: true, maximumOutputBytes: 1L << 30, byteProgress: null);
        Assert.Equal(4, written.Count);
        Assert.Equal("hello squash", File.ReadAllText(Path.Combine(temp.Path, "hello.txt"), Encoding.UTF8));
        Assert.Equal(MultiPayload, File.ReadAllBytes(Path.Combine(temp.Path, "multi.bin")));
        Assert.Equal("hello.txt", File.ReadAllText(Path.Combine(temp.Path, "sym.lnk"), Encoding.UTF8));
        Assert.Equal("inner\n", File.ReadAllText(Path.Combine(temp.Path, "sub", "inner.txt"), Encoding.UTF8));
        Assert.Equal([0u], parser.Ids.ToArray());
    }

    [Fact]
    public void Enumerate_GzipCompressedMetadata_DecodesThroughZlib()
    {
        var fixture = BuildImage(compressMetadata: true);
        using var ms = new MemoryStream(fixture.Image);
        var parser = new SquashfsParser(ms);
        var paths = parser.EnumerateEntries().Select(entry => entry.Path).ToArray();
        Assert.Equal(["hello.txt", "multi.bin", "sym.lnk", "sub", "sub/inner.txt"], paths);
    }

    [Fact]
    public void Extract_Lz4CompressedDataBlock_DecodesThroughK4os()
    {
        var fixture = BuildImage(compressionId: 5, lz4FirstDataBlock: true);
        using var ms = new MemoryStream(fixture.Image);
        var parser = new SquashfsParser(ms);
        using var temp = new TempDirectory();
        parser.ExtractToDirectory(temp.Path, overwrite: true, maximumOutputBytes: 1L << 30, byteProgress: null);
        Assert.Equal(MultiPayload, File.ReadAllBytes(Path.Combine(temp.Path, "multi.bin")));
    }

    [Fact]
    public void Enumerate_BigEndianStoredImage_UsesSwappedFields()
    {
        var fixture = BuildImage(bigEndian: true);
        using var ms = new MemoryStream(fixture.Image);
        var parser = new SquashfsParser(ms);
        var paths = parser.EnumerateEntries().Select(entry => entry.Path).ToArray();
        Assert.Equal(["hello.txt", "multi.bin", "sym.lnk", "sub", "sub/inner.txt"], paths);

        using var temp = new TempDirectory();
        parser.ExtractToDirectory(temp.Path, overwrite: true, maximumOutputBytes: 1L << 30, byteProgress: null);
        Assert.Equal(MultiPayload, File.ReadAllBytes(Path.Combine(temp.Path, "multi.bin")));
    }

    [Fact]
    public void Constructor_BadMagic_ThrowsInvalidData()
    {
        var fixture = BuildImage();
        fixture.Image[0] = 0;
        Assert.Throws<InvalidDataException>(() => new SquashfsParser(new MemoryStream(fixture.Image)));
    }

    [Fact]
    public void Constructor_MajorVersionThree_ThrowsInvalidData()
    {
        var fixture = BuildImage();
        BinaryPrimitives.WriteUInt16LittleEndian(fixture.Image.AsSpan(28), 3);
        var ex = Assert.Throws<InvalidDataException>(() => new SquashfsParser(new MemoryStream(fixture.Image)));
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_EnormousInodeCount_ThrowsInvalidData()
    {
        var fixture = BuildImage();
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Image.AsSpan(4), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => new SquashfsParser(new MemoryStream(fixture.Image)));
    }

    [Fact]
    public void Constructor_TruncatedImage_ThrowsInvalidData()
    {
        var fixture = BuildImage();
        var clipped = new byte[500];
        Array.Copy(fixture.Image, clipped, 500);
        Assert.Throws<InvalidDataException>(() => new SquashfsParser(new MemoryStream(clipped)));
    }

    [Fact]
    public void Constructor_MetadataBlockSizeBeyondImage_ThrowsInvalidData()
    {
        var fixture = BuildImage();
        BinaryPrimitives.WriteUInt16LittleEndian(fixture.Image.AsSpan(checked((int)fixture.InodeBlockAddress)), ushort.MaxValue);
        Assert.Throws<InvalidDataException>(() => new SquashfsParser(new MemoryStream(fixture.Image)));
    }

    [Fact]
    public void Enumerate_OversizedDirectoryEntryName_ThrowsInvalidData()
    {
        var fixture = BuildImage(dirNameLength: 300);
        Assert.Throws<InvalidDataException>(() =>
        {
            var parser = new SquashfsParser(new MemoryStream(fixture.Image));
            parser.EnumerateEntries();
        });
    }

    [Fact]
    public void Enumerate_LzmaCompressedWindows_ReportsUnsupportedCompressor()
    {
        var fixture = BuildImage(compressionId: 2, compressMetadata: true);
        var ex = Assert.Throws<InvalidDataException>(() => new SquashfsParser(new MemoryStream(fixture.Image)));
        Assert.Contains("not supported", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlerLifecycle_StoredImage_ReportsMetadataAndUnpacks()
    {
        var fixture = BuildImage();
        using var temp = new TempDirectory();
        var image = Path.Combine(temp.Path, "root.sqsh");
        File.WriteAllBytes(image, fixture.Image);

        var handler = new SquashfsFormatHandler();
        var token = TestContext.Current.CancellationToken;
        Assert.True(handler.CanHandle(image));

        var metadata = await handler.ReadMetadataAsync(image, token);
        Assert.True(metadata.Success);
        Assert.Equal("gzip", metadata.Metadata["Compression"]);
        Assert.Equal("4.0", metadata.Metadata["Version"]);

        var outDir = Path.Combine(temp.Path, "out");
        var unpacked = await handler.UnpackAsync(image, outDir, new UnpackOptions { Overwrite = true }, token);
        Assert.True(unpacked.Success);
        Assert.Equal("hello squash", File.ReadAllText(Path.Combine(outDir, "hello.txt"), Encoding.UTF8));
    }

    [Fact]
    public async Task UnpackAsync_OutputBudgetExceeded_ReportsFailure()
    {
        var fixture = BuildImage();
        using var temp = new TempDirectory();
        var image = Path.Combine(temp.Path, "tight.sqsh");
        File.WriteAllBytes(image, fixture.Image);

        var handler = new SquashfsFormatHandler();
        var token = TestContext.Current.CancellationToken;
        var result = await handler.UnpackAsync(image, Path.Combine(temp.Path, "out"), new UnpackOptions { MaximumOutputBytes = 8 }, token);
        Assert.False(result.Success);
        Assert.Contains("budget", result.Summary, StringComparison.Ordinal);
    }

    private static byte[] BuildMultiPayload()
    {
        var payload = new byte[4196];
        payload.AsSpan(0, 4096).Fill((byte)'A');
        payload.AsSpan(4096).Fill((byte)'B');
        return payload;
    }

    private static Fixture BuildImage(
        bool bigEndian = false,
        bool compressMetadata = false,
        int compressionId = 1,
        bool lz4FirstDataBlock = false,
        int dirNameLength = 0)
    {
        var firstPlain = MultiPayload.AsSpan(0, BlockSize).ToArray();
        var secondPlain = MultiPayload.AsSpan(BlockSize).ToArray();
        byte[] firstEncoded;
        uint firstWord;
        if (lz4FirstDataBlock)
        {
            var destination = new byte[LZ4Codec.MaximumOutputSize(firstPlain.Length)];
            var written = LZ4Codec.Encode(firstPlain, 0, firstPlain.Length, destination, 0, destination.Length, LZ4Level.L00_FAST);
            firstEncoded = destination.AsSpan(0, written).ToArray();
            firstWord = (uint)written;
        }
        else if (compressMetadata && compressionId == 1)
        {
            firstEncoded = ZlibCompress(firstPlain);
            firstWord = (uint)firstEncoded.Length;
        }
        else
        {
            firstEncoded = firstPlain;
            firstWord = (uint)(firstPlain.Length | (1 << 24));
        }

        var secondWord = (uint)(secondPlain.Length | (1 << 24));
        var rootDirLength = 72 + (dirNameLength > 0 ? 8 + dirNameLength : 0);

        var inodeBlock = WrapMetadata(BuildInodeWindow(bigEndian, firstWord, secondWord, rootDirLength), bigEndian, compressMetadata, compressionId);
        var dirBlock = WrapMetadata(BuildDirectoryWindow(bigEndian, dirNameLength), bigEndian, compressMetadata, compressionId);

        var dataAddress = 96L;
        var frag0Address = dataAddress + firstEncoded.Length + secondPlain.Length;
        var frag1Address = frag0Address + HelloBytes.Length;
        var fragIndexAddress = frag1Address + InnerBytes.Length;
        var fragmentBlock = WrapStoredMetadata(BuildFragmentWindow(bigEndian, frag0Address, frag1Address), bigEndian);
        var fragBlockAddress = fragIndexAddress + 8;
        var idIndexAddress = fragBlockAddress + fragmentBlock.Length;
        var idBlock = WrapStoredMetadata(BuildIdWindow(bigEndian), bigEndian);
        var idBlockAddress = idIndexAddress + 8;
        var inodeBlockAddress = idBlockAddress + idBlock.Length;
        var dirBlockAddress = inodeBlockAddress + inodeBlock.Length;

        using var image = new MemoryStream();
        image.Position = 96;
        image.Write(firstEncoded);
        image.Write(secondPlain);
        image.Write(HelloBytes);
        image.Write(InnerBytes);
        WriteU64(image, bigEndian, (ulong)fragBlockAddress);
        image.Write(fragmentBlock);
        WriteU64(image, bigEndian, (ulong)idBlockAddress);
        image.Write(idBlock);
        image.Write(inodeBlock);
        image.Write(dirBlock);

        var bytes = image.ToArray();
        WriteSuperblock(bytes, bigEndian, compressionId, fragIndexAddress, idIndexAddress, inodeBlockAddress, dirBlockAddress);
        return new Fixture(bytes, inodeBlockAddress);
    }

    private static byte[] BuildInodeWindow(bool bigEndian, uint firstWord, uint secondWord, int rootDirLength)
    {
        using var itab = new MemoryStream();
        WriteDirInode(itab, bigEndian, inodeNumber: 1, dirBlock: 0, dirOffset: 0, dirSize: rootDirLength, parent: 1);
        WriteFileInode(itab, bigEndian, inodeNumber: 2, start: 0, fragment: 0, fragmentOffset: 0, size: (uint)HelloBytes.Length, blocks: []);
        WriteFileInode(itab, bigEndian, inodeNumber: 3, start: 96, fragment: uint.MaxValue, fragmentOffset: 0, size: (uint)MultiPayload.Length, blocks: [firstWord, secondWord]);
        WriteSymlinkInode(itab, bigEndian, inodeNumber: 4);
        WriteDirInode(itab, bigEndian, inodeNumber: 5, dirBlock: 0, dirOffset: 72, dirSize: 29, parent: 1);
        WriteFileInode(itab, bigEndian, inodeNumber: 6, start: 0, fragment: 1, fragmentOffset: 0, size: (uint)InnerBytes.Length, blocks: []);
        return itab.ToArray();
    }

    private static byte[] BuildDirectoryWindow(bool bigEndian, int dirNameLength)
    {
        using var dtab = new MemoryStream();
        WriteU32(dtab, bigEndian, (uint)(3 + (dirNameLength > 0 ? 1 : 0)));
        WriteU32(dtab, bigEndian, 0);
        WriteU32(dtab, bigEndian, 1);
        WriteDirEntry(dtab, bigEndian, 32, 1, 2, "hello.txt"u8.ToArray());
        WriteDirEntry(dtab, bigEndian, 64, 1, 2, "multi.bin"u8.ToArray());
        WriteDirEntry(dtab, bigEndian, 104, 1, 3, "sym.lnk"u8.ToArray());
        WriteDirEntry(dtab, bigEndian, 137, 1, 1, "sub"u8.ToArray());
        if (dirNameLength > 0)
        {
            WriteDirEntry(dtab, bigEndian, 0, 1, 2, new byte[dirNameLength]);
        }

        WriteU32(dtab, bigEndian, 0);
        WriteU32(dtab, bigEndian, 0);
        WriteU32(dtab, bigEndian, 5);
        WriteDirEntry(dtab, bigEndian, 169, 1, 2, "inner.txt"u8.ToArray());
        return dtab.ToArray();
    }

    private static byte[] BuildFragmentWindow(bool bigEndian, long frag0Address, long frag1Address)
    {
        using var window = new MemoryStream();
        WriteFragmentEntry(window, bigEndian, frag0Address, HelloBytes.Length);
        WriteFragmentEntry(window, bigEndian, frag1Address, InnerBytes.Length);
        return window.ToArray();
    }

    private static byte[] BuildIdWindow(bool bigEndian)
    {
        using var window = new MemoryStream();
        WriteU32(window, bigEndian, 0);
        return window.ToArray();
    }

    private static void WriteFragmentEntry(MemoryStream stream, bool bigEndian, long start, int length)
    {
        WriteU64(stream, bigEndian, (ulong)start);
        WriteU32(stream, bigEndian, (uint)(length | (1 << 24)));
        WriteU32(stream, bigEndian, 0);
    }

    private static byte[] WrapMetadata(byte[] window, bool bigEndian, bool compress, int compressionId)
    {
        Span<byte> header = stackalloc byte[2];
        if (compress && compressionId is 1 or 2)
        {
            var payload = ZlibCompress(window);
            WriteUInt16(header, bigEndian, (ushort)payload.Length);
            return [.. header.ToArray(), .. payload];
        }

        WriteUInt16(header, bigEndian, (ushort)(window.Length | 0x8000));
        return [.. header.ToArray(), .. window];
    }

    private static byte[] WrapStoredMetadata(byte[] window, bool bigEndian) => WrapMetadata(window, bigEndian, compress: false, compressionId: 1);

    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static void WriteSuperblock(
        byte[] bytes,
        bool bigEndian,
        int compressionId,
        long fragIndexAddress,
        long idIndexAddress,
        long inodeBlockAddress,
        long dirBlockAddress)
    {
        var span = bytes.AsSpan(0, 96);
        WriteUInt32(span, bigEndian, 0, 0x73717368);
        WriteUInt32(span, bigEndian, 4, 6);
        WriteUInt32(span, bigEndian, 8, 1_774_000_000);
        WriteUInt32(span, bigEndian, 12, BlockSize);
        WriteUInt32(span, bigEndian, 16, 2);
        WriteUInt16(span, bigEndian, 20, (ushort)compressionId);
        WriteUInt16(span, bigEndian, 22, 12);
        WriteUInt16(span, bigEndian, 24, 0x40);
        WriteUInt16(span, bigEndian, 26, 1);
        WriteUInt16(span, bigEndian, 28, 4);
        WriteUInt16(span, bigEndian, 30, 0);
        WriteUInt64(span, bigEndian, 32, 0);
        WriteUInt64(span, bigEndian, 40, (ulong)bytes.Length);
        WriteUInt64(span, bigEndian, 48, (ulong)idIndexAddress);
        WriteUInt64(span, bigEndian, 56, ulong.MaxValue);
        WriteUInt64(span, bigEndian, 64, (ulong)inodeBlockAddress);
        WriteUInt64(span, bigEndian, 72, (ulong)dirBlockAddress);
        WriteUInt64(span, bigEndian, 80, (ulong)fragIndexAddress);
        WriteUInt64(span, bigEndian, 88, ulong.MaxValue);
    }

    private static void WriteDirInode(MemoryStream stream, bool bigEndian, uint inodeNumber, uint dirBlock, ushort dirOffset, int dirSize, uint parent)
    {
        WriteBaseInode(stream, bigEndian, type: 1, mode: 0x1ED, inodeNumber);
        WriteU32(stream, bigEndian, dirBlock);
        WriteU32(stream, bigEndian, 2);
        WriteU16(stream, bigEndian, (ushort)(dirSize - 3));
        WriteU16(stream, bigEndian, dirOffset);
        WriteU32(stream, bigEndian, parent);
    }

    private static void WriteFileInode(MemoryStream stream, bool bigEndian, uint inodeNumber, uint start, uint fragment, uint fragmentOffset, uint size, uint[] blocks)
    {
        WriteBaseInode(stream, bigEndian, type: 2, mode: 0x1A4, inodeNumber);
        WriteU32(stream, bigEndian, start);
        WriteU32(stream, bigEndian, fragment);
        WriteU32(stream, bigEndian, fragmentOffset);
        WriteU32(stream, bigEndian, size);
        foreach (var block in blocks)
        {
            WriteU32(stream, bigEndian, block);
        }
    }

    private static void WriteSymlinkInode(MemoryStream stream, bool bigEndian, uint inodeNumber)
    {
        WriteBaseInode(stream, bigEndian, type: 3, mode: 0x1FF, inodeNumber);
        WriteU32(stream, bigEndian, 1);
        WriteU32(stream, bigEndian, (uint)TargetBytes.Length);
        stream.Write(TargetBytes);
    }

    private static void WriteBaseInode(MemoryStream stream, bool bigEndian, ushort type, uint mode, uint inodeNumber)
    {
        WriteU16(stream, bigEndian, type);
        WriteU16(stream, bigEndian, (ushort)(mode & 0xFFF));
        WriteU16(stream, bigEndian, 0);
        WriteU16(stream, bigEndian, 0);
        WriteU32(stream, bigEndian, 1_774_000_000);
        WriteU32(stream, bigEndian, inodeNumber);
    }

    private static void WriteDirEntry(MemoryStream stream, bool bigEndian, ushort offset, short delta, ushort type, byte[] name)
    {
        WriteU16(stream, bigEndian, offset);
        WriteU16(stream, bigEndian, unchecked((ushort)delta));
        WriteU16(stream, bigEndian, type);
        WriteU16(stream, bigEndian, (ushort)(name.Length - 1));
        stream.Write(name);
    }

    private static void WriteU16(Stream stream, bool bigEndian, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        WriteUInt16(buffer, bigEndian, value);
        stream.Write(buffer);
    }

    private static void WriteU32(Stream stream, bool bigEndian, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        WriteUInt32(buffer, bigEndian, 0, value);
        stream.Write(buffer);
    }

    private static void WriteU64(Stream stream, bool bigEndian, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        }

        stream.Write(buffer);
    }

    private static void WriteUInt16(Span<byte> target, bool bigEndian, ushort value)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(target, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(target, value);
        }
    }

    private static void WriteUInt16(Span<byte> target, bool bigEndian, int offset, ushort value) =>
        WriteUInt16(target[offset..], bigEndian, value);

    private static void WriteUInt32(Span<byte> target, bool bigEndian, int offset, uint value)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(target[offset..], value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(target[offset..], value);
        }
    }

    private static void WriteUInt64(Span<byte> target, bool bigEndian, int offset, ulong value)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt64BigEndian(target[offset..], value);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(target[offset..], value);
        }
    }
}