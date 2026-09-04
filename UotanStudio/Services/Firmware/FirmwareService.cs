using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.Avb;
using UotanStudio.Firmware.BootImage;
using UotanStudio.Firmware.Common;
using UotanStudio.Firmware.Cpio;
using UotanStudio.Firmware.Cpio.Extraction;
using UotanStudio.Firmware.Cpio.Model;
using UotanStudio.Firmware.Dat;
using UotanStudio.Firmware.Erofs;
using UotanStudio.Firmware.Ext4;
using UotanStudio.Firmware.F2fs;
using UotanStudio.Firmware.Lp;
using UotanStudio.Firmware.Oppo;
using UotanStudio.Firmware.Oppo.Models;
using UotanStudio.Firmware.Payload;
using UotanStudio.Firmware.Sparse;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// Firmware operation result, carrying success flag and output summary.
/// <para>固件操作结果，承载成功标志与输出摘要。</para>
/// </summary>
public sealed class FirmwareOpResult
{
    /// <summary>
    /// Gets whether the operation succeeded.
    /// <para>获取操作是否成功。</para>
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets the operation summary.
    /// <para>获取操作摘要。</para>
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// Gets the list of entry names.
    /// <para>获取条目名称列表。</para>
    /// </summary>
    public IReadOnlyList<string> Entries { get; init; } = [];

    /// <summary>
    /// Creates a failed operation result.
    /// <para>创建失败的操作结果。</para>
    /// </summary>
    /// <param name="summary">Failure summary. <para>失败摘要。</para></param>
    public static FirmwareOpResult Fail(string summary) => new() { Success = false, Summary = summary };
}

/// <summary>
/// Firmware integrated service, based on the FirmwareKit ecosystem, wrapping CPIO unpack/pack, Payload OTA extraction, AVB VBMeta verification, and Oppo full-format reading.
/// <para>固件综合服务，基于 FirmwareKit 生态封装 CPIO 解包/打包、Payload OTA 提取、AVB VBMeta 校验与 Oppo 全格式读取。</para>
/// </summary>
public sealed class FirmwareService
{
    private static readonly Lazy<FirmwareService> LazyInstance = new(() => new FirmwareService());

    /// <summary>
    /// Gets the FirmwareService singleton instance.
    /// <para>获取 FirmwareService 单例。</para>
    /// </summary>
    public static FirmwareService Instance => LazyInstance.Value;

    private FirmwareService() { }

