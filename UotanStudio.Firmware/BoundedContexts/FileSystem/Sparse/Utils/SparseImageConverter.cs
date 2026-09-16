namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Sparse;

public static class SparseImageConverter
{
    /// <summary>
    /// Converts one or more sparse images to a raw image file.
    /// <para>将一个或多个稀疏镜像转换为原始镜像文件。</para>
    /// </summary>
    public static void ConvertSparseToRaw(IEnumerable<string> inputFiles, string outputFile)
    {
        using var outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
        long maxFileSize = 0;

        foreach (var inputFile in inputFiles)
        {
            var header = SparseReader.PeekHeader(inputFile);
            var fileSize = (long)header.TotalBlocks * header.BlockSize;
            if (fileSize > maxFileSize)
            {
                maxFileSize = fileSize;
            }
        }

        if (maxFileSize > 0)
        {
            outputStream.SetLength(maxFileSize);
        }

        foreach (var inputFile in inputFiles)
        {
            using var sparseFile = SparseReader.FromImageFile(inputFile);
            sparseFile.WriteRawToStream(outputStream);
        }

        outputStream.Flush();
    }

    /// <summary>
    /// Converts a raw image file to a sparse image file.
    /// <para>将原始镜像文件转换为稀疏镜像文件。</para>
    /// </summary>
    public static void ConvertRawToSparse(string inputFile, string outputFile, uint blockSize = 4096)
    {
        using var inputStream = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None);
        ConvertRawToSparse(inputStream, outputStream, blockSize, inputStream.Length);
    }

    /// <summary>
    /// Converts a raw image stream to a sparse image stream.
    /// <para>将原始镜像流转换为稀疏镜像流。</para>
    /// </summary>
    /// <param name="input">The source raw stream. 源原始流。</param>
    /// <param name="output">The destination sparse stream. 目标稀疏流。</param>
    /// <param name="blockSize">Sparse block size; must be a positive multiple of 4 per the
    /// Android sparse format. 稀疏块大小；须为 Android 稀疏格式要求的 4 的正整数倍。</param>
    /// <param name="length">Explicit source length; when omitted uses <paramref name="input"/>.Length.
    /// 显式源长度；省略时使用 <paramref name="input"/> 的 Length。</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="blockSize"/> is not
    /// a positive multiple of 4, which would make the bootloader misparse the sparse stream.
    /// 当 <paramref name="blockSize"/> 不是 4 的正整数倍时抛出，此时 bootloader 会错解析稀疏流。</exception>
    public static void ConvertRawToSparse(Stream input, Stream output, uint blockSize = 4096, long? length = null)
    {
        if (blockSize == 0 || blockSize % 4 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                "Sparse block size must be a positive multiple of 4 per the Android sparse format. 稀疏块大小必须是 Android 稀疏格式要求的 4 的正整数倍。");
        }

        var totalLength = length ?? input.Length;
        var totalBlocks = (uint)((totalLength + blockSize - 1) / blockSize);
        var sparseFile = new SparseFile(blockSize, totalLength);
        sparseFile.AddRawChunk(new StreamDataProvider(input, input.Position, totalLength), totalBlocks);
        sparseFile.WriteToStream(output, sparse: true);
    }

    /// <summary>
    /// Splits a large sparse image into multiple sparse images of a specified maximum size,
    /// mirroring AOSP libsparse <c>sparse_file_resparse</c> semantics.
    /// <para>按 AOSP libsparse <c>sparse_file_resparse</c> 的语义把大型稀疏镜像拆分为
    /// 指定最大大小的多个稀疏镜像。</para>
    /// </summary>
    /// <remarks>
    /// <para>与上游一致的关键不变量：每个输出分片的头部保留原始镜像的完整 <c>total_blks</c>，
    /// chunk 携带绝对块地址，chunk 之间的空隙由写入端补 DONT_CARE（skip），FILL/RAW chunk
    /// 类型原样保留（CRC 除外，读取阶段已丢弃）。bootloader 对每次 <c>flash:</c> 从分区偏移 0
    /// 开始顺序消费 chunk，前导 DONT_CARE 把写入偏移推进到本分片的数据块，因此官方 fastboot
    /// 对每个分片单独 download+flash（<c>flash_partition_files</c>）能落到正确位置。若分片改用
    /// 分片本地寻址（每片从块 0 开始），多次 flash 会反复覆盖分区前缀导致数据错位。</para>
    /// </remarks>
    public static void ResparseImage(string inputFile, string outputPattern, long maxFileSize)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputFile);
        ArgumentException.ThrowIfNullOrEmpty(outputPattern);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileSize, 1);

        using var sparseFile = SparseReader.FromImageFile(inputFile);
        var blockSize = sparseFile.Header.BlockSize;
        var blockBytes = (long)blockSize;
        var totalLength = checked((long)sparseFile.Header.TotalBlocks * blockBytes);
        if (totalLength == 0)
        {
            return;
        }

        // 与上游 move_chunks_up_to_len 相同的分片预算：除逻辑数据外，每个分片还携带稀疏头(28)、
        // 收尾 DONT_CARE 块头(12)与可选 CRC 块头+CRC(16)。
        // Piece budget mirrors upstream move_chunks_up_to_len: besides logical data each piece
        // carries the sparse header (28), a potential trailing skip chunk header (12) and the
        // optional CRC chunk (16).
        var perPieceOverhead = (long)SparseFormat.SparseHeaderSize + 2 * SparseFormat.ChunkHeaderSize + sizeof(uint);
        var usable = maxFileSize - perPieceOverhead;
        ArgumentOutOfRangeException.ThrowIfLessThan(usable, SparseFormat.ChunkHeaderSize + blockBytes, nameof(maxFileSize));
        var maxPieceDataBlocks = (usable - SparseFormat.ChunkHeaderSize) / blockBytes;

        // 预切分超大 RAW chunk 为不超过 maxPieceDataBlocks 的工作项，打包阶段不再二次切分。
        // Pre-split oversized RAW chunks into work items of at most maxPieceDataBlocks blocks so
        // the packing stage never needs to split again.
        var work = new List<WorkItem>();
        foreach (var chunk in sparseFile.Chunks)
        {
            switch ((ChunkType)chunk.Header.ChunkType)
            {
                case ChunkType.Raw when chunk.DataProvider is not null:
                    {
                        var remainingBlocks = chunk.Header.ChunkSize;
                        var dataOffset = 0L;
                        while (remainingBlocks > 0)
                        {
                            var blocks = (uint)Math.Min(remainingBlocks, maxPieceDataBlocks);
                            work.Add(new WorkItem(chunk, checked(chunk.StartBlock + (uint)(dataOffset / blockBytes)), blocks, dataOffset));
                            dataOffset = checked(dataOffset + blocks * blockBytes);
                            remainingBlocks -= blocks;
                        }
                        break;
                    }
                case ChunkType.Fill or ChunkType.DontCare:
                    work.Add(new WorkItem(chunk, chunk.StartBlock, chunk.Header.ChunkSize, 0));
                    break;
            }
        }

        var pieceIndex = 0;
        var pieceItems = new List<WorkItem>();
        long pieceFileLen = 0;
        uint lastEndBlock = 0;

        void FlushPiece()
        {
            if (pieceItems.Count == 0)
            {
                return;
            }

            var outPath = outputPattern.Contains("{0}") ? string.Format(outputPattern, pieceIndex) : $"{outputPattern}.{pieceIndex:D2}";
            var piece = new SparseFile(blockSize, totalLength);
            foreach (var item in pieceItems)
            {
                switch ((ChunkType)item.Source.Header.ChunkType)
                {
                    case ChunkType.Raw:
                        piece.AddRawChunk(
                            item.Source.DataProvider!.GetSubProvider(item.DataOffset, checked(item.Blocks * blockBytes)),
                            item.Blocks, item.StartBlock);
                        break;
                    case ChunkType.Fill:
                        piece.AddFillChunk(item.Source.FillValue, checked(item.Blocks * blockBytes), item.StartBlock);
                        break;
                    case ChunkType.DontCare:
                        piece.AddDontCareChunk(checked(item.Blocks * blockBytes), item.StartBlock);
                        break;
                }
            }

            using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
            piece.WriteToStream(fs, sparse: true);
            pieceIndex++;
            pieceItems.Clear();
            pieceFileLen = 0;
            lastEndBlock = 0;
        }

        foreach (var item in work)
        {
            // 分片内空隙同样由写入端补 DONT_CARE，只占一个块头，计入预算。
            // Gaps inside a piece also become DONT_CARE chunks on write; only the chunk header
            // is charged to the budget.
            var gapHeader = item.StartBlock > lastEndBlock ? (long)SparseFormat.ChunkHeaderSize : 0;
            var dataBytes = (ChunkType)item.Source.Header.ChunkType == ChunkType.Fill
                ? (long)sizeof(uint)
                : item.Blocks * blockBytes;
            var cost = checked(gapHeader + SparseFormat.ChunkHeaderSize + dataBytes);
            if (pieceFileLen + cost > usable)
            {
                FlushPiece();
                gapHeader = item.StartBlock > 0 ? (long)SparseFormat.ChunkHeaderSize : 0;
                cost = checked(gapHeader + SparseFormat.ChunkHeaderSize + dataBytes);
            }

            pieceItems.Add(item);
            pieceFileLen += cost;
            lastEndBlock = checked(item.StartBlock + item.Blocks);
        }

        FlushPiece();
    }

    private sealed record WorkItem(SparseChunk Source, uint StartBlock, uint Blocks, long DataOffset);
}