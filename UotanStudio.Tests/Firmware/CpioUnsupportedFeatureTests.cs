using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.Archive.Formats.Cpio;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Leaf-type coverage for the CPIO compressed-capture and CPIO writer exception classifications.
/// <para>为 CPIO 压缩捕获与 CPIO 写入器的异常分类提供叶子类型覆盖。</para>
/// </summary>
public sealed class CpioUnsupportedFeatureTests
{
    private static readonly byte[] TrailerOnlyNewcCpio = Encoding.ASCII.GetBytes(
        "070701"
        + "00000000"
        + "00000000"
        + "00000000"
        + "00000000"
        + "00000001"
        + "00000000"
        + "00000000"
        + "00000000"
        + "00000000"
        + "00000000"
        + "00000000"
        + "0000000B"
        + "00000000"
        + "TRAILER!!!\0\0\0\0");

    [Fact]
    public async Task CaptureRepackMetadataAsync_XzStreamWithSha256Check_ThrowsArchiveUnsupportedFeatureException()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(temp.Path, "ramdisk.cpio.xz");
        await File.WriteAllBytesAsync(source, BuildXzContainer(TrailerOnlyNewcCpio, 0x0A, false), token);

        var provider = (IRepackMetadataProvider)new CpioFormatHandler();
        var metadata = new RepackMetadata();
        var error = await Assert.ThrowsAsync<ArchiveUnsupportedFeatureException>(
            async () => await provider.CaptureRepackMetadataAsync(metadata, source, temp.Path, temp.Path, token));
        Assert.Equal("Unsupported XZ stream flags.", error.Message);
    }

    [Fact]
    public async Task CaptureRepackMetadataAsync_XzBlockWithTwoFilters_ThrowsArchiveUnsupportedFeatureException()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(temp.Path, "ramdisk.cpio.xz");
        await File.WriteAllBytesAsync(source, BuildXzContainer(TrailerOnlyNewcCpio, 0x01, true), token);

        var provider = (IRepackMetadataProvider)new CpioFormatHandler();
        var metadata = new RepackMetadata();
        var error = await Assert.ThrowsAsync<ArchiveUnsupportedFeatureException>(
            async () => await provider.CaptureRepackMetadataAsync(metadata, source, temp.Path, temp.Path, token));
        Assert.Equal("XZ filter chain cannot be preserved.", error.Message);
    }

    [Fact]
    public async Task CaptureRepackMetadataAsync_UnrecognisedCompressedFraming_ThrowsArchiveCorruptedException()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(temp.Path, "ramdisk.cpio.xz");
        await File.WriteAllBytesAsync(source,
            Encoding.ASCII.GetBytes("This is plainly not a compressed container at all, not even close."),
            token);

        var provider = (IRepackMetadataProvider)new CpioFormatHandler();
        var metadata = new RepackMetadata();
        var error = await Assert.ThrowsAsync<ArchiveCorruptedException>(
            async () => await provider.CaptureRepackMetadataAsync(metadata, source, temp.Path, temp.Path, token));
        Assert.Equal("Compressed CPIO framing is not recognized.", error.Message);
    }

    [Fact]
    public async Task CaptureRepackMetadataAsync_ValidXzAroundNonCpioPayload_ThrowsArchiveCorruptedException()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(temp.Path, "ramdisk.cpio.xz");
        await File.WriteAllBytesAsync(source,
            BuildXzContainer(Encoding.ASCII.GetBytes("not a cpio archive header at all, just filler bytes for the container."), 0x01, false),
            token);

        var provider = (IRepackMetadataProvider)new CpioFormatHandler();
        var metadata = new RepackMetadata();
        var error = await Assert.ThrowsAsync<ArchiveCorruptedException>(
            async () => await provider.CaptureRepackMetadataAsync(metadata, source, temp.Path, temp.Path, token));
        Assert.Equal("Unknown archive format.", error.Message);
    }

    [Fact]
    public void WriteAll_PayloadLengthAboveNewcMaximum_ThrowsArchiveUnsupportedFeatureException()
    {
        var entry = new ArchiveEntry
        {
            Name = "oversized",
            Data = [],
            DataStream = new FixedLengthTestStream(4_294_967_296L),
            Metadata = new ArchiveEntryMetadata { FileType = CpioFileType.Regular, LinkCount = 1 },
        };
        using var output = new MemoryStream();
        var error = Assert.Throws<ArchiveUnsupportedFeatureException>(
            () => new CpioArchiveWriter().WriteAll(output, [entry], ArchiveFormat.NewAscii));
        Assert.Contains("4294967296", error.Message);
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public void WriteAll_PayloadLengthAboveOldAsciiMaximum_ThrowsArchiveUnsupportedFeatureException()
    {
        var entry = new ArchiveEntry
        {
            Name = "oversized",
            Data = [],
            DataStream = new FixedLengthTestStream(8_589_934_592L),
            Metadata = new ArchiveEntryMetadata { FileType = CpioFileType.Regular, LinkCount = 1 },
        };
        using var output = new MemoryStream();
        var error = Assert.Throws<ArchiveUnsupportedFeatureException>(
            () => new CpioArchiveWriter().WriteAll(output, [entry], ArchiveFormat.OldAscii));
        Assert.Contains("8589934592", error.Message);
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public void WriteAll_DeclaredLengthMismatch_ThrowsArchiveCorruptedException()
    {
        var entry = new ArchiveEntry
        {
            Name = "mismatch",
            Data = [1, 2, 3],
            DeclaredDataLength = 5,
            Metadata = new ArchiveEntryMetadata { FileType = CpioFileType.Regular, LinkCount = 1 },
        };
        using var output = new MemoryStream();
        var error = Assert.Throws<ArchiveCorruptedException>(
            () => new CpioArchiveWriter().WriteAll(output, [entry], ArchiveFormat.NewAscii));
        Assert.Equal("Entry 'mismatch' declared length 5 does not match its data length 3.", error.Message);
    }

    [Fact]
    public void WriteAll_NegativeDeclaredLength_ThrowsArchiveCorruptedException()
    {
        var entry = new ArchiveEntry
        {
            Name = "negative",
            Data = [1, 2, 3],
            DeclaredDataLength = -1,
            Metadata = new ArchiveEntryMetadata { FileType = CpioFileType.Regular, LinkCount = 1 },
        };
        using var output = new MemoryStream();
        Assert.Throws<ArchiveCorruptedException>(
            () => new CpioArchiveWriter().WriteAll(output, [entry], ArchiveFormat.NewAscii));
    }

    [Fact]
    public void WriteAll_FactoryPayloadBeyondDeclaredLength_ThrowsArchiveCorruptedException()
    {
        var entry = new ArchiveEntry
        {
            Name = "beyond",
            Data = [],
            DeclaredDataLength = 3,
            DataStreamFactory = () => new MemoryStream([1, 2, 3, 4, 5]),
            Metadata = new ArchiveEntryMetadata { FileType = CpioFileType.Regular, LinkCount = 1 },
        };
        using var output = new MemoryStream();
        var error = Assert.Throws<ArchiveCorruptedException>(
            () => new CpioArchiveWriter().WriteAll(output, [entry], ArchiveFormat.NewAscii));
        Assert.Equal("Entry 'beyond' source contains data beyond its declared length 3.", error.Message);
    }

    private static byte[] BuildXzContainer(byte[] uncompressed, byte checkId, bool prependX86Filter)
    {
        List<byte> payload = [0x01, (byte)((uncompressed.Length - 1) >> 8), (byte)((uncompressed.Length - 1) & 0xFF),];
        payload.AddRange(uncompressed);
        payload.Add(0x00);

        var block = new List<byte> { 0x00, (byte)(0xC0 | (prependX86Filter ? 1 : 0)) };
        AppendVariableLength(block, (ulong)payload.Count);
        AppendVariableLength(block, (ulong)uncompressed.Length);
        if (prependX86Filter)
        {
            AppendVariableLength(block, 0x04);
            AppendVariableLength(block, 0x00);
        }
        AppendVariableLength(block, 0x21);
        AppendVariableLength(block, 0x01);
        block.Add(0x00);
        while (block.Count % 4 != 0)
        {
            block.Add(0x00);
        }
        block[0] = (byte)(block.Count / 4);

        var result = new List<byte> { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 0x00, checkId };
        AppendLittleEndian32(result, Crc32.HashToUInt32([0x00, checkId]));

        var headerBytes = block.ToArray();
        result.AddRange(headerBytes);
        AppendLittleEndian32(result, Crc32.HashToUInt32(headerBytes));

        result.AddRange(payload);
        while (result.Count % 4 != 0)
        {
            result.Add(0x00);
        }

        var check = checkId == 0x0A
            ? SHA256.HashData(uncompressed)
            : LittleEndian(Crc32.HashToUInt32(uncompressed));
        result.AddRange(check);

        var index = new List<byte> { 0x00 };
        AppendVariableLength(index, 1);
        AppendVariableLength(index, (ulong)(headerBytes.Length + 4 + payload.Count + check.Length));
        AppendVariableLength(index, (ulong)uncompressed.Length);
        while (index.Count % 4 != 0)
        {
            index.Add(0x00);
        }
        var indexBytes = index.ToArray();
        result.AddRange(indexBytes);
        AppendLittleEndian32(result, Crc32.HashToUInt32(indexBytes));

        var footer = new List<byte>();
        AppendLittleEndian32(footer, (uint)(indexBytes.Length / 4 - 1));
        footer.Add(0x00);
        footer.Add(checkId);
        var footerBytes = footer.ToArray();
        AppendLittleEndian32(result, Crc32.HashToUInt32(footerBytes));
        result.AddRange(footerBytes);
        result.AddRange([0x59, 0x5A]);
        return result.ToArray();
    }

    private static void AppendVariableLength(List<byte> target, ulong value)
    {
        while (value >= 0x80)
        {
            target.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        target.Add((byte)value);
    }

    private static void AppendLittleEndian32(List<byte> target, uint value) => target.AddRange(LittleEndian(value));

    private static byte[] LittleEndian(uint value) =>
        [(byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF), (byte)(value >> 24)];

    private sealed class FixedLengthTestStream(long length) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => 0L; set { } }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => 0L;

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}