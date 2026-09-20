using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// <see cref="SparseReadStream"/> random-access tests: the logical (unsparsified) image served by the
/// stream must match <see cref="SparseFile.WriteRawToStream"/> across raw / fill / dont-care chunks,
/// including cross-chunk reads, unaligned positions, EOF short reads and seek origins.
/// <para><see cref="SparseReadStream"/> 随机访问测试：流提供的逻辑（非稀疏）镜像必须与
/// <see cref="SparseFile.WriteRawToStream"/> 在 raw/fill/dont-care 数据块上完全一致，涵盖跨块读取、
/// 未对齐位置、EOF 短读与 Seek 起始点。</para>
/// </summary>
public sealed class SparseReadStreamTests
{
    private const uint BlockSize = 4096;

    [Fact]
    public void ReadAll_MatchesWriteRawToStream()
    {
        using var sparse = BuildFixture();
        byte[] expected;
        using (var rawMs = new MemoryStream())
        {
            sparse.WriteRawToStream(rawMs);
            expected = rawMs.ToArray();
        }

        using var stream = new SparseReadStream(sparse);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Equal(expected.Length, stream.Length);

        var buffer = new byte[expected.Length];
        stream.Position = 0;
        var read = stream.Read(buffer, 0, buffer.Length);
        Assert.Equal(expected.Length, read);
        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void RandomSeek_ReadsMatchLogicalImage()
    {
        using var sparse = BuildFixture();
        byte[] expected;
        using (var rawMs = new MemoryStream())
        {
            sparse.WriteRawToStream(rawMs);
            expected = rawMs.ToArray();
        }

        using var stream = new SparseReadStream(sparse);

        // 未对齐位置：从 block1 中部开始读 100 字节（跨到 block2）。
        // Unaligned position: read 100 bytes from mid-block-1 (crossing into block 2).
        stream.Position = BlockSize + 100;
        var mid = new byte[100];
        Assert.Equal(100, stream.Read(mid));
        Assert.Equal(expected.AsSpan((int)BlockSize + 100, 100).ToArray(), mid);

        // Fill 块（block 1）：4 字节模式应重复展开。
        // Fill chunk (block 1): the 4-byte pattern must repeat.
        stream.Position = BlockSize;
        var fill = new byte[16];
        Assert.Equal(16, stream.Read(fill));
        for (var i = 0; i < fill.Length; i++)
        {
            Assert.Equal((byte)(0xDEADBEEF >> (8 * (i % 4))), fill[i]);
        }

        // DontCare 块（block 2）：应为全零。
        // DontCare chunk (block 2): all zeros.
        stream.Position = BlockSize * 2;
        var zeros = new byte[BlockSize];
        Assert.Equal((int)BlockSize, stream.Read(zeros));
        Assert.All(zeros, static b => Assert.Equal(0, b));
    }

    [Fact]
    public void Seek_AllOrigins_BehaveLikeStream()
    {
        using var sparse = BuildFixture();
        using var stream = new SparseReadStream(sparse);
        var length = stream.Length;

        Assert.Equal(0, stream.Seek(0, SeekOrigin.Begin));
        Assert.Equal(0, stream.Position);
        Assert.Equal(5, stream.Seek(5, SeekOrigin.Begin));
        Assert.Equal(5, stream.Position);
        Assert.Equal(5 + 3, stream.Seek(3, SeekOrigin.Current));
        Assert.Equal(length - 1, stream.Seek(-1, SeekOrigin.End));

        stream.Position = length;
        var eof = new byte[4];
        Assert.Equal(0, stream.Read(eof)); // EOF 短读返回 0。
    }

    [Fact]
    public void Read_CrossingChunkBoundary_ReturnsContiguousData()
    {
        using var sparse = BuildFixture();
        byte[] expected;
        using (var rawMs = new MemoryStream())
        {
            sparse.WriteRawToStream(rawMs);
            expected = rawMs.ToArray();
        }

        using var stream = new SparseReadStream(sparse);
        // 从 block0 尾部跨到 block1（fill）再跨到 block2（dontcare）。
        // From the tail of block 0 across block 1 (fill) into block 2 (dontcare).
        var start = BlockSize - 16;
        stream.Position = start;
        var buffer = new byte[BlockSize + 32];
        var read = stream.Read(buffer, 0, buffer.Length);
        Assert.Equal(buffer.Length, read);
        Assert.Equal(expected.AsSpan((int)start, buffer.Length).ToArray(), buffer);
    }

    [Fact]
    public void ShortRead_AtImageEnd_ReturnsRemainder()
    {
        using var sparse = BuildFixture();
        using var stream = new SparseReadStream(sparse);
        stream.Position = stream.Length - 10;
        var buffer = new byte[64];
        Assert.Equal(10, stream.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void UnsupportedOperations_Throw()
    {
        using var sparse = BuildFixture();
        using var stream = new SparseReadStream(sparse);
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(100));
        Assert.Throws<NotSupportedException>(() => stream.Flush());
    }

    [Fact]
    public void PeekLogicalRange_ReturnsLogicalBytes()
    {
        using var sparse = BuildFixture();
        var tmp = Path.Combine(Path.GetTempPath(), $"sparse_peek_{Guid.NewGuid():N}.img");
        try
        {
            using (var fs = File.Create(tmp))
            {
                sparse.WriteToStream(fs, sparse: true);
            }

            // 逻辑偏移 BlockSize 处是 fill 块（4 字节模式重复）。
            // At logical offset BlockSize sits the fill chunk (4-byte pattern repeated).
            var peek = SparseReader.PeekLogicalRange(tmp, BlockSize, 8);
            Assert.Equal(8, peek.Length);
            for (var i = 0; i < peek.Length; i++)
            {
                Assert.Equal((byte)(0xDEADBEEF >> (8 * (i % 4))), peek[i]);
            }

            // 请求超出镜像末尾时短读。
            // Reading past the end of the image short-reads.
            var over = SparseReader.PeekLogicalRange(tmp, (long)BlockSize * 4 - 4, 16);
            Assert.Equal(4, over.Length);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    [Fact]
    public void LoadedFromBuffer_ServesSameLogicalImage()
    {
        using var sparse = BuildFixture();
        byte[] sparseBytes;
        using (var ms = new MemoryStream())
        {
            sparse.WriteToStream(ms, sparse: true);
            sparseBytes = ms.ToArray();
        }

        byte[] expected;
        using (var rawMs = new MemoryStream())
        {
            sparse.WriteRawToStream(rawMs);
            expected = rawMs.ToArray();
        }

        using var loaded = SparseReader.FromBuffer(sparseBytes);
        using var stream = new SparseReadStream(loaded);
        var buffer = new byte[expected.Length];
        Assert.Equal(expected.Length, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(expected, buffer);
    }

    /// <summary>
    /// <para>构造 4 块测试镜像：block0 raw（带图案）、block1 fill（0xDEADBEEF）、block2 dont-care、block3 raw。</para>
    /// Builds a 4-block fixture: block 0 raw (patterned), block 1 fill (0xDEADBEEF), block 2 dont-care,
    /// block 3 raw.
    /// </summary>
    private static SparseFile BuildFixture()
    {
        var sparse = new SparseFile(BlockSize, BlockSize * 4);
        var payload = new byte[BlockSize];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 13 + 7) & 0xFF);
        }
        sparse.AddRawChunk(payload);                 // block 0
        sparse.AddFillChunk(0xDEADBEEF, BlockSize);  // block 1
        sparse.AddDontCareChunk(BlockSize);          // block 2
        sparse.AddRawChunk(BuildBytes((int)BlockSize, 0xA5)); // block 3
        return sparse;
    }

    private static byte[] BuildBytes(int length, byte fill)
    {
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = fill;
        }
        return data;
    }
}