using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;

namespace UotanStudio.Firmware.BlockImage.Sparse;

/// <summary>
/// Format handler for the Android sparse image (simg) container: recognizes the sparse header magic, reports chunk
/// and block geometry, unpacks either the inner filesystem (ext4, erofs, f2fs) or the full raw image, and converts
/// raw images back to sparse. Sparse structures from untrusted files are validated by the reader; operation errors
/// surface as failure results.
/// <para>Android 稀疏镜像（simg）容器的格式处理器：识别稀疏头魔数、报告块与 chunk 几何信息、解包内部文件系统
/// （ext4、erofs、f2fs）或完整 raw 镜像，并把 raw 镜像转回稀疏格式。来自不可信文件的稀疏结构由读取器校验；
/// 操作错误以失败结果呈现。</para>
/// </summary>
public sealed class SparseFormatHandler : Core.IFormatHandler, IRepackMetadataProvider, Core.Bridges.ISparseContainerBridge
{
    /// <summary>
    /// Opens the logical view of a sparse container file for another bounded context, transferring ownership of the
    /// expanded model to the returned view.
    /// <para>为另一个限界上下文打开稀疏容器文件的逻辑视图，展开后模型的所有权转移给返回的视图。</para>
    /// </summary>
    /// <param name="imagePath">Path to the container file.</param>
    /// <returns>A disposable logical view.</returns>
    /// <exception cref="InvalidDataException">The file is not a readable sparse container.</exception>
    Core.Bridges.ISparseLogicalView Core.Bridges.ISparseContainerBridge.OpenLogicalView(string imagePath)
    {
        var model = SparseReader.FromImageFile(imagePath);
        return new SparseLogicalView(model, new SparseReadStream(model));
    }

    /// <summary>
    /// Opens the logical view of a sparse container held in a seekable stream. The caller keeps ownership of
    /// <paramref name="container"/>; only the expanded model is disposed with the view.
    /// <para>打开可寻址流内稀疏容器的逻辑视图。调用方保留对 <paramref name="container"/> 的所有权，
    /// 随视图释放的仅为展开后的模型。</para>
    /// </summary>
    /// <param name="container">Seekable container stream.</param>
    /// <returns>A disposable logical view.</returns>
    /// <exception cref="InvalidDataException">The stream is not a readable sparse container.</exception>
    Core.Bridges.ISparseLogicalView Core.Bridges.ISparseContainerBridge.OpenLogicalView(Stream container)
    {
        var model = SparseReader.FromStream(container);
        return new SparseLogicalView(model, new SparseReadStream(model));
    }

    /// <summary>
    /// Flattens sparse containers into one raw image file.
    /// <para>把稀疏容器展平为单个原始镜像文件。</para>
    /// </summary>
    /// <param name="inputFiles">Container files in concatenation order.</param>
    /// <param name="outputFile">Raw image destination.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    void Core.Bridges.ISparseContainerBridge.ConvertSparseToRaw(IEnumerable<string> inputFiles, string outputFile, CancellationToken cancellationToken)
        => SparseImageConverter.ConvertSparseToRawCancellable(inputFiles, outputFile, cancellationToken);

    /// <summary>
    /// Converts a raw image into a sparse container published atomically after structural validation.
    /// <para>把原始镜像转换为稀疏容器，结构校验通过后原子发布。</para>
    /// </summary>
    /// <param name="inputFile">Raw image source.</param>
    /// <param name="outputFile">Container destination replaced only on success.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    void Core.Bridges.ISparseContainerBridge.ConvertRawToSparseAtomic(string inputFile, string outputFile, CancellationToken cancellationToken)
        => SparseImageConverter.ConvertRawToSparseAtomic(inputFile, outputFile, 4096, cancellationToken);

    /// <summary>
    /// Converts a caller-owned raw stream into a caller-owned container stream and validates the result before the
    /// caller publishes it.
    /// <para>在调用方持有两个流的前提下完成 raw 到容器的转换，并在调用方发布前校验结果。</para>
    /// </summary>
    /// <param name="input">Raw image source.</param>
    /// <param name="output">Container destination written by this call.</param>
    /// <param name="blockSize">Block size for the produced container.</param>
    /// <param name="length">Expected raw length.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <param name="validationOutput">Optional readable stream carrying the produced bytes for validation.</param>
    void Core.Bridges.ISparseContainerBridge.ConvertRawToSparseValidated(Stream input, Stream output, uint blockSize, long length, CancellationToken cancellationToken, Stream? validationOutput)
        => SparseImageConverter.ConvertRawToSparseValidated(input, output, blockSize, length, cancellationToken, validationOutput);
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
        => UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemRepackMetadata.Configure(metadata, sourcePath, outputDirectory, sparse: true);

