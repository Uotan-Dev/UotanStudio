using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UotanStudio.Firmware.BlockImage.Sparse.DataProviders;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BlockImage.Sparse.Models;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.BlockImage.Sparse;

/// <summary>
/// Entry points that convert between Android sparse images and raw images, in either direction, plus
/// the split of an oversized sparse image into flashable pieces.
/// <para>在 Android 稀疏镜像与原始镜像之间双向转换的入口，并把超出大小限制的稀疏镜像拆分为可刷写的分片。</para>
/// </summary>
public static class SparseImageConverter
{
    /// <summary>Converts one or more sparse images to a raw image file.
    /// <para>将一个或多个稀疏镜像转换为原始镜像文件。</para></summary>
    /// <param name="inputFiles">Paths of the sparse images to expand, consumed in enumeration order.
    /// <para>待展开的稀疏镜像路径，按枚举顺序依次处理。</para></param>
    /// <param name="outputFile">Destination raw image path, replaced only after the whole conversion has succeeded.
    /// <para>目标原始镜像路径；仅在整体转换成功后才会被替换。</para></param>
    /// <example>
    /// <code>SparseImageConverter.ConvertSparseToRaw(new[] { "system.sparse" }, "system.img");</code>
    /// </example>
    /// <exception cref="ArgumentException"><paramref name="inputFiles"/> is empty or <paramref name="outputFile"/> is blank.
    /// <para><paramref name="inputFiles"/> 为空，或 <paramref name="outputFile"/> 为空白。</para></exception>
    public static void ConvertSparseToRaw(IEnumerable<string> inputFiles, string outputFile)
        => ConvertSparseToRawCancellable(inputFiles, outputFile, CancellationToken.None);

    /// <summary>Converts one or more sparse image chunks to a raw image with cancellation and atomic publication.
    /// <para>在支持取消的前提下把一个或多个稀疏镜像数据块转换为原始镜像并原子发布。</para></summary>
    /// <param name="inputFiles">Paths of the sparse images to expand; each must exist and must not be the destination path.
    /// <para>待展开的稀疏镜像路径；每个都必须存在且不等于目标路径。</para></param>
    /// <param name="outputFile">Destination raw image path; its directory is created when missing and an existing file is replaced.
    /// <para>目标原始镜像路径；缺失时创建其目录，已存在的文件会被替换。</para></param>
    /// <param name="cancellationToken">Checked per input file and observed by the output stream; cancellation leaves the destination content untouched because the temporary file is discarded.
    /// <para>在每个输入文件处检查并由输出流观察；取消不会改动目标内容，因为临时文件会被丢弃。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="inputFiles"/> is <see langword="null"/>.
    /// <para><paramref name="inputFiles"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">No input path was supplied, <paramref name="outputFile"/> is blank, or an input resolves to the destination path.
    /// <para>未提供任何输入路径、<paramref name="outputFile"/> 为空白，或某个输入解析后就是目标路径。</para></exception>
    /// <exception cref="FileNotFoundException">A sparse input image does not exist.
    /// <para>某个稀疏输入镜像不存在。</para></exception>
    /// <exception cref="EndOfStreamException">An input is shorter than the 28-byte sparse header.
    /// <para>某个输入短于 28 字节的稀疏头部。</para></exception>
    /// <exception cref="InvalidDataException">An input's header or chunks fail sparse parsing while the image is expanded.
    /// <para>某个输入在展开时其头部或数据块未通过稀疏解析。</para></exception>
    /// <exception cref="IOException">An input is the output file itself, or the temporary file cannot be written.
    /// <para>输入即输出文件本身，或临时文件无法写入。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public static void ConvertSparseToRawCancellable(IEnumerable<string> inputFiles, string outputFile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        var inputs = inputFiles.Select(Path.GetFullPath).ToArray();
        if (inputs.Length == 0)
        {
            throw new ArgumentException("At least one sparse input image is required.", nameof(inputFiles));
        }

