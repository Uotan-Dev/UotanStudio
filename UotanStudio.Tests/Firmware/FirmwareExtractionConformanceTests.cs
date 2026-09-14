using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Dz;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.UpdateApp;
using UotanStudio.Firmware.CrossCutting.Compress;
using UotanStudio.Firmware.SharedKernel;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class FirmwareExtractionConformanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-extraction-" + Guid.NewGuid().ToString("N"));
    public FirmwareExtractionConformanceTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dz_DecompressesEveryChunkAndPreservesNames(bool zstd)
    {
        var path = Write("test.dz", BuildDz([("one.bin", "one"u8.ToArray(), 10u),
            ("two.bin", "second payload"u8.ToArray(), 12u)], zstd));
        Assert.Equal(2, DzExtractor.Extract(path, Path.Combine(_root, "out"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("one", File.ReadAllText(Path.Combine(_root, "out", "one.bin")));
        Assert.Equal("second payload", File.ReadAllText(Path.Combine(_root, "out", "two.bin")));
    }

    [Fact]
    public void Dz_ReassemblesPhysicalOffsetsAndLeadingHoles()
    {
        var path = Write("test.dz", BuildDz([("one.bin", [1, 2, 3], 11u), ("two.bin", [4, 5], 13u)]));
        var output = Path.Combine(_root, "partition.img");
        DzExtractor.ExtractPartition(path, output, new DzPartitionLayout("system", 0, 10, 5), TestContext.Current.CancellationToken);
        var actual = File.ReadAllBytes(output);
        Assert.Equal(2560, actual.Length);
        Assert.All(actual[..512], b => Assert.Equal(0, b));
        Assert.Equal(new byte[] { 1, 2, 3 }, actual[512..515]);
        Assert.Equal(new byte[] { 4, 5 }, actual[1536..1538]);
    }

    [Fact]
    public void Dz_HashFailurePreservesDestination()
    {
        var bytes = BuildDz([("one.bin", [1, 2, 3], 10u)]);
        bytes[512 + 108] ^= 1;
        var path = Write("test.dz", bytes);
        var output = Path.Combine(_root, "out");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "one.bin"), "preserved");
        Assert.Throws<InvalidDataException>(() => DzExtractor.Extract(path, output, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("preserved", File.ReadAllText(Path.Combine(output, "one.bin")));
        Assert.Single(Directory.GetFiles(output));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("C:/escape")]
    [InlineData("nested/../../escape")]
    public void Dz_RejectsUnsafeNames(string name)
    {
        var path = Write("test.dz", BuildDz([(name, [1], 10u)]));
        Assert.Throws<InvalidDataException>(() => DzExtractor.Extract(path, Path.Combine(_root, "out"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Dz_RejectsMissingDeclaredChunks()
    {
        var bytes = BuildDz([("one.bin", [1], 10u)]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(192), 2);
        var path = Write("test.dz", bytes);
        Assert.Throws<InvalidDataException>(() => DzExtractor.Parse(path));
    }

    [Fact]
    public void Kdz_RejectsTraversalAndTruncatedRanges()
    {
        var bytes = new byte[512];
        DzConstants.KdzHeaderV2.CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("../escape").CopyTo(bytes, 8);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(264), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(272), 511);
        var path = Write("test.kdz", bytes);
        Assert.Throws<InvalidDataException>(() => KdzExtractor.Extract(path, Path.Combine(_root, "out"), cancellationToken: TestContext.Current.CancellationToken));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(264), 2);
        Write("test.kdz", bytes);
        Assert.Throws<InvalidDataException>(() => KdzExtractor.Parse(path));
    }

    [Fact]
    public void UpdateApp_RejectsTruncationAndKeepsAllDuplicates()
    {
        var entry = BuildUpdateEntry("SYSTEM", [1, 2, 3, 4]);
        var path = Write("UPDATE.APP", entry.Concat(entry).Concat(entry).ToArray());
        var output = Path.Combine(_root, "out");
        Assert.Equal(3, UpdateAppProcessor.Extract(path, output, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, Directory.GetFiles(output).Length);
        Assert.True(File.Exists(Path.Combine(output, "system_3.img")));
        Write("UPDATE.APP", entry[..^1]);
        Assert.Throws<InvalidDataException>(() => UpdateAppProcessor.Extract(path, output, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(output, "system.img")));
    }

    [Fact]
    public void UpdateApp_RejectsEscapingName()
    {
        var path = Write("UPDATE.APP", BuildUpdateEntry("../escape", [1]));
        Assert.Throws<InvalidDataException>(() => UpdateAppProcessor.Extract(path, Path.Combine(_root, "out"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Nb0_PaddedOffsetsMatchAllOddSizedFiles()
    {
        var builder = Nb0Packer.CreateBuilder();
        for (var i = 1; i < 4; i++)
            builder.AddFile(Write($"src{i}", Enumerable.Repeat((byte)i, i).ToArray()), $"file{i}.bin");
        var path = Path.Combine(_root, "test.nb0");
        builder.Build(path);
        var output = Path.Combine(_root, "out");
        var result = new Nb0Extractor().Extract(path, output);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        for (var i = 1; i < 4; i++)
            Assert.Equal(Enumerable.Repeat((byte)i, i), File.ReadAllBytes(Path.Combine(output, $"file{i}.bin")));
    }

    [Fact]
    public async Task CompressionBudgetFailurePreservesExistingFile()
    {
        var path = Path.Combine(_root, "large.gz");
        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            gzip.Write(new byte[10000]);
        var output = Path.Combine(_root, "out");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "large"), "preserved");
        var result = await new CompressFormatHandler().UnpackAsync(path, output,
            new UnpackOptions { MaximumOutputBytes = 100 }, TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("preserved", File.ReadAllText(Path.Combine(output, "large")));
        Assert.Single(Directory.GetFiles(output));
    }

    [Fact]
    public void CpioListingNeverReadsLargeRegularPayload()
    {
        var header = NewcHeader("large.bin", 0xC0000000);
        var trailer = NewcHeader("TRAILER!!!", 0);
        using var stream = new MetadataOnlyCpioStream(header, 0xC0000000L, trailer);
        var entry = Assert.Single(new CpioArchive().ListEntries(stream, ArchiveFormat.NewAscii));
        Assert.Equal("large.bin", entry.Name);
    }

    private static byte[] NewcHeader(string name, uint size)
    {
        uint[] fields = [1, 0x81A4, 0, 0, 1, 0, size, 0, 0, 0, 0, (uint)name.Length + 1, 0];
        var text = "070701" + string.Concat(fields.Select(f => f.ToString("x8"))) + name + "\0";
        var bytes = Encoding.ASCII.GetBytes(text);
        Array.Resize(ref bytes, (bytes.Length + 3) & ~3);
        return bytes;
    }

    private static byte[] BuildUpdateEntry(string name, byte[] payload)
    {
        var bytes = new byte[100 + payload.Length];
        UpdateAppProcessor.Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)payload.Length);
        Encoding.ASCII.GetBytes(name).CopyTo(bytes, 60);
        payload.CopyTo(bytes, 100);
        return bytes;
    }

    private static byte[] BuildDz((string Name, byte[] Payload, uint Sector)[] chunks, bool zstd = false)
    {
        using var result = new MemoryStream();
        var fileHeader = new byte[512];
        DzConstants.DzFileHeader.CopyTo(fileHeader, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(fileHeader.AsSpan(192), (uint)chunks.Length);
        result.Write(fileHeader);
        foreach (var chunk in chunks)
        {
            using var compressed = new MemoryStream();
            using var input = new MemoryStream(chunk.Payload);
            CompressionService.Compress(input, compressed, zstd ? CompressionFormat.Zstd : CompressionFormat.Zlib);
            var header = new byte[512];
            DzConstants.DzChunkHeader.CopyTo(header, 0);
            Encoding.ASCII.GetBytes("system").CopyTo(header, 4);
            Encoding.ASCII.GetBytes(chunk.Name).CopyTo(header, 36);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(100), (uint)chunk.Payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(104), (uint)compressed.Length);
            MD5.HashData(chunk.Payload).CopyTo(header, 108);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(124), chunk.Sector);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(128), 1);
            result.Write(header);
            compressed.Position = 0;
            compressed.CopyTo(result);
        }
        return result.ToArray();
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class MetadataOnlyCpioStream(byte[] header, long payloadSize, byte[] trailer) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => header.Length + payloadSize + trailer.Length;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (Position == Length) return 0;
            if (Position >= header.Length && Position < header.Length + payloadSize)
                throw new InvalidOperationException("Listing attempted to read a multi-gigabyte payload.");
            var source = Position < header.Length ? header.AsSpan((int)Position)
                : trailer.AsSpan((int)(Position - header.Length - payloadSize));
            var count = Math.Min(buffer.Length, source.Length);
            source[..count].CopyTo(buffer);
            Position += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}