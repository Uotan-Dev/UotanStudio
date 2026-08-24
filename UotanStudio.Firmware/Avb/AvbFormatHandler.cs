using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.Avb;

/// <summary>
/// IFormatHandler implementation for Android Verified Boot images (VBMeta / AVB footer).
/// <para>Android Verified Boot 镜像(VBMeta / AVB 尾部)的 IFormatHandler 实现。</para>
/// </summary>
public sealed class AvbFormatHandler : IFormatHandler
{
    /// <inheritdoc />
    public string FormatName => "Android Verified Boot (AVB)";

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions => ["img", "vbmeta"];

    /// <inheritdoc />
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (ext == "vbmeta" || Path.GetFileName(filePath).StartsWith("vbmeta", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            return AvbFooter.TryLoad(bytes) != null
                   || (bytes.Length >= AvbVBMetaImageHeader.Size && AvbVBMetaImageHeader.FromBytes(bytes).IsValid());
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);

            // Prefer a standalone VBMeta image (magic AVB0); fall back to AVB footer verification.
            if (bytes.Length >= AvbVBMetaImageHeader.Size && AvbVBMetaImageHeader.FromBytes(bytes).IsValid())
            {
                var header = AvbVBMetaImageHeader.FromBytes(bytes);
                var summary = $"VBMeta header parsed.\nAlgorithm: {header.AlgorithmType}\n" +
                              $"Descriptors size: {header.DescriptorsSize} bytes\n" +
                              $"Rollback index: {header.RollbackIndex}\nRelease: {header.ReleaseString}";
                return Task.FromResult(FirmwareOpResult.Ok(summary, metadata: new Dictionary<string, string>
                {
                    ["AlgorithmType"] = header.AlgorithmType.ToString(),
                    ["DescriptorsSize"] = header.DescriptorsSize.ToString(),
                    ["RollbackIndex"] = header.RollbackIndex.ToString(),
                    ["ReleaseString"] = header.ReleaseString
                }));
            }

            var footer = AvbFooter.TryLoad(bytes);
            if (footer == null)
            {
                return Task.FromResult(FirmwareOpResult.Fail("No AVB footer or VBMeta image found."));
            }

            var summary2 = $"AVB signed image.\nAlgorithm: {footer.Value.AlgorithmName}\n" +
                           $"Auth data: {footer.Value.AuthDataSize} bytes at offset {footer.Value.AuthDataOffset}";
            return Task.FromResult(FirmwareOpResult.Ok(summary2, metadata: new Dictionary<string, string>
            {
                ["AlgorithmName"] = footer.Value.AlgorithmName,
                ["AuthDataOffset"] = footer.Value.AuthDataOffset.ToString(),
                ["AuthDataSize"] = footer.Value.AuthDataSize.ToString()
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var result = AvbVerifier.VerifyVBMeta(bytes);
            if (result.IsSigned && result.VBMetaHeader.HasValue)
            {
                // Extract the raw data blocks (auth + auxiliary) as separate files.
                Directory.CreateDirectory(outputDirectory);
                var header = result.VBMetaHeader.Value;
                var baseName = Path.GetFileNameWithoutExtension(filePath);
                var authPath = Path.Combine(outputDirectory, $"{baseName}.authentication.bin");
                var auxPath = Path.Combine(outputDirectory, $"{baseName}.auxiliary.bin");
                var authLen = (int)Math.Min(header.AuthenticationDataBlockSize, (ulong)Math.Max(0, bytes.Length - AvbVBMetaImageHeader.Size));
                var auxLen = (int)Math.Min(header.AuxiliaryDataBlockSize, (ulong)Math.Max(0, bytes.Length - AvbVBMetaImageHeader.Size - authLen));
                File.WriteAllBytes(authPath, bytes.AsSpan(AvbVBMetaImageHeader.Size, authLen).ToArray());
                File.WriteAllBytes(auxPath, bytes.AsSpan(AvbVBMetaImageHeader.Size + authLen, auxLen).ToArray());
                return Task.FromResult(FirmwareOpResult.Ok(
                    $"Extracted VBMeta data blocks to {outputDirectory}", entries: [authPath, auxPath]));
            }

            // AVB footer on a boot image: extract the auth data block.
            var footerResult = AvbVerifier.Verify(bytes);
            if (footerResult.IsSigned && footerResult.Footer.HasValue)
            {
                Directory.CreateDirectory(outputDirectory);
                var f = footerResult.Footer.Value;
                var authPath2 = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(filePath)}.avb-auth.bin");
                var authData = bytes.AsSpan((int)f.AuthDataOffset, (int)f.AuthDataSize).ToArray();
                File.WriteAllBytes(authPath2, authData);
                return Task.FromResult(FirmwareOpResult.Ok(
                    $"Extracted AVB authentication data to {authPath2}", entries: [authPath2]));
            }

            return Task.FromResult(FirmwareOpResult.Fail("File is not an AVB-signed image or VBMeta image."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default) =>
        // AVB packing (signing) requires a private key; not supported in this handler.
        Task.FromResult(FirmwareOpResult.Fail("AVB packing/signing requires a private key and is not supported by this handler."));
}