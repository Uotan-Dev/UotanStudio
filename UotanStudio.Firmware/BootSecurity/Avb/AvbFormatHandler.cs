using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.BootSecurity.Avb;

/// <summary>
/// Format handler for Android Verified Boot: recognizes standalone VBMeta images and partition images carrying an
/// AVB footer, reads their metadata and extracts the VBMeta structures. All input is treated as untrusted and
/// bounds-checked; malformed images produce failure results instead of throwing.
/// <para>Android Verified Boot 的格式处理器：识别独立 VBMeta 镜像与带 AVB 尾部的分区镜像，读取元数据并提取 VBMeta 结构。
/// 输入一律视为不可信并做边界检查；畸形镜像返回失败结果而不抛出异常。</para>
/// </summary>
internal sealed class AvbFormatHandler : IFormatHandler
{
    /// <summary>Display name published with every operation result of this handler.
    /// <para>本处理器每次操作结果随附的显示名称。</para></summary>
    /// <value>The fixed string <c>Android Verified Boot (AVB)</c>.
    /// <para>固定字符串 <c>Android Verified Boot (AVB)</c>。</para></value>
    public string FormatName => "Android Verified Boot (AVB)";

    /// <summary>Lowercase file extensions this handler advertises for picker filters.
    /// <para>本处理器在文件选择过滤器中声明的小写扩展名。</para></summary>
    /// <value><c>img</c> and <c>vbmeta</c>; recognition still requires a header or footer magic match.
    /// <para><c>img</c> 与 <c>vbmeta</c>；识别仍需头部或尾部魔数匹配。</para></value>
    public IReadOnlyList<string> FileExtensions => ["img", "vbmeta"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">File to probe; a leading valid VBMeta header, or a trailing valid AVB footer whose content head is not already claimed by known filesystem or boot magic, identifies the format.
    /// <para>待探测文件；起始处有效的 VBMeta 头，或结尾有效且内容头部未被已知文件系统/启动魔数占用的 AVB 尾部，均可识别本格式。</para></param>
    /// <returns><see langword="true"/> when the file is AVB-related; <see langword="false"/> for blank, missing or unreadable files (all I/O errors are swallowed).
    /// <para>与 AVB 相关时为 <see langword="true"/>；空白、缺失或不可读的文件为 <see langword="false"/>（吞掉所有 I/O 错误）。</para></returns>
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
    /// <param name="filePath">Untrusted VBMeta or footer-bearing image; parsed sequentially with every declared range validated.
    /// <para>不可信的 VBMeta 或带尾部镜像；顺序解析并校验每个声明范围。</para></param>
    /// <param name="cancellationToken">Observed by the background task and the metadata reader. <para>由后台任务与元数据读取器共同观察。</para></param>
    /// <returns>Result whose metadata dictionary holds the decoded AVB fields on success, or a failure result describing the parse or I/O error.
    /// <para>成功时元数据字典包含解码出的 AVB 字段；解析或 I/O 错误以失败结果返回。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested before or during the read.
    /// <para>读取前或读取期间请求了取消。</para></exception>
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


                var metadata = AvbMetadataReader.Read(fs, cancellationToken);
                return FirmwareOpResult.Ok("AVB", metadata: metadata);
            }
            catch (OperationCanceledException)
            {
                throw;
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
    /// <param name="filePath">Signed VBMeta image (extracts <c>.authentication.bin</c> and <c>.auxiliary.bin</c>) or footer-bearing partition image (extracts <c>.vbmeta.bin</c>); footer-declared ranges are validated before reading.
    /// <para>已签名 VBMeta 镜像（提取 <c>.authentication.bin</c> 与 <c>.auxiliary.bin</c>），或带尾部的分区镜像（提取 <c>.vbmeta.bin</c>）；读取前校验尾部声明的范围。</para></param>
    /// <param name="outputDirectory">Created on demand; receives the extracted files plus a repack metadata record after a successful extraction.
    /// <para>按需创建；成功后接收提取文件及一份重打包元数据记录。</para></param>
    /// <param name="options">Unpack options; <c>Overwrite</c> also governs replacing an existing repack metadata record. May be <see langword="null"/>.
    /// <para>解包选项；<c>Overwrite</c> 同时控制是否替换既有重打包元数据记录。可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Checked before extraction and observed by the metadata write, where cancellation is rethrown; the sequential extraction reads themselves do not poll the token.
    /// <para>解包前检查并在元数据写入时被观察，此时取消会重新抛出；顺序解包读取本身不轮询该标记。</para></param>
    /// <returns>Success result listing the extracted files, or a failure result for unsigned images, invalid VBMeta ranges or I/O errors.
    /// <para>列出提取文件的成功结果；未签名镜像、无效 VBMeta 范围或 I/O 错误返回失败结果。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested before extraction or during the metadata write.
    /// <para>解包前或元数据写入期间请求了取消。</para></exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.Core.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
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
    /// <param name="inputPath">Accepted for interface conformity but never read; signing is not offered by this handler.
    /// <para>为满足接口一致性而接受，但从不读取；本处理器不提供签名功能。</para></param>
    /// <param name="outputPath">Accepted for interface conformity but never written. <para>为满足接口一致性而接受，从不写入。</para></param>
    /// <param name="cancellationToken">Not observed; the result is produced synchronously. <para>不被观察；结果同步产生。</para></param>
    /// <returns>Always a failure result stating that AVB packing requires a private key and redirecting callers to
    /// <c>AvbSigner</c>/<c>AvbResignOptions</c> for re-signing or <c>AvbFooterEditor</c> for footer rewrites; no file is created.
    /// <para>始终返回失败结果，声明 AVB 打包需要私钥，并指引调用方使用 <c>AvbSigner</c>/<c>AvbResignOptions</c> 重签名，
    /// 或使用 <c>AvbFooterEditor</c> 重写尾部；不创建任何文件。</para></returns>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(FirmwareOpResult.Fail(
            "AVB packing requires a private key and is not supported by this handler; re-sign with AvbSigner using AvbResignOptions, or rewrite the embedded footer with AvbFooterEditor."));
}