using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Conversion;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Parser;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Streams;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

[CollectionDefinition("Firmware concurrency", DisableParallelization = true)]
public sealed class FirmwareConcurrencyCollection;

[Collection("Firmware concurrency")]
public class FileSystemThroughputTests
{
    [Fact]
    public void MappedCopy_CoalescesRunsAndPreservesHolesFragmentationAndTail()
    {
        const int block = 4096;
        var bytes = new byte[600 * block];
        new Random(71).NextBytes(bytes);
        using var source = new MeasuredStream(bytes);
        using var output = new MemoryStream();
        long? Map(uint logical) => logical < 300 ? logical : logical < 310 ? null : logical - 100;
        var count = 520 * block - 17;
        MappedBlockCopy.Copy(source, output, count, block, Map);
        var expected = new byte[count];
        for (uint logical = 0; logical * block < count; logical++)
            if (Map(logical) is { } physical)
                bytes.AsSpan(checked((int)physical * block), Math.Min(block, count - (int)logical * block))
                    .CopyTo(expected.AsSpan((int)logical * block));
        Assert.Equal(expected, output.ToArray());
        Assert.InRange(source.Reads, 1, 4);
        Assert.InRange(source.LargestRead, 4096, 1024 * 1024);
    }

    [Fact]
    public void MappedCopy_TruncatedPhysicalBlockStillFails()
    {
        using var source = new MemoryStream(new byte[4095]);
        Assert.Throws<InvalidDataException>(() => MappedBlockCopy.Copy(source, Stream.Null, 1, 4096, _ => 0));
    }

    [Fact]
    public void SparseRawCopy_LargeBlocksKeepWritesBounded()
    {
        using var sparse = new SparseFile(256 * 1024, 1024 * 1024);
        sparse.AddFillChunk(0x1234abcd, 256 * 1024);
        using var output = new BoundedWriteRecorder();
        sparse.WriteRawToStream(output);
        var bytes = output.ToArray();
        Assert.Equal(1024 * 1024, bytes.Length);
        Assert.Equal(0xcd, bytes[0]);
        Assert.Equal(0x12, bytes[256 * 1024 - 1]);
        Assert.All(bytes.Skip(256 * 1024), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SparseFill_UnalignedReadsMatchLittleEndianPattern(int phase)
    {
        using var sparse = new SparseFile(4096, 8192);
        sparse.AddFillChunk(0x1234abcd, 8192);
        using var stream = new SparseReadStream(sparse);
        foreach (var length in new[] { 0, 1, 3, 4, 5, 17, 4099 })
        {
            stream.Position = phase;
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            byte[] pattern = [0xcd, 0xab, 0x34, 0x12];
            Assert.Equal(Enumerable.Range(0, length).Select(i => pattern[(i + phase) % 4]), bytes);
        }
    }

    [Fact]
    public void IndependentReaders_ShortReadsDoNotMixPositions()
    {
        var bytes = new byte[8 * 32768];
        new Random(42).NextBytes(bytes);
        using var source = new ShortReadStream(bytes);
        var gate = new object();
        Parallel.For(0, 8, i =>
        {
            using var reader = new IndependentReadStream(source, gate);
            reader.Position = i * 32768;
            var actual = new byte[32768];
            reader.ReadExactly(actual);
            Assert.Equal(bytes.AsSpan(i * 32768, 32768).ToArray(), actual);
        });
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData("erofs")]
    [InlineData("f2fs")]
    public void ParallelFilePlanning_PreservesDecodedFiles(string format)
    {
        using var temp = new TempDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var bytes = new byte[192 * 1024 + 37];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 37);
        for (var i = 0; i < 6; i++) File.WriteAllBytes(Path.Combine(source, $"file{i}"), bytes);
        var saved = FirmwareParallelism.Workers;
        try
        {
            foreach (var workers in new[] { 1, 4 })
            {
                FirmwareParallelism.Workers = workers;
                var image = Path.Combine(temp.Path, format + workers + ".img");
                var extracted = Path.Combine(temp.Path, "out" + workers);
                if (format == "erofs")
                {
                    new ErofsBuilder().Build(source, image);
                    using var parser = new ErofsParser(image);
                    var progress = new RecordingProgress();
                    parser.ExtractToDirectory(extracted, progress: progress);
                    Assert.Equal(Enumerable.Range(1, 6), progress.Counts);
                }
                else
                {
                    F2fsBuilder.Build(source, image, new F2fsOptions { EnableCompression = true, Uuid = Guid.Empty });
                    using var parser = new F2fsParser(image);
                    parser.ExtractToDirectory(extracted);
                }
                foreach (var file in Directory.EnumerateFiles(source))
                    Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(extracted, Path.GetFileName(file))));
            }
        }
        finally { FirmwareParallelism.Workers = saved; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Erofs_IndexWindowsCrossBoundariesWithFullAndCompactIndexes(bool compact)
    {
        using var temp = new TempDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        var input = Path.Combine(source, "large.img");
        var buffer = new byte[65536];
        for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)(i % 251);
        using (var file = File.Create(input))
            for (var i = 0; i < 2560; i++) file.Write(buffer);
        var image = Path.Combine(temp.Path, "erofs.img");
        new ErofsBuilder().Build(source, image, new ErofsOptions { UseCompactedIndexes = compact });
        var output = Path.Combine(temp.Path, "out");
        using (var parser = new ErofsParser(image)) parser.ExtractToDirectory(output);
        using var original = File.OpenRead(input);
        using var restored = File.OpenRead(Path.Combine(output, "large.img"));
        Assert.Equal(SHA256.HashData(original), SHA256.HashData(restored));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Super_ParallelPartitionsRoundTripThroughRawAndSparse(bool sparse)
    {
        using var temp = new TempDirectory();
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "source")).FullName;
        for (var i = 0; i < 5; i++)
        {
            var bytes = new byte[65536];
            new Random(i).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(source, $"part{i}.img"), bytes);
        }
        var image = Path.Combine(temp.Path, "super.img");
        var handler = new LpFormatHandler();
        var built = await handler.PackAsync(source, image, TestContext.Current.CancellationToken);
        Assert.True(built.Success, built.Summary);
        if (sparse)
        {
            var converted = Path.Combine(temp.Path, "super.sparse");
            SparseImageConverter.ConvertRawToSparse(image, converted);
            image = converted;
        }
        var extracted = Path.Combine(temp.Path, "out");
        var result = await handler.UnpackAsync(image, extracted, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Summary);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            using var original = File.OpenRead(file);
            using var actual = File.OpenRead(Path.Combine(extracted, Path.GetFileName(file)));
            Assert.Equal(SHA256.HashData(original), SHA256.HashData(actual));
        }
        Assert.Empty(Directory.GetFiles(extracted, "*.tmp-*"));
    }

    private sealed class RecordingProgress : IProgress<(int processed, int total)>
    {
        internal List<int> Counts { get; } = [];
        public void Report((int processed, int total) value) => Counts.Add(value.processed);
    }
    private sealed class MeasuredStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal int Reads;
        internal int LargestRead;
        public override int Read(Span<byte> buffer)
        {
            Reads++; LargestRead = Math.Max(LargestRead, buffer.Length);
            return base.Read(buffer);
        }
    }
    private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 17)]);
    }

    private sealed class BoundedWriteRecorder : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Assert.InRange(count, 0, 65536);
            base.Write(buffer, offset, count);
        }
    }
}