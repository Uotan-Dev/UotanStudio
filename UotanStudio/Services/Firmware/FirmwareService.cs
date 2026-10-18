using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.AndroidMeta.Apk;
using UotanStudio.Firmware.Application.Equivalence;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Archive;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.BootSecurity.Patcher;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.VendorFirmware.Oppo;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;
using UotanStudio.Hash.Digests;

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
    /// <value>True when the operation succeeded. <para>操作成功时为 true。</para></value>
    public bool Success { get; init; }

    /// <summary>
    /// Gets the operation summary.
    /// <para>获取操作摘要。</para>
    /// </summary>
    /// <value>The summary text describing the outcome. <para>描述结果情况的摘要文本。</para></value>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// Gets the list of entry names.
    /// <para>获取条目名称列表。</para>
    /// </summary>
    /// <value>The entry names reported by the operation. <para>操作上报的条目名称。</para></value>
    public IReadOnlyList<string> Entries { get; init; } = [];

    /// <summary>
    /// Gets the key/value facts carried by the operation, including the filesystem equivalence flags.
    /// <para>获取操作携带的键值信息，其中包含文件系统等价标记。</para>
    /// </summary>
    /// <value>The metadata keyed by fact name. <para>按事实名称索引的元数据。</para></value>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets the four-dimension equivalence report parsed from <see cref="Metadata"/>. A dimension whose flag is
    /// absent or unparsable stays <see cref="FirmwareEquivalenceState.NotEvaluated"/>.
    /// <para>获取依据 <see cref="Metadata"/> 解析出的四维度等价报告。标记缺失或无法解析的维度保持为不评估。</para>
    /// </summary>
    /// <value>The parsed equivalence report. <para>解析得到的等价报告。</para></value>
    public FilesystemEquivalenceReport Equivalence => FilesystemEquivalenceReport.FromMetadata(Metadata);

    /// <summary>
    /// Creates a failed operation result.
    /// <para>创建失败的操作结果。</para>
    /// </summary>
    /// <param name="summary">Failure summary. <para>失败摘要。</para></param>
    /// <returns>A failed result carrying that summary. <para>携带该摘要的失败结果。</para></returns>
    public static FirmwareOpResult Fail(string summary) => new() { Success = false, Summary = summary };

    /// <summary>
    /// Converts a library operation result into this result type so metadata survives the mapping.
    /// <para>把库侧操作结果转换为本结果类型，使元数据在映射中得以保留。</para>
    /// </summary>
    /// <param name="result">Library result to mirror. <para>待映射的库结果。</para></param>
    /// <returns>The converted result carrying the same success flag, summary, entries and metadata.
    /// <para>携带相同成功标志、摘要、明细行与元数据的转换结果。</para></returns>
    public static FirmwareOpResult From(UotanStudio.Firmware.Core.FirmwareOpResult result)
        => new()
        {
            Success = result.Success,
            Summary = result.Summary,
            Entries = result.Entries,
            Metadata = result.Metadata,
        };
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
    /// <value>The shared service instance. <para>共享的服务实例。</para></value>
    public static FirmwareService Instance => LazyInstance.Value;

    private FirmwareService() { }

    /// <summary>
    /// Inspects a root package for the boot patch format and variant it carries.
    /// <para>检查 root 包所携带的引导补丁格式与变体。</para>
    /// </summary>
    /// <param name="packagePath">The root package file path. <para>root 包文件路径。</para></param>
    /// <param name="cancellationToken">Cancels the inspection before it completes. <para>在完成前取消检查。</para></param>
    /// <returns>The detected patch format and variant. <para>识别出的补丁格式与变体。</para></returns>
    public Task<BootPatchDetection> DetectBootPatchAsync(string packagePath, CancellationToken cancellationToken = default)
        => new BootPatchService().DetectAsync(packagePath, cancellationToken);

    /// <summary>
    /// Applies a root package to a boot image with in-process patching and writes the result to a new file.
    /// <para>以进程内补丁将 root 包应用到 boot 镜像，并把结果写入新文件。</para>
    /// </summary>
    /// <param name="packagePath">The root package file path. <para>root 包文件路径。</para></param>
    /// <param name="bootPath">The source boot image path. <para>源 boot 镜像路径。</para></param>
    /// <param name="outputPath">The patched boot image path. <para>打过补丁的 boot 镜像路径。</para></param>
    /// <param name="options">Patch options; <see langword="null"/> uses the defaults. <para>补丁选项；空值使用默认设置。</para></param>
    /// <param name="cancellationToken">Cancels patching and publication. <para>取消补丁与发布。</para></param>
    /// <returns>The patch outcome with its summary and outputs. <para>包含摘要与输出项的补丁结果。</para></returns>
    public Task<BootPatchResult> PatchBootAsync(string packagePath, string bootPath, string outputPath,
        BootPatchOptions? options = null, CancellationToken cancellationToken = default)
        => new BootPatchService().PatchAsync(packagePath, bootPath, outputPath, options, cancellationToken);

    private static RepackMetadataContext CaptureOperationContext(string path)
        => RepackMetadata.HasContext ? RepackMetadataContext.Unspecified.Capture()
            : Projects.ProjectContextService.Instance.CaptureMetadataContext(path);

    private static async Task<FirmwareOpResult> CompleteUnpackAsync(string sourcePath, string outputDirectory,
        Func<Task<FirmwareOpResult>> unpack, CancellationToken cancellationToken, IReadonlyFormatHandler? expectedHandler = null,
        bool overwrite = true, RepackMetadataContext context = default)
    {
        context = context.IsUnspecified ? CaptureOperationContext(outputDirectory) : context.Capture();
        using var metadataScope = RepackMetadata.UseContext(context);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadataDirectory = RepackMetadata.DefaultMetadataRoot is { } root
                ? Path.Combine(root, RepackMetadata.DetectPartitionName(Path.GetFileName(sourcePath))) : outputDirectory;
            var metadataPath = Path.Combine(metadataDirectory, RepackMetadata.FileName);
            if (File.Exists(metadataPath) && (!overwrite || !RepackMetadata.IsMetadataFile(metadataPath)))
                throw new IOException($"Repack metadata output already exists: {metadataPath}");
            var result = await unpack().ConfigureAwait(false);
            if (!result.Success) return result;
            FirmwareFormatRegistry.RegisterAll();
            var handler = expectedHandler ?? FormatDetector.Detect(sourcePath)
                ?? throw new InvalidDataException("Cannot identify the source format for repack metadata.");
            await RepackMetadata.WriteAsync(handler, sourcePath, outputDirectory, context, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return FirmwareOpResult.Fail(ex.Message); }
    }

    /// <summary>
    /// Lists all entry names in a CPIO/TAR archive without unpacking to disk.
    /// <para>列举 CPIO/TAR 归档的全部条目名称（不解包到磁盘）。</para>
    /// </summary>
    /// <param name="archivePath">The archive file path. <para>归档文件路径。</para></param>
    /// <returns>Operation result whose entries are the archive member names. <para>操作结果，Entries 为归档成员名。</para></returns>
    public async Task<FirmwareOpResult> ListCpioEntriesAsync(string archivePath)
    {
        try
        {
            await using var fs = File.OpenRead(archivePath);
            var archive = new CpioArchive();
            var entries = archive.ListEntries(fs);
            var names = entries.Select(e => e.Name).Where(n => n.Length > 0).ToList();
            return new FirmwareOpResult { Success = true, Summary = $"{names.Count} entries", Entries = names };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Replaces an extraction directory after staging a CPIO/TAR archive, restoring its contents on failure.
    /// <para>暂存 CPIO/TAR 解包结果后替换输出目录，失败时恢复既有内容。</para>
    /// </summary>
    /// <param name="archivePath">The archive file path. <para>归档文件路径。</para></param>
    /// <param name="outputDirectory">The output directory. <para>输出目录。</para></param>
    /// <param name="overwrite">Whether to replace a nonempty output directory. <para>是否替换非空输出目录。</para></param>
    /// <param name="cancellationToken">Cancels extraction before metadata publication. <para>在元数据发布前取消提取。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> UnpackCpioAsync(string archivePath, string outputDirectory,
        bool overwrite = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var context = CaptureOperationContext(target);
        var staging = Path.Combine(Path.GetDirectoryName(target)!,
            $".{Path.GetFileName(target)}.cpio-{Guid.NewGuid():N}");
        try
        {
            using var scope = RepackMetadata.UseContext(context);
            var metadataDirectory = RepackMetadata.DefaultMetadataRoot is { } root
                ? Path.Combine(root, RepackMetadata.DetectPartitionName(Path.GetFileName(archivePath))) : target;
            var metadataPath = Path.Combine(metadataDirectory, RepackMetadata.FileName);
            if (File.Exists(metadataPath) && (!overwrite || !RepackMetadata.IsMetadataFile(metadataPath)))
            {
                return FirmwareOpResult.Fail($"Repack metadata output already exists: {metadataPath}");
            }
            if (!overwrite && Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                return FirmwareOpResult.Fail($"Extraction output already exists: {target}");
            }
            var extracted = await UnpackCpioCoreAsync(archivePath, staging, true, cancellationToken).ConfigureAwait(false);
            if (!extracted.Success)
            {
                return extracted;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return await ExtractionDirectory.ReplaceAsync(target, () => CompleteUnpackAsync(
                archivePath, target, () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.Delete(target);
                    Directory.Move(staging, target);
                    return Task.FromResult(new FirmwareOpResult
                    {
                        Success = true,
                        Summary = $"Extracted archive to {target}",
                        Entries = extracted.Entries
                    });
                }, cancellationToken, new CpioFormatHandler(), overwrite, context)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private async Task<FirmwareOpResult> UnpackCpioCoreAsync(string archivePath, string outputDirectory,
        bool overwrite, CancellationToken cancellationToken)
    {
        using var scope = RepackMetadata.UseContext(RepackMetadataContext.LocalDirectory);
        var result = await new CpioFormatHandler().UnpackAsync(archivePath, outputDirectory,
            new UnpackOptions { Overwrite = overwrite }, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            File.Delete(Path.Combine(outputDirectory, RepackMetadata.FileName));
        }
        return FirmwareOpResult.From(result);
    }

    /// <summary>
    /// Packs a directory with verified atomic publication, restoring captured archive headers and links when available.
    /// <para>校验后原子发布目录归档；有重打包元数据时恢复归档头与链接，无元数据时使用目录构建器默认值。</para>
    /// </summary>
    /// <param name="inputDirectory">The input directory. <para>输入目录。</para></param>
    /// <param name="outputArchivePath">The output archive path. <para>输出归档路径。</para></param>
    /// <param name="cancellationToken">Cancels enumeration, payload copying and validation before publication. <para>在发布前取消枚举、数据复制与校验。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> PackCpioAsync(string inputDirectory, string outputArchivePath, CancellationToken cancellationToken = default)
    {
        using var scope = RepackMetadata.UseContext(CaptureOperationContext(inputDirectory));
        var result = await new CpioFormatHandler().PackAsync(inputDirectory, outputArchivePath, cancellationToken)
            .ConfigureAwait(false);
        return FirmwareOpResult.From(result);
    }

    /// <summary>
    /// Extracts specified partitions (or all partitions) from an Android OTA payload.bin to an output directory.
    /// <para>从 Android OTA payload.bin 提取指定分区（或全部分区）到输出目录。</para>
    /// </summary>
    /// <param name="payloadPath">The Android OTA payload file path. <para>Android OTA payload 文件路径。</para></param>
    /// <param name="outputDirectory">The output directory. <para>输出目录。</para></param>
    /// <param name="partitionName">Target partition name; empty means all partitions. <para>目标分区名；为空表示全部分区。</para></param>
    /// <param name="cancellationToken">Cancels metadata reads, extraction and metadata publication. <para>取消元数据读取、提取与元数据发布。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public Task<FirmwareOpResult> ExtractPayloadAsync(string payloadPath, string outputDirectory,
        string? partitionName = null, CancellationToken cancellationToken = default)
        => CompleteUnpackAsync(payloadPath, outputDirectory,
            () => ExtractPayloadCoreAsync(payloadPath, outputDirectory, partitionName, cancellationToken),
            cancellationToken, new PayloadFormatHandler(), context: CaptureOperationContext(outputDirectory));

    private async Task<FirmwareOpResult> ExtractPayloadCoreAsync(string payloadPath, string outputDirectory,
        string? partitionName, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(outputDirectory);
            var metadata = await PayloadParser.GetPayloadMetadataAsync(payloadPath, cancellationToken).ConfigureAwait(false);
            var allNames = metadata.PartitionNames;
            var targets = string.IsNullOrWhiteSpace(partitionName)
                ? allNames.Where(n => !string.IsNullOrEmpty(n)).ToArray()
                : [partitionName];
            if (targets.Length == 0 || targets.Any(name => !allNames.Contains(name, StringComparer.Ordinal)))
            {
                throw new InvalidDataException("The requested payload partition was not found.");
            }

            await PayloadParser.ExtractAsync(payloadPath, targets, outputDirectory, cancellationToken).ConfigureAwait(false);
            if (targets.Any(name => !File.Exists(Path.Combine(outputDirectory, name + ".img"))))
            {
                throw new InvalidDataException("Payload extraction did not produce every requested partition image.");
            }
            return new FirmwareOpResult { Success = true, Summary = $"Extracted {targets.Length} partitions", Entries = [.. targets] };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Verifies an Android Verified Boot VBMeta image by recomputing its embedded digest and checking its signature.
    /// <para>通过重算内嵌摘要并校验签名来验证 Android Verified Boot VBMeta 镜像。</para>
    /// </summary>
    /// <param name="vbmetaPath">The VBMeta image file path. <para>VBMeta 镜像文件路径。</para></param>
    /// <param name="publicKeyPath">An optional trusted public key file in AVB, PEM or DER form; the embedded key is used when omitted.
    /// <para>可选的受信任公钥文件（AVB、PEM 或 DER 形式），省略时使用内嵌公钥。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result whose summary reports signature validity and descriptors. <para>操作结果，摘要报告签名有效性与描述符。</para></returns>
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

                return new FirmwareOpResult
                {
                    Success = result.IsSigned && result.HashIsValid && result.SignatureIsValid,
                    Summary = sb.ToString().TrimEnd()
                };
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
    /// Signs an Android Verified Boot VBMeta image with an RSA private key, embedding the given hash descriptors.
    /// <para>使用 RSA 私钥对 Android Verified Boot VBMeta 镜像签名，并嵌入给定的哈希描述符。</para>
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

            using var key = AvbSigner.LoadRsaPrivateKey(privateKeyPath);
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

    /// <summary>Signs an APK with user-provided key material (v1/v2/v3). The input file is not modified.
    /// <para>使用用户提供的密钥材料签署 APK（v1/v2/v3）。不修改输入文件。</para></summary>
    /// <param name="inputApk">Input APK path.<para>输入 APK 路径。</para></param>
    /// <param name="outputApk">Output signed APK path.<para>输出已签名 APK 路径。</para></param>
    /// <param name="keyPath">Private key file (PKCS#8/PKCS#1, PEM or DER).
    /// <para>私钥文件（PKCS#8/PKCS#1，PEM 或 DER）。</para></param>
    /// <param name="certPath">Certificate file (PEM/DER/PFX).<para>证书文件（PEM/DER/PFX）。</para></param>
    /// <param name="keyPassword">Optional private key password.<para>可选私钥密码。</para></param>
    /// <param name="certPassword">Optional certificate (PFX) password.<para>可选证书（PFX）密码。</para></param>
    /// <param name="enableV1">Enable JAR signature.<para>启用 JAR 签名。</para></param>
    /// <param name="enableV2">Enable scheme v2.<para>启用 v2 方案。</para></param>
    /// <param name="enableV3">Enable scheme v3.<para>启用 v3 方案。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>Operation result.<para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> SignApkAsync(
        string inputApk,
        string outputApk,
        string keyPath,
        string certPath,
        string? keyPassword = null,
        string? certPassword = null,
        bool enableV1 = true,
        bool enableV2 = true,
        bool enableV3 = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var keyMaterial = ApkSignerKeyMaterial.Load(
                    keyPath, keyPassword, certPath, certPassword);
                var options = new UotanStudio.Firmware.AndroidMeta.Apk.Signing.ApkSigningOptions
                {
                    EnableV1 = enableV1,
                    EnableV2 = enableV2,
                    EnableV3 = enableV3,
                };
                UotanStudio.Firmware.AndroidMeta.Apk.Signing.ApkSigner.Sign(
                    inputApk, outputApk, keyMaterial, options, cancellationToken);

                List<string> schemes = [];
                if (enableV1) { schemes.Add("v1"); }
                if (enableV2) { schemes.Add("v2"); }
                if (enableV3) { schemes.Add("v3"); }
                return new FirmwareOpResult
                {
                    Success = true,
                    Summary = $"APK signed ({string.Join(", ", schemes)})  APK 已签名（{string.Join(", ", schemes)}）",
                    Entries = [outputApk],
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail($"APK sign failed: {ex.Message}  APK 签名失败：{ex.Message}");
        }
    }

    /// <summary>Generates a fresh APK signing key pair and self-signed certificate, round-trip verified before commit.
    /// <para>生成全新的 APK 签名密钥对与自签证书，提交前先经回环自检。</para></summary>
    /// <param name="options">Generation options (key type, subject, validity, password).
    /// <para>生成选项（密钥类型、主题、有效期、密码）。</para></param>
    /// <param name="outputDirectory">Target directory for the generated files.
    /// <para>生成文件的目标目录。</para></param>
    /// <param name="baseName">Optional base file name without extension.
    /// <para>可选的不含扩展名的基础文件名。</para></param>
    /// <param name="exportPfx">Also export a PFX containing the private key.
    /// <para>是否额外导出含私钥的 PFX。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>Paths of the generated files.<para>生成文件的路径。</para></returns>
    public async Task<ApkSigningKeyFiles> GenerateApkSigningKeyAsync(
        ApkSigningKeyOptions options,
        string outputDirectory,
        string? baseName = null,
        bool exportPfx = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return await Task.Run(() => ApkSigningKeyGenerator.Generate(options, outputDirectory, baseName, exportPfx), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Disables AVB verification on a VBMeta image by setting the disable flags and zeroing the authentication block.
    /// <para>通过置位禁用标志并清零认证块，在 VBMeta 镜像上禁用 AVB 校验。</para>
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
    /// Adds an AVB v2 hash footer to a partition image with the given RSA private key and a signed VBMeta block.
    /// <para>使用给定 RSA 私钥与已签名 VBMeta 块，为分区镜像追加 AVB v2 哈希尾部。</para>
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

            using var key = AvbSigner.LoadRsaPrivateKey(privateKeyPath);
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

    /// <summary>Re-signs an AVB partition with explicit layout and rollback parameters.
    /// <para>使用明确的布局与回滚参数重新签名 AVB 分区。</para></summary>
    /// <param name="imagePath">Source raw or Sparse image. <para>源原始或 Sparse 镜像。</para></param>
    /// <param name="privateKeyPath">RSA private key file. <para>RSA 私钥文件。</para></param>
    /// <param name="outputPath">Atomic output destination. <para>原子发布目标。</para></param>
    /// <param name="options">Protection, salt, capacity and rollback parameters.
    /// <para>保护方式、盐、容量与回滚参数。</para></param>
    /// <param name="cancellationToken">Cancels signing and publication. <para>取消签名与发布。</para></param>
    /// <returns>The backend's structured signing result. <para>后端的结构化签名结果。</para></returns>
    public async Task<AvbSignResult> ResignAvbImageAsync(
        string imagePath, string privateKeyPath, string outputPath, AvbResignOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        using var key = AvbSigner.LoadRsaPrivateKey(privateKeyPath);
        if (key is null)
        {
            return new AvbSignResult { ErrorMessage = "Failed to load the RSA private key. 无法加载 RSA 私钥。" };
        }
        return await Task.Run(() => AvbResigner.ResignImageAsync(
            imagePath, outputPath, key, options, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Verifies an explicitly mapped AVB chain without accessing a device.
    /// <para>使用显式分区映射离线验证 AVB 链，不访问设备。</para></summary>
    /// <param name="options">Exact partition paths with an optional trusted AVB root public key and rollback minima.
    /// <para>精确分区路径，附可选的受信任 AVB 根公钥与回滚下限。</para></param>
    /// <param name="cancellationToken">Cancels image reads and verification.
    /// <para>取消镜像读取与验证。</para></param>
    /// <returns>Per-image and per-partition checks, trust and rollback status, and diagnostics.
    /// <para>逐镜像和逐分区检查、信任与回滚状态以及诊断信息。</para></returns>
    /// <exception cref="ArgumentNullException">The options or required mapping is null.
    /// <para>选项或必需的映射为 null。</para></exception>
    /// <exception cref="ArgumentException">The root name, slot suffix, map size or public-key size is invalid.
    /// <para>根名称、槽位后缀、映射大小或公钥大小无效。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.
    /// <para>请求了取消。</para></exception>
    public Task<AvbChainVerifyResult> VerifyAvbChainAsync(
        AvbChainVerifyOptions options, CancellationToken cancellationToken = default)
        => AvbChainVerifier.VerifyAsync(options, cancellationToken);

    /// <summary>
    /// Unpacks a boot or vendor_boot image into a component directory with the metadata needed to repack it.
    /// <para>将 boot 或 vendor_boot 镜像解包为组件目录，并写出重打包所需的元数据。</para>
    /// </summary>
    /// <param name="bootPath">The boot / vendor_boot image file path. <para>boot / vendor_boot 镜像文件路径。</para></param>
    /// <param name="outputDirectory">The output directory. <para>输出目录。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public Task<FirmwareOpResult> UnpackBootAsync(string bootPath, string outputDirectory, CancellationToken cancellationToken = default)
        => CompleteUnpackAsync(bootPath, outputDirectory, () => UnpackBootCoreAsync(bootPath, outputDirectory, cancellationToken), cancellationToken, new BootImageFormatHandler(), context: CaptureOperationContext(outputDirectory));

    /// <summary>Extracts Boot components and metadata using one explicit context.
    /// <para>使用固定上下文提取 Boot 组件和元数据。</para></summary>
    /// <param name="bootPath">The boot image file path. <para>boot 镜像文件路径。</para></param>
    /// <param name="outputDirectory">The output directory. <para>输出目录。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="context">The repack metadata context to apply. <para>要应用的重打包元数据上下文。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public Task<FirmwareOpResult> UnpackBootAsync(string bootPath, string outputDirectory, CancellationToken cancellationToken,
        RepackMetadataContext context)
        => CompleteUnpackAsync(bootPath, outputDirectory, () => UnpackBootCoreAsync(bootPath, outputDirectory, cancellationToken),
            cancellationToken, new BootImageFormatHandler(), context: context.Capture());

    private async Task<FirmwareOpResult> UnpackBootCoreAsync(string bootPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);

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
    /// <c>dtb</c> and <c>signature</c> files and the <c>metadata.json</c> metadata.
    /// <para>将 <see cref="UnpackBootAsync"/> 产出的组件目录按原头部版本重打包为 boot 镜像，
    /// 使用 <c>kernel</c>、<c>ramdisk</c>、<c>second</c>、<c>dtb</c>、<c>signature</c> 文件与
    /// <c>metadata.json</c> 元数据。</para>
    /// </summary>
    /// <param name="inputDirectory">The directory produced by <see cref="UnpackBootAsync"/>. <para>由 <see cref="UnpackBootAsync"/> 产出的目录。</para></param>
    /// <param name="outputPath">The output boot image path. <para>输出 boot 镜像路径。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public Task<FirmwareOpResult> PackBootAsync(string inputDirectory, string outputPath, CancellationToken cancellationToken = default)
        => PackBootAsync(inputDirectory, outputPath, cancellationToken, CaptureOperationContext(inputDirectory));

    /// <summary>Reads Boot configuration and packs with a fixed metadata context.
    /// <para>使用固定元数据上下文读取 Boot 配置并打包。</para></summary>
    /// <param name="inputDirectory">The unpacked component directory. <para>已解包的组件目录。</para></param>
    /// <param name="outputPath">The output boot image path. <para>输出 boot 镜像路径。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="context">The repack metadata context to apply. <para>要应用的重打包元数据上下文。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> PackBootAsync(string inputDirectory, string outputPath, CancellationToken cancellationToken,
        RepackMetadataContext context)
    {
        var result = await new BootImageFormatHandler().PackAsync(inputDirectory, outputPath, cancellationToken, context.Capture()).ConfigureAwait(false);
        return FirmwareOpResult.From(result);
    }

    private static byte[] DecompressRamdisk(byte[] raw, UotanStudio.Firmware.BootSecurity.BootImage.Models.CompressionType compression)
    {
        return compression == UotanStudio.Firmware.BootSecurity.BootImage.Models.CompressionType.None || raw.Length == 0
            ? raw
            : BootCompressor.TryDecompress(raw, compression) ?? raw;
    }

    /// <summary>
    /// Reads the 8-byte magic of a boot or vendor_boot image without loading the whole file.
    /// <para>只读取 boot 或 vendor_boot 镜像的 8 字节魔数，不加载整个文件。</para>
    /// </summary>
    /// <param name="bootPath">The boot image file path. <para>boot 镜像文件路径。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The decoded magic string of the header. <para>解码后的头部魔数字符串。</para></returns>
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

    /// <summary>
    /// Detects whether a file starts with the Android sparse image magic.
    /// <para>检测文件是否以 Android sparse 镜像魔数开头。</para>
    /// </summary>
    /// <param name="path">The file path to probe. <para>待探测的文件路径。</para></param>
    /// <returns>True when the sparse magic is present. <para>存在 sparse 魔数时为 true。</para></returns>
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
    /// <param name="source">The stream to read from. <para>读取来源流。</para></param>
    /// <param name="target">The stream to write to. <para>写入目标流。</para></param>
    /// <param name="length">The exact byte count to copy. <para>要复制的精确字节数。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
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
    /// <param name="firmwarePath">The firmware package path. <para>固件包路径。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result whose summary reports the format and entry count. <para>操作结果，摘要报告格式与条目数。</para></returns>
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
    /// <param name="firmwarePath">The firmware package path. <para>固件包路径。</para></param>
    /// <param name="outputDirectory">The output directory. <para>输出目录。</para></param>
    /// <param name="overwrite">Whether to overwrite existing files. <para>是否覆盖已存在的文件。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public Task<FirmwareOpResult> ExtractOppoAsync(string firmwarePath, string outputDirectory, bool overwrite = true, CancellationToken cancellationToken = default)
        => CompleteUnpackAsync(firmwarePath, outputDirectory, () => ExtractOppoCoreAsync(firmwarePath, outputDirectory, overwrite, cancellationToken), cancellationToken, new OppoFormatHandler(), overwrite, context: CaptureOperationContext(outputDirectory));

    private async Task<FirmwareOpResult> ExtractOppoCoreAsync(string firmwarePath, string outputDirectory, bool overwrite = true, CancellationToken cancellationToken = default)
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
    /// <param name="filePath">The file path. <para>文件路径。</para></param>
    /// <param name="expectedHash">Expected SHA256 hex string; empty reports only the computed value. <para>预期的 SHA256 十六进制串，为空时仅报告计算值。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result whose summary is the computed value or the comparison outcome. <para>操作结果，摘要为计算值或比对结论。</para></returns>
    public async Task<FirmwareOpResult> VerifyChecksumAsync(string filePath, string? expectedHash = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var fs = File.OpenRead(filePath);
            var hash = await Sha256.ComputeHashAsync(fs, cancellationToken);
            var hex = Convert.ToHexStringLower(hash);
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
    /// Unpacks the logical partition images from an Android super image by parsing its LP metadata and copying extent ranges.
    /// <para>解析 Android super 镜像的 LP 元数据，并按各分区线性 extent 范围复制出逻辑分区镜像。</para>
    /// </summary>
    /// <param name="superPath">The super image file path. <para>super 镜像文件路径。</para></param>
    /// <param name="outputDirectory">The output directory for the extracted partition images. <para>分区镜像输出目录。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result, Entries is the extracted partition name list. <para>操作结果，Entries 为已提取的分区名称列表。</para></returns>
    public Task<FirmwareOpResult> UnpackSuperAsync(string superPath, string outputDirectory, CancellationToken cancellationToken = default)
        => CompleteUnpackAsync(superPath, outputDirectory, () => UnpackSuperCoreAsync(superPath, outputDirectory, cancellationToken), cancellationToken, context: CaptureOperationContext(outputDirectory));

    private async Task<FirmwareOpResult> UnpackSuperCoreAsync(string superPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        var result = await Task.Run(() => new LpFormatHandler().UnpackAsync(superPath, outputDirectory,
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        return FirmwareOpResult.From(result);
    }

    /// <summary>Converts an image between raw and Android sparse formats (a thin wrapper over <see cref="SparseImageConverter"/>): <c>toSparse = true</c> calls <see cref="SparseImageConverter.ConvertRawToSparse(string, string, uint)"/>, <c>false</c> calls <see cref="SparseImageConverter.ConvertSparseToRaw(IEnumerable{string}, string)"/>.
    /// <para>在 raw 与 Android sparse 格式之间转换镜像（<see cref="SparseImageConverter"/> 的薄封装）： <c>toSparse = true</c> 调用 <see cref="SparseImageConverter.ConvertRawToSparse(string, string, uint)"/>， <c>false</c> 调用 <see cref="SparseImageConverter.ConvertSparseToRaw(IEnumerable{string}, string)"/>。</para></summary>
    /// <param name="inputPath">The input image file path.<para>输入镜像文件路径。</para></param>
    /// <param name="outputPath">The output image file path.<para>输出镜像文件路径。</para></param>
    /// <param name="toSparse">Whether to convert raw to sparse (<c>true</c>) or sparse to raw (<c>false</c>).
    /// <para>是否转换为稀疏格式（<c>true</c> 为 raw→sparse，<c>false</c> 为 sparse→raw）。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>Operation result.<para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> ConvertSparseAsync(string inputPath, string outputPath, bool toSparse, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
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
    /// Merges an ordered set of Android sparse chunks into one raw image published atomically.
    /// <para>将一组按序的 Android sparse 分块合并为单个原子发布的 raw 镜像。</para>
    /// </summary>
    /// <param name="chunkPaths">The ordered chunk file paths. <para>按序的分块文件路径。</para></param>
    /// <param name="outputPath">The destination image path. <para>目标镜像路径。</para></param>
    /// <param name="deleteSources">When set, source chunks are deleted after publication. <para>置位时源分块在发布后删除。</para></param>
    /// <param name="cancellationToken">Cancels the merge before publication. <para>在发布前取消合并。</para></param>
    /// <returns>Operation result whose entries list the merged chunk names. <para>操作结果，Entries 列出已合并的分块名。</para></returns>
    public async Task<FirmwareOpResult> MergeSparseChunksAsync(
        IReadOnlyList<string> chunkPaths,
        string outputPath,
        bool deleteSources = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunkPaths);
        try
        {
            if (chunkPaths.Count == 0)
            {
                return FirmwareOpResult.Fail("At least one sparse chunk is required.");
            }

            var chunks = chunkPaths.Select(Path.GetFullPath).ToArray();
            if (chunks.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count()
                != chunks.Length)
            {
                return FirmwareOpResult.Fail("Sparse chunk paths must be unique.");
            }

            await Task.Run(() => SparseImageConverter.ConvertSparseToRawCancellable(chunks, outputPath, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (deleteSources)
            {
                foreach (var chunk in chunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Delete(chunk);
                }
            }

            return new FirmwareOpResult
            {
                Success = true,
                Summary = $"Merged {chunks.Length} sparse chunk(s) into '{outputPath}'.",
                Entries = [.. chunks.Select(path => Path.GetFileName(path) ?? path)]
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Restores a numbered sparsechunk flash set into one image published atomically from its ordered pieces.
    /// <para>将带编号的 sparsechunk 分块集合还原为单个镜像，并按有序分块原子发布。</para>
    /// </summary>
    /// <param name="selectedChunkPath">Any numbered member of the set, whose siblings are resolved from its name. <para>集合中任意带编号成员，同级分块按其名称解析。</para></param>
    /// <param name="outputPath">The destination image path. <para>目标镜像路径。</para></param>
    /// <param name="deleteSources">When set, input pieces are removed after a successful merge. <para>置位时，输入分块在合并成功后删除。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result whose entries list the merged piece names in merge order. <para>操作结果，Entries 按合并顺序列出已合并分块名。</para></returns>
    public async Task<FirmwareOpResult> RestoreSparseChunksAsync(string selectedChunkPath, string outputPath,
        bool deleteSources = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedChunkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        try
        {
            var destination = Path.GetFullPath(outputPath);
            var set = await SparseChunkSplitter.MergeAsync(selectedChunkPath, destination,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (deleteSources)
            {
                foreach (var part in set.Parts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Delete(part);
                }
            }

            return new FirmwareOpResult
            {
                Success = true,
                Summary = $"Merged {set.Parts.Count} sparsechunk piece(s) into '{destination}'.",
                Entries = [.. set.Parts.Select(path => Path.GetFileName(path) ?? path)],
                Metadata = new Dictionary<string, string> { ["variant"] = set.Variant.ToString() }
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Splits a raw image into fixed-size numbered sparsechunk pieces, each published atomically.
    /// <para>将 raw 镜像切分为固定大小的带编号 sparsechunk 分块，每块原子发布。</para>
    /// </summary>
    /// <param name="inputPath">The raw image to slice. <para>待切分的 raw 镜像。</para></param>
    /// <param name="basePath">The piece name prefix followed by the two-digit index. <para>分块名称前缀，其后接两位编号。</para></param>
    /// <param name="chunkSizeBytes">Physical bytes per piece except the final remainder; at least 1. <para>除末尾余量外每块的物理字节数，至少为 1。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result whose entries list the produced piece names in merge order. <para>操作结果，Entries 按合并顺序列出生成的分块名。</para></returns>
    public async Task<FirmwareOpResult> SplitSparseChunksAsync(string inputPath, string basePath,
        long chunkSizeBytes, CancellationToken cancellationToken = default)
    {
        try
        {
            var pieces = await SparseChunkSplitter.SplitRawAsync(inputPath, basePath, chunkSizeBytes,
                cancellationToken).ConfigureAwait(false);
            return new FirmwareOpResult
            {
                Success = true,
                Summary = $"Split '{inputPath}' into {pieces.Count} sparsechunk piece(s).",
                Entries = [.. pieces.Select(path => Path.GetFileName(path) ?? path)]
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Unpacks an Android sparse image to a directory, cascade-unpacking an inner filesystem or falling back to raw conversion.
    /// <para>将 Android sparse 镜像解包到目录，内层文件系统级联解包，否则回退为 raw 转换。</para>
    /// </summary>
    /// <param name="sparsePath">The sparse image file path. <para>稀疏镜像文件路径。</para></param>
    /// <param name="outputDirectory">The output directory. <para>输出目录。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="byteProgress">Optional filesystem byte-progress reporter. <para>可选的文件系统字节进度上报器。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> UnpackSparseImageAsync(string sparsePath, string outputDirectory, CancellationToken cancellationToken = default, IProgress<FileSystemByteProgress>? byteProgress = null)
    {
        using var metadataScope = RepackMetadata.UseContext(CaptureOperationContext(outputDirectory));
        var result = await new SparseFormatHandler().UnpackAsync(sparsePath, outputDirectory, new UnpackOptions { FileSystemProgress = byteProgress }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return FirmwareOpResult.From(result);
    }

    /// <summary>
    /// Unpacks an ext4, EROFS or F2FS image to a directory with the matching streaming parser, failing without output otherwise.
    /// <para>用匹配的流式解析器将 ext4、EROFS 或 F2FS 镜像解包到目录，其余镜像失败且不写出文件。</para>
    /// </summary>
    /// <param name="imagePath">The filesystem image file path. <para>文件系统镜像文件路径。</para></param>
    /// <param name="outputDirectory">The output directory. <para>输出目录。</para></param>
    /// <param name="progress">Optional progress reporter as (processed file count, total file count). <para>可选进度回调，值为 (已处理文件数, 文件总数)。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="byteProgress">Optional filesystem byte-progress reporter. <para>可选的文件系统字节进度上报器。</para></param>
    /// <param name="erofsExtraDevicePaths">EROFS device images in device-table order, when required. <para>需要时按设备表顺序给出的 EROFS 设备镜像。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> UnpackFileSystemImageAsync(string imagePath, string outputDirectory, IProgress<(int processed, int total)>? progress = null, CancellationToken cancellationToken = default, IProgress<FileSystemByteProgress>? byteProgress = null, IReadOnlyList<string>? erofsExtraDevicePaths = null)
    {
        using var scope = RepackMetadata.UseContext(CaptureOperationContext(outputDirectory));
        FirmwareFormatRegistry.RegisterAll();
        var handler = FormatDetector.Detect(imagePath);
        if (handler is not (Ext4FormatHandler or ErofsFormatHandler or F2fsFormatHandler))
            return FirmwareOpResult.Fail("Unrecognized filesystem image.");
        var result = await FileSystemPreservation.UnpackAsync(handler, imagePath, outputDirectory,
            new UnpackOptions { FileSystemProgress = byteProgress, ErofsExtraDevicePaths = erofsExtraDevicePaths }, cancellationToken,
            async frozen =>
            {
                var extracted = await UnpackFileSystemImageCoreAsync(frozen, outputDirectory, progress, cancellationToken, byteProgress).ConfigureAwait(false);
                return extracted.Success ? UotanStudio.Firmware.Core.FirmwareOpResult.Ok(extracted.Summary)
                    : UotanStudio.Firmware.Core.FirmwareOpResult.Fail(extracted.Summary);
            }).ConfigureAwait(false);
        return FirmwareOpResult.From(result);
    }

    private async Task<FirmwareOpResult> UnpackFileSystemImageCoreAsync(string imagePath, string outputDirectory, IProgress<(int processed, int total)>? progress = null, CancellationToken cancellationToken = default, IProgress<FileSystemByteProgress>? byteProgress = null)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                {
                    return FirmwareOpResult.Fail($"Input image not found: {imagePath ?? "<empty>"}  找不到输入镜像。");
                }

                Directory.CreateDirectory(outputDirectory);
                var header = FormatDetector.ReadHeader(imagePath, 1082);

                if (header.Length >= 1082
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1080, 2)) == 0xEF53)
                {
                    using var parser = new Ext4Parser(imagePath);
                    parser.ExtractToDirectory(outputDirectory, progress: progress, byteProgress: FileSystemByteProgress.ForStage(byteProgress, FileSystemWorkStage.Extracting), cancellationToken: cancellationToken);
                    var entries = parser.ListFiles();
                    return new FirmwareOpResult
                    {
                        Success = true,
                        Summary = $"Extracted {entries.Count} entries from '{imagePath}' to '{outputDirectory}'  已从 '{imagePath}' 解出 {entries.Count} 个条目到 '{outputDirectory}'",
                    };
                }

                if (header.Length >= 1028
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xE0F5E1E2)
                {
                    using var input = File.OpenRead(imagePath);
                    using var parser = FileSystemPreservation.OpenErofsParser(input, cancellationToken);
                    parser.ExtractToDirectory(outputDirectory, progress: progress, byteProgress: FileSystemByteProgress.ForStage(byteProgress, FileSystemWorkStage.Extracting), cancellationToken: cancellationToken);
                    var files = parser.EnumerateFiles();
                    return new FirmwareOpResult
                    {
                        Success = true,
                        Summary = $"Extracted {files.Count} files from '{imagePath}' to '{outputDirectory}'  已从 '{imagePath}' 解出 {files.Count} 个文件到 '{outputDirectory}'",
                    };
                }

                if (header.Length >= 1028
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == 0xF2F52010)
                {
                    using var parser = new F2fsParser(imagePath);
                    parser.ExtractToDirectory(outputDirectory, progress: progress, byteProgress: FileSystemByteProgress.ForStage(byteProgress, FileSystemWorkStage.Extracting), cancellationToken: cancellationToken);
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
    /// Writes an Android OTA v4 DAT image pair from a raw image as its data file plus the derived transfer list.
    /// <para>由原始镜像写出 Android OTA v4 DAT 镜像对，即数据文件与推导出的传输清单。</para>
    /// </summary>
    /// <param name="transferListPath">The source raw image path from which the DAT pair is derived. <para>用于推导 DAT 镜像对的源原始镜像路径。</para></param>
    /// <param name="outputPath">The output DAT data file path; the transfer list path derives from it. <para>输出 DAT 数据文件路径，传输清单路径由它推导。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Operation result. <para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> WriteDatAsync(string transferListPath, string outputPath, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
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
    /// Inspects a numbered DAT or recovery backup volume set without importing it.
    /// <para>检查带编号的 DAT 或 recovery 备份卷集合，但不执行导入。</para>
    /// </summary>
    /// <param name="selectedPath">Any member of the volume set. <para>卷集合中的任意成员。</para></param>
    /// <param name="cancellationToken">Cancels inspection before it starts. <para>在检查开始前取消。</para></param>
    /// <returns>The validated set and artifact kind. <para>经校验的卷集合及工件类型。</para></returns>
    public DatSplitSet InspectSplitDatOrTwrp(string selectedPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DatSplitImporter.Inspect(selectedPath);
    }

    /// <summary>
    /// Imports numbered DAT or recovery backup volumes to one validated artifact without changing existing output on failure.
    /// <para>将带编号的 DAT 或 recovery 备份卷导入为单个经校验的工件，失败时不更改现有输出。</para>
    /// </summary>
    /// <param name="selectedPath">Any numbered source volume. <para>任意带编号的源卷。</para></param>
    /// <param name="outputPath">Output artifact path. <para>输出工件路径。</para></param>
    /// <param name="progress">Optional processed and total input bytes. <para>可选的已处理与总输入字节数。</para></param>
    /// <param name="cancellationToken">Cancels import before publication. <para>在发布前取消导入。</para></param>
    /// <returns>Operation result with the imported source volume names. <para>含已导入源卷名称的操作结果。</para></returns>
    public async Task<FirmwareOpResult> ImportSplitDatOrTwrpAsync(
        string selectedPath, string outputPath,
        IProgress<(long Processed, long Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var set = await DatSplitImporter.ImportAsync(selectedPath, outputPath, progress, cancellationToken)
                .ConfigureAwait(false);
            return new FirmwareOpResult
            {
                Success = true,
                Summary = $"Imported {set.Kind} artifact to {outputPath}. 已导入 {set.Kind} 工件。",
                Entries = [.. set.Parts.Select(path => Path.GetFileName(path)!)]
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Reconstructs a full OTA image from numbered new-data volumes and their same-stem transfer list.
    /// <para>从带编号的新数据卷与同词干传输清单重建全量 OTA 镜像。</para>
    /// </summary>
    /// <param name="selectedPath">Any numbered new-data volume. <para>任意带编号的新数据卷。</para></param>
    /// <param name="outputImagePath">Output image path. <para>输出镜像路径。</para></param>
    /// <param name="progress">Optional reconstruction fraction. <para>可选的重建进度比例。</para></param>
    /// <param name="cancellationToken">Cancels reconstruction before publication. <para>在发布前取消重建。</para></param>
    /// <returns>Operation result; patch-data volumes cannot be reconstructed without a source image.
    /// <para>操作结果；补丁数据卷缺少源镜像时无法重建。</para></returns>
    public async Task<FirmwareOpResult> ReconstructSplitDatImageAsync(
        string selectedPath, string outputImagePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var set = await DatSplitImporter.ReconstructDatImageAsync(
                selectedPath, outputImagePath, progress, cancellationToken).ConfigureAwait(false);
            return new FirmwareOpResult
            {
                Success = true,
                Summary = $"Reconstructed {set.Kind} image to {outputImagePath}. 已重建 {set.Kind} 镜像。",
                Entries = [.. set.Parts.Select(path => Path.GetFileName(path)!)]
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>Builds an Android super image from raw or sparse partition images by writing LP metadata and laying out partition data.
    /// <para>由 raw 或 sparse 分区镜像构建 Android super 镜像，写入 LP 元数据并排布分区数据。</para></summary>
    /// <param name="partitions">The partitions (name + source image path).
    /// <para>分区（名称 + 源镜像路径）。</para></param>
    /// <param name="outputPath">The output super image path.<para>输出 super 镜像路径。</para></param>
    /// <param name="deviceSize">The total device (super) size in bytes.
    /// <para>设备（super）总大小（字节）。</para></param>
    /// <param name="groupName">The partition group name.<para>分区组名称。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <param name="metadataSize">The LP metadata area size in bytes.<para>LP 元数据区的字节大小。</para></param>
    /// <param name="slots">The number of metadata slots.<para>元数据槽位数量。</para></param>
    /// <param name="blockSize">The block size in bytes.<para>块大小（字节）。</para></param>
    /// <param name="sparse">Whether to sparsify the produced image.
    /// <para>是否将产出镜像转为 sparse 格式。</para></param>
    /// <param name="virtualAb">Whether to mark the image as virtual A/B.
    /// <para>是否将镜像标记为虚拟 A/B。</para></param>
    /// <returns>Operation result.<para>操作结果。</para></returns>
    public async Task<FirmwareOpResult> SynthesizeSuperAsync(
        IReadOnlyList<SuperPartitionInput> partitions,
        string outputPath,
        ulong deviceSize,
        string groupName,
        CancellationToken cancellationToken = default,
        uint metadataSize = 65536,
        uint slots = 2,
        uint blockSize = 4096,
        bool sparse = true,
        bool virtualAb = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (partitions is null || partitions.Count == 0)
                return FirmwareOpResult.Fail("No partitions supplied. 未提供任何分区。");
            var files = partitions.Select(p => new Delivery.DeliveryFile
            { Partition = p.Name, SourcePath = p.Path }).ToArray();
            await Delivery.DeliverySuperBuilder.BuildAsync(files, new()
            {
                DeviceSize = deviceSize,
                Group = string.IsNullOrWhiteSpace(groupName) ? "default" : groupName,
                MetadataSize = metadataSize,
                Slots = slots,
                BlockSize = blockSize,
                Sparse = sparse,
                VirtualAb = virtualAb
            }, outputPath, token: cancellationToken).ConfigureAwait(false);
            return new FirmwareOpResult { Success = true, Summary = $"Synthesized super image with {partitions.Count} partition(s) -> {outputPath}" };
        }
        catch (OperationCanceledException) { return FirmwareOpResult.Fail("Super synthesis cancelled. 合成 super 已取消。"); }
        catch (Exception ex) { return FirmwareOpResult.Fail(ex.Message); }
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
    /// <value>The partition name. <para>分区名称。</para></value>
    public string Name { get; }

    /// <summary>
    /// Gets the source image file path. <para>获取源镜像文件路径。</para>
    /// </summary>
    /// <value>The source image file path. <para>源镜像文件路径。</para></value>
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