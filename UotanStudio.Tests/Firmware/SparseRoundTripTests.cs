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
    public void ConvertRawToSparse_ZeroRunsBecomeDontCare_FileStaysSmall()
    {
        // 稀疏 super 依赖 DONT_CARE 跳过元数据区、分区间与尾部的大片全零区，因此文件很小。
        // 重打包时若把整个设备当作单个 RAW chunk 写出（不做零折叠），文件将膨胀到约等于完整
        // DeviceSize，超出原始稀疏 super，导致无法刷入。本用例固定这个不变量：
        // 全零块必须编码为 DONT_CARE，输出体积与真实非零数据成正比。
        // A sparse super stays small because DONT_CARE skips the metadata, inter-partition and
        // trailing zero regions. Emitting the whole device as one RAW chunk (no zero folding) would
        // bloat the file to ~the full DeviceSize and exceed the original sparse super, making it
        // unflashable. This case pins that invariant: zero blocks become DONT_CARE so the output
        // size scales with non-zero data, not with the device size.
        const int rawLength = 8 * 1024 * 1024; // 8 MiB
        var raw = new byte[rawLength];         // all zeros by default

        // Plant a few non-zero regions akin to a super layout: leading metadata plus two sparse
        // partition regions separated by zero padding.
        // 模拟 super 布局：前部元数据 + 两段被零填充分隔的分区数据。
        BuildBytes(BlockSize * 2, 5).CopyTo(raw, 0);
        BuildBytes(BlockSize, 31).CopyTo(raw, 4 * 1024 * 1024);
        BuildBytes(BlockSize * 2, 43).CopyTo(raw, 6 * 1024 * 1024);

        using var input = new MemoryStream(raw);
        using var output = new MemoryStream();
        SparseImageConverter.ConvertRawToSparse(input, output, blockSize: BlockSize);

        // Compact sparse output must scale with non-zero data, not the full raw size.
        Assert.True(output.Length < rawLength / 2,
            $"Sparse output {output.Length} bytes is not smaller than half the {rawLength}-byte raw.");

        // Logical bytes survive the conversion exactly.
        output.Position = 0;
        using var loaded = SparseReader.FromStream(output);
        using var expanded = new MemoryStream();
        loaded.WriteRawToStream(expanded);
        Assert.Equal(raw, expanded.ToArray());

        // Zero regions are encoded as DONT_CARE rather than RAW data.
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
        // 每个分片携带绝对块地址并保留完整 total_blks（对齐上游 sparse_file_resparse）；
        // bootloader 按绝对块地址把各分片的 RAW/FILL chunk 写入分区，逐片 flash 累积成完整镜像。
        // Each piece carries absolute block addresses and the full total_blks (mirroring upstream
        // sparse_file_resparse); the bootloader writes each piece's RAW/FILL chunks at their
        // absolute block addresses, so flashing pieces sequentially accumulates the full image.
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
                sparse.WriteToStream(fs, sparse: true);
            }

            // ResparseImage 直接流式切分，不经整张 raw 的内存中转。
            // ResparseImage splits directly from the source without a whole-raw MemoryStream.
            SparseImageConverter.ResparseImage(input, Path.Combine(tmpDir, "part{0}.img"), BlockSize * 4);

            byte[] expected;
            using (var rawMs = new MemoryStream())
            {
                sparse.WriteRawToStream(rawMs);
                expected = rawMs.ToArray();
            }

            var parts = Directory.EnumerateFiles(tmpDir, "part*.img").OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.Equal(2, parts.Count);

            // 模拟 bootloader：逐分片按绝对块地址累积 RAW/FILL 数据，得到完整分区内容。
            // Simulate the bootloader: accumulate RAW/FILL data across pieces at absolute block
            // addresses to obtain the complete partition content.
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
        // 回归测试：输出分片除逻辑数据外还携带稀疏头/块头开销，切分时必须预留，
        // 否则单个分片文件会超过 maxFileSize，刷写下载时触发
        // "download payload ... exceeds max-download-size"。
        // Regression: an output piece also carries sparse/chunk header overhead; the split must
        // reserve it, otherwise a single piece file exceeds maxFileSize and the download fails with
        // "download payload ... exceeds max-download-size" when flashing.
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

            // 模拟 bootloader：逐分片按绝对块地址累积 RAW/FILL 数据，得到完整分区内容。
            // Simulate the bootloader: accumulate RAW/FILL data across pieces at absolute block
            // addresses to obtain the complete partition content.
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
        // 上游 sparse_file_resparse 语义：FILL chunk 类型保留、分片 chunk 携带绝对块地址、
        // 空隙由写入端补 DONT_CARE；bootloader 依赖这些不变量把多次 flash 写到正确偏移。
        // Upstream sparse_file_resparse semantics: FILL chunk types are preserved, piece chunks
        // carry absolute block addresses, and gaps become DONT_CARE on write; bootloaders rely on
        // these invariants to land each flash at the correct partition offset.
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
                sparse.WriteToStream(fs, sparse: true);
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

            // 逐分片按绝对块地址累积后与完整 raw 一致。
            // Accumulating pieces at absolute block addresses matches the complete raw.
            Assert.Equal(expected, FlashPiecesInOrder(parts, BlockSize * 10));

            // 首片保留 FILL chunk（含原填充值）与 DONT_CARE chunk。
            // The first piece keeps the FILL chunk (with the original fill value) and the
            // DONT_CARE chunk.
            using var first = SparseReader.FromImageFile(parts[0]);
            Assert.Contains(first.Chunks, c => (ChunkType)c.Header.ChunkType == ChunkType.Fill && c.FillValue == 0xDEADBEEF);
            Assert.Contains(first.Chunks, c => (ChunkType)c.Header.ChunkType == ChunkType.DontCare);

            // RAW chunk 携带绝对块地址：第二段 5 块 RAW 数据出现在后续分片且起始块仍为 5，
            // 而不是分片本地地址 0；否则逐次 flash 会反复覆盖分区前缀。
            // RAW chunks carry absolute block addresses: the second 5-block RAW payload appears in a
            // later piece still starting at block 5 rather than a piece-local block 0; otherwise
            // repeated flashes would overwrite the start of the partition.
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
                            // 必须写入分区缓冲的 Span 切片：数组的 range 索引会复制出新数组，
                            // 用其作为目标会让写入丢失，组装结果恒为全零。
                            // Must write into a Span slice of the partition buffer: an array range
                            // index copies into a new array, so writing to it silently discards the
                            // data and the assembled image stays zeroed.
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