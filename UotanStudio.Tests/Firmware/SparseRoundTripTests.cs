using System.Buffers.Binary;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.BlockImage.Sparse.DataProviders;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BlockImage.Sparse.Models;
using UotanStudio.Tests.Comm.TestDoubles;
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
    public void ConvertSparseToRaw_InvalidChunk_PreservesExistingDestination()
    {
        using var temp = new TempDirectory();
        var raw = temp.WriteBytes("raw.img", BuildBytes(BlockSize * 2, 9));
        var sparse = Path.Combine(temp.Path, "valid.sparse");
        SparseImageConverter.ConvertRawToSparse(raw, sparse, BlockSize);
        var invalid = temp.WriteBytes("invalid.sparse", "not sparse"u8.ToArray());
        var existing = "existing destination"u8.ToArray();
        var output = temp.WriteBytes("merged.img", existing);

        Assert.ThrowsAny<Exception>(() => SparseImageConverter.ConvertSparseToRawCancellable([sparse, invalid], output,
            TestContext.Current.CancellationToken));

        Assert.Equal(existing, File.ReadAllBytes(output));
    }

    [Fact]
    public void ConvertSparseToRaw_PreCancelled_PreservesExistingDestination()
    {
        using var temp = new TempDirectory();
        var raw = temp.WriteBytes("raw.img", BuildBytes(BlockSize * 2, 9));
        var sparse = Path.Combine(temp.Path, "valid.sparse");
        SparseImageConverter.ConvertRawToSparse(raw, sparse, BlockSize);
        var existing = "existing destination"u8.ToArray();
        var output = temp.WriteBytes("merged.img", existing);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SparseImageConverter.ConvertSparseToRawCancellable([sparse], output,
            cancellation.Token));

        Assert.Equal(existing, File.ReadAllBytes(output));
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
    public void ConvertRawToSparse_ZeroRunsBecomeDontCare_FileStaysSmall()
    {
        const int rawLength = 8 * 1024 * 1024;
        var raw = new byte[rawLength];

        BuildBytes(BlockSize * 2, 5).CopyTo(raw, 0);
        BuildBytes(BlockSize, 31).CopyTo(raw, 4 * 1024 * 1024);
        BuildBytes(BlockSize * 2, 43).CopyTo(raw, 6 * 1024 * 1024);

        using var input = new MemoryStream(raw);
        using var output = new MemoryStream();
        SparseImageConverter.ConvertRawToSparse(input, output, blockSize: BlockSize);

        Assert.True(output.Length < rawLength / 2,
            $"Sparse output {output.Length} bytes is not smaller than half the {rawLength}-byte raw.");

        output.Position = 0;
        using var loaded = SparseReader.FromStream(output);
        using var expanded = new MemoryStream();
        loaded.WriteRawToStream(expanded);
        Assert.Equal(raw, expanded.ToArray());

        Assert.Contains(loaded.Chunks, c => (ChunkType)c.Header.ChunkType == ChunkType.DontCare);
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

        sparse.AddRawChunk(payload);
        sparse.AddFillChunk(0xAABBCCDD, BlockSize);
        sparse.AddDontCareChunk(BlockSize);
        sparse.AddRawChunk(BuildBytes(BlockSize, 5));

        byte[] sparseBytes;
        using (var ms = new MemoryStream())
        {
            sparse.WriteToStream(ms, sparse: true, cancellationToken: TestContext.Current.CancellationToken);
            sparseBytes = ms.ToArray();
        }

        Assert.Equal(SparseFormat.SparseHeaderMagic, BinaryPrimitives.ReadUInt32LittleEndian(sparseBytes));

        using var loaded = SparseReader.FromBuffer(sparseBytes);
        Assert.Equal(sparse.Header.BlockSize, loaded.Header.BlockSize);
        Assert.Equal(sparse.Header.TotalBlocks, loaded.Header.TotalBlocks);
        Assert.Equal(sparse.Header.MajorVersion, loaded.Header.MajorVersion);

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
            sparse.WriteToStream(ms, sparse: true, includeCrc: true, cancellationToken: TestContext.Current.CancellationToken);
            sparseBytes = ms.ToArray();
        }

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
            sparse.WriteToStream(ms, sparse: true, cancellationToken: TestContext.Current.CancellationToken);
            sparseBytes = ms.ToArray();
        }

        using var loaded = SparseReader.FromBuffer(sparseBytes);
        using var rawMs = new MemoryStream();
        loaded.WriteRawToStream(rawMs);
        var raw = rawMs.ToArray();

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
                sparse.WriteToStream(fs, sparse: true, cancellationToken: TestContext.Current.CancellationToken);
            }

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
                sparse.WriteToStream(fs, sparse: true, includeCrc: true, cancellationToken: TestContext.Current.CancellationToken);
            }

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
        sparse.AddRawChunk(BuildBytes(BlockSize * 3, 23));

        var tmpDir = Path.Combine(Path.GetTempPath(), "sparse_resp_probe");
        Directory.CreateDirectory(tmpDir);
        var input = Path.Combine(tmpDir, "input.img");
        try
        {
            using (var fs = File.Create(input))
            {
                sparse.WriteToStream(fs, sparse: true, cancellationToken: TestContext.Current.CancellationToken);
            }

            SparseImageConverter.ResparseImage(input, Path.Combine(tmpDir, "part{0}.img"), BlockSize * 4);

            byte[] expected;
            using (var rawMs = new MemoryStream())
            {
                sparse.WriteRawToStream(rawMs);
                expected = rawMs.ToArray();
            }

            var parts = Directory.EnumerateFiles(tmpDir, "part*.img").OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.Equal(2, parts.Count);

            Assert.Equal(expected, FlashPiecesInOrder(parts, (uint)(BlockSize * 6)));
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
        using var sparse = new SparseFile(BlockSize, BlockSize * 9);
        sparse.AddRawChunk(BuildBytes(BlockSize * 3, 13));
        sparse.AddFillChunk(0x5A5A5A5A, BlockSize * 3);
        sparse.AddRawChunk(BuildBytes(BlockSize * 3, 29));

        var tmpDir = Path.Combine(Path.GetTempPath(), $"sparse_resp_bounded_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var input = Path.Combine(tmpDir, "input.img");
        try
        {
            using (var fs = File.Create(input))
            {
                sparse.WriteToStream(fs, sparse: true, cancellationToken: TestContext.Current.CancellationToken);
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

            Assert.Equal(expected, FlashPiecesInOrder(parts, BlockSize * 9));
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
    public void ResparseImage_PreservesChunkTypes_AndAbsoluteBlockAddresses()
    {
        using var sparse = new SparseFile(BlockSize, BlockSize * 10);
        sparse.AddRawChunk(BuildBytes(BlockSize * 2, 7));
        sparse.AddDontCareChunk(BlockSize * 1);
        sparse.AddFillChunk(0xDEADBEEF, BlockSize * 2);
        sparse.AddRawChunk(BuildBytes(BlockSize * 5, 31));

        var tmpDir = Path.Combine(Path.GetTempPath(), $"sparse_resp_types_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        var input = Path.Combine(tmpDir, "input.img");
        try
        {
            using (var fs = File.Create(input))
            {
                sparse.WriteToStream(fs, sparse: true, cancellationToken: TestContext.Current.CancellationToken);
            }

            SparseImageConverter.ResparseImage(input, Path.Combine(tmpDir, "part{0}.img"), BlockSize * 4);

            byte[] expected;
            using (var rawMs = new MemoryStream())
            {
                sparse.WriteRawToStream(rawMs);
                expected = rawMs.ToArray();
            }

            var parts = Directory.EnumerateFiles(tmpDir, "part*.img").OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.True(parts.Count >= 2, "Expected the 10-block image to split under a 4-block budget.");

            foreach (var part in parts)
            {
                using var split = SparseReader.FromImageFile(part);
                Assert.Equal((uint)10, split.Header.TotalBlocks);
            }

            Assert.Equal(expected, FlashPiecesInOrder(parts, BlockSize * 10));

            using var first = SparseReader.FromImageFile(parts[0]);
            Assert.Contains(first.Chunks, c => (ChunkType)c.Header.ChunkType == ChunkType.Fill && c.FillValue == 0xDEADBEEF);
            Assert.Contains(first.Chunks, c => (ChunkType)c.Header.ChunkType == ChunkType.DontCare);

            var rawStarts = new List<uint>();
            foreach (var part in parts)
            {
                using var split = SparseReader.FromImageFile(part);
                rawStarts.AddRange(split.Chunks
                    .Where(c => (ChunkType)c.Header.ChunkType == ChunkType.Raw)
                    .Select(c => c.StartBlock));
            }

            Assert.Contains(0u, rawStarts);
            Assert.Contains(5u, rawStarts);
        }
        finally
        {
            if (Directory.Exists(tmpDir))
            {
                Directory.Delete(tmpDir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Simulates the bootloader consuming resparse pieces: for each piece in order, RAW/FILL
    /// chunks are written into a partition buffer at their absolute block addresses, and
    /// DONT_CARE chunks (and the write cursor) are skipped — the ABOOT/fastbootd semantics the
    /// upstream resparse invariants target.
    /// <para>模拟 bootloader 消费重稀疏化分片：按序把每个分片的 RAW/FILL chunk 按绝对块地址
    /// 写入分区缓冲，DONT_CARE（及写入游标）被跳过——即上游重稀疏化不变量针对的
    /// ABOOT/fastbootd 语义。</para>
    /// </summary>
    /// <param name="pieces"><para>按刷写顺序排列的分片文件。</para>The piece files in flash order.</param>
    /// <param name="partitionSize"><para>分区逻辑容量（字节）。</para>The partition's logical capacity in bytes.</param>
    private static byte[] FlashPiecesInOrder(IReadOnlyList<string> pieces, long partitionSize)
    {
        var partition = new byte[partitionSize];
        foreach (var piece in pieces)
        {
            using var sparse = SparseReader.FromImageFile(piece);
            foreach (var chunk in sparse.Chunks)
            {
                switch ((ChunkType)chunk.Header.ChunkType)
                {
                    case ChunkType.Raw when chunk.DataProvider is not null:
                        {
                            var dst = partition.AsSpan((int)(chunk.StartBlock * BlockSize));
                            var filled = 0;
                            while (filled < chunk.Header.ChunkSize * BlockSize)
                            {
                                var read = chunk.DataProvider.Read(filled, dst[filled..]);
                                if (read <= 0)
                                {
                                    break;
                                }
                                filled += read;
                            }
                            Assert.Equal((long)chunk.Header.ChunkSize * BlockSize, filled);
                            break;
                        }
                    case ChunkType.Fill:
                        SparseFill.Fill(partition.AsSpan((int)(chunk.StartBlock * BlockSize), (int)(chunk.Header.ChunkSize * BlockSize)), chunk.FillValue);
                        break;
                }
            }
        }
        return partition;
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