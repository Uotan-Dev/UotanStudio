using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.BlockImage.Sparse.DataProviders;
using UotanStudio.Firmware.BlockImage.Sparse.Models;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Verifies that <see cref="SparseRawZeroMode.Seek"/> produces the exact bytes of <see cref="SparseRawZeroMode.Write"/>
/// for RAW, FILL, zero-FILL, DONT_CARE and tail-padded layouts, and that every ineligible destination or chunk layout
/// falls back to writing zeros instead of leaving holes.
/// <para>验证 <see cref="SparseRawZeroMode.Seek"/> 对 RAW、FILL、零 FILL、DONT_CARE 与尾部补齐布局产出与
/// <see cref="SparseRawZeroMode.Write"/> 完全一致的字节，并验证任何不合格的目标或块布局都回退为写出零而不是留下空洞。</para>
/// </summary>
public class SparseRawZeroModeTests
{
    private const uint BlockSize = 4096;
    private const int BlockSizeInt = 4096;

    [Fact]
    public void WriteRawToStream_SeekModeMixedLayout_MatchesWriteModeByteForByte()
    {
        using var sparse = BuildMixedImage(13);
        using var expected = new MemoryStream();
        sparse.WriteRawToStream(expected);
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "expanded.img");

        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            sparse.WriteRawToStream(file, SparseRawZeroMode.Seek);
            Assert.Equal(9L * BlockSize, file.Position);
            Assert.Equal(13L * BlockSize, file.Length);
        }

        Assert.Equal(13L * BlockSize, expected.Position);
        var actual = File.ReadAllBytes(path);
        Assert.Equal(13L * BlockSize, actual.Length);
        Assert.Equal(expected.ToArray(), actual);
    }

    [Fact]
    public void WriteRawToStream_SeekModeAllZeroChunks_ProducesSameZeroFilledLength()
    {
        using var sparse = new SparseFile(BlockSize, 6 * BlockSize);
        sparse.AddDontCareChunk(2 * BlockSize);
        sparse.AddFillChunk(0, BlockSize);
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "zeros.img");

        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            sparse.WriteRawToStream(file, SparseRawZeroMode.Seek);
            Assert.Equal(0L, file.Position);
            Assert.Equal(6L * BlockSize, file.Length);
        }

        var actual = File.ReadAllBytes(path);
        Assert.Equal(6L * BlockSize, actual.Length);
        Assert.Equal(new byte[6 * BlockSizeInt], actual);
    }

    [Fact]
    public void WriteRawToStream_SeekModeNonSeekableDestination_FallsBackToWrittenZeros()
    {
        using var sparse = BuildMixedImage(13);
        using var expected = new MemoryStream();
        sparse.WriteRawToStream(expected);
        using var fallback = new MemoryStream();

        sparse.WriteRawToStream(fallback, SparseRawZeroMode.Seek);

        Assert.Equal(expected.ToArray(), fallback.ToArray());
    }

    [Fact]
    public void WriteRawToStream_SeekModeGappedChunkList_FallsBackAndKeepsShiftedBytes()
    {
        using var sparse = new SparseFile(BlockSize, 9 * BlockSize);
        sparse.AddRawChunk(BuildPattern(BlockSizeInt, 5), 0);
        sparse.AddDontCareChunk(2 * BlockSize, blockIndex: 5);
        using var expected = new MemoryStream();
        sparse.WriteRawToStream(expected);
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "gapped.img");

        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            sparse.WriteRawToStream(file, SparseRawZeroMode.Seek);
        }

        Assert.Equal(expected.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteRawToStream_SeekModeNonEmptyDestination_FallsBackAndOverwritesFromPositionZero()
    {
        using var sparse = BuildMixedImage(13);
        using var expected = new MemoryStream();
        sparse.WriteRawToStream(expected);
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "occupied.img");
        File.WriteAllBytes(path, [0xFF]);

        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            sparse.WriteRawToStream(file, SparseRawZeroMode.Seek);
        }

        Assert.Equal(expected.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteRawToStream_SeekModeChunkExceedingLogicalLength_ExtendsToChunkEnd()
    {
        using var sparse = new SparseFile(BlockSize, BlockSize);
        sparse.AddRawChunk(BuildPattern(2 * BlockSizeInt, 11), 0);
        using var expected = new MemoryStream();
        sparse.WriteRawToStream(expected);
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "overrun.img");

        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            sparse.WriteRawToStream(file, SparseRawZeroMode.Seek);
        }

        Assert.Equal(2L * BlockSize, expected.Length);
        Assert.Equal(expected.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteRawToStream_SeekModeTruncatedRawProvider_ThrowsBeforeLaterChunksShift()
    {
        using var sparse = new SparseFile(BlockSize, 3 * BlockSize);
        sparse.AddRawChunk(new TruncatedProvider(BlockSize, (int)(BlockSize / 2)), 1);
        sparse.AddDontCareChunk(BlockSize);
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "truncated.img");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);

        Assert.Throws<InvalidDataException>(() => sparse.WriteRawToStream(file, SparseRawZeroMode.Seek));
    }

    [Fact]
    public void WriteRawToStream_UndefinedZeroMode_Throws()
    {
        using var sparse = new SparseFile(BlockSize, BlockSize);
        using var stream = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => sparse.WriteRawToStream(stream, (SparseRawZeroMode)17));
    }

    [Fact]
    public void WriteRawToStream_NullStream_Throws()
    {
        using var sparse = new SparseFile(BlockSize, BlockSize);

        Assert.Throws<ArgumentNullException>(() => sparse.WriteRawToStream(null!, SparseRawZeroMode.Seek));
    }

    private static SparseFile BuildMixedImage(uint totalBlocks)
    {
        var sparse = new SparseFile(BlockSize, totalBlocks * BlockSize);
        sparse.AddRawChunk(BuildPattern(BlockSizeInt, 3), 0);
        sparse.AddFillChunk(0x11223344, 3 * BlockSize);
        sparse.AddDontCareChunk(2 * BlockSize);
        sparse.AddFillChunk(0, BlockSize);
        sparse.AddRawChunk(BuildPattern(2 * BlockSizeInt, 7));
        sparse.AddDontCareChunk(2 * BlockSize);
        return sparse;
    }

    private static byte[] BuildPattern(int length, int factor)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((i * factor + 1) & 0xFF);
        }
        return data;
    }

    private sealed class TruncatedProvider(long declaredLength, int actualLength) : ISparseDataProvider
    {
        public long Length => declaredLength;

        public void WriteTo(Stream stream) => stream.Write(new byte[actualLength]);

        public Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            WriteTo(stream);
            return Task.CompletedTask;
        }

        public int Read(long offset, byte[] buffer, int bufferOffset, int count) => 0;

        public int Read(long offset, Span<byte> buffer) => 0;

        public ISparseDataProvider GetSubProvider(long offset, long length) => this;

        public void Dispose()
        {
        }
    }
}