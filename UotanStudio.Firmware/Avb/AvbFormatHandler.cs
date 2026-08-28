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
            // 只流式读取头部（VBMeta 头 256 字节）与尾部（Footer 84 字节），避免整文件驻留内存
            // （大型 .img 可达数百 MB，整读会造成 GB 级峰值）。
            // Stream only the head (256-byte VBMeta header) and the tail (84-byte footer) instead of
            // loading the whole file (large .img images can be hundreds of MB).
            using var fs = File.OpenRead(filePath);

            if (fs.Length >= AvbVBMetaImageHeader.Size)
            {
                Span<byte> head = stackalloc byte[AvbVBMetaImageHeader.Size];
                fs.Position = 0;
                if (fs.Read(head) == AvbVBMetaImageHeader.Size
                    && AvbVBMetaImageHeader.FromBytes(head).IsValid())
                {
                    return true;
                }
            }

            if (fs.Length >= AvbFooter.Size)
            {
                Span<byte> tail = stackalloc byte[AvbFooter.Size];
                fs.Position = fs.Length - AvbFooter.Size;
                if (fs.Read(tail) == AvbFooter.Size && AvbFooter.TryLoad(tail) is not null)
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // 同步解析放在后台线程，避免大型镜像的头部/尾部读取阻塞 UI。
        // Run the synchronous parsing on a background thread so the head/tail reads of large images
        // never block the UI.
        return Task.Run(() =>
        {
            try
            {
                using var fs = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);

                // Prefer a standalone VBMeta image (magic AVB0 at the head); fall back to AVB footer.
                if (fs.Length >= AvbVBMetaImageHeader.Size)
                {
                    Span<byte> head = stackalloc byte[AvbVBMetaImageHeader.Size];
                    fs.Position = 0;
                    if (fs.Read(head) == AvbVBMetaImageHeader.Size)
                    {
                        var header = AvbVBMetaImageHeader.FromBytes(head);
                        if (header.IsValid())
                        {
                            var summary = $"VBMeta header parsed.\nAlgorithm: {header.AlgorithmType}\n" +
                                          $"Descriptors size: {header.DescriptorsSize} bytes\n" +
                                          $"Rollback index: {header.RollbackIndex}\nRelease: {header.ReleaseString}";
                            return FirmwareOpResult.Ok(summary, metadata: new Dictionary<string, string>
                            {
                                ["AlgorithmType"] = header.AlgorithmType.ToString(),
                                ["DescriptorsSize"] = header.DescriptorsSize.ToString(),
                                ["RollbackIndex"] = header.RollbackIndex.ToString(),
                                ["ReleaseString"] = header.ReleaseString
                            });
                        }
                    }
                }

                if (fs.Length >= AvbFooter.Size)
                {
                    Span<byte> tail = stackalloc byte[AvbFooter.Size];
                    fs.Position = fs.Length - AvbFooter.Size;
                    if (fs.Read(tail) == AvbFooter.Size && AvbFooter.TryLoad(tail) is { } footer)
                    {
                        var summary = $"AVB signed image.\nFooter version: {footer.VersionMajor}.{footer.VersionMinor}\n" +
                                       $"VBMeta: {footer.VbmetaSize} bytes at offset {footer.VbmetaOffset}";
                        return FirmwareOpResult.Ok(summary, metadata: new Dictionary<string, string>
                        {
                            ["FooterVersion"] = $"{footer.VersionMajor}.{footer.VersionMinor}",
                            ["OriginalImageSize"] = footer.OriginalImageSize.ToString(),
                            ["VbmetaOffset"] = footer.VbmetaOffset.ToString(),
                            ["VbmetaSize"] = footer.VbmetaSize.ToString()
                        });
                    }
                }

                return FirmwareOpResult.Fail("No AVB footer or VBMeta image found.");
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail(ex.Message);
            }
        }, cancellationToken);
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
            if (footerResult.Footer.HasValue && footerResult.VBMetaHeader.HasValue &&
                (footerResult.IsValid || footerResult.VBMetaHeader.Value.AlgorithmType == AvbSignatureVerifier.AlgorithmNone))
            {
                var f = footerResult.Footer.Value;
                var imageLength = (ulong)bytes.Length;
                if (f.VbmetaOffset > imageLength ||
                    f.VbmetaSize > imageLength - f.VbmetaOffset ||
                    f.VbmetaOffset > int.MaxValue ||
                    f.VbmetaSize > int.MaxValue)
                {
                    return Task.FromResult(FirmwareOpResult.Fail("AVB footer VBMeta range is invalid."));
                }

                Directory.CreateDirectory(outputDirectory);
                var authPath2 = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(filePath)}.vbmeta.bin");
                var authData = bytes.AsSpan((int)f.VbmetaOffset, (int)f.VbmetaSize).ToArray();
                File.WriteAllBytes(authPath2, authData);
                return Task.FromResult(FirmwareOpResult.Ok(
                    $"Extracted VBMeta image to {authPath2}", entries: [authPath2]));
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