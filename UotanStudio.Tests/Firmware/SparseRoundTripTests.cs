using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Android sparse image round-trip tests: build a minimal sparse file in memory
/// (header + raw / fill / dont-care chunks), serialize it, read it back and verify
/// the logical image bytes and header fields are preserved.
/// <para>Android 稀疏镜像往返测试：在内存中构造最小稀疏文件（头部 + raw/fill/dont-care
/// 数据块），序列化后读回，验证逻辑镜像字节与头部字段一致。</para>
/// </summary>
public class SparseRoundTripTests
{
    private const uint BlockSize = 4096;

    [Fact]
    public void ConvertRawToSparse_RejectsInvalidBlockSize()
    {
        using var input = new MemoryStream(new byte[8192]);
        using var output = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() => SparseImageConverter.ConvertRawToSparse(input, output, blockSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SparseImageConverter.ConvertRawToSparse(input, output, blockSize: 6));
    }

    [Fact]
    public void ConvertRawToSparse_AcceptsMultipleOfFourBlockSize()
    {
        var payload = new byte[BlockSize * 3];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 7) & 0xFF);
        }

        using var input = new MemoryStream(payload);
        using var output = new MemoryStream();
        SparseImageConverter.ConvertRawToSparse(input, output, blockSize: 2048);

        output.Position = 0;
        using var loaded = SparseReader.FromStream(output);
        Assert.Equal(2048u, loaded.Header.BlockSize);
        Assert.Equal((uint)(payload.Length / 2048), loaded.Header.TotalBlocks);
    }

    [Fact]
    public void WriteThenRead_RoundTripsChunksAndLogicalData()
    {
        using var sparse = new SparseFile(BlockSize, BlockSize * 8);

        var payload = new byte[BlockSize];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 13) & 0xFF);
        }

        sparse.AddRawChunk(payload);                        // block 0
        sparse.AddFillChunk(0xAABBCCDD, BlockSize);         // block 1
        sparse.AddDontCareChunk(BlockSize);                 // block 2
        sparse.AddRawChunk(BuildBytes(BlockSize, 5));       // block 3

        byte[] sparseBytes;
        using (var ms = new MemoryStream())
        {
            sparse.WriteToStream(ms, sparse: true);
            sparseBytes = ms.ToArray();
        }

        Assert.Equal(SparseFormat.SparseHeaderMagic, BinaryPrimitives.ReadUInt32LittleEndian(sparseBytes));

        using var loaded = SparseReader.FromBuffer(sparseBytes);
        Assert.Equal(sparse.Header.BlockSize, loaded.Header.BlockSize);
        Assert.Equal(sparse.Header.TotalBlocks, loaded.Header.TotalBlocks);
        Assert.Equal(sparse.Header.MajorVersion, loaded.Header.MajorVersion);

        // The un-sparsified logical bytes must match exactly.
        byte[] rawOriginal;
        using (var rawMs = new MemoryStream())
        {
            sparse.WriteRawToStream(rawMs);
            rawOriginal = rawMs.ToArray();
        }

        byte[] rawLoaded;
        using (var rawMs = new MemoryStream())
        {
            loaded.WriteRawToStream(rawMs);
            rawLoaded = rawMs.ToArray();
        }

        Assert.Equal(rawOriginal, rawLoaded);
    }

    [Fact]
    public void SparseWriter_WithCrc32_ReadsBack()
    {
        using var sparse = new SparseFile(BlockSize, BlockSize * 2);
        sparse.AddRawChunk(BuildBytes(BlockSize, 9));
        sparse.AddFillChunk(0x12345678, BlockSize);

        byte[] sparseBytes;
        using (var ms = new MemoryStream())
        {
            sparse.WriteToStream(ms, sparse: true, includeCrc: true);
            sparseBytes = ms.ToArray();
        }

        // validateCrc = true exercises the trailing CRC32 chunk verification.
        using var loaded = SparseReader.FromBuffer(sparseBytes, validateCrc: true);

        byte[] rawOriginal;
        using (var rawMs = new MemoryStream())
        {
            sparse.WriteRawToStream(rawMs);
            rawOriginal = rawMs.ToArray();
        }

        byte[] rawLoaded;
        using (var rawMs = new MemoryStream())
        {
            loaded.WriteRawToStream(rawMs);
            rawLoaded = rawMs.ToArray();
        }

        Assert.Equal(rawOriginal, rawLoaded);
    }

    [Fact]
    public void RoundTripThroughRawImage_KeepsPayload()
    {
        using var sparse = new SparseFile(BlockSize, BlockSize * 2);
        var payload = BuildBytes(BlockSize, 21);
        sparse.AddRawChunk(payload);

        byte[] sparseBytes;
        using (var ms = new MemoryStream())
        {
            sparse.WriteToStream(ms, sparse: true);
            sparseBytes = ms.ToArray();
        }

        using var loaded = SparseReader.FromBuffer(sparseBytes);
        using var rawMs = new MemoryStream();
        loaded.WriteRawToStream(rawMs);
        var raw = rawMs.ToArray();

        // First block is the payload; the second block (total blocks 2) is absent from output.
        Assert.Equal(payload, raw.AsSpan(0, payload.Length).ToArray());
    }

    [Fact]
    public void FromImageFile_StreamsRawChunks_WithoutMaterializing()
    {
        using var sparse = new SparseFile(BlockSize, BlockSize * 4);
        var payload = BuildBytes(BlockSize * 3, 7);
        sparse.AddRawChunk(payload);
        sparse.AddFillChunk(0xDEADBEEF, BlockSize);

        var tmp = Path.Combine(Path.GetTempPath(), $"sparse_stream_{Guid.NewGuid():N}.img");
        try
        {
            using (var fs = File.Create(tmp))
            {
                sparse.WriteToStream(fs, sparse: true);
            }

            // 从磁盘加载：RAW 块应保持流式引用源文件范围，而非物化为 MemoryDataProvider。
            // Loading from disk must keep the RAW chunk backed by a stream range, not materialize it.
            using var loaded = SparseReader.FromImageFile(tmp);
            var rawChunk = loaded.Chunks.Single(c => c.Header.ChunkType == (ushort)ChunkType.Raw);
            Assert.IsType<StreamDataProvider>(rawChunk.DataProvider);
            Assert.Equal(BlockSize * 3, rawChunk.DataProvider!.Length);

            byte[] rawOriginal;
            using (var rawMs = new MemoryStream())
            {
                sparse.WriteRawToStream(rawMs);
                rawOriginal = rawMs.ToArray();
            }

            byte[] rawLoaded;
            using (var rawMs = new MemoryStream())
            {
                loaded.WriteRawToStream(rawMs);
                rawLoaded = rawMs.ToArray();
            }

            Assert.Equal(rawOriginal, rawLoaded);
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
    public void FromImageFile_WithCrc32_ValidatesStreaming()
    {
        using var sparse = new SparseFile(BlockSize, BlockSize * 3);
        sparse.AddRawChunk(BuildBytes(BlockSize * 2, 3));
        sparse.AddFillChunk(0x11223344, BlockSize);

        var tmp = Path.Combine(Path.GetTempPath(), $"sparse_crc_stream_{Guid.NewGuid():N}.img");
        try
        {
            using (var fs = File.Create(tmp))
            {
                sparse.WriteToStream(fs, sparse: true, includeCrc: true);
            }

            // validateCrc 在加载时对 RAW 块做池化增量 CRC，且不物化数据。
            // validateCrc computes the CRC incrementally over pooled buffers without materializing.
            using var loaded = SparseReader.FromImageFile(tmp, validateCrc: true);

            byte[] rawOriginal;
            using (var rawMs = new MemoryStream())
            {
                sparse.WriteRawToStream(rawMs);
                rawOriginal = rawMs.ToArray();
            }

            byte[] rawLoaded;
            using (var rawMs = new MemoryStream())
            {
                loaded.WriteRawToStream(rawMs);
                rawLoaded = rawMs.ToArray();
            }

            Assert.Equal(rawOriginal, rawLoaded);
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
    public void ResparseImage_SplitsIntoStreamingPieces_AndReassembles()
    {
        using var sparse = new SparseFile(BlockSize, BlockSize * 6);
        sparse.AddRawChunk(BuildBytes(BlockSize * 3, 11));
        sparse.AddFillChunk(0xCAFEBABE, BlockSize * 3);

        var tmpDir = Path.Combine(Path.GetTempPath(), $"sparse_resp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var input = Path.Combine(tmpDir, "input.img");
        try
        {
            using (var fs = File.Create(input))
            {
                sparse.WriteToStream(fs, sparse: true);
            }

            // ResparseImage 现在直接流式切分，不再经 MemoryStream 中转整张 raw。
            // ResparseImage now splits directly from the source without a whole-raw MemoryStream.
            SparseImageConverter.ResparseImage(input, Path.Combine(tmpDir, "part{0}.img"), BlockSize * 4);

            byte[] expected;
            using (var rawMs = new MemoryStream())
            {
                sparse.WriteRawToStream(rawMs);
                expected = rawMs.ToArray();
            }

            var parts = Directory.EnumerateFiles(tmpDir, "part*.img").OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.Equal(2, parts.Count);

            using var combined = new MemoryStream();
            foreach (var part in parts)
            {
                using var split = SparseReader.FromImageFile(part);
                split.WriteRawToStream(combined);
            }

            Assert.Equal(expected, combined.ToArray());
        }
        finally
        {
            if (Directory.Exists(tmpDir))
            {
                Directory.Delete(tmpDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ResparseImage_PiecesStayWithinMaxFileSize_AndReassemble()
    {
        // 回归测试：输出分片除逻辑数据外还携带稀疏头/块头（共 40 字节）开销，
        // 切分时必须预留，否则单个分片文件会超过 maxFileSize，刷写下载时触发
        // "download payload ... exceeds max-download-size"。
        // Regression: an output piece also carries sparse/chunk header overhead (40 bytes); the split must
        // reserve it, otherwise a single piece file exceeds maxFileSize and the download fails with
        // "download payload ... exceeds max-download-size" when flashing.
        using var sparse = new SparseFile(BlockSize, BlockSize * 6);
        sparse.AddRawChunk(BuildBytes(BlockSize * 3, 13));
        sparse.AddFillChunk(0x5A5A5A5A, BlockSize * 3);

        var tmpDir = Path.Combine(Path.GetTempPath(), $"sparse_resp_bounded_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var input = Path.Combine(tmpDir, "input.img");
        try
        {
            using (var fs = File.Create(input))
            {
                sparse.WriteToStream(fs, sparse: true);
            }

            var maxFileSize = BlockSize * 4;
            SparseImageConverter.ResparseImage(input, Path.Combine(tmpDir, "part{0}.img"), maxFileSize);

            byte[] expected;
            using (var rawMs = new MemoryStream())
            {
                sparse.WriteRawToStream(rawMs);
                expected = rawMs.ToArray();
            }

            var parts = Directory.EnumerateFiles(tmpDir, "part*.img").OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.NotEmpty(parts);
            foreach (var part in parts)
            {
                var length = new FileInfo(part).Length;
                Assert.True(length <= maxFileSize, $"Output piece {Path.GetFileName(part)} is {length} bytes which exceeds {maxFileSize}");
            }

            using var combined = new MemoryStream();
            foreach (var part in parts)
            {
                using var split = SparseReader.FromImageFile(part);
                split.WriteRawToStream(combined);
            }

            Assert.Equal(expected, combined.ToArray());
        }
        finally
        {
            if (Directory.Exists(tmpDir))
            {
                Directory.Delete(tmpDir, recursive: true);
            }
        }
    }

    private static byte[] BuildBytes(uint length, int factor)
    {
        var data = new byte[length];
        for (var i = 0u; i < length; i++)
        {
            data[i] = (byte)((i * (uint)factor) & 0xFF);
        }

        return data;
    }
}