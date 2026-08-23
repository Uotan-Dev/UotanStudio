using System.Buffers.Binary;

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