        var destination = Path.GetFullPath(outputFile);
        var destinationDirectory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(destinationDirectory);
        var temporary = Path.Combine(destinationDirectory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        long maxFileSize = 0;
        try
        {
            foreach (var inputFile in inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(inputFile))
                {
                    throw new FileNotFoundException("Sparse input image was not found.", inputFile);
                }
                if (string.Equals(inputFile, destination, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    throw new IOException("A sparse input image cannot also be the output image.");
                }
                var header = SparseReader.PeekHeader(inputFile);
                var fileSize = checked((long)header.TotalBlocks * header.BlockSize);
                if (fileSize > maxFileSize)
                {
                    maxFileSize = fileSize;
                }
            }

            using (var outputStream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
            using (var cancellable = new CountingWriteStream(outputStream, long.MaxValue, leaveOpen: true,
                       cancellationToken: cancellationToken))
            {
                if (maxFileSize > 0)
                {
                    cancellable.SetLength(maxFileSize);
                }

                foreach (var inputFile in inputs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var sparseFile = SparseReader.FromImageFile(inputFile);
                    sparseFile.WriteRawToStream(cancellable);
                }

                cancellable.Flush();
                outputStream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            AtomicPublish.Publish(temporary, destination);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
            }
        }
    }

    /// <summary>Converts a raw image file to a sparse image file.
    /// <para>将原始镜像文件转换为稀疏镜像文件。</para></summary>
    /// <param name="inputFile">Path of the raw image read from its beginning.
    /// <para>从起始位置读取的原始镜像文件路径。</para></param>
    /// <param name="outputFile">Destination sparse path, truncated in place and left partially written when the conversion fails.
    /// <para>目标稀疏路径；会被就地截断，转换失败时留下部分写入的内容。</para></param>
    /// <param name="blockSize">Logical block size used to classify zero, fill and raw runs; defaults to <c>4096</c> and must be a positive multiple of <c>4</c>. When <paramref name="options"/> is supplied, its validated <see cref="SparseWriteOptions"/> geometry additionally caps RAW chunks, appends the CRC32 chunk and pads the total block count; the classification block size stays <paramref name="blockSize"/>.
    /// <para>用于判定零块、Fill 块与 Raw 块的逻辑块大小；默认为 <c>4096</c>，必须是 <c>4</c> 的正整数倍。 提供 <paramref name="options"/> 时，其已校验的几何会额外限制 RAW 块、追加 CRC32 块并填充总块数；分类块大小仍为 <paramref name="blockSize"/>。</para></param>
    /// <param name="options">Optional write geometry; <see langword="null"/> selects the defaults of <see cref="SparseWriteOptions"/>.
    /// <para>可选写出几何；<see langword="null"/> 采用 <see cref="SparseWriteOptions"/> 的默认几何。</para></param>
    /// <example>
    /// <code>SparseImageConverter.ConvertRawToSparse("system.img", "system.sparse.img", blockSize: 4096);</code>
    /// </example>
    /// <exception cref="FileNotFoundException"><paramref name="inputFile"/> does not exist.
    /// <para><paramref name="inputFile"/> 不存在。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is not a positive multiple of <c>4</c>.
    /// <para><paramref name="blockSize"/> 不是 <c>4</c> 的正整数倍。</para></exception>
    /// <exception cref="IOException">Either path cannot be opened for the required access.
    /// <para>某个路径无法以所需访问方式打开。</para></exception>
    public static void ConvertRawToSparse(string inputFile, string outputFile, uint blockSize = 4096,
        SparseWriteOptions? options = null)
    {
        options?.Validate();
        using var inputStream = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None);
        ConvertRawToSparseCancellable(inputStream, outputStream, blockSize, inputStream.Length, CancellationToken.None, options);
    }

