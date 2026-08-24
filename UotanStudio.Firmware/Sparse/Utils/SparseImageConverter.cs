namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// Utility class for sparse image format conversion operations.
/// <para>稀疏镜像格式转换操作的实用工具类。</para>
/// </summary>
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
    public static void ConvertRawToSparse(Stream input, Stream output, uint blockSize = 4096, long? length = null)
    {
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
    /// <param name="inputFile">The path to the input sparse image file. <para>输入稀疏镜像文件路径。</para></param>
    /// <param name="outputPattern">Output path pattern; may include {0} for the index. <para>输出文件路径模式;可包含 {0} 作为索引占位符。</para></param>
    /// <param name="maxFileSize">Maximum size in bytes for each split file. <para>每个拆分文件的最大字节数。</para></param>
    public static void ResparseImage(string inputFile, string outputPattern, long maxFileSize)
    {
        using var sparseFile = SparseReader.FromImageFile(inputFile);
        var rawStream = new MemoryStream();
        sparseFile.WriteRawToStream(rawStream);
        rawStream.Position = 0;

        var blockSize = sparseFile.Header.BlockSize;
        var splitCount = (int)((rawStream.Length + maxFileSize - 1) / maxFileSize);

        for (var i = 0; i < splitCount; i++)
        {
            var start = i * maxFileSize;
            var len = Math.Min(maxFileSize, rawStream.Length - start);
            rawStream.Position = start;
            var part = new byte[len];
            rawStream.ReadExactly(part);

            var outPath = outputPattern.Contains("{0}") ? string.Format(outputPattern, i) : $"{outputPattern}.{i:D2}";
            using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
            var sparse = new SparseFile(blockSize, len);
            sparse.AddRawChunk(new MemoryDataProvider(part), (uint)((len + blockSize - 1) / blockSize));
            sparse.WriteToStream(fs, sparse: true);
        }
    }
}