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
    /// Splits a large sparse image into multiple sparse images of a specified maximum size.
    /// <para>将大型稀疏镜像拆分为指定最大大小的多个稀疏镜像。</para>
    /// </summary>
    public static void ResparseImage(string inputFile, string outputPattern, long maxFileSize)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputFile);
        ArgumentException.ThrowIfNullOrEmpty(outputPattern);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileSize, 1);

        using var sparseFile = SparseReader.FromImageFile(inputFile);
        var blockSize = sparseFile.Header.BlockSize;
        var totalLength = checked((long)sparseFile.Header.TotalBlocks * blockSize);

        // 输出分片除逻辑数据外还带稀疏头(28)与块头(12)共 40 字节开销；按逻辑长度直接切到
        // maxFileSize 会使文件实际长度 = maxFileSize + 40，刷写下载时超过设备 max-download-size。
        // 因此先扣掉头部开销并做块对齐，保证每个输出分片文件长度 ≤ maxFileSize。
        // Besides the logical data, an output piece carries the sparse header (28) plus a chunk
        // header (12), 40 bytes in total; splitting to maxFileSize by logical length yields an actual
        // file of maxFileSize + 40 which exceeds the device max-download-size on flash. So reserve
        // the header overhead and keep the size block-aligned so every output piece file is ≤ maxFileSize.
        var perChunkOverhead = (long)SparseFormat.SparseHeaderSize + SparseFormat.ChunkHeaderSize;
        var usable = maxFileSize - perChunkOverhead;
        var maxBlockCount = usable > 0 ? usable / blockSize : 0;
        if (maxBlockCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileSize), "Maximum size is too small for a single sparse chunk.");
        }

        var maxLogical = checked(maxBlockCount * blockSize);
        var splitCount = totalLength == 0 ? 0 : checked(((totalLength - 1) / maxLogical) + 1);

        for (long i = 0; i < splitCount; i++)
        {
            var start = checked(i * maxLogical);
            var len = Math.Min(maxLogical, totalLength - start);

            var outPath = outputPattern.Contains("{0}") ? string.Format(outputPattern, i) : $"{outputPattern}.{i:D2}";
            using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
            var sparse = new SparseFile(blockSize, len);
            var splitBlocks = checked((uint)(checked(len + blockSize - 1L) / blockSize));
            sparse.AddRawChunk(new SparseFileRangeProvider(sparseFile, start, len), splitBlocks);
            sparse.WriteToStream(fs, sparse: true);
        }
    }
}