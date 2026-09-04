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
                    // AVB footer 是附加签名层：带 AVB 签名的文件系统/boot 镜像（Android 常见）
                    // 应先按内容格式识别，避免 ext4/erofs/f2fs/boot 镜像被误判为 AVB。
                    // An AVB footer is an appended signature layer: images signed with AVB (common
                    // on Android) should be recognized by their content format first, so that
                    // ext4/erofs/f2fs/boot images are not misdetected as AVB.
                    if (HasKnownContentMagic(fs))
                    {
                        return false;
                    }
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

    /// <summary>
    /// 检查镜像头部是否命中已知内容格式魔数（ext4 0xEF53@1080 / erofs 0xE0F5E1E2@1024 /
    /// f2fs 0xF2F52010@1024 / boot "ANDROID!"、"VNDRBOOT"@0）。流式读取，不驻留整文件。
    /// <para>Checks whether the image head matches known content-format magic (ext4 0xEF53@1080 /
    /// erofs 0xE0F5E1E2@1024 / f2fs 0xF2F52010@1024 / boot "ANDROID!", "VNDRBOOT"@0). Streams the
    /// head without loading the whole file.</para>
    /// </summary>
    private static bool HasKnownContentMagic(FileStream fs)
    {
        Span<byte> head = stackalloc byte[1082];
        fs.Position = 0;
        var read = 0;
        while (read < head.Length)
        {
            var n = fs.Read(head[read..]);
            if (n <= 0)
            {
                break;
            }
            read += n;
        }
        if (read < 4)
        {
            return false;
        }

        // boot 镜像头部魔数 "ANDROID!" / "VNDRBOOT"。
        // Boot image head magic "ANDROID!" / "VNDRBOOT".
        if (read >= 8)
        {
            var bootMagic = System.Text.Encoding.ASCII.GetString(head[..8]);
            if (bootMagic.StartsWith("ANDROID!", StringComparison.Ordinal) ||
                bootMagic.StartsWith("VNDRBOOT", StringComparison.Ordinal))
            {
                return true;
            }
        }

        // erofs / f2fs 魔数位于偏移 1024。
        // erofs / f2fs magic at offset 1024.
        if (read >= 1028)
        {
            var fsMagic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head[1024..1028]);
            if (fsMagic == 0xE0F5E1E2u || fsMagic == 0xF2F52010u)
            {
                return true;
            }
        }

        // ext4 魔数 0xEF53 位于偏移 1080。
        // ext4 magic 0xEF53 at offset 1080.
        if (read >= 1082
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(head[1080..1082]) == 0xEF53)
        {
            return true;
        }

        return false;
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
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.SequentialScan);
            var imageLength = fs.Length;

            // 路径一：独立 VBMeta 镜像（头部含 AVB0 魔数）。仅读取头 + auth + aux 块，不载入整文件。
            // Path 1: standalone VBMeta image (AVB0 magic at head). Read only header + auth + aux blocks.
            if (imageLength >= AvbVBMetaImageHeader.Size)
            {
                var vmResult = AvbVerifier.VerifyVBMetaFromStream(fs, imageLength);
                if (vmResult.IsSigned && vmResult.VBMetaHeader.HasValue)
                {
                    Directory.CreateDirectory(outputDirectory);
                    var header = vmResult.VBMetaHeader.Value;
                    var baseName = Path.GetFileNameWithoutExtension(filePath);
                    var authPath = Path.Combine(outputDirectory, $"{baseName}.authentication.bin");
                    var auxPath = Path.Combine(outputDirectory, $"{baseName}.auxiliary.bin");
                    var fileAvailable = imageLength - AvbVBMetaImageHeader.Size;
                    var authLen = (int)Math.Min(header.AuthenticationDataBlockSize, (ulong)Math.Max(0, fileAvailable));
                    var auxAvailable = fileAvailable - authLen;
                    var auxLen = (int)Math.Min(header.AuxiliaryDataBlockSize, (ulong)Math.Max(0, auxAvailable));

                    fs.Position = AvbVBMetaImageHeader.Size;
                    var authData = new byte[authLen];
                    fs.ReadExactly(authData);
                    File.WriteAllBytes(authPath, authData);

                    var auxData = new byte[auxLen];
                    fs.ReadExactly(auxData);
                    File.WriteAllBytes(auxPath, auxData);

                    return Task.FromResult(FirmwareOpResult.Ok(
                        $"Extracted VBMeta data blocks to {outputDirectory}", entries: [authPath, auxPath]));
                }
            }

            // 路径二：带 AVB 尾部的镜像（boot/vendor_boot 等）。仅读取尾部 + VBMeta 区域。
            // Path 2: image with AVB footer (boot/vendor_boot etc.). Read only tail + VBMeta region.
            if (imageLength >= AvbFooter.Size)
            {
                var footerResult = AvbVerifier.VerifyFromStream(fs, imageLength);
                if (footerResult.Footer.HasValue && footerResult.VBMetaHeader.HasValue &&
                    (footerResult.IsValid || footerResult.VBMetaHeader.Value.AlgorithmType == AvbSignatureVerifier.AlgorithmNone))
                {
                    var f = footerResult.Footer.Value;
                    var imgLen = (ulong)imageLength;
                    if (f.VbmetaOffset > imgLen ||
                        f.VbmetaSize > imgLen - f.VbmetaOffset ||
                        f.VbmetaOffset > long.MaxValue ||
                        f.VbmetaSize > long.MaxValue)
                    {
                        return Task.FromResult(FirmwareOpResult.Fail("AVB footer VBMeta range is invalid."));
                    }

                    Directory.CreateDirectory(outputDirectory);
                    var vbmetaPath = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(filePath)}.vbmeta.bin");
                    fs.Position = (long)f.VbmetaOffset;
                    var vbmetaData = new byte[(int)f.VbmetaSize];
                    fs.ReadExactly(vbmetaData);
                    File.WriteAllBytes(vbmetaPath, vbmetaData);
                    return Task.FromResult(FirmwareOpResult.Ok(
                        $"Extracted VBMeta image to {vbmetaPath}", entries: [vbmetaPath]));
                }
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