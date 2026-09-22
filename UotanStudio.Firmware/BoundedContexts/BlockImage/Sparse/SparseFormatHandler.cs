using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.Conversion;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Ext4.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.F2fs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse;

public sealed class SparseFormatHandler : SharedKernel.IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
        => FileSystemRepackMetadata.Configure(metadata, sourcePath, outputDirectory, sparse: true);

    Task IRepackMetadataProvider.CaptureRepackMetadataAsync(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken)
        => FileSystemRepackMetadata.CaptureAsync(metadata, sourcePath, outputDirectory, metadataDirectory, cancellationToken, sparse: true);

    public string FormatName => "Android Sparse Image (simg)";

    public IReadOnlyList<string> FileExtensions => ["img", "simg", "sparse"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
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
    public Task<SharedKernel.FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (Directory.Exists(inputPath) && UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding.FileSystemPreservation.TryRestore(inputPath, outputPath, FormatName, cancellationToken))
                return Task.FromResult(SharedKernel.FirmwareOpResult.Ok("Packed the filesystem using preserved source attributes."));
            if (Directory.Exists(inputPath))
            {


                var rawFile = Directory.EnumerateFiles(inputPath)
                    .FirstOrDefault(path => !UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.IsMetadataFile(path));
                if (rawFile == null)
                {
                    return Task.FromResult(SharedKernel.FirmwareOpResult.Fail("Input directory is empty."));
                }
                SparseImageConverter.ConvertRawToSparse(rawFile, outputPath);
                return Task.FromResult(SharedKernel.FirmwareOpResult.Ok($"Packed {rawFile} -> {outputPath}"));
            }

            SparseImageConverter.ConvertRawToSparse(inputPath, outputPath);
            return Task.FromResult(SharedKernel.FirmwareOpResult.Ok($"Packed {inputPath} -> {outputPath}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(SharedKernel.FirmwareOpResult.Fail(ex.Message));
        }
    }
}