    Task IRepackMetadataProvider.CaptureRepackMetadataAsync(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken)
        => UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemRepackMetadata.CaptureAsync(metadata, sourcePath, outputDirectory, metadataDirectory, cancellationToken, sparse: true);

    /// <summary>Display name published with every operation result of this handler.
    /// <para>本处理器每次操作结果随附的显示名称。</para></summary>
    /// <value>The fixed string <c>Android Sparse Image (simg)</c>.
    /// <para>固定字符串 <c>Android Sparse Image (simg)</c>。</para></value>
    public string FormatName => "Android Sparse Image (simg)";

    /// <summary>Lowercase file extensions this handler advertises for picker filters.
    /// <para>本处理器在文件选择过滤器中声明的小写扩展名。</para></summary>
    /// <value><c>img</c>, <c>simg</c> and <c>sparse</c>; <c>img</c> files still need the sparse header magic to be recognized.
    /// <para><c>img</c>、<c>simg</c> 与 <c>sparse</c>；<c>img</c> 文件仍需稀疏头魔数才能被识别。</para></value>
    public IReadOnlyList<string> FileExtensions => ["img", "simg", "sparse"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">File to probe; a <c>.simg</c> or <c>.sparse</c> extension matches directly, an <c>.img</c> (or other) file matches when its first four little-endian bytes equal the sparse header magic.
    /// <para>待探测文件；<c>.simg</c> 或 <c>.sparse</c> 扩展名直接匹配，<c>.img</c>（及其他）文件在前四个小端字节等于稀疏头魔数时匹配。</para></param>
    /// <returns><see langword="true"/> when the extension or magic matches; <see langword="false"/> for blank, missing or unreadable files (all I/O errors are swallowed).
    /// <para>扩展名或魔数匹配时为 <see langword="true"/>；空白、缺失或不可读文件为 <see langword="false"/>（吞掉所有 I/O 错误）。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (ext is "simg" or "sparse")
        {
            return true;
        }
        return HeaderProbe.ProbeMagic(filePath, sizeof(uint), 0, SparseFormat.SparseHeaderMagic);
    }

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
    /// <param name="filePath">Sparse image file; only the fixed-size header is peeked, and the reported image size is TotalBlocks x BlockSize rather than the on-disk file length.
    /// <para>稀疏镜像文件；仅窥探固定大小的头部，报告的镜像大小为 TotalBlocks × BlockSize，而非磁盘文件长度。</para></param>
    /// <param name="cancellationToken">Checked once at entry. <para>在入口处检查一次。</para></param>
    /// <returns>Success result summarizing chunk/block geometry with the corresponding metadata entries, or a failure result describing the header read error.
    /// <para>汇总 chunk/块几何信息的成功结果及对应元数据条目；头部读取错误以失败结果返回。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested at entry. <para>入口处请求了取消。</para></exception>
    public Task<Core.FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = SparseReader.PeekHeader(filePath);
            var fileSize = (long)header.TotalBlocks * header.BlockSize;
            var summary = $"Sparse image: {header.TotalChunks} chunks, {header.TotalBlocks} blocks x {header.BlockSize}B = {fileSize} bytes";
            return Task.FromResult(Core.FirmwareOpResult.Ok(summary, metadata: new Dictionary<string, string>
            {
                ["BlockSize"] = header.BlockSize.ToString(),
                ["TotalBlocks"] = header.TotalBlocks.ToString(),
                ["TotalChunks"] = header.TotalChunks.ToString(),
                ["ImageSize"] = fileSize.ToString(),
                ["MajorVersion"] = header.MajorVersion.ToString(),
                ["MinorVersion"] = header.MinorVersion.ToString(),
                ["FileHeaderSize"] = header.FileHeaderSize.ToString(),
                ["ChunkHeaderSize"] = header.ChunkHeaderSize.ToString(),
                ["ImageChecksum"] = $"0x{header.ImageChecksum:X8}"
            }));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(Core.FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    /// <param name="filePath">Sparse image, frozen to a temporary copy first so concurrent writes cannot mix into the extraction; inner-format probing reads only the bounded magic windows the recognized filesystem handlers request.
    /// <para>稀疏镜像；先冻结为临时副本，防止并发写入混入解包过程；内部格式探测仅读取被识别的文件系统处理器所需的有界魔数窗口。</para></param>
    /// <param name="outputDirectory">Created on demand; receives the inner filesystem tree, or a single <c>&lt;name&gt;.raw</c> file when no known filesystem magic is present, plus a repack metadata record on success.
    /// <para>按需创建；成功时接收内部文件系统树，或在无已知文件系统魔数时的单个 <c>&lt;name&gt;.raw</c> 文件，另加重打包元数据记录。</para></param>
    /// <param name="options">Unpack options; <c>Overwrite</c> defaults to enabled and <c>FileSystemProgress</c> receives byte progress. May be <see langword="null"/>.
    /// <para>解包选项；<c>Overwrite</c> 默认开启，<c>FileSystemProgress</c> 接收字节进度。可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancellation during the source freeze or the metadata write is rethrown; cancellation observed inside the extraction itself is reported as a failure result.
    /// <para>源冻结或元数据写入期间的取消会重新抛出；解包内部捕获到的取消以失败结果报告。</para></param>
    /// <returns>Success result with the extracted entry paths and inner-format metadata, or a failure result for unreadable or malformed images.
    /// <para>带提取条目路径与内部格式元数据的成功结果；不可读或畸形镜像返回失败结果。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested during the source freeze or the metadata write.
    /// <para>源冻结或元数据写入期间请求了取消。</para></exception>
    public Task<Core.FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, Core.UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemPreservation.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, frozen => UnpackCoreAsync(frozen, outputDirectory, options, cancellationToken));