    /// <summary>
    /// Lists all entry names in a CPIO/TAR archive without unpacking to disk.
    /// <para>列举 CPIO/TAR 归档的全部条目名称（不解包到磁盘）。</para>
    /// </summary>
    /// <param name="archivePath">The archive file path.</param>
    /// <returns>Operation result, Entries is the entry name list.</returns>
    public async Task<FirmwareOpResult> ListCpioEntriesAsync(string archivePath)
    {
        try
        {
            await using var fs = File.OpenRead(archivePath);
            var archive = new CpioArchive();
            var entries = archive.ReadAll(fs);
            var names = entries.Select(e => e.Name ?? string.Empty).Where(n => n.Length > 0).ToList();
            return new FirmwareOpResult { Success = true, Summary = $"{names.Count} entries", Entries = names };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Unpacks a CPIO/TAR archive to the specified directory.
    /// <para>将 CPIO/TAR 归档解包到指定目录。</para>
    /// </summary>
    /// <param name="archivePath">The archive file path.</param>
    /// <param name="outputDirectory">The output directory.</param>
    /// <param name="overwrite">Whether to overwrite existing files.</param>
    /// <returns>Operation result.</returns>
    public async Task<FirmwareOpResult> UnpackCpioAsync(string archivePath, string outputDirectory, bool overwrite = true)
    {
        try
        {
            await using var fs = File.OpenRead(archivePath);
            var archive = new CpioArchive();
            Directory.CreateDirectory(outputDirectory);
            archive.ExtractToDirectory(fs, outputDirectory, new ExtractOptions
            {
                MakeDirectories = true,
                Overwrite = overwrite
            });
            var count = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories).Count();
            return new FirmwareOpResult { Success = true, Summary = $"Extracted {count} files to {outputDirectory}" };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Packs a directory into a CPIO archive (newc format).
    /// <para>将目录内容打包为 CPIO 归档（newc 格式）。</para>
    /// </summary>
    /// <param name="inputDirectory">The input directory.</param>
    /// <param name="outputArchivePath">The output archive path.</param>
    /// <returns>Operation result.</returns>
    public async Task<FirmwareOpResult> PackCpioAsync(string inputDirectory, string outputArchivePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var archive = new CpioArchive();
            var entries = new List<ArchiveEntry>();
            foreach (var file in Directory.EnumerateFiles(inputDirectory, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(inputDirectory, file);
                // 惰性打开文件流，避免把整个目录的所有文件一次性读入内存。
                // Open each file lazily so the whole directory is never materialized in memory.
                entries.Add(new ArchiveEntry
                {
                    Name = rel.Replace('\\', '/'),
                    Data = [],
                    DataStreamFactory = () => File.OpenRead(file),
                    DeclaredDataLength = new FileInfo(file).Length,
                    Metadata = new ArchiveEntryMetadata
                    {
                        FileType = CpioFileType.Regular,
                        UnixPermissions = 0x1A4,
                        LinkCount = 1
                    }
                });
            }
            using var output = File.Create(outputArchivePath);
            archive.SaveAll(output, entries, ArchiveFormat.NewAscii);
            return new FirmwareOpResult { Success = true, Summary = $"Packed {entries.Count} entries to {outputArchivePath}" };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Extracts specified partitions (or all partitions) from an Android OTA payload.bin to an output directory.
    /// <para>从 Android OTA payload.bin 提取指定分区（或全部分区）到输出目录。</para>
    /// </summary>
    /// <param name="payloadPath">The payload.bin file path.</param>
    /// <param name="outputDirectory">The output directory.</param>
    /// <param name="partitionName">Target partition name; null or empty means all partitions.</param>
    /// <returns>Operation result.</returns>
    public async Task<FirmwareOpResult> ExtractPayloadAsync(string payloadPath, string outputDirectory, string? partitionName = null)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var metadata = await PayloadParser.GetPayloadMetadataAsync(payloadPath);
            var allNames = metadata?.PartitionNames ?? [];
            var targets = string.IsNullOrWhiteSpace(partitionName)
                ? allNames.Where(n => !string.IsNullOrEmpty(n)).ToArray()
                : [partitionName];

            await PayloadParser.ExtractAsync(payloadPath, targets, outputDirectory);
            return new FirmwareOpResult { Success = true, Summary = $"Extracted {targets.Length} partitions", Entries = [.. targets] };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Verifies an Android Verified Boot VBMeta image: parses the header, extracts descriptors,
    /// recomputes the embedded digest and verifies the RSASSA-PSS / PKCS#1 v1.5 signature with the
    /// key embedded in the image or with a user-supplied trusted public key.
    /// <para>校验 Android Verified Boot VBMeta 镜像：解析头部、提取描述符、重新计算内嵌摘要，
    /// 并使用镜像内嵌密钥或用户提供的受信任公钥验证 RSASSA-PSS / PKCS#1 v1.5 签名。</para>
    /// </summary>
    /// <param name="vbmetaPath">The vbmeta.img file path.</param>
    /// <param name="publicKeyPath">Optional trusted public key file (AVB <c>extract_public_key</c> blob, PEM or DER);
    /// when omitted the public key embedded in the vbmeta auxiliary data block is used.
    /// <para>可选的受信任公钥文件（AVB <c>extract_public_key</c> 数据块、PEM 或 DER）；
    /// 省略时使用 vbmeta 辅助数据块中内嵌的公钥。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result, Summary contains the signature validity and descriptor summary.</returns>
    public async Task<FirmwareOpResult> VerifyAvbAsync(string vbmetaPath, string? publicKeyPath = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(vbmetaPath, cancellationToken);
            RSA? trustedKey = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(publicKeyPath))
                {
                    var keyBytes = await File.ReadAllBytesAsync(publicKeyPath, cancellationToken);
                    trustedKey = AvbSignatureVerifier.ImportAvbRsaPublicKey(keyBytes)
                                 ?? AvbSignatureVerifier.ImportPublicKeyPem(keyBytes);
                    if (trustedKey == null)
                    {
                        return FirmwareOpResult.Fail($"Failed to import AVB public key from '{publicKeyPath}'. 无法从 '{publicKeyPath}' 导入 AVB 公钥。");
                    }
                }

                var result = AvbSignatureVerifier.Verify(bytes, trustedKey);

                var sb = new StringBuilder();
                sb.AppendLine(result.IsSigned
                    ? $"Signed vbmeta image. Algorithm: {result.AlgorithmName}  已签名 vbmeta 镜像。算法：{result.AlgorithmName}"
                    : "Unsigned vbmeta image (algorithm NONE). 未签名 vbmeta 镜像（算法 NONE）。");
                sb.AppendLine(result.HashIsValid
                    ? "Embedded digest: VALID. 内嵌摘要：有效。"
                    : "Embedded digest: invalid or absent. 内嵌摘要：无效或缺失。");
                sb.AppendLine(result.SignatureIsValid
                    ? "Signature: VALID. 签名：有效。"
                    : "Signature: INVALID. 签名：无效。");
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                {
                    sb.AppendLine($"Detail: {result.ErrorMessage}");
                }

                sb.AppendLine($"Descriptors ({result.Descriptors.Count}):");
                foreach (var d in result.Descriptors)
                {
                    sb.AppendLine($"  - tag={d.Tag} type={d.TypeName} size={d.Size} partition={d.PartitionName ?? "-"} hashAlg={d.HashAlgorithm ?? "-"}");
                }

                return new FirmwareOpResult { Success = true, Summary = sb.ToString().TrimEnd() };
            }
            finally
            {
                trustedKey?.Dispose();
            }
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Signs an Android Verified Boot VBMeta image with the given RSA private key: rebuilds the header,
    /// embeds the supplied hash descriptors and the AVB-format public key, and streams the signed image
    /// to the output path. The output can be verified by <see cref="AvbVerifier.VerifyVBMeta"/>.
    /// <para>使用给定 RSA 私钥对 Android Verified Boot VBMeta 镜像签名：重建头部、嵌入给定哈希
    /// 描述符与 AVB 格式公钥，并将签名镜像流式写出到输出路径。输出可被
    /// <see cref="AvbVerifier.VerifyVBMeta"/> 验证。</para>
    /// </summary>
    /// <param name="vbmetaPath">The source VBMeta image (header metadata is preserved).
    /// <para>源 VBMeta 镜像（保留头部元数据）。</para></param>
    /// <param name="privateKeyPath">The RSA private key file (PEM or DER). <para>RSA 私钥文件（PEM 或 DER）。</para></param>
    /// <param name="outputPath">The output signed VBMeta image path. <para>输出的已签名 VBMeta 镜像路径。</para></param>
    /// <param name="descriptors">The hash descriptors to embed. <para>要嵌入的哈希描述符。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> SignVbmetaAsync(
        string vbmetaPath,
        string privateKeyPath,
        string outputPath,
        IEnumerable<AvbHashDescriptor> descriptors,
        CancellationToken cancellationToken = default)
    {
        try
        {
            LogService.Instance.Write(LogLevel.Info, "Firmware", $"Signing VBMeta {vbmetaPath} -> {outputPath}");

            var key = AvbSigner.LoadRsaPrivateKey(privateKeyPath);
            if (key == null)
            {
                return FirmwareOpResult.Fail($"Failed to load RSA private key from '{privateKeyPath}'. 无法从 '{privateKeyPath}' 加载 RSA 私钥。");
            }

            var result = await AvbSigner.SignVbmetaAsync(vbmetaPath, key, outputPath, descriptors, cancellationToken);
            LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Error, "Firmware",
                result.Success
                    ? $"VBMeta signed: {result.AlgorithmName}, {result.DescriptorCount} descriptor(s), {result.OutputSize} bytes -> {outputPath}"
                    : $"VBMeta signing failed: {result.ErrorMessage}");

            return result.Success
                ? new FirmwareOpResult
                {
                    Success = true,
                    Summary = $"Signed VBMeta ({result.AlgorithmName}, {result.DescriptorCount} descriptor(s)) -> {outputPath}  已签名 VBMeta（{result.AlgorithmName}，{result.DescriptorCount} 个描述符）。"
                }
                : FirmwareOpResult.Fail(result.ErrorMessage ?? "VBMeta signing failed. VBMeta 签名失败。");
        }
        catch (OperationCanceledException)
        {
            return FirmwareOpResult.Fail("VBMeta signing cancelled. VBMeta 签名已取消。");
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// <para>一键禁用 VBMeta 镜像的 AVB 校验：置位 hashtree/verification 两个禁用标志并清零认证块
    /// （见 <see cref="AvbSigner.DisableVbmetaVerificationAsync"/>），无需私钥。输出经临时文件原子提交。</para>
    /// Disables AVB verification on a VBMeta image in one step: sets the hashtree/verification
    /// disable flags and zeroes the authentication block (see
    /// <see cref="AvbSigner.DisableVbmetaVerificationAsync"/>); no private key required. The output
    /// is committed atomically via a temp file.
    /// </summary>
    /// <param name="vbmetaPath">The source VBMeta image. <para>源 VBMeta 镜像。</para></param>
    /// <param name="outputPath">The patched output path (must differ from the source).
    /// <para>补丁输出路径（必须与源不同）。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> DisableVbmetaVerificationAsync(
        string vbmetaPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            LogService.Instance.Write(LogLevel.Info, "Firmware", $"Disabling AVB verification on {vbmetaPath} -> {outputPath}");

            var result = await AvbSigner.DisableVbmetaVerificationAsync(vbmetaPath, outputPath, cancellationToken);
            LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Error, "Firmware",
                result.Success
                    ? $"AVB verification disabled -> {outputPath}"
                    : $"Disable AVB verification failed: {result.ErrorMessage}");

            return result.Success
                ? new FirmwareOpResult
                {
                    Success = true,
                    Summary = $"AVB verification disabled -> {outputPath}  已禁用 AVB 校验。"
                }
                : FirmwareOpResult.Fail(result.ErrorMessage ?? "Disable AVB verification failed. 禁用 AVB 校验失败。");
        }
        catch (OperationCanceledException)
        {
            return FirmwareOpResult.Fail("Disable AVB verification cancelled. 禁用 AVB 校验已取消。");
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Adds an AVB v2 hash footer to a partition image with the given RSA private key: streams the
    /// image out while computing its digest, aligns to 4K, appends a signed self-contained VBMeta
    /// image and the standard footer. The output can be verified by <see cref="AvbVerifier.Verify"/>.
    /// <para>使用给定 RSA 私钥为分区镜像追加 AVB v2 哈希尾部：流式复制镜像的同时计算其摘要，
    /// 对齐到 4K，追加签名后的自包含 VBMeta 镜像与标准尾部。输出可被
    /// <see cref="AvbVerifier.Verify"/> 验证。</para>
    /// </summary>
    /// <param name="imagePath">The partition image to sign. <para>要签名的分区镜像。</para></param>
    /// <param name="partitionName">The target partition name. <para>目标分区名称。</para></param>
    /// <param name="privateKeyPath">The RSA private key file (PEM or DER). <para>RSA 私钥文件（PEM 或 DER）。</para></param>
    /// <param name="outputPath">The output signed image path. <para>输出的已签名镜像路径。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> AddHashFooterAsync(
        string imagePath,
        string partitionName,
        string privateKeyPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            LogService.Instance.Write(LogLevel.Info, "Firmware", $"Adding AVB hash footer to {imagePath} -> {outputPath}");

            var key = AvbSigner.LoadRsaPrivateKey(privateKeyPath);
            if (key == null)
            {
                return FirmwareOpResult.Fail($"Failed to load RSA private key from '{privateKeyPath}'. 无法从 '{privateKeyPath}' 加载 RSA 私钥。");
            }

            var result = await AvbSigner.AddHashFooterAsync(imagePath, partitionName, key, outputPath, cancellationToken);
            LogService.Instance.Write(result.Success ? LogLevel.Info : LogLevel.Error, "Firmware",
                result.Success
                    ? $"AVB hash footer added: {result.AlgorithmName}, {result.OutputSize} bytes -> {outputPath}"
                    : $"Adding AVB hash footer failed: {result.ErrorMessage}");

            return result.Success
                ? new FirmwareOpResult
                {
                    Success = true,
                    Summary = $"Added AVB hash footer ({result.AlgorithmName}) -> {outputPath}  已添加 AVB 哈希尾部（{result.AlgorithmName}）。"
                }
                : FirmwareOpResult.Fail(result.ErrorMessage ?? "Adding AVB hash footer failed. 添加 AVB 哈希尾部失败。");
        }
        catch (OperationCanceledException)
        {
            return FirmwareOpResult.Fail("Adding AVB hash footer cancelled. 添加 AVB 哈希尾部已取消。");
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Unpacks a boot or vendor_boot image into a component directory: <c>kernel</c>, <c>ramdisk</c>
    /// (raw) and <c>ramdisk.cpio</c> (decompressed per <see cref="BootCompressor"/> detection),
    /// <c>second</c>, <c>dtb</c>, <c>dtbo</c>, <c>signature</c>, plus a <c>header.json</c> that
    /// <see cref="PackBootAsync"/> reads back for byte-identical repacking.
    /// <para>将 boot 或 vendor_boot 镜像解包到组件目录：<c>kernel</c>、<c>ramdisk</c>（原始）与
    /// <c>ramdisk.cpio</c>（按 <see cref="BootCompressor"/> 检测结果解压）、<c>second</c>、
    /// <c>dtb</c>、<c>dtbo</c>、<c>signature</c>，以及可供 <see cref="PackBootAsync"/> 读回的
    /// <c>header.json</c>（用于按原版本逐字节重打包）。</para>
    /// </summary>
    /// <param name="bootPath">The boot / vendor_boot image file path. <para>boot / vendor_boot 镜像文件路径。</para></param>
    /// <param name="outputDirectory">The output directory. <para>输出目录。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> UnpackBootAsync(string bootPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);

            // 流式读取头部 8 字节判定镜像类型，避免整个镜像读入内存。
            // Read the 8-byte header streamingly to decide the image type instead of loading the
            // whole image into memory.
            var magic = await ReadHeaderMagicAsync(bootPath, cancellationToken);
            switch (magic)
            {
                case "ANDROID!":
                    using (var parser = new BootImageParser())
                    {
                        parser.ParseFile(bootPath);
                        WriteComponent(outputDirectory, "kernel", parser.Kernel);
                        WriteComponent(outputDirectory, "ramdisk", parser.Ramdisk);
                        WriteComponent(outputDirectory, "ramdisk.cpio", DecompressRamdisk(parser.Ramdisk, parser.Header.RamdiskCompression));
                        WriteComponent(outputDirectory, "second", parser.Second);
                        WriteComponent(outputDirectory, "dtb", parser.Dtb);
                        WriteComponent(outputDirectory, "dtbo", parser.RecoveryDtbo);
                        WriteComponent(outputDirectory, "signature", parser.Signature);
                        await File.WriteAllTextAsync(
                            Path.Combine(outputDirectory, "header.json"),
                            BuildBootHeaderJson(parser.Header).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                            cancellationToken);
                    }

                    return new FirmwareOpResult { Success = true, Summary = $"Unpacked boot image to {outputDirectory}  已解包 boot 镜像。" };

                case "VNDRBOOT":
                    using (var parser = new VendorBootParser())
                    {
                        parser.ParseFile(bootPath);
                        WriteComponent(outputDirectory, "vendor_ramdisk", parser.VendorRamdisk);
                        WriteComponent(outputDirectory, "vendor_ramdisk.cpio", DecompressRamdisk(parser.VendorRamdisk, parser.Header.VendorRamdiskCompression));
                        WriteComponent(outputDirectory, "dtb", parser.Dtb);
                        WriteComponent(outputDirectory, "bootconfig", parser.Bootconfig);
                        await File.WriteAllTextAsync(
                            Path.Combine(outputDirectory, "header.json"),
                            BuildVendorBootHeaderJson(parser).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                            cancellationToken);
                    }

                    return new FirmwareOpResult { Success = true, Summary = $"Unpacked vendor_boot image to {outputDirectory}  已解包 vendor_boot 镜像。" };

                default:
                    return FirmwareOpResult.Fail($"Unsupported boot image magic: {magic}  不支持的 boot 镜像魔数。");
            }
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Packs a component directory produced by <see cref="UnpackBootAsync"/> back into a boot image
    /// with the original header version, using the <c>kernel</c>, <c>ramdisk</c>, <c>second</c>,
    /// <c>dtb</c> and <c>signature</c> files and the <c>header.json</c> metadata.
    /// <para>将 <see cref="UnpackBootAsync"/> 产出的组件目录按原头部版本重打包为 boot 镜像，
    /// 使用 <c>kernel</c>、<c>ramdisk</c>、<c>second</c>、<c>dtb</c>、<c>signature</c> 文件与
    /// <c>header.json</c> 元数据。</para>
    /// </summary>
    /// <param name="inputDirectory">The directory produced by <see cref="UnpackBootAsync"/>. <para>由 <see cref="UnpackBootAsync"/> 产出的目录。</para></param>
    /// <param name="outputPath">The output boot image path. <para>输出 boot 镜像路径。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> PackBootAsync(string inputDirectory, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var headerPath = Path.Combine(inputDirectory, "header.json");
            if (!File.Exists(headerPath))
            {
                return FirmwareOpResult.Fail("header.json not found — unpack a boot image first. 未找到 header.json——请先解包 boot 镜像。");
            }

            var jsonText = await File.ReadAllTextAsync(headerPath, cancellationToken);
            var json = JsonNode.Parse(jsonText) as JsonObject;
            if (json == null)
            {
                return FirmwareOpResult.Fail("Invalid header.json. header.json 格式无效。");
            }

            if (json["magic"]?.GetValue<string>() == "VNDRBOOT")
            {
                return FirmwareOpResult.Fail("vendor_boot repacking is not supported yet; only the vendor_boot unpack path is available. 暂不支持 vendor_boot 重打包，仅提供 vendor_boot 解包。");
            }

            var header = ReadBootHeaderJson(json);
            if (header == null || header.HeaderVersion == BootImageVersion.Unknown)
            {
                return FirmwareOpResult.Fail("Failed to read boot header from header.json. 无法从 header.json 读取 boot 头部。");
            }

            var ramdiskPath = Path.Combine(inputDirectory, "ramdisk");
            if (!File.Exists(ramdiskPath))
            {
                return FirmwareOpResult.Fail("ramdisk component missing — repack needs the raw (possibly compressed) ramdisk from unpack for a byte-identical image. 缺少 ramdisk 组件——为逐字节一致重打包，需要解包产出的原始（可能已压缩）ramdisk。");
            }

            var packer = new BootImagePacker
            {
                Header = header,
                Kernel = await ReadComponentAsync(inputDirectory, "kernel", cancellationToken),
                Ramdisk = await ReadComponentAsync(inputDirectory, "ramdisk", cancellationToken),
                Second = await ReadComponentAsync(inputDirectory, "second", cancellationToken),
                Dtb = await ReadComponentAsync(inputDirectory, "dtb", cancellationToken),
                RecoveryDtbo = await ReadComponentAsync(inputDirectory, "dtbo", cancellationToken),
                Signature = await ReadComponentAsync(inputDirectory, "signature", cancellationToken)
            };

            packer.PackToFile(outputPath);
            return new FirmwareOpResult { Success = true, Summary = $"Packed boot image (header v{(int)header.HeaderVersion}) to {outputPath}  已按头版本 v{(int)header.HeaderVersion} 重打包 boot 镜像。" };
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    private static byte[] DecompressRamdisk(byte[] raw, UotanStudio.Firmware.BootImage.CompressionType compression)
    {
        return compression == UotanStudio.Firmware.BootImage.CompressionType.None || raw.Length == 0
            ? raw
            : BootCompressor.TryDecompress(raw, compression) ?? raw;
    }

    /// <summary>
    /// Reads the 8-byte magic of a boot / vendor_boot image without loading the whole file.
    /// <para>只读取 boot / vendor_boot 镜像的 8 字节魔数，不加载整个文件。</para>
    /// </summary>
    private static async Task<string> ReadHeaderMagicAsync(string bootPath, CancellationToken cancellationToken)
    {
        using var fs = new FileStream(bootPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[8];
        var read = await fs.ReadAsync(buffer, cancellationToken);
        if (read < 8)
        {
            throw new InvalidDataException($"File too small to be a boot image: {read} bytes  文件过小，不是有效的 boot 镜像。");
        }

        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    private static void WriteComponent(string directory, string name, byte[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        File.WriteAllBytes(Path.Combine(directory, name), data);
    }

    private static async Task<byte[]> ReadComponentAsync(string directory, string name, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, name);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : [];
    }

    private static JsonObject BuildBootHeaderJson(BootImageHeader header)
    {
        var ids = new JsonArray();
        foreach (var id in header.Ids ?? [])
        {
            // 显式转型到 JsonNode，强制选择非泛型 Add(JsonNode?) 重载（避免 AOT 裁剪告警）。
            // Explicit cast to JsonNode forces the non-generic Add(JsonNode?) overload (AOT-safe).
            ids.Add((JsonNode)JsonValue.Create(id));
        }

        return new JsonObject
        {
            ["magic"] = "ANDROID!",
            ["header_version"] = (int)header.HeaderVersion,
            ["header_size"] = header.HeaderSize,
            ["kernel_size"] = header.KernelSize,
            ["kernel_addr"] = header.KernelAddr,
            ["ramdisk_size"] = header.RamdiskSize,
            ["ramdisk_addr"] = header.RamdiskAddr,
            ["second_size"] = header.SecondSize,
            ["second_addr"] = header.SecondAddr,
            ["tags_addr"] = header.TagsAddr,
            ["page_size"] = header.PageSize,
            ["os_version"] = header.OsVersion,
            ["image_name"] = header.ImageName,
            ["command_line"] = header.CommandLine,
            ["product_name"] = header.ProductName,
            ["architecture"] = header.Architecture,
            ["ids"] = ids,
            ["recovery_dtbo_size"] = header.RecoveryDtboSize,
            ["recovery_dtbo_offset"] = header.RecoveryDtboOffset,
            ["dtb_size"] = header.DtbSize,
            ["kernel_load_addr"] = header.KernelLoadAddr,
            ["signature_size"] = header.SignatureSize,
            ["kernel_compression"] = header.KernelCompression.ToString(),
            ["ramdisk_compression"] = header.RamdiskCompression.ToString()
        };
    }

    private static BootImageHeader? ReadBootHeaderJson(JsonObject json)
    {
        try
        {
            var versionValue = GetInt(json, "header_version") ?? -1;
            var header = new BootImageHeader
            {
                HeaderVersion = versionValue is >= 0 and <= 4
                    ? (BootImageVersion)versionValue
                    : BootImageVersion.Unknown,
                HeaderSize = GetUInt(json, "header_size") ?? 0,
                KernelSize = GetUInt(json, "kernel_size") ?? 0,
                KernelAddr = GetULong(json, "kernel_addr") ?? 0,
                RamdiskSize = GetUInt(json, "ramdisk_size") ?? 0,
                RamdiskAddr = GetULong(json, "ramdisk_addr") ?? 0,
                SecondSize = GetUInt(json, "second_size") ?? 0,
                SecondAddr = GetULong(json, "second_addr") ?? 0,
                TagsAddr = GetULong(json, "tags_addr") ?? 0,
                PageSize = GetUInt(json, "page_size") ?? 0,
                OsVersion = GetUInt(json, "os_version") ?? 0,
                ImageName = json["image_name"]?.GetValue<string>() ?? string.Empty,
                CommandLine = json["command_line"]?.GetValue<string>() ?? string.Empty,
                ProductName = json["product_name"]?.GetValue<string>() ?? string.Empty,
                Architecture = GetUInt(json, "architecture") ?? 0,
                RecoveryDtboSize = GetUInt(json, "recovery_dtbo_size") ?? 0,
                RecoveryDtboOffset = GetULong(json, "recovery_dtbo_offset") ?? 0,
                DtbSize = GetUInt(json, "dtb_size") ?? 0,
                KernelLoadAddr = GetULong(json, "kernel_load_addr") ?? 0,
                SignatureSize = GetUInt(json, "signature_size") ?? 0
            };

            if (json["ids"] is JsonArray idsArray)
            {
                var ids = new uint[8];
                var i = 0;
                foreach (var node in idsArray)
                {
                    if (i >= 8)
                    {
                        break;
                    }

                    ids[i++] = node?.GetValue<uint>() ?? 0;
                }

                header.Ids = ids;
            }

            return header;
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject BuildVendorBootHeaderJson(VendorBootParser parser)
    {
        var header = parser.Header;
        return new JsonObject
        {
            ["magic"] = "VNDRBOOT",
            ["header_version"] = header.HeaderVersion,
            ["page_size"] = header.PageSize,
            ["kernel_load_address"] = header.KernelLoadAddress,
            ["ramdisk_load_address"] = header.RamdiskLoadAddress,
            ["vendor_ramdisk_size"] = header.VendorRamdiskSize,
            ["vendor_cmdline"] = header.VendorCmdline,
            ["tags_load_address"] = header.TagsLoadAddress,
            ["dtb_size"] = header.DtbSize,
            ["dtb_address"] = header.DtbAddress,
            ["vendor_ramdisk_compression"] = header.VendorRamdiskCompression.ToString(),
            ["ramdisk_table_count"] = parser.RamdiskTable.Count
        };
    }

    private static uint? GetUInt(JsonObject json, string key) => json[key] is JsonValue value && value.TryGetValue<uint>(out var result) ? result : null;

    private static ulong? GetULong(JsonObject json, string key) => json[key] is JsonValue value && value.TryGetValue<ulong>(out var result) ? result : null;

    private static int? GetInt(JsonObject json, string key) => json[key] is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;

    /// <summary>
    /// Detects whether a file starts with the Android sparse image magic.
    /// <para>检测文件是否以 Android sparse 镜像魔数开头。</para>
    /// </summary>
    private static bool IsSparseImage(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[4];
            var read = stream.Read(header);
            return read >= 4 && header[0] == 0x3a && header[1] == 0xff && header[2] == 0x26 && header[3] == 0xed;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Copies exactly <paramref name="length"/> bytes from <paramref name="source"/> to <paramref name="target"/>,
    /// honoring the cancellation token, throwing <see cref="EndOfStreamException"/> on premature EOF.
    /// <para>从 <paramref name="source"/> 向 <paramref name="target"/> 精确复制 <paramref name="length"/> 字节，
    /// 遵循取消令牌，过早到达流尾时抛出 <see cref="EndOfStreamException"/>。</para>
    /// </summary>
    private static void CopyRange(Stream source, Stream target, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException($"Unexpected end of stream; expected {length} bytes.");
            }
            target.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    /// <summary>
    /// Reads metadata of an Oppo firmware package (auto-detecting OFP/OPS/OZIP), outputting format and entry count.
    /// <para>读取 Oppo 固件包（自动检测 OFP/OPS/OZIP）的元数据，输出格式与条目数。</para>
    /// </summary>
    /// <param name="firmwarePath">The firmware package path.</param>
    /// <returns>Operation result.</returns>
    public async Task<FirmwareOpResult> ReadOppoMetadataAsync(string firmwarePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var reader = new OppReader();
            var metadata = await reader.ReadMetadataAsync(firmwarePath, cancellationToken);
            var summary = $"Format: {metadata.FormatName}\nEntries: {metadata.EntryCount}";
            return new FirmwareOpResult { Success = true, Summary = summary };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Unpacks an Oppo firmware package (auto-detecting OFP/OPS/OZIP) to an output directory.
    /// <para>解包 Oppo 固件包（自动检测 OFP/OPS/OZIP）到输出目录。</para>
    /// </summary>
    /// <param name="firmwarePath">The firmware package path.</param>
    /// <param name="outputDirectory">The output directory.</param>
    /// <param name="overwrite">Whether to overwrite existing files.</param>
    /// <returns>Operation result.</returns>
    public async Task<FirmwareOpResult> ExtractOppoAsync(string firmwarePath, string outputDirectory, bool overwrite = true, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var reader = new OppReader();
            var result = await reader.ExtractAsync(firmwarePath, outputDirectory, new OppExtractOptions
            {
                Overwrite = overwrite
            }, progress: null, cancellationToken);
            var ok = result.ExtractedFileCount;
            return new FirmwareOpResult { Success = ok >= 0, Summary = $"Extracted {ok} files" };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Verifies a file's SHA256, for firmware integrity checking.
    /// <para>校验文件 SHA256，用于固件完整性核对。</para>
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <param name="expectedHash">Expected SHA256 hex string; empty returns only computed value.</param>
    /// <returns>Operation result, Summary is computed value or comparison result.</returns>
    public async Task<FirmwareOpResult> VerifyChecksumAsync(string filePath, string? expectedHash = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var fs = File.OpenRead(filePath);
            var hash = await System.Security.Cryptography.SHA256.HashDataAsync(fs, cancellationToken);
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            var ok = string.IsNullOrEmpty(expectedHash) || string.Equals(hex, expectedHash, StringComparison.OrdinalIgnoreCase);
            return new FirmwareOpResult
            {
                Success = ok,
                Summary = string.IsNullOrEmpty(expectedHash) ? $"SHA256: {hex}" : (ok ? "Checksum match." : $"Mismatch: expected={expectedHash}, actual={hex}")
            };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Unpacks the logical partition images from an Android super (LP) image into an output directory by
    /// parsing the LP metadata (via <see cref="MetadataReader"/>) and copying each partition's linear extent
    /// range out of the image file. Sparse super images are first expanded to a temporary raw file.
    /// <para>解析 Android super（LP）镜像的 LP 元数据（经 <see cref="MetadataReader"/>），并按各分区线性
    /// extent 范围从镜像文件复制出逻辑分区镜像到输出目录。稀疏格式的 super 镜像先展开为临时 raw 文件。</para>
    /// </summary>
    /// <param name="superPath">The super image file path. <para>super 镜像文件路径。</para></param>
    /// <param name="outputDirectory">The output directory for the extracted partition images. <para>分区镜像输出目录。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result, Entries is the extracted partition name list. <para>操作结果，Entries 为已提取的分区名称列表。</para></returns>
    public async Task<FirmwareOpResult> UnpackSuperAsync(string superPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        return await Task.Run<FirmwareOpResult>(() =>
        {
            string? tempRaw = null;
            try
            {
                if (string.IsNullOrWhiteSpace(superPath) || !File.Exists(superPath))
                {
                    return FirmwareOpResult.Fail($"Super image not found: {superPath ?? "<empty>"}  找不到 super 镜像。");
                }

                // Android super 镜像通常是 sparse 格式，需先展开为 raw 临时文件以便随机读取分区。
                // Android super images are usually sparse; expand to a temporary raw file for random-access partition reads.
                var sourcePath = superPath;
                if (IsSparseImage(superPath))
                {
                    tempRaw = Path.Combine(Path.GetTempPath(), $"uotan_super_{Guid.NewGuid():N}.raw");
                    using (var sparse = SparseReader.FromImageFile(superPath))
                    using (var raw = new FileStream(tempRaw, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        sparse.WriteRawToStream(raw);
                    }
                    sourcePath = tempRaw;
                }

                Directory.CreateDirectory(outputDirectory);
                using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var metadata = MetadataReader.ReadFromImageStream(stream);
                var firstLogicalSector = metadata.BlockDevices.Count > 0 ? metadata.BlockDevices[0].FirstLogicalSector : 0UL;

                var extracted = new List<string>();
                foreach (var partition in metadata.Partitions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = partition.NameString;
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    // 取第一个线性 extent；不支持零填充或多 extent 布局时给出清晰错误而非崩溃。
                    // Use the first linear extent; return a clear error (rather than crash) for unsupported layouts.
                    LpMetadataExtent linear = default;
                    var foundLinear = false;
                    for (uint i = 0; i < partition.NumExtents; i++)
                    {
                        var extent = metadata.Extents[(int)(partition.FirstExtentIndex + i)];
                        if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_LINEAR)
                        {
                            linear = extent;
                            foundLinear = true;
                            break;
                        }
                    }

                    if (!foundLinear)
                    {
                        return FirmwareOpResult.Fail($"Partition '{name}' has no linear extent; unsupported super layout.  分区 '{name}' 无线性 extent，不支持该 super 布局。");
                    }

                    var offset = (long)((firstLogicalSector + linear.TargetData) * MetadataFormat.LP_SECTOR_SIZE);
                    var length = (long)linear.NumSectors * MetadataFormat.LP_SECTOR_SIZE;
                    if (offset < 0 || length <= 0 || offset + length > stream.Length)
                    {
                        return FirmwareOpResult.Fail($"Partition '{name}' extent out of range (offset={offset}, length={length}).  分区 '{name}' extent 超出镜像范围。");
                    }

                    var outputPath = Path.Combine(outputDirectory, name);
                    using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        stream.Position = offset;
                        CopyRange(stream, output, length, cancellationToken);
                    }

                    extracted.Add(name);
                }

                return new FirmwareOpResult
                {
                    Success = true,
                    Summary = $"Extracted {extracted.Count} partition(s) from super to {outputDirectory}  已从 super 解出 {extracted.Count} 个分区。",
                    Entries = extracted
                };
            }
            catch (OperationCanceledException)
            {
                return FirmwareOpResult.Fail("Super unpack cancelled.  解包 super 已取消。");
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail(ex.Message);
            }
            finally
            {
                if (tempRaw != null && File.Exists(tempRaw))
                {
                    File.Delete(tempRaw);
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Converts an image between raw and Android sparse formats (a thin wrapper over
    /// <see cref="SparseImageConverter"/>): <c>toSparse = true</c> calls <see cref="SparseImageConverter.ConvertRawToSparse(string, string, uint)"/>,
    /// <c>false</c> calls <see cref="SparseImageConverter.ConvertSparseToRaw(IEnumerable{string}, string)"/>.
    /// <para>在 raw 与 Android sparse 格式之间转换镜像（<see cref="SparseImageConverter"/> 的薄封装）：
    /// <c>toSparse = true</c> 调用 <see cref="SparseImageConverter.ConvertRawToSparse(string, string, uint)"/>，
    /// <c>false</c> 调用 <see cref="SparseImageConverter.ConvertSparseToRaw(IEnumerable{string}, string)"/>。</para>
    /// </summary>
    /// <param name="inputPath">The input image file path. <para>输入镜像文件路径。</para></param>
    /// <param name="outputPath">The output image file path. <para>输出镜像文件路径。</para></param>
    /// <param name="toSparse">Whether to convert raw to sparse (<c>true</c>) or sparse to raw (<c>false</c>). <para>是否转换为稀疏格式（<c>true</c> 为 raw→sparse，<c>false</c> 为 sparse→raw）。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> ConvertSparseAsync(string inputPath, string outputPath, bool toSparse, CancellationToken cancellationToken = default)
    {
        return await Task.Run<FirmwareOpResult>(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                {
                    return FirmwareOpResult.Fail($"Input image not found: {inputPath ?? "<empty>"}  找不到输入镜像。");
                }

                if (toSparse)
                {
                    SparseImageConverter.ConvertRawToSparse(inputPath, outputPath);
                }
                else
                {
                    SparseImageConverter.ConvertSparseToRaw([inputPath], outputPath);
                }

                var direction = toSparse ? "raw -> sparse" : "sparse -> raw";
                return new FirmwareOpResult { Success = true, Summary = $"Converted image ({direction}): {outputPath}  已转换镜像（{direction}）。" };
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail(ex.Message);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Unpacks an Android sparse image to a directory: when the inner content is a filesystem
    /// (ext4/erofs/f2fs) it is cascade-unpacked via the corresponding streaming parser, otherwise
    /// it falls back to a plain raw conversion. A thin wrapper over
    /// <see cref="SparseFormatHandler.UnpackAsync"/>.
    /// <para>将 Android sparse 镜像解包到目录：内部内容为文件系统（ext4/erofs/f2fs）时通过对应
    /// 的流式解析器级联解包，否则回退为普通 raw 转换。<see cref="SparseFormatHandler.UnpackAsync"/> 的薄封装。</para>
    /// </summary>
    /// <param name="sparsePath">The sparse image file path. <para>稀疏镜像文件路径。</para></param>
    /// <param name="outputDirectory">The output directory. <para>输出目录。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> UnpackSparseImageAsync(string sparsePath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var result = new SparseFormatHandler().UnpackAsync(sparsePath, outputDirectory, cancellationToken: cancellationToken).GetAwaiter().GetResult();
            return new FirmwareOpResult
            {
                Success = result.Success,
                Summary = result.Summary,
                Entries = result.Entries,
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Unpacks a filesystem image (ext4/erofs/f2fs) to a directory via the matching streaming parser.
    /// The format is auto-detected from the image magic bytes; an unsupported or unrecognized image
    /// returns a failed result without writing output.
    /// <para>将文件系统镜像（ext4/erofs/f2fs）通过对应的流式解析器解包到目录。格式按镜像魔数自动
    /// 识别；不支持或未识别的镜像返回失败结果且不写出文件。</para>
    /// </summary>
    /// <param name="imagePath">The filesystem image file path. <para>文件系统镜像文件路径。</para></param>
    /// <param name="outputDirectory">The output directory. <para>输出目录。</para></param>
    /// <param name="progress">Optional progress reporter as (processed file count, total file count). <para>可选进度回调，值为 (已处理文件数, 文件总数)。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> UnpackFileSystemImageAsync(string imagePath, string outputDirectory, IProgress<(int processed, int total)>? progress = null, CancellationToken cancellationToken = default)
    {
        return await Task.Run<FirmwareOpResult>(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                {
                    return FirmwareOpResult.Fail($"Input image not found: {imagePath ?? "<empty>"}  找不到输入镜像。");
                }

                Directory.CreateDirectory(outputDirectory);
                var header = FormatDetector.ReadHeader(imagePath, 1082);

                // ext4：魔数 0xEF53 位于偏移 1080。
                // ext4: magic 0xEF53 at offset 1080.
                if (header.Length >= 1082
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1080, 2)) == 0xEF53)
                {
                    using var parser = new Ext4Parser(imagePath);
                    parser.ExtractToDirectory(outputDirectory, progress: progress);
                    var entries = parser.ListFiles();
                    return new FirmwareOpResult
                    {
                        Success = true,
                        Summary = $"Extracted {entries.Count} entries from '{imagePath}' to '{outputDirectory}'  已从 '{imagePath}' 解出 {entries.Count} 个条目到 '{outputDirectory}'",
                    };
                }

                // erofs：魔数 0xE0F5E1E2 位于偏移 1024。
                // erofs: magic 0xE0F5E1E2 at offset 1024.
                if (header.Length >= 1028
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xE0F5E1E2)
                {
                    using var parser = new ErofsParser(imagePath);
                    parser.ExtractToDirectory(outputDirectory, progress: progress);
                    var files = parser.EnumerateFiles();
                    return new FirmwareOpResult
                    {
                        Success = true,
                        Summary = $"Extracted {files.Count} files from '{imagePath}' to '{outputDirectory}'  已从 '{imagePath}' 解出 {files.Count} 个文件到 '{outputDirectory}'",
                    };
                }

                // f2fs：魔数 0xF2F52010 位于偏移 1024。
                // f2fs: magic 0xF2F52010 at offset 1024.
                if (header.Length >= 1028
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xF2F52010)
                {
                    using var parser = new F2fsParser(imagePath);
                    parser.ExtractToDirectory(outputDirectory, progress: progress);
                    var entries = parser.ListFiles();
                    return new FirmwareOpResult
                    {
                        Success = true,
                        Summary = $"Extracted {entries.Count} entries from '{imagePath}' to '{outputDirectory}'  已从 '{imagePath}' 解出 {entries.Count} 个条目到 '{outputDirectory}'",
                    };
                }

                return FirmwareOpResult.Fail($"Unrecognized filesystem image (no ext4/erofs/f2fs magic): {imagePath}  未识别的文件系统镜像（无 ext4/erofs/f2fs 魔数）：{imagePath}");
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail(ex.Message);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Writes an Android OTA v4 DAT image pair (a thin wrapper over <see cref="DatWriter"/>): the input raw
    /// image is converted into a <c>.new.dat</c> data file plus a derived <c>.transfer.list</c>. Note that
    /// <see cref="DatWriter"/> consumes a raw image (not a transfer list), so the <paramref name="transferListPath"/>
    /// parameter is the source raw image path; <paramref name="outputPath"/> is the <c>.new.dat</c> output and the
    /// <c>.transfer.list</c> path is derived from it.
    /// <para>写出 Android OTA v4 DAT 镜像对（<see cref="DatWriter"/> 的薄封装）：将输入 raw 镜像转为
    /// <c>.new.dat</c> 数据文件与推导出的 <c>.transfer.list</c>。注意 <see cref="DatWriter"/> 的输入是
    /// 原始镜像（而非 transfer list），因此 <paramref name="transferListPath"/> 实为源镜像路径，
    /// <paramref name="outputPath"/> 为 <c>.new.dat</c> 输出，<c>.transfer.list</c> 路径由它推导。</para>
    /// </summary>
    /// <param name="transferListPath">The source raw image file path (the DAT pair is derived from it). <para>源原始镜像文件路径（DAT 镜像对由它生成）。</para></param>
    /// <param name="outputPath">The output <c>.new.dat</c> file path. <para>输出 <c>.new.dat</c> 文件路径。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> WriteDatAsync(string transferListPath, string outputPath, CancellationToken cancellationToken = default)
    {
        return await Task.Run<FirmwareOpResult>(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(transferListPath) || !File.Exists(transferListPath))
                {
                    return FirmwareOpResult.Fail($"Input image not found: {transferListPath ?? "<empty>"}  找不到输入镜像。");
                }

                var transferListOut = Path.GetFileName(outputPath).EndsWith(".new.dat", StringComparison.OrdinalIgnoreCase)
                    ? outputPath[..^".new.dat".Length] + ".transfer.list"
                    : outputPath + ".transfer.list";

                DatWriter.Write(transferListPath, outputPath, transferListOut);

                return new FirmwareOpResult
                {
                    Success = true,
                    Summary = $"Wrote DAT image pair: {outputPath} / {transferListOut}  已写出 DAT 镜像对。"
                };
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail(ex.Message);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Builds an Android super (logical partition) image from a set of raw/sparse partition images:
    /// constructs LP metadata via <see cref="MetadataBuilder"/>, writes the metadata into the reserved
    /// area, lays out the partition data at the linear extent offsets, then sparsifies the whole image.
    /// <para>从一组 raw/sparse 分区镜像构建 Android super（逻辑分区）镜像：通过
    /// <see cref="MetadataBuilder"/> 构建 LP 元数据，将元数据写入保留区，按线性 extent 偏移排布分区数据，
    /// 最后将整镜像转为 sparse 格式。</para>
    /// </summary>
    /// <param name="partitions">The partitions (name + source image path). <para>分区（名称 + 源镜像路径）。</para></param>
    /// <param name="outputPath">The output super image path. <para>输出 super 镜像路径。</para></param>
    /// <param name="deviceSize">The total device (super) size in bytes. <para>设备（super）总大小（字节）。</para></param>
    /// <param name="groupName">The partition group name. <para>分区组名称。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result.</returns>
    public async Task<FirmwareOpResult> SynthesizeSuperAsync(
        IReadOnlyList<SuperPartitionInput> partitions,
        string outputPath,
        ulong deviceSize,
        string groupName,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run<FirmwareOpResult>(() =>
        {
            try
            {
                if (partitions is null || partitions.Count == 0)
                {
                    return FirmwareOpResult.Fail("No partitions supplied. 未提供任何分区。");
                }

                var group = string.IsNullOrWhiteSpace(groupName) ? "default" : groupName;
                var builder = MetadataBuilder.New(deviceSize, 64 * 1024, 2);
                builder.AddGroup(group, deviceSize);
                foreach (var partition in partitions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    builder.AddPartition(partition.Name, group, (ulong)new FileInfo(partition.Path).Length);
                }

                var metadata = builder.Build();
                var firstLogicalSector = metadata.BlockDevices.Count > 0 ? metadata.BlockDevices[0].FirstLogicalSector : 0UL;

                var tempRaw = Path.Combine(Path.GetDirectoryName(outputPath) ?? ".", Path.GetFileNameWithoutExtension(outputPath) + ".raw");
                try
                {
                    using (var stream = new FileStream(tempRaw, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                    {
                        stream.SetLength((long)deviceSize);
                        MetadataWriter.WriteToImageStream(stream, metadata);
                        for (var i = 0; i < metadata.Partitions.Count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var extent = metadata.Extents[(int)metadata.Partitions[i].FirstExtentIndex];
                            var offset = (long)((firstLogicalSector + extent.TargetData) * MetadataFormat.LP_SECTOR_SIZE);
                            stream.Position = offset;
                            using var source = SparseReader.ImportAuto(partitions[i].Path);
                            source.WriteRawToStream(stream);
                        }
                    }

                    SparseImageConverter.ConvertRawToSparse(tempRaw, outputPath);
                    return new FirmwareOpResult
                    {
                        Success = true,
                        Summary = $"Synthesized super image with {partitions.Count} partition(s) -> {outputPath}  已合成 super 镜像（{partitions.Count} 个分区）。"
                    };
                }
                finally
                {
                    if (File.Exists(tempRaw))
                    {
                        File.Delete(tempRaw);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return FirmwareOpResult.Fail("Super synthesis cancelled. 合成 super 已取消。");
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail(ex.Message);
            }
        }, cancellationToken);
    }
}

/// <summary>
/// Describes one partition for super image synthesis: the partition name and its source image file path.
/// <para>描述 super 镜像合成的一个分区：分区名称及其源镜像文件路径。</para>
/// </summary>
public sealed class SuperPartitionInput
{
    /// <summary>
    /// Gets the partition name. <para>获取分区名称。</para>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the source image file path. <para>获取源镜像文件路径。</para>
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Creates a super partition input.
    /// <para>创建 super 分区输入。</para>
    /// </summary>
    /// <param name="name">The partition name. <para>分区名称。</para></param>
    /// <param name="path">The source image file path. <para>源镜像文件路径。</para></param>
    public SuperPartitionInput(string name, string path)
    {
        Name = name;
        Path = path;
    }
}