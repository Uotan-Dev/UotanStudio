using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UotanStudio.Compress.Zip;
using UotanStudio.Delivery;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Firmware.BootSecurity.Avb.Verification;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Verification;
using UotanStudio.Services.Delivery;

namespace UotanStudio.Services.Firmware;

/// <summary>
/// AVB protection scheme written into a re-signed OTA partition image.
/// <para>写入重签 OTA 分区镜像的 AVB 保护方案。</para>
/// </summary>
public enum AvbOtaFooterMode
{
    /// <summary>
    /// Salted SHA-256 hash footer used by boot, dtbo and other small partitions.
    /// <para>加盐 SHA-256 哈希尾部，用于 boot、dtbo 等小分区。</para>
    /// </summary>
    Hash,

    /// <summary>
    /// dm-verity Merkle hashtree footer used by system, vendor and product partitions.
    /// <para>dm-verity Merkle 哈希树尾部，用于 system、vendor、product 等分区。</para>
    /// </summary>
    Hashtree,
}

/// <summary>
/// Inputs for re-signing the partitions of an A/B OTA payload and republishing the package.
/// <para>A/B OTA 载荷分区重签名并重新发布安装包的输入。</para>
/// </summary>
public sealed record AvbOtaPatchRequest
{
    /// <summary>
    /// Source file: either a raw <c>payload.bin</c> or an A/B OTA ZIP containing one; read only and never modified.
    /// <para>源文件：原始 <c>payload.bin</c>，或包含它的 A/B OTA ZIP；只读取，绝不修改。</para>
    /// </summary>
    public required string InputPath { get; init; }

    /// <summary>
    /// Destination of the rebuilt OTA package: an OTA ZIP for ZIP inputs and a <c>payload.bin</c> for raw payload inputs; an existing file at this path is replaced.
    /// <para>重建产物的目标位置：ZIP 输入为 OTA ZIP，原始载荷输入为 <c>payload.bin</c>；该路径上的既有文件会被替换。</para>
    /// </summary>
    public required string OutputPath { get; init; }

    /// <summary>
    /// RSA private key (PEM or DER) that signs the partition footers and the top-level vbmeta; must carry a private key.
    /// <para>用于签名分区尾部与顶层 vbmeta 的 RSA 私钥（PEM 或 DER）；必须包含私钥。</para>
    /// </summary>
    public required string AvbPrivateKeyPath { get; init; }

    /// <summary>
    /// Manifest partition names to re-sign; an empty list selects every named partition except the <c>vbmeta*</c> metadata partitions.
    /// <para>要重签的清单分区名；为空列表时选择除 <c>vbmeta*</c> 元数据分区外的全部已命名分区。</para>
    /// </summary>
    public IReadOnlyList<string> PartitionNames { get; init; } = [];

    /// <summary>
    /// AVB footer scheme applied to every target partition; defaults to <see cref="AvbOtaFooterMode.Hash"/>.
    /// <para>应用于每个目标分区的 AVB 尾部方案；默认为 <see cref="AvbOtaFooterMode.Hash"/>。</para>
    /// </summary>
    public AvbOtaFooterMode FooterMode { get; init; } = AvbOtaFooterMode.Hash;

    /// <summary>
    /// Clears the vbmeta authentication block and sets the verification-disabled flags after the top-level vbmeta is re-signed.
    /// <para>重签顶层 vbmeta 后清零其认证块并设置禁用校验标志。</para>
    /// </summary>
    public bool DisableAvbVerification { get; init; }

    /// <summary>
    /// Optional RSA private key that signs the rebuilt payload; empty leaves the payload unsigned.
    /// <para>可选的重建载荷签名 RSA 私钥；留空表示载荷不签名。</para>
    /// </summary>
    public string PayloadPrivateKeyPath { get; init; } = "";

    /// <summary>
    /// OTA signing certificate used together with <see cref="OtaSigningKeyPath"/>; empty leaves the ZIP unsigned.
    /// <para>与 <see cref="OtaSigningKeyPath"/> 配合使用的 OTA 签名证书；留空表示 ZIP 不签名。</para>
    /// </summary>
    public string OtaCertificatePath { get; init; } = "";

    /// <summary>
    /// OTA whole-file signing key used together with <see cref="OtaCertificatePath"/>; empty leaves the ZIP unsigned.
    /// <para>与 <see cref="OtaCertificatePath"/> 配合使用的 OTA 整包签名密钥；留空表示 ZIP 不签名。</para>
    /// </summary>
    public string OtaSigningKeyPath { get; init; } = "";

    /// <summary>
    /// Anti-rollback counter stored in every re-signed footer.
    /// <para>写入每个重签尾部的防回滚计数器。</para>
    /// </summary>
    public ulong RollbackIndex { get; init; }

    /// <summary>
    /// Rollback index slot (0 through 31) stored in every re-signed footer.
    /// <para>写入每个重签尾部的回滚索引槽位（0 到 31）。</para>
    /// </summary>
    public uint RollbackIndexLocation { get; init; }

    /// <summary>
    /// Exact output capacity per re-signed partition in bytes, including metadata and the footer; <see langword="null"/> emits the smallest block-aligned image.
    /// <para>每个重签分区的精确输出容量（字节），含元数据与尾部；<see langword="null"/> 输出最小块对齐镜像。</para>
    /// </summary>
    public long? PartitionSize { get; init; }

    /// <summary>
    /// Verifies the rebuilt ZIP structurally before publishing it; applies only to ZIP inputs.
    /// <para>发布前对重建 ZIP 执行结构校验；仅适用于 ZIP 输入。</para>
    /// </summary>
    public bool VerifyOutput { get; init; } = true;
}

