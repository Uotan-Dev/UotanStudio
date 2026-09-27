using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Conversion;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse;

/// <summary>
/// Format handler for the Android sparse image (simg) container: recognizes the sparse header magic, reports chunk
/// and block geometry, unpacks either the inner filesystem (ext4, erofs, f2fs) or the full raw image, and converts
/// raw images back to sparse. Sparse structures from untrusted files are validated by the reader; operation errors
/// surface as failure results.
/// <para>Android 稀疏镜像（simg）容器的格式处理器：识别稀疏头魔数、报告块与 chunk 几何信息、解包内部文件系统
/// （ext4、erofs、f2fs）或完整 raw 镜像，并把 raw 镜像转回稀疏格式。来自不可信文件的稀疏结构由读取器校验；
/// 操作错误以失败结果呈现。</para>
/// </summary>
public sealed class SparseFormatHandler : SharedKernel.IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
        => FileSystemRepackMetadata.Configure(metadata, sourcePath, outputDirectory, sparse: true);

    Task IRepackMetadataProvider.CaptureRepackMetadataAsync(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken)
        => FileSystemRepackMetadata.CaptureAsync(metadata, sourcePath, outputDirectory, metadataDirectory, cancellationToken, sparse: true);

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
        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> magic = stackalloc byte[4];
            return fs.Read(magic) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(magic) == SparseFormat.SparseHeaderMagic;
        }
        catch
        {
            return false;
        }
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
    public Task<SharedKernel.FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = SparseReader.PeekHeader(filePath);
            var fileSize = (long)header.TotalBlocks * header.BlockSize;
            var summary = $"Sparse image: {header.TotalChunks} chunks, {header.TotalBlocks} blocks x {header.BlockSize}B = {fileSize} bytes";
            return Task.FromResult(SharedKernel.FirmwareOpResult.Ok(summary, metadata: new Dictionary<string, string>
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
            return Task.FromResult(SharedKernel.FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    /// <param name="filePath">Sparse image, frozen to a temporary copy first so concurrent writes cannot mix into the extraction; the probe never exceeds a bounded 1082-byte logical read.
    /// <para>稀疏镜像；先冻结为临时副本，防止并发写入混入解包过程；探测读取不超过有界的 1082 字节逻辑范围。</para></param>
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
    public Task<SharedKernel.FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, SharedKernel.UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding.FileSystemPreservation.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, frozen => UnpackCoreAsync(frozen, outputDirectory, options, cancellationToken));

    private Task<SharedKernel.FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, SharedKernel.UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);


            var filesystemResult = TryUnpackFilesystem(filePath, outputDirectory, options?.Overwrite ?? true, options?.FileSystemProgress, cancellationToken);
            if (filesystemResult != null)
            {
                return Task.FromResult(filesystemResult);
            }

            var outputPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(filePath) + ".raw");
            using var sparseFile = SparseReader.FromImageFile(filePath);
            using var output = File.Create(outputPath);
            sparseFile.WriteRawToStream(output);
            var size = new FileInfo(outputPath).Length;
            return Task.FromResult(SharedKernel.FirmwareOpResult.Ok($"Extracted {size} bytes to {outputPath}", entries: [outputPath]));
        }
        catch (Exception ex)
        {
            return Task.FromResult(SharedKernel.FirmwareOpResult.Fail(ex.Message));
        }
    }


    private const ushort Ext4Magic = 0xEF53;
    private const uint ErofsMagic = 0xE0F5E1E2;
    private const uint F2fsMagic = 0xF2F52010;

    /// <summary>
    /// Probes the inner filesystem magic of a sparse image (logical offsets, bounded 1082-byte read) and, when matched, cascade-unpacks the inner filesystem through the corresponding streaming parser (Ext4Parser, ErofsParser or F2fsParser).
    /// <para>探测 sparse 镜像的内部文件系统魔数（逻辑偏移，有界 1082 字节读取），命中时通过对应的流式解析器 （Ext4Parser、ErofsParser 或 F2fsParser）级联解包内部文件系统。</para>
    /// </summary>
    private static SharedKernel.FirmwareOpResult? TryUnpackFilesystem(string filePath, string outputDirectory, bool overwrite, IProgress<SharedKernel.FileSystemByteProgress>? byteProgress, CancellationToken cancellationToken)
    {
        byte[] probe;
        try
        {
            probe = SparseReader.PeekLogicalRange(filePath, 0, 1082);
        }
        catch
        {


            return null;
        }

        if (probe.Length >= 1082 && BinaryPrimitives.ReadUInt16LittleEndian(probe.AsSpan(1080, 2)) == Ext4Magic)
        {
            using var sparseFile = SparseReader.FromImageFile(filePath);
            using var stream = new SparseReadStream(sparseFile);
            using var parser = new Ext4Parser(stream);
            parser.ExtractToDirectory(outputDirectory, SharedKernel.FileSystemByteProgress.ForStage(byteProgress, SharedKernel.FileSystemWorkStage.Extracting), cancellationToken, overwrite);
            var entries = parser.ListFiles();
            return SharedKernel.FirmwareOpResult.Ok(
                $"Extracted {entries.Count} entries from '{filePath}' to '{outputDirectory}'.",
                [.. entries.Select(e => e.Path)], metadata: new Dictionary<string, string>
                {
                    ["InnerFormat"] = "EXT4",
                    ["FilesystemBlockSize"] = parser.BlockSize.ToString()
                });
        }

        if (probe.Length >= 1028 && BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(1024, 4)) == ErofsMagic)
        {
            using var sparseFile = SparseReader.FromImageFile(filePath);
            using var stream = new SparseReadStream(sparseFile);
            using var parser = new ErofsParser(stream);
            parser.ExtractToDirectory(outputDirectory, SharedKernel.FileSystemByteProgress.ForStage(byteProgress, SharedKernel.FileSystemWorkStage.Extracting), cancellationToken, overwrite);
            var files = parser.EnumerateFiles();
            return SharedKernel.FirmwareOpResult.Ok(
                $"Extracted {files.Count} entries from '{filePath}' to '{outputDirectory}'.",
                [.. files.Select(f => f.Path)], metadata: new Dictionary<string, string>
                {
                    ["InnerFormat"] = "EROFS",
                    ["FilesystemBlockSize"] = parser.BlockSize.ToString(),
                    ["VolumeName"] = parser.VolumeName
                });
        }

        if (probe.Length >= 1028 && BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(1024, 4)) == F2fsMagic)
        {
            using var sparseFile = SparseReader.FromImageFile(filePath);
            using var stream = new SparseReadStream(sparseFile);
            using var parser = new F2fsParser(stream);
            parser.ExtractToDirectory(outputDirectory, SharedKernel.FileSystemByteProgress.ForStage(byteProgress, SharedKernel.FileSystemWorkStage.Extracting), cancellationToken, overwrite);
            var files = parser.ListFiles();
            return SharedKernel.FirmwareOpResult.Ok(
                $"Extracted {files.Count} entries from '{filePath}' to '{outputDirectory}'.",
                [.. files.Select(f => f.Path)], metadata: new Dictionary<string, string>
                {
                    ["InnerFormat"] = "F2FS",
                    ["FilesystemBlockSize"] = parser.BlockSize.ToString(),
                    ["VolumeName"] = parser.VolumeName
                });
        }

        return null;
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
    public Task<SharedKernel.FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(PackCore(inputPath, outputPath, options: null, cancellationToken));
        }
        catch (Exception ex)
        {
            return Task.FromResult(SharedKernel.FirmwareOpResult.Fail(ex.Message));
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
    public Task<SharedKernel.FirmwareOpResult> PackAsync(string inputPath, string outputPath,
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
            return Task.FromResult(SharedKernel.FirmwareOpResult.Fail(ex.Message));
        }
    }

    private SharedKernel.FirmwareOpResult PackCore(string inputPath, string outputPath,
        SparseWriteOptions? options, CancellationToken cancellationToken)
    {
        if (Directory.Exists(inputPath) && UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding.FileSystemPreservation.TryRestore(inputPath, outputPath, FormatName, cancellationToken))
        {
            return SharedKernel.FirmwareOpResult.Ok("Packed the filesystem using preserved source attributes.");
        }
        var rawFile = inputPath;
        if (Directory.Exists(inputPath))
        {
            rawFile = Directory.EnumerateFiles(inputPath)
                .FirstOrDefault(path => !UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.IsMetadataFile(path));
            if (rawFile == null)
            {
                return SharedKernel.FirmwareOpResult.Fail("Input directory is empty.");
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
        return SharedKernel.FirmwareOpResult.Ok($"Packed {rawFile} -> {outputPath}");
    }
}