    /// <summary>Converts a raw image and atomically publishes a structurally validated Android Sparse image.
    /// <para>转换原始镜像并原子发布一个经过结构校验的 Android Sparse 镜像。</para></summary>
    /// <param name="inputFile">Path of the raw image; its file size at the start of the call fixes the converted logical length.
    /// <para>原始镜像路径；调用开始时的文件大小决定转换后的逻辑长度。</para></param>
    /// <param name="outputFile">Destination sparse path, replaced only after validation succeeds.
    /// <para>目标稀疏路径；仅在校验成功后才被替换。</para></param>
    /// <param name="blockSize">Logical block size in bytes; must be a positive multiple of <c>4</c>.
    /// <para>逻辑块大小（字节数）；必须是 <c>4</c> 的正整数倍。</para></param>
    /// <param name="cancellationToken">Checked before the publication and between blocks; cancellation aborts publication so the destination keeps its previous content.
    /// <para>在发布之前以及各块之间检查；取消会中止发布，目标保持原有内容。</para></param>
    /// <example>
    /// <code>SparseImageConverter.ConvertRawToSparseAtomic("vendor.img", "vendor.sparse.img", 4096, token);</code>
    /// </example>
    /// <exception cref="ArgumentException"><paramref name="inputFile"/> or <paramref name="outputFile"/> is blank.
    /// <para><paramref name="inputFile"/> 或 <paramref name="outputFile"/> 为空白。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is not a positive multiple of <c>4</c>.
    /// <para><paramref name="blockSize"/> 不是 <c>4</c> 的正整数倍。</para></exception>
    /// <exception cref="InvalidDataException">The published sparse image does not match the raw length or carries trailing data.
    /// <para>发布的稀疏镜像与原始长度不符，或尾部存在多余数据。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="IOException">The input cannot be read or the publication cannot be written.
    /// <para>无法读取输入或无法写入发布结果。</para></exception>
    public static void ConvertRawToSparseAtomic(
        string inputFile,
        string outputFile,
        uint blockSize = 4096,
        CancellationToken cancellationToken = default)
        => ConvertRawToSparseAtomic(
            inputFile,
            outputFile,
            blockSize,
            static (input, output, size, length, token) => ConvertRawToSparseCancellable(input, output, size, length, token),
            cancellationToken);

    /// <summary>Converts a raw image with explicit <see cref="SparseWriteOptions"/> geometry and atomically publishes the structurally validated result. The alignment check honours <see cref="SparseWriteOptions.FileAlignment"/>, so a padded image is validated against the aligned block count rather than the raw length alone.
    /// <para>使用显式 <see cref="SparseWriteOptions"/> 几何转换原始镜像，并原子发布经过结构校验的结果。 对齐检查遵循 <see cref="SparseWriteOptions.FileAlignment"/>，因此填充后的镜像按对齐后的块数校验，而非仅按原始长度。</para></summary>
    /// <param name="inputFile">Path of the raw image; its file size at the start of the call fixes the converted logical length.
    /// <para>原始镜像路径；调用开始时的文件大小决定转换后的逻辑长度。</para></param>
    /// <param name="outputFile">Destination sparse path, replaced only after validation succeeds.
    /// <para>目标稀疏路径；仅在校验成功后才被替换。</para></param>
    /// <param name="blockSize">Classification block size; must be a positive multiple of <c>4</c> and is not overridden by <see cref="SparseWriteOptions.BlockSize"/>.
    /// <para>分类块大小；必须是 <c>4</c> 的正整数倍，且不会被 <see cref="SparseWriteOptions.BlockSize"/> 覆盖。</para></param>
    /// <param name="options">Validated write geometry supplying the CRC chunk, RAW cap and alignment padding.
    /// <para>已校验的写出几何，提供 CRC 块、RAW 上限与对齐填充。</para></param>
    /// <param name="cancellationToken">Checked before the publication and between blocks; cancellation aborts publication.
    /// <para>在发布之前以及各块之间检查；取消会中止发布。</para></param>
    /// <exception cref="ArgumentException"><paramref name="inputFile"/> or <paramref name="outputFile"/> is blank.
    /// <para><paramref name="inputFile"/> 或 <paramref name="outputFile"/> 为空白。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is not a positive multiple of <c>4</c>, or the options are invalid.
    /// <para><paramref name="blockSize"/> 不是 <c>4</c> 的正整数倍，或选项无效。</para></exception>
    /// <exception cref="InvalidDataException">The published sparse image does not match the expected padded length or carries trailing data.
    /// <para>发布的稀疏镜像与预期的填充长度不符，或尾部存在多余数据。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="IOException">The input cannot be read or the publication cannot be written.
    /// <para>无法读取输入或无法写入发布结果。</para></exception>
    public static void ConvertRawToSparseAtomic(
        string inputFile,
        string outputFile,
        uint blockSize,
        SparseWriteOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ConvertRawToSparseAtomic(
            inputFile,
            outputFile,
            blockSize,
            (input, output, size, length, token) => ConvertRawToSparseCancellable(input, output, size, length, token, options),
            cancellationToken,
            options);
    }