/// <summary>
/// Outcome of an AVB OTA patch run: published location, patched partitions and collected diagnostics.
/// <para>AVB OTA 修补结果：发布位置、已修补分区与收集到的诊断信息。</para>
/// </summary>
/// <param name="Success">
/// <see langword="true"/> when the output was published and no error was recorded.
/// <para>输出已发布且未记录错误时为 <see langword="true"/>。</para>
/// </param>
/// <param name="Summary">
/// Concise bilingual summary of the run, including the failure reason when <paramref name="Success"/> is <see langword="false"/>.
/// <para>本次运行的中英双语摘要；<paramref name="Success"/> 为 <see langword="false"/> 时包含失败原因。</para>
/// </param>
/// <param name="OutputPath">
/// Published output path when the run succeeded; <see langword="null"/> when it did not.
/// <para>运行成功时的已发布输出路径；未成功时为 <see langword="null"/>。</para>
/// </param>
/// <param name="PatchedPartitions">
/// Manifest names of the partitions whose images were re-signed, in request order.
/// <para>镜像已重签的分区清单名，按请求顺序排列。</para>
/// </param>
/// <param name="Warnings">
/// Non-fatal findings such as a manifest without <c>vbmeta</c> or ignored signing options.
/// <para>非致命问题，例如清单缺少 <c>vbmeta</c> 或签名选项被忽略。</para>
/// </param>
/// <param name="Errors">
/// Fatal findings that prevented publication; empty on success.
/// <para>阻止发布的致命问题；成功时为空。</para>
/// </param>
public sealed record AvbOtaPatchResult(
    bool Success,
    string Summary,
    string? OutputPath,
    IReadOnlyList<string> PatchedPartitions,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

/// <summary>
/// Inputs for offline verification of an A/B OTA payload or ZIP, including optional trust anchors.
/// <para>A/B OTA 载荷或 ZIP 离线校验的输入，包含可选的信任锚。</para>
/// </summary>
public sealed record AvbOtaVerifyRequest
{
    /// <summary>
    /// Source file: either a raw <c>payload.bin</c> or an A/B OTA ZIP containing one; read only and never modified.
    /// <para>源文件：原始 <c>payload.bin</c>，或包含它的 A/B OTA ZIP；只读取，绝不修改。</para>
    /// </summary>
    public required string InputPath { get; init; }

    /// <summary>
    /// Optional AVB public-key blob (<c>avb_pkmd.bin</c>) that pins the top-level vbmeta signer; empty leaves root trust unverified.
    /// <para>可选的 AVB 公钥 Blob（<c>avb_pkmd.bin</c>），用于固定顶层 vbmeta 签名者；留空表示不校验根信任。</para>
    /// </summary>
    public string TrustedRootPublicKeyPath { get; init; } = "";

    /// <summary>
    /// Certificate used to verify the payload RSA signatures; empty falls back to <c>META-INF/com/android/otacert</c> inside a ZIP input.
    /// <para>用于校验载荷 RSA 签名的证书；留空时对 ZIP 输入回退到其中的 <c>META-INF/com/android/otacert</c>。</para>
    /// </summary>
    public string CertificatePath { get; init; } = "";

    /// <summary>
    /// Verifies the payload RSA signature slots when <see langword="true"/>; <see langword="false"/> leaves the verdict unknown.
    /// <para>为 <see langword="true"/> 时校验载荷 RSA 签名槽；为 <see langword="false"/> 时结论保持未知。</para>
    /// </summary>
    public bool VerifyPayloadSignatures { get; init; } = true;

    /// <summary>
    /// Suffix applied by the AVB chain to descriptors without DO_NOT_USE_AB, for example <c>_a</c>; empty means no suffix.
    /// <para>AVB 链为未设置 DO_NOT_USE_AB 的描述符添加的后缀，例如 <c>_a</c>；空值表示不加后缀。</para>
    /// </summary>
    public string SlotSuffix { get; init; } = "";
}

/// <summary>
/// Outcome of an AVB OTA verification run, carrying per-signature verdicts and the AVB chain report.
/// <para>AVB OTA 校验结果，包含逐项签名结论与 AVB 链报告。</para>
/// </summary>
/// <param name="Success">
/// <see langword="true"/> when no error was recorded.
/// <para>未记录任何错误时为 <see langword="true"/>。</para>
/// </param>
/// <param name="Summary">
/// Concise bilingual summary with partition, image and check counts plus the outcome.
/// <para>包含分区、镜像与检查数量以及最终结论的中英双语摘要。</para>
/// </param>
/// <param name="PayloadSignatureIsValid">
/// <see langword="true"/> when every present payload signature slot verified, <see langword="false"/> when one failed, and <see langword="null"/> when signatures were skipped or absent.
/// <para>已存在的载荷签名槽全部通过为 <see langword="true"/>，有一项失败为 <see langword="false"/>，跳过或缺失时为 <see langword="null"/>。</para>
/// </param>
/// <param name="RootTrusted">
/// Verdict of the trusted-root pin check; <see langword="null"/> when no trusted key was supplied.
/// <para>受信任根固定校验的结论；未提供受信任密钥时为 <see langword="null"/>。</para>
/// </param>
/// <param name="ChainSuccess">
/// Whether the AVB chain traversal completed without failures; <see langword="null"/> when it could not run.
/// <para>AVB 链遍历是否无失败完成；无法运行时为 <see langword="null"/>。</para>
/// </param>
/// <param name="Errors">
/// Fatal findings; empty only on success.
/// <para>致命问题；仅在成功时为空。</para>
/// </param>
/// <param name="Warnings">
/// Non-fatal findings such as an unsigned payload or ZIP property warnings.
/// <para>非致命问题，例如载荷未签名或 ZIP 属性警告。</para>
/// </param>
/// <param name="Images">
/// Per-metadata-image signature and rollback checks produced by the AVB chain verifier.
/// <para>AVB 链校验器给出的逐元数据镜像签名与回滚检查。</para>
/// </param>
/// <param name="Partitions">
/// Per-partition content checks produced by the AVB chain verifier.
/// <para>AVB 链校验器给出的逐分区内容检查。</para>
/// </param>
public sealed record AvbOtaVerifyResult(
    bool Success,
    string Summary,
    bool? PayloadSignatureIsValid,
    bool? RootTrusted,
    bool? ChainSuccess,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<AvbChainImageResult> Images,
    IReadOnlyList<AvbChainPartitionResult> Partitions);

/// <summary>
/// Host-side orchestration for re-signing the AVB partitions of an A/B OTA package and for verifying an existing payload or OTA ZIP offline.
/// <para>宿主机侧的 A/B OTA 安装包 AVB 分区重签编排，以及既有载荷或 OTA ZIP 的离线校验。</para>
/// </summary>
/// <remarks>
/// The service is stateless and safe to construct per caller. Intermediates are created under <c>Path.GetTempPath()</c> with GUID names and deleted on every path; inputs are never modified.
/// Every payload may come from a raw <c>payload.bin</c> or from the <c>payload.bin</c> member of an A/B OTA ZIP, which is capped at 16 GiB.
/// <para>该服务无状态，可由调用方随时构造。中间产物以 GUID 名称创建在 <c>Path.GetTempPath()</c> 下并在任何路径上删除；输入文件不会被修改。
/// 载荷可以来自原始 <c>payload.bin</c>，也可以来自 A/B OTA ZIP 中的 <c>payload.bin</c> 成员，后者上限为 16 GiB。</para>
/// </remarks>
public sealed class AvbOtaService
{
    private const string PayloadEntryName = "payload.bin";
    private const string OtaCertificateEntryName = "META-INF/com/android/otacert";
    private const string VbmetaPartitionName = "vbmeta";
    private const long MaximumPayloadBytes = 16L * 1024 * 1024 * 1024;
    private const int MaximumCertificateBytes = 1024 * 1024;
    private const int MaximumTrustedRootBytes = 2056;
    private const int MaximumVbmetaBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Lists the manifest partition names of a raw payload or of the <c>payload.bin</c> inside an A/B OTA ZIP.
    /// <para>列出原始载荷或 A/B OTA ZIP 内 <c>payload.bin</c> 的清单分区名。</para>
    /// </summary>
    /// <param name="inputPath">Payload file or ZIP archive; it must exist and carry a <c>CrAU</c> payload.
    /// <para>载荷文件或 ZIP 归档；必须存在并带有 <c>CrAU</c> 载荷。</para></param>
    /// <param name="cancellationToken">Cancels materialization and manifest parsing.
    /// <para>取消物化与清单解析。</para></param>
    /// <returns>Partition names in manifest declaration order.
    /// <para>按清单声明顺序排列的分区名。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="inputPath"/> is empty or whitespace.
    /// <para><paramref name="inputPath"/> 为空或仅包含空白字符。</para></exception>
    /// <exception cref="FileNotFoundException">The input file does not exist.
    /// <para>输入文件不存在。</para></exception>
    /// <exception cref="InvalidDataException">The input is neither a payload nor a ZIP carrying <c>payload.bin</c>.
    /// <para>输入既不是载荷，也不是包含 <c>payload.bin</c> 的 ZIP。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已收到取消信号。</para></exception>
    public async Task<IReadOnlyList<string>> ListPartitionsAsync(
        string inputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        using var scratch = new ScratchSpace();
        var payload = await MaterializePayloadAsync(inputPath, scratch.Root, cancellationToken).ConfigureAwait(false);
        var metadata = await PayloadParser.GetPayloadMetadataAsync(payload.PayloadPath, cancellationToken).ConfigureAwait(false);
        return [.. metadata.PartitionNames];
    }

    /// <summary>
    /// Re-signs the selected partitions, updates the top-level <c>vbmeta</c> descriptor table and republishes the payload or OTA ZIP.
    /// <para>重签所选分区、更新顶层 <c>vbmeta</c> 描述符表，并重新发布载荷或 OTA ZIP。</para>
    /// </summary>
    /// <param name="request">Input, output, key and footer parameters; see <see cref="AvbOtaPatchRequest"/>.
    /// <para>输入、输出、密钥与尾部参数；见 <see cref="AvbOtaPatchRequest"/>。</para></param>
    /// <param name="cancellationToken">Cancels extraction, signing, payload rebuild and publication.
    /// <para>取消提取、签名、载荷重建与发布。</para></param>
    /// <returns>The published location, patched partitions and diagnostics; failures are reported instead of thrown.
    /// <para>已发布位置、已修补分区与诊断信息；失败以结果上报而不抛出异常。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.
    /// <para><paramref name="request"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已收到取消信号。</para></exception>
    public async Task<AvbOtaPatchResult> PatchAsync(
        AvbOtaPatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<string>();
        var errors = new List<string>();
        var patched = new List<string>();
        using var scratch = new ScratchSpace();
        string? stagedPayload = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePatchPaths(request, errors);
            if (errors.Count > 0)
            {
                return FailPatch("The AVB OTA patch request was rejected. 已拒绝该 AVB OTA 修补请求。", warnings, errors);
            }

            var payload = await MaterializePayloadAsync(request.InputPath, scratch.Root, cancellationToken).ConfigureAwait(false);
            if (payload.FromZip)
            {
                ValidateOtaSigningOptions(request, errors);
                if (errors.Count > 0)
                {
                    return FailPatch("The OTA signing options are incomplete. OTA 签名选项不完整。", warnings, errors);
                }
            }
            else if (!string.IsNullOrWhiteSpace(request.OtaCertificatePath) || !string.IsNullOrWhiteSpace(request.OtaSigningKeyPath))
            {
                warnings.Add("OTA whole-file signing options were ignored because the input is a raw payload. 输入为原始载荷，已忽略 OTA 整包签名选项。");
            }

            var metadata = await PayloadParser.GetPayloadMetadataAsync(payload.PayloadPath, cancellationToken).ConfigureAwait(false);
            var targets = ResolveTargets(request.PartitionNames, metadata.PartitionNames, errors);
            if (errors.Count > 0)
            {
                return FailPatch("The requested partitions cannot be patched. 无法修补所请求的分区。", warnings, errors);
            }

            var vbmetaName = ResolveManifestName(metadata.PartitionNames, VbmetaPartitionName);
            var extractionDirectory = scratch.CreateDirectory("images");
            var extractNames = new List<string>(targets);
            if (vbmetaName is not null)
            {
                extractNames.Add(vbmetaName);
            }

            await PayloadParser.ExtractAsync(
                payload.PayloadPath, [.. extractNames], extractionDirectory, cancellationToken).ConfigureAwait(false);

            foreach (var name in targets)
            {
                if (!File.Exists(Path.Combine(extractionDirectory, name + ".img")))
                {
                    errors.Add($"Partition '{name}' produced no image during extraction. 提取时未生成分区 '{name}' 的镜像。");
                }
            }

            if (errors.Count > 0)
            {
                return FailPatch("The payload did not provide every requested partition. 载荷未提供全部所请求的分区。", warnings, errors);
            }

            using var avbKey = AvbSigner.LoadRsaPrivateKey(request.AvbPrivateKeyPath);
            if (avbKey is null)
            {
                errors.Add($"The AVB private key '{request.AvbPrivateKeyPath}' could not be loaded. 无法加载 AVB 私钥 '{request.AvbPrivateKeyPath}'。");
                return FailPatch("The AVB private key could not be loaded. 无法加载 AVB 私钥。", warnings, errors);
            }

            var signedDirectory = scratch.CreateDirectory("signed");
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var signedPath = Path.Combine(signedDirectory, name + ".img");
                var signResult = await AvbResigner.ResignImageAsync(
                    Path.Combine(extractionDirectory, name + ".img"),
                    signedPath,
                    avbKey,
                    new AvbResignOptions
                    {
                        Mode = request.FooterMode == AvbOtaFooterMode.Hashtree
                            ? AvbResignMode.Hashtree
                            : AvbResignMode.Hash,
                        PartitionName = name,
                        RollbackIndex = request.RollbackIndex,
                        RollbackIndexLocation = request.RollbackIndexLocation,
                        PartitionSize = request.PartitionSize,
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!signResult.Success)
                {
                    errors.Add($"Re-signing partition '{name}' failed: {signResult.ErrorMessage ?? "unknown error"}. 重签分区 '{name}' 失败：{signResult.ErrorMessage ?? "未知错误"}。");
                    continue;
                }

                replacements[name] = signedPath;
                patched.Add(name);
            }

            if (errors.Count > 0)
            {
                return FailPatch("At least one partition image could not be re-signed. 至少有一个分区镜像重签失败。", warnings, errors);
            }

            if (vbmetaName is null)
            {
                warnings.Add("The payload manifest has no vbmeta partition, so the top-level AVB metadata was not re-signed. 载荷清单没有 vbmeta 分区，未重签顶层 AVB 元数据。");
            }
            else
            {
                var vbmetaPath = Path.Combine(extractionDirectory, vbmetaName + ".img");
                if (!File.Exists(vbmetaPath))
                {
                    warnings.Add("The payload declares vbmeta but no image was extracted, so the top-level AVB metadata was not re-signed. 清单声明了 vbmeta 但未提取到镜像，未重签顶层 AVB 元数据。");
                }
                else
                {
                    var signedImages = new List<(string Name, string Path)>(targets.Count);
                    foreach (var name in targets)
                    {
                        signedImages.Add((name, replacements[name]));
                    }

                    var vbmetaOutput = await ResignVbmetaAsync(
                        vbmetaPath, signedImages, signedDirectory, avbKey, request.DisableAvbVerification,
                        errors, cancellationToken).ConfigureAwait(false);
                    if (vbmetaOutput is null)
                    {
                        return FailPatch("The top-level vbmeta could not be re-signed. 顶层 vbmeta 重签失败。", warnings, errors);
                    }

                    replacements[vbmetaName] = vbmetaOutput;
                }
            }

            using var payloadKey = LoadPayloadKey(request.PayloadPrivateKeyPath, errors);
            if (errors.Count > 0)
            {
                return FailPatch("The payload signing key could not be loaded. 无法加载载荷签名密钥。", warnings, errors);
            }

            if (payloadKey is null)
            {
                warnings.Add("No payload signing key was supplied; the rebuilt payload is unsigned. 未提供载荷签名密钥，重建后的载荷未签名。");
            }

            string outputPath;
            if (payload.FromZip)
            {
                var rebuildDirectory = scratch.CreateDirectory("rebuild");
                var rebuiltPayload = Path.Combine(rebuildDirectory, PayloadEntryName);
                await PayloadPatcher.ReplaceAsync(
                    payload.PayloadPath, rebuiltPayload, replacements, payloadKey, cancellationToken).ConfigureAwait(false);
                var rebuild = await OtaZipPatchService.RebuildAsync(
                    request.InputPath,
                    request.OutputPath,
                    new OtaZipRebuildOptions
                    {
                        PayloadPath = rebuiltPayload,
                        CertificatePath = request.OtaCertificatePath,
                        SigningKeyPath = request.OtaSigningKeyPath,
                        VerifyOutput = request.VerifyOutput,
                    },
                    new DeliveryKeyProvider(),
                    cancellationToken).ConfigureAwait(false);
                outputPath = rebuild.OutputPath;
                foreach (var warning in rebuild.Verification.Warnings)
                {
                    warnings.Add(warning);
                }

                if (!request.VerifyOutput)
                {
                    foreach (var verificationError in rebuild.Verification.Errors)
                    {
                        warnings.Add(verificationError);
                    }
                }
            }
            else
            {
                outputPath = Path.GetFullPath(request.OutputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                stagedPayload = TempFileHelper.CreateAdjacentPath(outputPath);
                await PayloadPatcher.ReplaceAsync(
                    payload.PayloadPath, stagedPayload, replacements, payloadKey, cancellationToken).ConfigureAwait(false);
                File.Move(stagedPayload, outputPath, overwrite: true);
                stagedPayload = null;
            }

            if (request.VerifyOutput)
            {
                await VerifyPatchOutputAsync(outputPath, payload.FromZip, [.. replacements.Keys], errors, cancellationToken)
                    .ConfigureAwait(false);
                if (errors.Count > 0)
                {
                    return FailPatch("The rebuilt output failed verification. 重建产物未通过校验。", warnings, errors);
                }
            }

            var summary = $"Patched {patched.Count} partition(s) and rebuilt the {(payload.FromZip ? "OTA ZIP" : "payload")}: '{outputPath}'. "
                + $"已修补 {patched.Count} 个分区并重建{(payload.FromZip ? " OTA ZIP" : "载荷")}：'{outputPath}'。";
            return new AvbOtaPatchResult(true, summary, outputPath, [.. patched], [.. warnings], [.. errors]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AvbOtaPatchResult(
                false,
                $"The AVB OTA patch failed: {ex.Message}. AVB OTA 修补失败：{ex.Message}。",
                null,
                [.. patched],
                [.. warnings],
                [.. errors, ex.Message]);
        }
        finally
        {
            TempFileHelper.SafeDelete(stagedPayload);
        }
    }

    /// <summary>
    /// Verifies an A/B OTA package offline: ZIP structure, payload RSA signatures and the AVB chain rooted at <c>vbmeta</c>.
    /// <para>离线校验 A/B OTA 安装包：ZIP 结构、载荷 RSA 签名，以及以 <c>vbmeta</c> 为根的 AVB 链。</para>
    /// </summary>
    /// <param name="request">Input, certificate, trust anchor and slot options; see <see cref="AvbOtaVerifyRequest"/>.
    /// <para>输入、证书、信任锚与槽位选项；见 <see cref="AvbOtaVerifyRequest"/>。</para></param>
    /// <param name="cancellationToken">Cancels materialization, extraction and verification.
    /// <para>取消物化、提取与校验。</para></param>
    /// <returns>Per-signature verdicts, the AVB chain report and collected diagnostics; failures are reported instead of thrown.
    /// <para>逐项签名结论、AVB 链报告与收集到的诊断信息；失败以结果上报而不抛出异常。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.
    /// <para><paramref name="request"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已收到取消信号。</para></exception>
    public async Task<AvbOtaVerifyResult> VerifyAsync(
        AvbOtaVerifyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<string>();
        var errors = new List<string>();
        IReadOnlyList<AvbChainImageResult> images = [];
        IReadOnlyList<AvbChainPartitionResult> partitions = [];
        bool? payloadSignatureIsValid = null;
        bool? rootTrusted = null;
        bool? chainSuccess = null;
        using var scratch = new ScratchSpace();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(request.InputPath))
            {
                const string missingInput = "InputPath is required. 必须提供 InputPath。";
                errors.Add(missingInput);
                return new AvbOtaVerifyResult(false, missingInput, null, null, null, [.. errors], [.. warnings], [], []);
            }

            var payload = await MaterializePayloadAsync(request.InputPath, scratch.Root, cancellationToken).ConfigureAwait(false);
            var metadata = await PayloadParser.GetPayloadMetadataAsync(payload.PayloadPath, cancellationToken).ConfigureAwait(false);

            if (payload.FromZip)
            {
                var zipVerification = OtaZipPatchService.Verify(request.InputPath);
                foreach (var error in zipVerification.Errors)
                {
                    errors.Add(error);
                }

                foreach (var warning in zipVerification.Warnings)
                {
                    warnings.Add(warning);
                }
            }

            if (request.VerifyPayloadSignatures)
            {
                payloadSignatureIsValid = await VerifyPayloadSignatureAsync(
                    request, payload, scratch.Root, warnings, errors, cancellationToken).ConfigureAwait(false);
            }

            var chain = await VerifyChainAsync(
                request, metadata.PartitionNames, payload.PayloadPath, scratch, errors, cancellationToken).ConfigureAwait(false);
            if (chain is { } outcome)
            {
                chainSuccess = outcome.Success;
                rootTrusted = outcome.RootTrusted;
                images = outcome.Images;
                partitions = outcome.Partitions;
            }

            var success = errors.Count == 0;
            var summary = BuildVerifySummary(metadata.PartitionNames.Count, success, payloadSignatureIsValid, images.Count, partitions.Count);
            return new AvbOtaVerifyResult(
                success, summary, payloadSignatureIsValid, rootTrusted, chainSuccess,
                [.. errors], [.. warnings], images, partitions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AvbOtaVerifyResult(
                false,
                $"AVB OTA verification failed: {ex.Message}. AVB OTA 校验失败：{ex.Message}。",
                payloadSignatureIsValid,
                rootTrusted,
                chainSuccess,
                [.. errors, ex.Message],
                [.. warnings],
                images,
                partitions);
        }
    }

    private static void ValidatePatchPaths(AvbOtaPatchRequest request, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(request.InputPath))
        {
            errors.Add("InputPath is required. 必须提供 InputPath。");
        }
        else if (!File.Exists(request.InputPath))
        {
            errors.Add($"The input '{request.InputPath}' does not exist. 输入文件 '{request.InputPath}' 不存在。");
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            errors.Add("OutputPath is required. 必须提供 OutputPath。");
        }

        if (string.IsNullOrWhiteSpace(request.AvbPrivateKeyPath))
        {
            errors.Add("AvbPrivateKeyPath is required. 必须提供 AvbPrivateKeyPath。");
        }
        else if (!File.Exists(request.AvbPrivateKeyPath))
        {
            errors.Add($"The AVB private key '{request.AvbPrivateKeyPath}' does not exist. AVB 私钥 '{request.AvbPrivateKeyPath}' 不存在。");
        }

        if (!string.IsNullOrWhiteSpace(request.PayloadPrivateKeyPath) && !File.Exists(request.PayloadPrivateKeyPath))
        {
            errors.Add($"The payload private key '{request.PayloadPrivateKeyPath}' does not exist. 载荷私钥 '{request.PayloadPrivateKeyPath}' 不存在。");
        }
    }

    private static void ValidateOtaSigningOptions(AvbOtaPatchRequest request, List<string> errors)
    {
        var hasCertificate = !string.IsNullOrWhiteSpace(request.OtaCertificatePath);
        var hasSigningKey = !string.IsNullOrWhiteSpace(request.OtaSigningKeyPath);
        if (hasCertificate != hasSigningKey)
        {
            errors.Add("The OTA certificate and signing key must be supplied together or both left empty. OTA 证书与签名密钥必须同时提供或同时留空。");
            return;
        }

        if (hasCertificate && !File.Exists(request.OtaCertificatePath))
        {
            errors.Add($"The OTA certificate '{request.OtaCertificatePath}' does not exist. OTA 证书 '{request.OtaCertificatePath}' 不存在。");
        }

        if (hasSigningKey && !File.Exists(request.OtaSigningKeyPath))
        {
            errors.Add($"The OTA signing key '{request.OtaSigningKeyPath}' does not exist. OTA 签名密钥 '{request.OtaSigningKeyPath}' 不存在。");
        }
    }

    private static List<string> ResolveTargets(
        IReadOnlyList<string> requested,
        IReadOnlyList<string> manifestNames,
        List<string> errors)
    {
        var targets = new List<string>();
        if (requested.Count == 0)
        {
            foreach (var name in manifestNames)
            {
                if (IsVbmetaMetadataPartition(name))
                {
                    continue;
                }

                targets.Add(name);
            }

            if (targets.Count == 0)
            {
                errors.Add("The payload manifest declares no partition other than the vbmeta* metadata partitions that can be re-signed. 载荷清单除 vbmeta* 元数据分区外没有可重签的分区。");
            }

            return targets;
        }

        foreach (var requestedName in requested)
        {
            if (string.IsNullOrWhiteSpace(requestedName))
            {
                errors.Add("A requested partition name is empty. 存在空白的分区名请求。");
                continue;
            }

            var name = requestedName.Trim();
            if (IsVbmetaMetadataPartition(name))
            {
                errors.Add($"Partition '{name}' is AVB metadata and cannot be a patch target; only the top-level vbmeta is re-signed from the updated descriptor table. 分区 '{name}' 属于 AVB 元数据，不能作为修补目标；只有顶层 vbmeta 会依据更新后的描述符表重签。");
                continue;
            }

            var resolved = ResolveManifestName(manifestNames, name);
            if (resolved is null)
            {
                errors.Add($"Partition '{name}' is not declared by the payload manifest. 载荷清单未声明分区 '{name}'。");
                continue;
            }

            if (!targets.Contains(resolved, StringComparer.Ordinal))
            {
                targets.Add(resolved);
            }
        }

        if (targets.Count == 0 && errors.Count == 0)
        {
            errors.Add("No partition remains to be patched. 没有剩余可修补的分区。");
        }

        return targets;
    }

    private static bool IsVbmetaMetadataPartition(string name)
        => name.StartsWith(VbmetaPartitionName, StringComparison.OrdinalIgnoreCase);

    private static async Task VerifyPatchOutputAsync(
        string outputPath,
        bool fromZip,
        IReadOnlyList<string> expectedPartitions,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        if (fromZip)
        {
            var verification = OtaZipPatchService.Verify(outputPath);
            foreach (var error in verification.Errors)
            {
                errors.Add(error);
            }

            return;
        }

        var metadata = await PayloadParser.GetPayloadMetadataAsync(outputPath, cancellationToken).ConfigureAwait(false);
        foreach (var name in expectedPartitions)
        {
            if (ResolveManifestName(metadata.PartitionNames, name) is null)
            {
                errors.Add($"The rebuilt payload no longer declares partition '{name}'. 重建后的载荷不再声明分区 '{name}'。");
            }
        }
    }

    private static async Task<string?> ResignVbmetaAsync(
        string vbmetaPath,
        IReadOnlyList<(string Name, string Path)> signedImages,
        string outputDirectory,
        RSA key,
        bool disableVerification,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var table = await ReadDescriptorTableAsync(vbmetaPath, cancellationToken).ConfigureAwait(false);
        foreach (var (name, path) in signedImages)
        {
            var descriptors = await AvbDescriptorTable.FromSignedImageAsync(path, cancellationToken).ConfigureAwait(false);
            table = table.ReplaceContentDescriptors(name, descriptors);
        }

        var signedVbmetaPath = Path.Combine(outputDirectory, "vbmeta.signed.img");
        var signResult = await AvbSigner.SignVbmetaWithDescriptorTableAsync(
            vbmetaPath, key, signedVbmetaPath, table, cancellationToken).ConfigureAwait(false);
        if (!signResult.Success)
        {
            errors.Add($"Re-signing the top-level vbmeta failed: {signResult.ErrorMessage ?? "unknown error"}. 重签顶层 vbmeta 失败：{signResult.ErrorMessage ?? "未知错误"}。");
            return null;
        }

        if (!disableVerification)
        {
            return await PadVbmetaToPayloadBlockAsync(signedVbmetaPath, outputDirectory, cancellationToken)
                .ConfigureAwait(false);
        }

        var disabledVbmetaPath = Path.Combine(outputDirectory, "vbmeta.disabled.img");
        var disableResult = await AvbSigner.DisableVbmetaVerificationAsync(
            signedVbmetaPath, disabledVbmetaPath, cancellationToken).ConfigureAwait(false);
        if (!disableResult.Success)
        {
            errors.Add($"Disabling vbmeta verification failed: {disableResult.ErrorMessage ?? "unknown error"}. 禁用 vbmeta 校验失败：{disableResult.ErrorMessage ?? "未知错误"}。");
            return null;
        }

        return await PadVbmetaToPayloadBlockAsync(disabledVbmetaPath, outputDirectory, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> PadVbmetaToPayloadBlockAsync(
        string vbmetaPath, string outputDirectory, CancellationToken cancellationToken)
    {
        const int blockSize = 4096;
        var length = new FileInfo(vbmetaPath).Length;
        var remainder = length % blockSize;
        if (remainder == 0)
        {
            return vbmetaPath;
        }

        var paddedPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(vbmetaPath) + ".padded.img");
        await using (var source = new FileStream(vbmetaPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var target = new FileStream(paddedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(target, 65536, cancellationToken).ConfigureAwait(false);
            await target.WriteAsync(new byte[blockSize - (int)remainder], cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return paddedPath;
    }

    private static async Task<AvbDescriptorTable> ReadDescriptorTableAsync(
        string vbmetaPath, CancellationToken cancellationToken)
    {
        var magic = new byte[4];
        await using (var stream = new FileStream(
            vbmetaPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length <= magic.Length)
            {
                throw new InvalidDataException($"'{vbmetaPath}' is too small to carry AVB metadata.");
            }

            await stream.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
            if (!magic.AsSpan().SequenceEqual("AVB0"u8))
            {
                return await AvbDescriptorTable.FromSignedImageAsync(vbmetaPath, cancellationToken).ConfigureAwait(false);
            }

            if (stream.Length > MaximumVbmetaBytes)
            {
                throw new InvalidDataException($"'{vbmetaPath}' exceeds the 16 MiB vbmeta limit.");
            }

            var vbmeta = new byte[(int)stream.Length];
            stream.Position = 0;
            await stream.ReadExactlyAsync(vbmeta, cancellationToken).ConfigureAwait(false);
            return AvbDescriptorTable.Parse(vbmeta);
        }
    }

    private static async Task<bool?> VerifyPayloadSignatureAsync(
        AvbOtaVerifyRequest request,
        MaterializedPayload payload,
        string workDirectory,
        List<string> warnings,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            var certificatePath = request.CertificatePath;
            if (string.IsNullOrWhiteSpace(certificatePath) && payload.FromZip)
            {
                certificatePath = await ExtractOtaCertificateAsync(
                    request.InputPath, workDirectory, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(certificatePath))
            {
                errors.Add("Payload signature verification was requested but no OTA certificate was supplied or found in the ZIP. 请求了载荷签名校验，但未提供 OTA 证书，ZIP 中也没有该证书。");
                return null;
            }

            if (!File.Exists(certificatePath))
            {
                errors.Add($"The OTA certificate '{certificatePath}' does not exist. OTA 证书 '{certificatePath}' 不存在。");
                return null;
            }

            using var certificate = LoadCertificate(certificatePath);
            using var publicKey = certificate.GetRSAPublicKey();
            if (publicKey is null)
            {
                errors.Add($"The certificate '{certificatePath}' does not carry an RSA public key. 证书 '{certificatePath}' 不包含 RSA 公钥。");
                return null;
            }

            var signature = await PayloadSignatureVerifier.VerifyAsync(
                payload.PayloadPath, publicKey, cancellationToken).ConfigureAwait(false);
            if (signature.MetadataSignatureIsValid is null && signature.PayloadSignatureIsValid is null)
            {
                errors.Add($"The payload carries no RSA signature to verify: {signature.ErrorMessage ?? "no signature slot"}. 载荷未携带可校验的 RSA 签名：{signature.ErrorMessage ?? "没有签名槽"}。");
                return null;
            }

            if (!signature.IsSigned)
            {
                errors.Add($"The payload signature does not verify: {signature.ErrorMessage ?? "signature mismatch"}. 载荷签名校验失败：{signature.ErrorMessage ?? "签名不匹配"}。");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"Payload signature verification failed: {ex.Message}. 载荷签名校验失败：{ex.Message}。");
            return null;
        }
    }

    private static async Task<ChainOutcome?> VerifyChainAsync(
        AvbOtaVerifyRequest request,
        IReadOnlyList<string> manifestNames,
        string payloadPath,
        ScratchSpace scratch,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        if (ResolveManifestName(manifestNames, VbmetaPartitionName) is null)
        {
            errors.Add("The payload manifest has no vbmeta partition, so the AVB chain cannot be verified. 载荷清单没有 vbmeta 分区，无法校验 AVB 链。");
            return null;
        }

        try
        {
            var extractionDirectory = scratch.CreateDirectory("images");
            await PayloadParser.ExtractAsync(
                payloadPath, [.. manifestNames], extractionDirectory, cancellationToken).ConfigureAwait(false);

            var partitionPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in manifestNames)
            {
                var imagePath = Path.Combine(extractionDirectory, name + ".img");
                if (File.Exists(imagePath))
                {
                    partitionPaths[name] = imagePath;
                }
            }

            if (!partitionPaths.ContainsKey(VbmetaPartitionName))
            {
                errors.Add("The extracted images do not contain vbmeta, so the AVB chain cannot be verified. 提取结果中没有 vbmeta，无法校验 AVB 链。");
                return null;
            }

            byte[]? trustedRoot = null;
            var trustedRootPath = request.TrustedRootPublicKeyPath;
            if (!string.IsNullOrWhiteSpace(trustedRootPath))
            {
                if (!File.Exists(trustedRootPath))
                {
                    errors.Add($"The trusted root public key '{trustedRootPath}' does not exist. 受信任根公钥 '{trustedRootPath}' 不存在。");
                    return null;
                }

                var length = new FileInfo(trustedRootPath).Length;
                if (length is <= 0 or > MaximumTrustedRootBytes)
                {
                    errors.Add($"The trusted root public key '{trustedRootPath}' must contain 1 to {MaximumTrustedRootBytes} bytes. 受信任根公钥 '{trustedRootPath}' 必须包含 1 到 {MaximumTrustedRootBytes} 字节。");
                    return null;
                }

                trustedRoot = await File.ReadAllBytesAsync(trustedRootPath, cancellationToken).ConfigureAwait(false);
            }

            var result = await AvbChainVerifier.VerifyAsync(
                new AvbChainVerifyOptions
                {
                    RootPartitionName = VbmetaPartitionName,
                    PartitionPaths = partitionPaths,
                    SlotSuffix = request.SlotSuffix,
                    TrustedRootPublicKey = trustedRoot,
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var error in result.Errors)
            {
                errors.Add(error);
            }

            if (!result.Success && result.Errors.Count == 0)
            {
                errors.Add("The AVB chain did not verify. AVB 链未通过校验。");
            }

            return new ChainOutcome(result.Success, result.RootKeyIsTrusted, result.Images, result.Partitions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"AVB chain verification failed: {ex.Message}. AVB 链校验失败：{ex.Message}。");
            return null;
        }
    }

    private static string BuildVerifySummary(
        int partitionCount, bool success, bool? payloadSignature, int imageCount, int contentCount)
    {
        var signature = payloadSignature switch
        {
            true => "valid",
            false => "invalid",
            _ => "not checked",
        };
        var signatureChinese = payloadSignature switch
        {
            true => "有效",
            false => "无效",
            _ => "未检查",
        };
        return $"AVB OTA verification {(success ? "succeeded" : "failed")}: {partitionCount} partition(s), {imageCount} metadata image(s), {contentCount} content check(s), payload signature {signature}. "
            + $"AVB OTA 校验{(success ? "通过" : "失败")}：{partitionCount} 个分区、{imageCount} 个元数据镜像、{contentCount} 个内容检查，载荷签名{signatureChinese}。";
    }

    private static async Task<MaterializedPayload> MaterializePayloadAsync(
        string inputPath, string workDirectory, CancellationToken cancellationToken)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"The OTA input '{inputPath}' does not exist. OTA 输入文件 '{inputPath}' 不存在。", inputPath);
        }

        if (PayloadTool.IsPayload(inputPath))
        {
            return new MaterializedPayload(inputPath, false);
        }

        ZipArchive archive;
        try
        {
            archive = ZipContainer.OpenRead(inputPath);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                $"The input '{inputPath}' is neither a payload nor an A/B OTA ZIP: {ex.Message}. 输入 '{inputPath}' 既不是载荷也不是 A/B OTA ZIP：{ex.Message}。", ex);
        }

        using (archive)
        {
            var entry = FindEntry(archive, PayloadEntryName)
                ?? throw new InvalidDataException(
                    $"The ZIP '{inputPath}' does not contain {PayloadEntryName}. ZIP '{inputPath}' 不包含 {PayloadEntryName}。");
            var target = Path.Combine(workDirectory, PayloadEntryName);
            await using (var output = new FileStream(
                target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await Task.Run(
                    () => ZipContainer.CopyEntryTo(entry, output, MaximumPayloadBytes, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return new MaterializedPayload(target, true);
        }
    }

    private static async Task<string?> ExtractOtaCertificateAsync(
        string zipPath, string workDirectory, CancellationToken cancellationToken)
    {
        using var archive = ZipContainer.OpenRead(zipPath);
        var entry = FindEntry(archive, OtaCertificateEntryName);
        if (entry is null)
        {
            return null;
        }

        var target = Path.Combine(workDirectory, "otacert.pem");
        await using (var output = new FileStream(
            target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await Task.Run(
                () => ZipContainer.CopyEntryTo(entry, output, MaximumCertificateBytes, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return target;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string entryName)
    {
        var direct = archive.GetEntry(entryName);
        if (direct is not null)
        {
            return direct;
        }

        foreach (var entry in archive.Entries)
        {
            if (string.Equals(entry.FullName.Replace('\\', '/'), entryName, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static X509Certificate2 LoadCertificate(string path)
    {
        var bytes = ReadBounded(path, MaximumCertificateBytes);
        if (bytes.AsSpan().StartsWith("-----BEGIN CERTIFICATE-----"u8))
        {
            return X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(bytes));
        }

        return X509CertificateLoader.LoadCertificate(bytes);
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        var length = new FileInfo(path).Length;
        if (length <= 0 || length > maximumBytes)
        {
            throw new InvalidDataException($"'{path}' must contain 1 to {maximumBytes} bytes.");
        }

        return File.ReadAllBytes(path);
    }

    private static RSA? LoadPayloadKey(string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var key = AvbSigner.LoadRsaPrivateKey(path);
        if (key is null)
        {
            errors.Add($"The payload private key '{path}' could not be loaded. 无法加载载荷私钥 '{path}'。");
        }

        return key;
    }

    private static string? ResolveManifestName(IReadOnlyList<string> manifestNames, string name)
    {
        foreach (var candidate in manifestNames)
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static AvbOtaPatchResult FailPatch(string summary, List<string> warnings, List<string> errors)
        => new(false, summary, null, [], [.. warnings], [.. errors]);

    private readonly record struct MaterializedPayload(string PayloadPath, bool FromZip);

    private readonly record struct ChainOutcome(
        bool? Success,
        bool? RootTrusted,
        IReadOnlyList<AvbChainImageResult> Images,
        IReadOnlyList<AvbChainPartitionResult> Partitions);

    private sealed class ScratchSpace : IDisposable
    {
        internal ScratchSpace()
        {
            Root = Path.Combine(Path.GetTempPath(), $"uotan-avb-ota-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal string CreateDirectory(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}