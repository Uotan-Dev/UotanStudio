using System.Buffers.Binary;
using UotanStudio.Firmware.Erofs;
using UotanStudio.Firmware.Ext4;
using UotanStudio.Firmware.F2fs;

namespace UotanStudio.Firmware.Sparse;

/// <summary>
/// IFormatHandler implementation for Android sparse images.
/// <para>Android 稀疏镜像的 IFormatHandler 实现。</para>
/// </summary>
public sealed class SparseFormatHandler : Common.IFormatHandler
{
    /// <inheritdoc />
    public string FormatName => "Android Sparse Image (simg)";

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions => ["img", "simg", "sparse"];

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task<Common.FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var header = SparseReader.PeekHeader(filePath);
            var fileSize = (long)header.TotalBlocks * header.BlockSize;
            var summary = $"Sparse image: {header.TotalChunks} chunks, {header.TotalBlocks} blocks x {header.BlockSize}B = {fileSize} bytes";
            return Task.FromResult(Common.FirmwareOpResult.Ok(summary, metadata: new Dictionary<string, string>
            {
                ["BlockSize"] = header.BlockSize.ToString(),
                ["TotalBlocks"] = header.TotalBlocks.ToString(),
                ["TotalChunks"] = header.TotalChunks.ToString(),
                ["ImageSize"] = fileSize.ToString()
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Common.FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <inheritdoc />
    public Task<Common.FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, Common.UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);

            // 先探测内部文件系统魔数（有界 1082 字节探测）；命中则级联解包内部文件系统，
            // 未命中（或探测失败）时保留原行为：转 .raw。
            // Probe the inner filesystem magic first (a bounded 1082-byte probe); when matched, cascade-unpack
            // the inner filesystem, otherwise (miss or probe failure) keep the previous .raw conversion.
            var filesystemResult = TryUnpackFilesystem(filePath, outputDirectory, options?.Overwrite ?? true);
            if (filesystemResult != null)
            {
                return Task.FromResult(filesystemResult);
            }

            var outputPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(filePath) + ".raw");
            using var sparseFile = SparseReader.FromImageFile(filePath);
            using var output = File.Create(outputPath);
            sparseFile.WriteRawToStream(output);
            var size = new FileInfo(outputPath).Length;
            return Task.FromResult(Common.FirmwareOpResult.Ok($"Extracted {size} bytes to {outputPath}", entries: [outputPath]));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Common.FirmwareOpResult.Fail(ex.Message));
        }
    }

    // 文件系统魔数（均为逻辑偏移，磁盘上小端存储）：ext4 0xEF53 @1080、erofs 0xE0F5E1E2 @1024、f2fs 0xF2F52010 @1024。
    // Filesystem magic values (all at logical offsets, little-endian on disk): ext4 0xEF53 @1080, erofs 0xE0F5E1E2 @1024, f2fs 0xF2F52010 @1024.
    private const ushort Ext4Magic = 0xEF53;
    private const uint ErofsMagic = 0xE0F5E1E2;
    private const uint F2fsMagic = 0xF2F52010;

    /// <summary>
    /// Probes the inner filesystem magic of a sparse image (logical offsets, bounded 1082-byte read) and,
    /// when matched, cascade-unpacks the inner filesystem through the corresponding streaming parser
    /// (<see cref="Ext4Parser"/>, <see cref="ErofsParser"/> or <see cref="F2fsParser"/>). Returns null when
    /// no known filesystem magic is detected, letting the caller fall back to the .raw conversion.
    /// <para>探测 sparse 镜像的内部文件系统魔数（逻辑偏移，有界 1082 字节读取），命中时通过对应的流式解析器
    /// （<see cref="Ext4Parser"/>、<see cref="ErofsParser"/> 或 <see cref="F2fsParser"/>）级联解包内部文件系统。
    /// 未命中任何已知文件系统魔数时返回 null，由调用方回退为转 .raw。</para>
    /// </summary>
    private static Common.FirmwareOpResult? TryUnpackFilesystem(string filePath, string outputDirectory, bool overwrite)
    {
        byte[] probe;
        try
        {
            probe = SparseReader.PeekLogicalRange(filePath, 0, 1082);
        }
        catch
        {
            // 探测失败按未命中处理，保留转 .raw 回退。
            // Treat a failed probe as a miss and keep the .raw fallback.
            return null;
        }

        if (probe.Length >= 1082 && BinaryPrimitives.ReadUInt16LittleEndian(probe.AsSpan(1080, 2)) == Ext4Magic)
        {
            using var sparseFile = SparseReader.FromImageFile(filePath);
            using var stream = new SparseReadStream(sparseFile);
            using var parser = new Ext4Parser(stream);
            parser.ExtractToDirectory(outputDirectory, overwrite);
            var entries = parser.ListFiles();
            return Common.FirmwareOpResult.Ok(
                $"Extracted {entries.Count} entries from '{filePath}' to '{outputDirectory}'.",
                [.. entries.Select(e => e.Path)]);
        }

        if (probe.Length >= 1028 && BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(1024, 4)) == ErofsMagic)
        {
            using var sparseFile = SparseReader.FromImageFile(filePath);
            using var stream = new SparseReadStream(sparseFile);
            using var parser = new ErofsParser(stream);
            parser.ExtractToDirectory(outputDirectory, overwrite);
            var files = parser.EnumerateFiles();
            return Common.FirmwareOpResult.Ok(
                $"Extracted {files.Count} entries from '{filePath}' to '{outputDirectory}'.",
                [.. files.Select(f => f.Path)]);
        }

        if (probe.Length >= 1028 && BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(1024, 4)) == F2fsMagic)
        {
            using var sparseFile = SparseReader.FromImageFile(filePath);
            using var stream = new SparseReadStream(sparseFile);
            using var parser = new F2fsParser(stream);
            parser.ExtractToDirectory(outputDirectory, overwrite);
            var files = parser.ListFiles();
            return Common.FirmwareOpResult.Ok(
                $"Extracted {files.Count} entries from '{filePath}' to '{outputDirectory}'.",
                [.. files.Select(f => f.Path)]);
        }

        return null;
    }

    /// <inheritdoc />
    public Task<Common.FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (Directory.Exists(inputPath))
            {
                // Pack every file in the directory as a separate sparse image? Not meaningful;
                // a single raw image is expected. Pack the directory's first raw file if any.
                var rawFile = Directory.EnumerateFiles(inputPath).FirstOrDefault();
                if (rawFile == null)
                {
                    return Task.FromResult(Common.FirmwareOpResult.Fail("Input directory is empty."));
                }
                SparseImageConverter.ConvertRawToSparse(rawFile, outputPath);
                return Task.FromResult(Common.FirmwareOpResult.Ok($"Packed {rawFile} -> {outputPath}"));
            }

            SparseImageConverter.ConvertRawToSparse(inputPath, outputPath);
            return Task.FromResult(Common.FirmwareOpResult.Ok($"Packed {inputPath} -> {outputPath}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Common.FirmwareOpResult.Fail(ex.Message));
        }
    }
}