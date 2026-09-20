using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Verification;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb;

public sealed class AvbFormatHandler : IFormatHandler
{
    public string FormatName => "Android Verified Boot (AVB)";

    public IReadOnlyList<string> FileExtensions => ["img", "vbmeta"];

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
        try
        {


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
    /// Checks whether the image head matches known content-format magic (ext4, erofs, f2fs, boot).
    /// <para>检查镜像头部是否命中已知内容格式魔数（ext4、erofs、f2fs、boot）。</para>
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


        if (read >= 8)
        {
            var bootMagic = System.Text.Encoding.ASCII.GetString(head[..8]);
            if (bootMagic.StartsWith("ANDROID!", StringComparison.Ordinal) ||
                bootMagic.StartsWith("VNDRBOOT", StringComparison.Ordinal))
            {
                return true;
            }
        }


        if (read >= 1028)
        {
            var fsMagic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head[1024..1028]);
            if (fsMagic == 0xE0F5E1E2u || fsMagic == 0xF2F52010u)
            {
                return true;
            }
        }


        if (read >= 1082
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(head[1080..1082]) == 0xEF53)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {


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

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.SequentialScan);
            var imageLength = fs.Length;


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

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default) =>

        Task.FromResult(FirmwareOpResult.Fail("AVB packing/signing requires a private key and is not supported by this handler."));
}