    private async Task<Core.FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, Core.UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var filesystemResult = await TryUnpackFilesystemAsync(filePath, outputDirectory, options?.Overwrite ?? true, options?.FileSystemProgress, cancellationToken).ConfigureAwait(false);
            if (filesystemResult != null)
            {
                return filesystemResult;
            }

            var outputPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(filePath) + ".raw");
            using var sparseFile = SparseReader.FromImageFile(filePath);
            using var output = File.Create(outputPath);
            sparseFile.WriteRawToStream(output);
            var size = new FileInfo(outputPath).Length;
            return Core.FirmwareOpResult.Ok($"Extracted {size} bytes to {outputPath}", entries: [outputPath]);
        }
        catch (Exception ex)
        {
            return Core.FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Cascade-unpacks the inner filesystem of a sparse image by handing its logical stream to the registered
    /// format handlers that publish the <see cref="Core.IStreamUnpackableFormatHandler"/> capability and
    /// selecting the first one that recognises the stream. The sparse context never names a concrete filesystem
    /// parser; an unopened, unreadable or unclaimed stream yields <see langword="null"/> so the caller falls back
    /// to raw-image handling, while extraction errors propagate to the caller's failure channel.
    /// <para>把稀疏镜像的逻辑流交给注册了 <see cref="Core.IStreamUnpackableFormatHandler"/> 能力的
    /// 格式处理器，并选取首个识别该流的处理器来级联解包内部文件系统。稀疏上下文绝不指名具体文件系统解析器；
    /// 无法打开、无法读取或无人认领的逻辑流返回 <see langword="null"/>，调用方据此回退到 raw 镜像处理；
    /// 提取错误则向上传播到调用方的失败通道。</para>
    /// </summary>
    /// <param name="filePath">Frozen sparse image supplying the logical stream for the capability probes;
    /// opening or probe read errors yield <see langword="null"/> instead of a failure result.
    /// <para>为能力探测提供逻辑流的已冻结稀疏镜像；打开或探测读取错误返回 <see langword="null"/> 而非失败结果。</para></param>
    /// <param name="outputDirectory">Destination directory receiving the extracted tree.
    /// <para>接收解出目录树的目标目录。</para></param>
    /// <param name="overwrite">Whether existing output files may be replaced; forwarded to the inner handler.
    /// <para>是否允许替换已存在的输出文件；原样转发给内部处理器。</para></param>
    /// <param name="byteProgress">Byte progress forwarded to the inner handler, which routes it to its extraction stage.
    /// <para>转发给内部处理器的字节进度，由其归入提取阶段。</para></param>
    /// <param name="cancellationToken">Cancellation during extraction propagates as an exception.
    /// <para>提取期间的取消以异常传播。</para></param>
    /// <returns>The composed success result whose entries and metadata come from the inner handler while the
    /// summary names the sparse image path, the inner handler's failure result forwarded unchanged, or
    /// <see langword="null"/> when no handler claims the logical stream.
    /// <para>组合后的成功结果：条目与元数据来自内部处理器，摘要指名稀疏镜像路径；内部处理器上报的失败结果
    /// 原样转发；没有处理器认领该逻辑流时返回 <see langword="null"/>。</para></returns>
    private static async Task<Core.FirmwareOpResult?> TryUnpackFilesystemAsync(string filePath, string outputDirectory, bool overwrite, IProgress<Core.FileSystemByteProgress>? byteProgress, CancellationToken cancellationToken)
    {
        SparseReadStream logicalStream;
        Core.IStreamUnpackableFormatHandler? innerHandler;
        try
        {
            logicalStream = new SparseReadStream(SparseReader.FromImageFile(filePath));
            innerHandler = Core.FormatDetector.RegisteredHandlers
                .OfType<Core.IStreamUnpackableFormatHandler>()
                .FirstOrDefault(handler => handler.CanHandleLogicalStream(logicalStream));
        }
        catch
        {
            return null;
        }

        using (logicalStream)
        {
            if (innerHandler is null)
            {
                return null;
            }

            var inner = await innerHandler.UnpackLogicalStreamAsync(logicalStream, outputDirectory, byteProgress, overwrite, cancellationToken).ConfigureAwait(false);
            if (!inner.Success)
            {
                return inner;
            }

            return Core.FirmwareOpResult.Ok(
                $"Extracted {inner.Entries.Count} entries from '{filePath}' to '{outputDirectory}'.",
                inner.Entries,
                metadata: inner.Metadata);
        }
    }

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
    /// <param name="inputPath">
    /// Raw file, or an unpacked directory whose preserved-attribute metadata is restored first; otherwise its first non-metadata file is converted.
    /// <para>raw 文件，或先恢复其保留属性元数据的已解包目录；否则转换目录中第一个非元数据文件。</para></param>
    /// <param name="outputPath">Destination sparse image written by the conversion. <para>转换写入的目标稀疏镜像。</para></param>
    /// <param name="cancellationToken">Observed while restoring preserved-attribute metadata; the raw-to-sparse conversion itself runs synchronously without polling it.
    /// <para>在恢复保留属性元数据时会被观察；raw 到稀疏的转换本身同步执行且不轮询该标记。</para></param>
    /// <returns>Success result naming the produced file, or a failure result for an empty input directory or a conversion error.
    /// <para>指明生成文件的成功结果；空输入目录或转换错误返回失败结果。</para></returns>
    public Task<Core.FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(PackCore(inputPath, outputPath, options: null, cancellationToken));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Core.FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Packs the input into a sparse image with explicit write geometry, publishing atomically.
    /// <para>使用显式写出几何把输入打包为稀疏镜像，并原子发布。</para>
    /// </summary>
    /// <param name="inputPath">
    /// Raw file, or an unpacked directory whose preserved-attribute metadata is restored first; otherwise its first non-metadata file is converted.
    /// <para>raw 文件，或先恢复其保留属性元数据的已解包目录；否则转换目录中第一个非元数据文件。</para></param>
    /// <param name="outputPath">
    /// Destination sparse path, written to a temporary publication file first and replaced only after structural validation.
    /// <para>目标稀疏路径；先写入临时发布文件，仅结构校验通过后替换。</para></param>
    /// <param name="options">
    /// Validated write geometry: classification block size, optional RAW chunk cap, trailing CRC32 chunk and total-block alignment padding.
    /// <para>已校验的写出几何：分类块大小、可选 RAW 块上限、CRC32 尾块与总块数对齐填充。</para></param>
    /// <param name="cancellationToken">
    /// Observed while restoring preserved-attribute metadata and during the atomic conversion.
    /// <para>在恢复保留属性元数据以及原子转换期间被观察。</para></param>
    /// <returns>
    /// Success result naming the produced file, or a failure result for an empty input directory or a conversion error.
    /// <para>指明生成文件的成功结果；空输入目录或转换错误返回失败结果。</para></returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="options"/> 为 <see langword="null"/>。</para>
    /// </exception>
    public Task<Core.FirmwareOpResult> PackAsync(string inputPath, string outputPath,
        SparseWriteOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        try
        {
            return Task.FromResult(PackCore(inputPath, outputPath, options, cancellationToken));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Core.FirmwareOpResult.Fail(ex.Message));
        }
    }

    private Core.FirmwareOpResult PackCore(string inputPath, string outputPath, SparseWriteOptions? options, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string>? rebuildReport = null;
        if (Directory.Exists(inputPath) && UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemPreservation.TryRestore(inputPath, outputPath, FormatName, cancellationToken,
            report => rebuildReport = report))
        {
            return Core.FirmwareOpResult.Ok("Packed the filesystem using preserved source attributes.",
                metadata: rebuildReport);
        }
        var rawFile = inputPath;
        if (Directory.Exists(inputPath))
        {
            rawFile = Directory.EnumerateFiles(inputPath)
                .FirstOrDefault(path => !UotanStudio.Firmware.Core.Repacking.RepackMetadata.IsMetadataFile(path));
            if (rawFile == null)
            {
                return Core.FirmwareOpResult.Fail("Input directory is empty.");
            }
        }

        if (options is null)
        {
            SparseImageConverter.ConvertRawToSparse(rawFile, outputPath);
        }
        else
        {
            SparseImageConverter.ConvertRawToSparseAtomic(rawFile, outputPath, options.BlockSize, options, cancellationToken);
        }
        return Core.FirmwareOpResult.Ok($"Packed {rawFile} -> {outputPath}");
    }
}