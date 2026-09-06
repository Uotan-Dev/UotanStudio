using System.Buffers.Binary;
using UotanStudio.Firmware.Erofs;
using UotanStudio.Firmware.Ext4;
using UotanStudio.Firmware.F2fs;

namespace UotanStudio.Firmware.Sparse;

public sealed class SparseFormatHandler : Common.IFormatHandler
{
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

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    public Task<Common.FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, Common.UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);


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


    private const ushort Ext4Magic = 0xEF53;
    private const uint ErofsMagic = 0xE0F5E1E2;
    private const uint F2fsMagic = 0xF2F52010;

    /// <summary>
    /// Probes the inner filesystem magic of a sparse image (logical offsets, bounded 1082-byte read) and, when matched, cascade-unpacks the inner filesystem through the corresponding streaming parser (Ext4Parser, ErofsParser or F2fsParser).
    /// <para>探测 sparse 镜像的内部文件系统魔数（逻辑偏移，有界 1082 字节读取），命中时通过对应的流式解析器 （Ext4Parser、ErofsParser 或 F2fsParser）级联解包内部文件系统。</para>
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

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
    public Task<Common.FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (Directory.Exists(inputPath))
            {


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