    internal static void ConvertRawToSparseAtomic(
        string inputFile,
        string outputFile,
        uint blockSize,
        Action<Stream, Stream, uint, long, CancellationToken> converter,
        CancellationToken cancellationToken,
        SparseWriteOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        ArgumentNullException.ThrowIfNull(converter);
        cancellationToken.ThrowIfCancellationRequested();
        var inputPath = Path.GetFullPath(inputFile);
        var sourceLength = new FileInfo(inputPath).Length;
        FileExtraction.WriteAtomic(outputFile, output =>
        {
            using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            converter(input, output, blockSize, sourceLength, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            output.Flush(flushToDisk: true);
            ValidateSparseOutput(output, sourceLength, blockSize, options);
            cancellationToken.ThrowIfCancellationRequested();
        });
    }

    /// <summary>Converts a raw image stream to a sparse image stream.
    /// <para>将原始镜像流转换为稀疏镜像流。</para></summary>
    /// <param name="input">The source raw stream.<para>源原始流。</para></param>
    /// <param name="output">The destination sparse stream.<para>目标稀疏流。</para></param>
    /// <param name="blockSize">Sparse block size; must be a positive multiple of 4 per the Android sparse format.
    /// <para>稀疏块大小；须为 Android 稀疏格式要求的 4 的正整数倍。</para></param>
    /// <param name="length">Explicit source length; when omitted uses <paramref name="input"/>.Length.
    /// <para>显式源长度；省略时使用 <paramref name="input"/> 的 Length。</para></param>
    /// <param name="options">Optional write geometry; <see langword="null"/> reproduces the historical byte output. The classification block size remains <paramref name="blockSize"/>; options add RAW chunk caps, the trailing CRC32 chunk and total-block alignment padding.
    /// <para>可选写出几何；<see langword="null"/> 复现历史字节输出。分类块大小仍为 <paramref name="blockSize"/>； 选项额外提供 RAW 块上限、CRC32 尾块与总块数对齐填充。</para></param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="blockSize"/> is not a positive multiple of 4, which would make the sparse stream unparseable.
    /// <para>当 <paramref name="blockSize"/> 不是 4 的正整数倍时抛出，此时稀疏流会无法被正确解析。</para></exception>
    /// <exception cref="EndOfStreamException"><paramref name="input"/> holds fewer bytes than the length it is asked to convert.
    /// <para><paramref name="input"/> 实际字节数少于待转换的长度。</para></exception>
    public static void ConvertRawToSparse(Stream input, Stream output, uint blockSize = 4096, long? length = null,
        SparseWriteOptions? options = null)
        => ConvertRawToSparseCancellable(input, output, blockSize, length, CancellationToken.None, options);

    private static void ConvertRawToSparseCancellable(
        Stream input,
        Stream output,
        uint blockSize,
        long? length,
        CancellationToken cancellationToken,
        SparseWriteOptions? options = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options?.Validate();
        if (blockSize == 0 || blockSize % 4 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                "Sparse block size must be a positive multiple of 4 per the Android sparse format.");
        }

        var totalLength = length ?? input.Length;
        var totalBlocks = (uint)((totalLength + blockSize - 1) / blockSize);
        var sparseFile = new SparseFile(blockSize, totalLength);

        if (!input.CanSeek || totalBlocks == 0)
        {
            if (totalBlocks > 0)
            {
                sparseFile.AddRawChunk(new StreamDataProvider(input, input.Position, totalLength), totalBlocks);
            }
            sparseFile.WriteToStream(new CancellationWriteStream(output, cancellationToken), sparse: true,
                includeCrc: options?.EmitCrc ?? false, options: options, cancellationToken: cancellationToken);
            return;
        }

        const uint ClassifyWindowBytes = 4 * 1024 * 1024;
        var windowBlocks = Math.Max(1U, ClassifyWindowBytes / blockSize);
        var windowBytes = checked(windowBlocks * blockSize);
        var buffer = ArrayPool<byte>.Shared.Rent((int)windowBytes);
        try
        {
            const byte KindZero = 1, KindFill = 2, KindRaw = 3;
            byte kind = 0;
            var fillValue = 0u;
            var startBlock = 0u;
            var startByte = 0L;
            var blocks = 0u;

            void Flush()
            {
                if (kind == 0 || blocks == 0)
                {
                    return;
                }
                switch (kind)
                {
                    case KindZero:
                        sparseFile.AddDontCareChunk(checked(blocks * blockSize), startBlock);
                        break;
                    case KindFill:
                        sparseFile.AddFillChunk(fillValue, checked(blocks * blockSize), startBlock);
                        break;
                    default:
                        sparseFile.AddRawChunk(new StreamDataProvider(input, startByte, checked(blocks * blockSize)), blocks, startBlock);
                        break;
                }
                kind = 0;
                fillValue = 0;
                blocks = 0;
            }

            for (var windowStart = 0U; windowStart < totalBlocks;)
            {
                var windowCount = (uint)Math.Min(windowBlocks, totalBlocks - windowStart);
                var windowLength = (int)Math.Min(windowBytes, totalLength - (long)windowStart * blockSize);
                input.ReadExactly(buffer, 0, windowLength);
                if (windowLength < (int)windowBytes)
                {
                    Array.Clear(buffer, windowLength, (int)windowBytes - windowLength);
                }

                for (var offset = 0U; offset < windowCount; offset++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var block = windowStart + offset;
                    var thisBlockBytes = (int)Math.Min(blockSize, totalLength - (long)block * blockSize);
                    var blockSpan = buffer.AsSpan((int)offset * (int)blockSize, (int)blockSize);

                    byte nextKind;
                    var nextFill = 0u;
                    if (IsZeroBlock(blockSpan))
                    {
                        nextKind = KindZero;
                    }
                    else if (thisBlockBytes == (int)blockSize && IsUniformFill(blockSpan, out nextFill))
                    {
                        nextKind = KindFill;
                    }
                    else
                    {
                        nextKind = KindRaw;
                    }

                    if (kind != 0 && (nextKind != kind || (nextKind == KindFill && nextFill != fillValue)))
                    {
                        Flush();
                    }
                    if (kind == 0)
                    {
                        kind = nextKind;
                        fillValue = nextFill;
                        startBlock = block;
                        startByte = (long)block * blockSize;
                    }
                    blocks++;
                }
                windowStart += windowCount;
            }
            Flush();

            sparseFile.WriteToStream(new CancellationWriteStream(output, cancellationToken), sparse: true,
                includeCrc: options?.EmitCrc ?? false, options: options, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static void ConvertRawToSparseValidated(
        Stream input,
        Stream output,
        uint blockSize,
        long length,
        CancellationToken cancellationToken,
        Stream? validationOutput = null,
        SparseWriteOptions? options = null)
    {
        ConvertRawToSparseCancellable(input, output, blockSize, length, cancellationToken, options);
        cancellationToken.ThrowIfCancellationRequested();
        output.Flush();
        var readableOutput = validationOutput ?? output;
        if (readableOutput is FileStream fileOutput)
        {
            fileOutput.Flush(flushToDisk: true);
        }
        else
        {
            readableOutput.Flush();
        }
        ValidateSparseOutput(readableOutput, length, blockSize, options);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void ValidateSparseOutput(Stream output, long sourceLength, uint blockSize, SparseWriteOptions? options)
    {
        if (!output.CanSeek || !output.CanRead)
        {
            throw new InvalidOperationException("Sparse publication validation requires a readable, seekable output stream.");
        }

        var end = output.Length;
        output.Position = 0;
        using var sparse = SparseReader.FromStream(output);
        var expectedBlocks = checked((ulong)((sourceLength + blockSize - 1) / blockSize));
        if (options is { FileAlignment: > 1 })
        {
            var alignment = options.FileAlignment;
            expectedBlocks = (expectedBlocks + alignment - 1) / alignment * alignment;
        }
        if (sparse.Header.BlockSize != blockSize || sparse.Header.TotalBlocks != expectedBlocks || output.Position != end)
        {
            throw new InvalidDataException("Converted sparse image does not match the raw image length or contains trailing data.");
        }
    }

    private static bool IsZeroBlock(ReadOnlySpan<byte> block) => block.IndexOfAnyExcept((byte)0) < 0;

    private static bool IsUniformFill(ReadOnlySpan<byte> block, out uint value)
    {
        value = BinaryPrimitives.ReadUInt32LittleEndian(block);
        if ((block.Length & 3) != 0)
        {
            return false;
        }
        return MemoryMarshal.Cast<byte, uint>(block).IndexOfAnyExcept(value) < 0;
    }

    /// <summary>
    /// Splits a large sparse image into multiple sparse images of a specified maximum size,
    /// preserving absolute-address chunking semantics.
    /// <para>把大型稀疏镜像按指定最大大小拆分为多个稀疏镜像，并保留绝对寻址分块语义。</para>
    /// </summary>
    /// <remarks>
    /// <para>关键不变量：每个输出分片的头部保留原始镜像的完整 <c>total_blks</c>，
    /// chunk 携带绝对块地址，chunk 之间的空隙由写入端补 DONT_CARE（skip），FILL/RAW chunk
    /// 类型原样保留（CRC 除外，读取阶段已丢弃）。逐分片刷写时从分区偏移 0 顺序消费 chunk，
    /// 前导 DONT_CARE 把写入偏移推进到本分片的数据块，因此每个分片单独下载并刷写能落到正确位置；
    /// 若分片改用分片本地寻址（每片从块 0 开始），多次刷写会反复覆盖分区前缀导致数据错位。</para>
    /// </remarks>
    /// <param name="inputFile">
    /// Path of the sparse image to split; it is parsed and fully expanded block addresses are preserved.
    /// <para>待拆分稀疏镜像的路径；解析后保留完整的展开块地址。</para>
    /// </param>
    /// <param name="outputPattern">
    /// Output path pattern: a pattern containing <c>{0}</c> is formatted with the piece number, and any
    /// other pattern has the piece number appended as a two-digit <c>.NN</c> suffix. Pieces are written
    /// with <c>FileMode.Create</c>, replacing files that already exist at those paths.
    /// <para>输出路径模式：包含 <c>{0}</c> 的模式会用分片序号格式化，其他模式则追加分片序号的两位
    /// <c>.NN</c> 后缀。分片以 <c>FileMode.Create</c> 写入，会替换这些路径上已存在的文件。</para>
    /// </param>
    /// <param name="maxFileSize">
    /// Hard ceiling in bytes for every output piece, counted together with the piece's own headers;
    /// must be at least <c>1</c> and large enough to hold one data block plus its headers.
    /// <para>每个输出分片的字节硬上限，包含分片自身的头部；至少为 <c>1</c>，且必须能容纳一个数据块及其头部。</para>
    /// </param>
    /// <example>
    /// <code>SparseImageConverter.ResparseImage("system.sparse.img", "sys.part{0}", 100 * 1024 * 1024);</code>
    /// </example>
    /// <exception cref="ArgumentException">
    /// <paramref name="inputFile"/> or <paramref name="outputPattern"/> is empty.
    /// <para><paramref name="inputFile"/> 或 <paramref name="outputPattern"/> 为空。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxFileSize"/> is below <c>1</c> or too small to hold a single chunk plus one block.
    /// <para><paramref name="maxFileSize"/> 小于 <c>1</c>，或不足以容纳单个数据块头部加一个块。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The input does not parse as a sparse image.
    /// <para>输入无法按稀疏镜像解析。</para>
    /// </exception>
    /// <remarks>
    /// A logical image of zero blocks writes no output file at all.
    /// <para>逻辑块数为 <c>0</c> 的镜像不会写出任何输出文件。</para>
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

        var perPieceOverhead = (long)SparseFormat.SparseHeaderSize + 2 * SparseFormat.ChunkHeaderSize + sizeof(uint);
        var usable = maxFileSize - perPieceOverhead;
        ArgumentOutOfRangeException.ThrowIfLessThan(usable, SparseFormat.ChunkHeaderSize + blockBytes, nameof(maxFileSize));
        var maxPieceDataBlocks = (usable - SparseFormat.ChunkHeaderSize) / blockBytes;

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

            var outPath = outputPattern.Contains("{0}", StringComparison.Ordinal) ? string.Format(outputPattern, pieceIndex) : $"{outputPattern}.{pieceIndex:D2}";
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