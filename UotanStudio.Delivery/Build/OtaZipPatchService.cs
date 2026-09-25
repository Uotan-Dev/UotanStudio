using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Parser;

namespace UotanStudio.Services.Delivery;

/// <summary>
/// Selects which ZIP members an existing A/B OTA package replaces during a bounded patch.
/// <para>选择在受限修补过程中替换既有 A/B OTA 安装包的哪些 ZIP 成员。</para>
/// </summary>
public sealed record OtaZipPatchOptions
{
    /// <summary>
    /// Maps ZIP member names to the on-disk files whose bytes replace them; members absent from the map are
    /// copied unchanged and map keys with no existing member are appended to the archive.
    /// <para>将 ZIP 成员名映射到用于替换其字节内容的磁盘文件；未出现在映射中的成员原样复制，映射中不存在于
    /// 原包的键会作为新成员追加到归档。</para>
    /// </summary>
    /// <value>Member-name to file-path map; keys are normalized to forward slashes before use.
    /// <para>成员名到文件路径的映射；键在使用前会被规范化为正斜杠。</para></value>
    /// <returns>The replacement set consumed by <see cref="OtaZipPatchService.PatchAsync"/>.
    /// <para>供 <see cref="OtaZipPatchService.PatchAsync"/> 消费的替换集合。</para></returns>
    public IReadOnlyDictionary<string, string> ReplacementFiles { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Permits <c>payload.bin</c> to be replaced, which additionally requires a matching
    /// <c>payload_properties.txt</c> entry so the CrAU header metadata stays consistent.
    /// <para>允许替换 <c>payload.bin</c>，此时还必须提供与之配套的 <c>payload_properties.txt</c> 条目，
    /// 以保证 CrAU 头部元数据保持一致。</para>
    /// </summary>
    /// <value><see langword="false"/> by default, so a payload swap without properties is rejected.
    /// <para>默认为 <see langword="false"/>，从而拒绝在没有配套属性文件时替换 payload。</para></value>
    /// <returns>Whether a <c>payload.bin</c> replacement is authorized.
    /// <para>是否授权替换 <c>payload.bin</c>。</para></returns>
    public bool AllowPayloadReplacement { get; init; }
}

/// <summary>
/// Reports the published archive, the members that were written from replacement files, and whether the
/// outer OTA signature was lost.
/// <para>报告已发布的归档、由替换文件写入的成员，以及外层 OTA 签名是否已失效。</para>
/// </summary>
/// <param name="OutputPath">
/// Absolute path of the newly published ZIP; the previous file at that path has been overwritten.
/// <para>新发布 ZIP 的绝对路径；该路径上原有的文件已被覆盖。</para>
/// </param>
/// <param name="ReplacedEntries">
/// Normalized member names taken from the replacement map, in source order.
/// <para>取自替换映射的规范化成员名，保持源顺序。</para>
/// </param>
/// <param name="OuterSignatureInvalidated">
/// Always <see langword="true"/>: rewriting the archive drops the signature block under
/// <c>META-INF/com/android/</c>, so the caller must re-sign or accept an unsigned package.
/// <para>恒为 <see langword="true"/>：重写归档会丢弃 <c>META-INF/com/android/</c> 下的签名块，
/// 调用方必须重新签名或接受未签名的包。</para>
/// </param>
public sealed record OtaZipPatchResult(
    string OutputPath,
    IReadOnlyList<string> ReplacedEntries,
    bool OuterSignatureInvalidated);

/// <summary>
/// Inputs for rebuilding an existing A/B OTA around a replacement Payload, including optional whole-file
/// signing and the verification policy applied before publication.
/// <para>围绕替换 Payload 重建既有 A/B OTA 的输入，包含可选的整包签名以及在发布前应用的校验策略。</para>
/// </summary>
public sealed record OtaZipRebuildOptions
{
    /// <summary>
    /// Replacement <c>payload.bin</c> on disk. It must carry a <c>CrAU</c> magic with version 2 or 3, be at
    /// most 16 GiB, and have a manifest plus metadata-signature prefix within 64 MiB.
    /// <para>磁盘上的替换 <c>payload.bin</c>。它必须带有 <c>CrAU</c> 魔数且版本为 2 或 3，大小不超过 16 GiB，
    /// 并且清单与元数据签名前缀不超过 64 MiB。</para>
    /// </summary>
    /// <value>The required source path read but never modified by the rebuild.
    /// <para>重建过程中只读取而不修改的必需源路径。</para></value>
    /// <returns>The Payload file staged as <c>payload.bin</c>.
    /// <para>作为 <c>payload.bin</c> 写入的 Payload 文件。</para></returns>
    public required string PayloadPath { get; init; }

    /// <summary>
    /// Additional member replacements applied alongside the generated Payload, properties and metadata.
    /// The entries owned by the rebuild — <c>payload.bin</c>, <c>payload_properties.txt</c>,
    /// <c>META-INF/com/android/metadata</c> and <c>META-INF/com/android/otacert</c> — may not appear here.
    /// <para>在生成的 Payload、属性文件与元数据之外附加的成员替换。重建自身负责的条目——<c>payload.bin</c>、
    /// <c>payload_properties.txt</c>、<c>META-INF/com/android/metadata</c> 与 <c>META-INF/com/android/otacert</c>——
    /// 不得出现在此处。</para>
    /// </summary>
    /// <value>Member-name to file-path map, empty by default.
    /// <para>成员名到文件路径的映射，默认为空。</para></value>
    /// <returns>The extra replacement set merged with the generated entries.
    /// <para>与生成条目合并后的附加替换集合。</para></returns>
    public IReadOnlyDictionary<string, string> ReplacementFiles { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Certificate used both for whole-file signing and for regenerating the embedded
    /// <c>META-INF/com/android/otacert</c> PEM; must be set together with <see cref="SigningKeyPath"/>.
    /// <para>既用于整包签名、也用于重新生成内嵌 <c>META-INF/com/android/otacert</c> PEM 的证书；
    /// 必须与 <see cref="SigningKeyPath"/> 同时设置。</para>
    /// </summary>
    /// <value>An empty string leaves the rebuilt package unsigned and its original <c>otacert</c> untouched.
    /// <para>空字符串表示重建包不签名，且保留原有 <c>otacert</c>。</para></value>
    /// <returns>The certificate path loaded through the package builder.
    /// <para>由包构建器加载的证书路径。</para></returns>
    public string CertificatePath { get; init; } = "";

    /// <summary>
    /// Private signing key applied to the finished ZIP after its offset metadata is finalized; must be set
    /// together with <see cref="CertificatePath"/>.
    /// <para>在偏移元数据定稿后应用于成品 ZIP 的私钥；必须与 <see cref="CertificatePath"/> 同时设置。</para>
    /// </summary>
    /// <value>An empty string produces an unsigned but structurally valid OTA.
    /// <para>空字符串产生未签名但结构有效的 OTA。</para></value>
    /// <returns>The signing key path handed to the package signer.
    /// <para>传给包签名器的签名密钥路径。</para></returns>
    public string SigningKeyPath { get; init; } = "";

    /// <summary>
    /// Runs structural and Payload verification on the staged archive, refusing to publish when it fails.
    /// <para>对暂存的归档执行结构与 Payload 校验，失败时拒绝发布。</para>
    /// </summary>
    /// <value><see langword="true"/> by default; when <see langword="false"/> the verification result is
    /// still computed and returned without gating publication.
    /// <para>默认为 <see langword="true"/>；为 <see langword="false"/> 时仍会计算并返回校验结果，
    /// 但不作为发布的门槛。</para></value>
    /// <returns>Whether a failed verification aborts the publication.
    /// <para>校验失败是否中止发布。</para></returns>
    public bool VerifyOutput { get; init; } = true;
}

/// <summary>
/// Reports a verified, atomically published A/B OTA rebuild and its final verification state.
/// <para>报告已通过校验并原子发布的 A/B OTA 重建及其最终校验状态。</para>
/// </summary>
/// <param name="OutputPath">
/// Absolute path of the published OTA; staging files next to it have been deleted.
/// <para>已发布 OTA 的绝对路径；其旁边的暂存文件已被删除。</para>
/// </param>
/// <param name="ReplacedEntries">
/// Every member written by the rebuild — the caller's replacements plus the generated Payload, properties,
/// metadata and certificate entries — ordered by ordinal name.
/// <para>重建写入的每个成员——调用方的替换项，加上生成的 Payload、属性文件、元数据与证书条目——
/// 按序数名称排序。</para>
/// </param>
/// <param name="OuterSignatureApplied">
/// <see langword="true"/> when a signing key was supplied and the whole-file signature was written after the
/// final verification pass.
/// <para>提供了签名密钥并在最终校验之后写入整包签名时为 <see langword="true"/>。</para>
/// </param>
/// <param name="Verification">
/// Verification of the published archive: the staged result when unsigned, otherwise the result recomputed
/// after signing.
/// <para>对已发布归档的校验结果：未签名时为暂存件的校验结果，否则为签名后重新计算的校验结果。</para>
/// </param>
public sealed record OtaZipRebuildResult(
    string OutputPath,
    IReadOnlyList<string> ReplacedEntries,
    bool OuterSignatureApplied,
    OtaZipVerificationResult Verification);

/// <summary>
/// Structural and property-level verification result for an OTA ZIP, collected without throwing.
/// <para>对 OTA ZIP 的结构与属性级校验结果，收集错误而不抛出异常。</para>
/// </summary>
/// <param name="IsValid">
/// <see langword="true"/> when no error was recorded; warnings alone still leave the package valid.
/// <para>未记录任何错误时为 <see langword="true"/>；仅有警告时包仍然有效。</para>
/// </param>
/// <param name="Errors">
/// Fatal findings such as duplicate or oversized members, a missing <c>payload_properties.txt</c>, and
/// <c>FILE_HASH</c>, <c>FILE_SIZE</c>, <c>METADATA_HASH</c> or <c>METADATA_SIZE</c> mismatches.
/// <para>致命问题，例如成员重复或过大、缺少 <c>payload_properties.txt</c>，
/// 以及 <c>FILE_HASH</c>、<c>FILE_SIZE</c>、<c>METADATA_HASH</c> 或 <c>METADATA_SIZE</c> 不匹配。</para>
/// </param>
/// <param name="Warnings">
/// Non-fatal findings such as an absent <c>payload.bin</c>, an unsupported CrAU version, or an empty
/// partition manifest.
/// <para>非致命问题，例如缺少 <c>payload.bin</c>、CrAU 版本不受支持，或分区清单为空。</para>
/// </param>
/// <param name="Properties">
/// Parsed <c>payload_properties.txt</c> keys with case-insensitive lookup, enriched with
/// <c>PAYLOAD_MANIFEST_PARTITIONS</c> and <c>PAYLOAD_MANIFEST_LENGTH</c> when the manifest was parsed.
/// <para>已解析的 <c>payload_properties.txt</c> 键值（查找时忽略大小写），在清单解析成功时还会补充
/// <c>PAYLOAD_MANIFEST_PARTITIONS</c> 与 <c>PAYLOAD_MANIFEST_LENGTH</c>。</para>
/// </param>
public sealed record OtaZipVerificationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, string> Properties);

/// <summary>
/// Provides bounded patching and layered verification for existing OTA ZIP files. Every ZIP member is
/// rewritten unstored, each member is capped at 16 GiB, and outputs are staged in a temporary file that is
/// moved into place only after the archive is complete.
/// <para>为既有 OTA ZIP 提供受限修补与分层校验。每个 ZIP 成员都以不压缩方式重写，单个成员上限为 16 GiB，
/// 输出先写入临时文件，只有在归档完整后才被移动到目标位置。</para>
/// </summary>
public static class OtaZipPatchService
{
    private const long MaximumEntryLength = 16L * 1024 * 1024 * 1024;
    private const ulong MaximumPayloadMetadataLength = 64UL * 1024 * 1024;
    private const int PropertyRangeReservation = 512;

    /// <summary>
    /// Rebuilds an existing A/B OTA around a replacement Payload, regenerates properties and offset metadata,
    /// optionally signs the complete ZIP, verifies it, and atomically publishes it.
    /// <para>围绕替换 Payload 重建既有 A/B OTA，重新生成属性与偏移元数据，可选地对完整 ZIP 签名，对其进行校验，
    /// 最后原子地发布。</para>
    /// </summary>
    /// <param name="inputPath">
    /// Existing OTA ZIP supplying the member set that is copied through and the
    /// <c>META-INF/com/android/metadata</c> base whose streaming property lines are replaced. Entries larger
    /// than 16 GiB and unsafe member names abort the rebuild.
    /// <para>提供需要原样复制的成员集合、以及作为 <c>META-INF/com/android/metadata</c> 基底（其流式属性行会被替换）
    /// 的既有 OTA ZIP。超过 16 GiB 的条目和不安全的成员名会中止重建。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination archive, overwritten only after verification succeeds. Its directory is created when
    /// missing; staging files are written beside it under a GUID suffix and always removed afterwards.
    /// <para>目标归档文件，只有在校验成功后才会被覆盖。其目录不存在时会被创建；暂存文件以 GUID 后缀写在其旁边，
    /// 并在结束后必定删除。</para>
    /// </param>
    /// <param name="options">
    /// Replacement Payload, extra member substitutions, optional signing pair and verification policy.
    /// <para>替换 Payload、附加成员替换、可选的签名密钥对以及校验策略。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels file reads, hash computations and the publication move; a cancelled run leaves the previous
    /// <paramref name="outputPath"/> content untouched.
    /// <para>取消文件读取、哈希计算与发布移动操作；被取消的运行不会改动 <paramref name="outputPath"/> 原有内容。</para>
    /// </param>
    /// <returns>
    /// The published path, the ordered set of written members, whether the outer signature was applied, and
    /// the final verification result.
    /// <para>已发布路径、按序写出的成员集合、是否应用外层签名，以及最终校验结果。</para>
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="inputPath"/> or <paramref name="outputPath"/> is
    /// <see langword="null"/> or blank.
    /// <para><paramref name="inputPath"/> 或 <paramref name="outputPath"/> 为 <see langword="null"/> 或空白。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="inputPath"/>, the replacement Payload, or a
    /// replacement file is missing.
    /// <para><paramref name="inputPath"/>、替换 Payload 或某个替换文件不存在。</para></exception>
    /// <exception cref="InvalidDataException">
    /// Only one of certificate and signing key is set, a replacement claims an entry owned by the rebuild,
    /// the Payload has an unsupported CrAU header or out-of-range metadata, a generated metadata line exceeds
    /// the 512-byte reservation, an entry offset shifts between the provisional and final pass, verification
    /// fails while <see cref="OtaZipRebuildOptions.VerifyOutput"/> is set, or an archive member is unsafe or
    /// oversized.
    /// <para>仅设置了证书或签名密钥之一；替换项声称了由重建负责的条目；Payload 的 CrAU 头部不受支持或元数据越界；
    /// 生成的元数据行超出 512 字节预留；条目偏移在临时与最终两趟之间发生变化；<see cref="OtaZipRebuildOptions.VerifyOutput"/>
    /// 开启时校验失败；或归档成员不安全或过大。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已收到取消信号。</para></exception>
    /// <example>
    /// <code>
    /// var rebuilt = await OtaZipPatchService.RebuildAsync(
    ///     "ota.zip",
    ///     "out/ota-fixed.zip",
    ///     new OtaZipRebuildOptions { PayloadPath = "out/payload.bin" });
    /// </code>
    /// </example>
    public static Task<OtaZipRebuildResult> RebuildAsync(
        string inputPath,
        string outputPath,
        OtaZipRebuildOptions options,
        CancellationToken cancellationToken = default)
        => RebuildCoreAsync(inputPath, outputPath, options, null, cancellationToken);

    internal static Task<OtaZipRebuildResult> RebuildAsync(
        string inputPath,
        string outputPath,
        OtaZipRebuildOptions options,
        IDeliverySigningKeyProvider signingKeys,
        CancellationToken cancellationToken = default)
        => RebuildCoreAsync(inputPath, outputPath, options, signingKeys, cancellationToken);

    private static async Task<OtaZipRebuildResult> RebuildCoreAsync(
        string inputPath,
        string outputPath,
        OtaZipRebuildOptions options,
        IDeliverySigningKeyProvider? signingKeys,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("OTA ZIP was not found.", inputPath);
        }
        if (!File.Exists(options.PayloadPath))
        {
            throw new FileNotFoundException("Replacement Payload was not found.", options.PayloadPath);
        }
        if ((options.CertificatePath.Length == 0) != (options.SigningKeyPath.Length == 0))
        {
            throw new InvalidDataException("OTA whole-file signing requires both a certificate and a private key.");
        }

        var replacements = NormalizeReplacements(options.ReplacementFiles);
        foreach (var generated in new[] { "payload.bin", "payload_properties.txt", "META-INF/com/android/metadata", "META-INF/com/android/otacert" })
        {
            if (replacements.ContainsKey(generated))
            {
                throw new InvalidDataException($"OTA rebuild owns generated entry '{generated}'.");
            }
        }

        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var suffix = ".tmp-" + Guid.NewGuid().ToString("N");
        var staged = output + suffix + ".zip";
        var provisional = output + suffix + ".provisional.zip";
        var propertiesPath = output + suffix + ".properties";
        var metadataPath = output + suffix + ".metadata";
        var certificatePath = output + suffix + ".otacert";
        try
        {
            var payloadProperties = await CreatePayloadPropertiesAsync(options.PayloadPath, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(propertiesPath, payloadProperties.Text, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            var metadataBase = ReadMetadataBase(inputPath);
            await File.WriteAllTextAsync(
                metadataPath,
                BuildMetadata(metadataBase, "", ""),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            replacements["payload.bin"] = options.PayloadPath;
            replacements["payload_properties.txt"] = propertiesPath;
            replacements["META-INF/com/android/metadata"] = metadataPath;
            if (options.CertificatePath.Length > 0)
            {
                using var certificate = DeliveryPackageBuilder.LoadCertificate(options.CertificatePath);
                await File.WriteAllTextAsync(
                    certificatePath,
                    certificate.ExportCertificatePem(),
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
                replacements["META-INF/com/android/otacert"] = certificatePath;
            }

            await PatchAsync(
                inputPath,
                provisional,
                new OtaZipPatchOptions { ReplacementFiles = replacements, AllowPayloadReplacement = true },
                cancellationToken).ConfigureAwait(false);
            var expectedRanges = DeliveryPackageBuilder.ReadStoredRanges(provisional);
            var payloadRange = RequiredRange(expectedRanges, "payload.bin");
            var propertiesRange = RequiredRange(expectedRanges, "payload_properties.txt");
            var metadataRange = RequiredRange(expectedRanges, "META-INF/com/android/metadata");
            var streaming =
                $"payload.bin:{payloadRange.Offset}:{payloadRange.Size},payload_properties.txt:{propertiesRange.Offset}:{propertiesRange.Size},metadata:{metadataRange.Offset}:{metadataRange.Size}";
            var all = $"payload_metadata.bin:{payloadRange.Offset}:{payloadProperties.MetadataSize},{streaming}";
            await File.WriteAllTextAsync(
                metadataPath,
                BuildMetadata(metadataBase, streaming, all),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            await PatchAsync(
                inputPath,
                staged,
                new OtaZipPatchOptions { ReplacementFiles = replacements, AllowPayloadReplacement = true },
                cancellationToken).ConfigureAwait(false);
            var actualRanges = DeliveryPackageBuilder.ReadStoredRanges(staged);
            foreach (var entry in new[] { "payload.bin", "payload_properties.txt", "META-INF/com/android/metadata" })
            {
                if (RequiredRange(actualRanges, entry) != RequiredRange(expectedRanges, entry))
                {
                    throw new InvalidDataException($"OTA ZIP entry offset changed while finalizing {entry} metadata.");
                }
            }

            var verification = Verify(staged);
            if (options.VerifyOutput && !verification.IsValid)
            {
                throw new InvalidDataException("Rebuilt OTA verification failed: " + string.Join("; ", verification.Errors));
            }
            var signed = options.SigningKeyPath.Length > 0;
            if (signed)
            {
                await DeliveryPackageBuilder.SignAsync(
                    staged,
                    new DeliveryPackageOptions
                    {
                        CertificatePath = options.CertificatePath,
                        SigningKeyPath = options.SigningKeyPath
                    },
                    cancellationToken,
                    signingKeys).ConfigureAwait(false);
                verification = Verify(staged);
                if (options.VerifyOutput && !verification.IsValid)
                {
                    throw new InvalidDataException("Signed OTA verification failed: " + string.Join("; ", verification.Errors));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staged, output, overwrite: true);
            return new(
                output,
                [.. replacements.Keys.Order(StringComparer.Ordinal)],
                signed,
                verification);
        }
        finally
        {
            File.Delete(staged);
            File.Delete(provisional);
            File.Delete(propertiesPath);
            File.Delete(metadataPath);
            File.Delete(certificatePath);
        }
    }

    /// <summary>
    /// Patches selected ZIP members and atomically publishes the new archive. Members are rewritten
    /// unstored in the source order, replacement entries absent from the source are appended, and the outer
    /// OTA signature is therefore always invalidated.
    /// <para>修补选定的 ZIP 成员并原子发布新归档。成员按源顺序以不压缩方式重写，源中不存在的替换条目会被追加，
    /// 因此外层 OTA 签名总是失效。</para>
    /// </summary>
    /// <param name="inputPath">
    /// Source OTA ZIP read through <see cref="ZipFile.OpenRead(string)"/>; it stays open for reading only and
    /// is never modified.
    /// <para>通过 <see cref="ZipFile.OpenRead(string)"/> 打开读取的源 OTA ZIP；仅只读，绝不被修改。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination archive path, resolved to an absolute path and overwritten after the temporary archive is
    /// complete. The containing directory is created when missing.
    /// <para>目标归档路径，会被解析为绝对路径，并在临时归档完成后被覆盖写入。其所在目录不存在时会被创建。</para>
    /// </param>
    /// <param name="options">
    /// Replacement map and whether a <c>payload.bin</c> swap is authorized.
    /// <para>替换映射，以及是否授权替换 <c>payload.bin</c>。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before each member and honoured by every copy; a cancelled run deletes the temporary archive
    /// and leaves <paramref name="outputPath"/> untouched.
    /// <para>在写入每个成员前检查，并被所有复制操作遵守；被取消的运行会删除临时归档并保持
    /// <paramref name="outputPath"/> 不变。</para>
    /// </param>
    /// <returns>
    /// The published path, the replacement member names as given, and <c>OuterSignatureInvalidated</c> set to
    /// <see langword="true"/>.
    /// <para>已发布路径、按输入给出的替换成员名，并且 <c>OuterSignatureInvalidated</c> 恒为
    /// <see langword="true"/>。</para>
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="inputPath"/> or <paramref name="outputPath"/> is
    /// <see langword="null"/> or blank.
    /// <para><paramref name="inputPath"/> 或 <paramref name="outputPath"/> 为 <see langword="null"/> 或空白。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="inputPath"/> or a mapped replacement file does
    /// not exist. <para><paramref name="inputPath"/> 或映射的替换文件不存在。</para></exception>
    /// <exception cref="InvalidDataException">
    /// An entry name is absolute or contains empty, <c>.</c> or <c>..</c> segments, a member repeats or exceeds
    /// 16 GiB, or a <c>payload.bin</c> replacement lacks <see cref="OtaZipPatchOptions.AllowPayloadReplacement"/>
    /// or its companion <c>payload_properties.txt</c>.
    /// <para>条目名为绝对路径或包含空段、<c>.</c> 或 <c>..</c>；成员重复或超过 16 GiB；或替换
    /// <c>payload.bin</c> 时未设置 <see cref="OtaZipPatchOptions.AllowPayloadReplacement"/>，
    /// 或缺少配套的 <c>payload_properties.txt</c>。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已收到取消信号。</para></exception>
    /// <example>
    /// <code>
    /// var patched = await OtaZipPatchService.PatchAsync(
    ///     "ota.zip",
    ///     "ota-patched.zip",
    ///     new OtaZipPatchOptions
    ///     {
    ///         ReplacementFiles = new Dictionary&lt;string, string&gt; { ["updater-script"] = "updater-script" },
    ///     });
    /// </code>
    /// </example>
    public static async Task<OtaZipPatchResult> PatchAsync(string inputPath, string outputPath,
        OtaZipPatchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("OTA ZIP was not found.", inputPath);

        var replacements = NormalizeReplacements(options.ReplacementFiles);
        var payloadReplacement = replacements.ContainsKey("payload.bin");
        if (payloadReplacement && !options.AllowPayloadReplacement)
            throw new InvalidDataException("Replacing payload.bin requires AllowPayloadReplacement.");
        if (payloadReplacement && !replacements.ContainsKey("payload_properties.txt"))
            throw new InvalidDataException("Replacing payload.bin also requires payload_properties.txt.");

        var output = Path.GetFullPath(outputPath);
        var temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var source = ZipFile.OpenRead(inputPath);
                using var archive = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);
                var copied = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in source.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = NormalizeEntryName(entry.FullName);
                    if (!copied.Add(name))
                        throw new InvalidDataException($"Duplicate OTA ZIP entry: {name}");
                    if (entry.Length > MaximumEntryLength)
                        throw new InvalidDataException($"OTA ZIP entry is too large: {name}");
                    var targetEntry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                    await using var destination = targetEntry.Open();
                    if (replacements.TryGetValue(name, out var replacement))
                        await CopyFileAsync(replacement, destination, cancellationToken).ConfigureAwait(false);
                    else
                    {
                        await using var content = entry.Open();
                        await content.CopyToAsync(destination, 65536, cancellationToken).ConfigureAwait(false);
                    }
                }

                foreach (var replacement in replacements)
                {
                    if (copied.Contains(replacement.Key))
                        continue;
                    var targetEntry = archive.CreateEntry(replacement.Key, CompressionLevel.NoCompression);
                    await using var destination = targetEntry.Open();
                    await CopyFileAsync(replacement.Value, destination, cancellationToken).ConfigureAwait(false);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite: true);
            return new(output, [.. replacements.Keys], OuterSignatureInvalidated: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    /// Verifies ZIP structure, A/B payload members and payload properties. Duplicate or oversized entries, a
    /// missing <c>payload_properties.txt</c>, an invalid <c>CrAU</c> magic, and <c>FILE_HASH</c>,
    /// <c>FILE_SIZE</c>, <c>METADATA_HASH</c> or <c>METADATA_SIZE</c> disagreements with the stored Payload are
    /// reported as errors; SHA-256 digests are compared as the Base64 text written in the properties file.
    /// <para>校验 ZIP 结构、A/B payload 成员以及 payload 属性。条目重复或过大、缺少
    /// <c>payload_properties.txt</c>、<c>CrAU</c> 魔数无效，以及与归档内 Payload 不符的 <c>FILE_HASH</c>、
    /// <c>FILE_SIZE</c>、<c>METADATA_HASH</c> 或 <c>METADATA_SIZE</c> 都作为错误上报；SHA-256 摘要按属性文件中
    /// 记录的 Base64 文本进行比较。</para>
    /// </summary>
    /// <param name="zipPath">
    /// Archive opened for reading only. A missing or unreadable file is reported through
    /// <see cref="OtaZipVerificationResult.Errors"/> instead of throwing.
    /// <para>仅以只读方式打开的归档。文件缺失或不可读时通过
    /// <see cref="OtaZipVerificationResult.Errors"/> 上报，而不抛出异常。</para>
    /// </param>
    /// <returns>
    /// Collected errors, warnings and properties with <see cref="OtaZipVerificationResult.IsValid"/> set when
    /// no error occurred. A package without <c>payload.bin</c> is treated as a possible Recovery ZIP and only
    /// warned about; a Payload whose CrAU version is neither 2 nor 3 skips manifest parsing with a warning.
    /// <para>收集的错误、警告与属性，未出现错误时 <see cref="OtaZipVerificationResult.IsValid"/> 为真。
    /// 不含 <c>payload.bin</c> 的包被视为可能的 Recovery ZIP，仅给出警告；CrAU 版本非 2 或 3 的 Payload
    /// 会跳过清单解析并给出警告。</para>
    /// </returns>
    /// <example>
    /// <code>
    /// var check = OtaZipPatchService.Verify("ota.zip");
    /// if (!check.IsValid)
    ///     Console.Error.WriteLine(string.Join("; ", check.Errors));
    /// </code>
    /// </example>
    public static OtaZipVerificationResult Verify(string zipPath)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entries = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                var name = NormalizeEntryName(entry.FullName);
                if (!entries.Add(name)) errors.Add($"Duplicate ZIP entry: {name}");
                if (entry.Length > MaximumEntryLength) errors.Add($"ZIP entry exceeds limit: {name}");
            }

            var payload = archive.GetEntry("payload.bin");
            var propertyEntry = archive.GetEntry("payload_properties.txt");
            if (payload is null)
            {
                warnings.Add("payload.bin is absent; this may be a Recovery ZIP rather than an A/B OTA.");
                return new(errors.Count == 0, errors, warnings, properties);
            }
            if (propertyEntry is null)
            {
                errors.Add("payload_properties.txt is missing.");
                return new(false, errors, warnings, properties);
            }

            using (var reader = new StreamReader(propertyEntry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                var parsed = ParseProperties(reader.ReadToEnd());
                foreach (var pair in parsed) properties[pair.Key] = pair.Value;
            }
            using var payloadStream = payload.Open();
            Span<byte> header = stackalloc byte[24];
            payloadStream.ReadExactly(header);
            if (!header[..4].SequenceEqual("CrAU"u8)) errors.Add("payload.bin has an invalid CrAU magic.");
            else if (BinaryPrimitives.ReadUInt64BigEndian(header[4..12]) is 2 or 3)
            {
                try
                {
                    using var manifestStream = payload.Open();
                    var metadata = PayloadParser.GetPayloadMetadata(manifestStream);
                    properties["PAYLOAD_MANIFEST_PARTITIONS"] = metadata.Manifest.Partitions.Count.ToString();
                    properties["PAYLOAD_MANIFEST_LENGTH"] = metadata.ManifestLength.ToString();
                    var actualMetadataSize = checked((long)metadata.ManifestLength + metadata.MetadataSignatureLength + 24);
                    if (properties.TryGetValue("METADATA_SIZE", out var metadataSizeText)
                        && (!long.TryParse(metadataSizeText, out var declaredMetadataSize)
                            || declaredMetadataSize != actualMetadataSize))
                        errors.Add($"METADATA_SIZE={metadataSizeText} does not equal the CrAU metadata prefix ({actualMetadataSize}).");
                    if (metadata.Manifest.Partitions.Count == 0) warnings.Add("Payload manifest contains no partition updates.");
                    if (properties.TryGetValue("METADATA_HASH", out var metadataHash)
                        && metadataHash.Length > 0)
                    {
                        using var metadataInput = payload.Open();
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        CopyExactly(metadataInput, new HashingStream(hash), actualMetadataSize);
                        var actualMetadataHash = Convert.ToBase64String(hash.GetHashAndReset());
                        if (!string.Equals(actualMetadataHash, metadataHash, StringComparison.Ordinal))
                            errors.Add("METADATA_HASH does not match the CrAU manifest bytes.");
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
                {
                    errors.Add($"Payload manifest validation failed: {ex.Message}");
                }
            }
            else
            {
                warnings.Add("Payload manifest parsing skipped because the CrAU version is not supported by the managed parser.");
            }
            var fileSize = payload.Length;
            if (properties.TryGetValue("FILE_SIZE", out var fileSizeText) && long.TryParse(fileSizeText, out var declaredSize) && declaredSize != fileSize)
                errors.Add($"FILE_SIZE={declaredSize} does not match payload.bin length {fileSize}.");
            if (properties.TryGetValue("FILE_HASH", out var declaredHash) && declaredHash.Length > 0)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var hashInput = payload.Open();
                hashInput.CopyTo(new HashingStream(hash));
                var actualHash = Convert.ToBase64String(hash.GetHashAndReset());
                if (!string.Equals(actualHash, declaredHash, StringComparison.Ordinal))
                    errors.Add("FILE_HASH does not match payload.bin.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            errors.Add(ex.Message);
        }
        return new(errors.Count == 0, errors, warnings, properties);
    }

    private static Dictionary<string, string> NormalizeReplacements(IReadOnlyDictionary<string, string> replacements)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in replacements)
        {
            var name = NormalizeEntryName(pair.Key);
            if (!File.Exists(pair.Value)) throw new FileNotFoundException("OTA replacement file was not found.", pair.Value);
            result.Add(name, pair.Value);
        }
        return result;
    }

    private static async Task<(string Text, ulong MetadataSize)> CreatePayloadPropertiesAsync(
        string payloadPath,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length < 24 || input.Length > MaximumEntryLength)
        {
            throw new InvalidDataException("Replacement Payload length is outside the supported range.");
        }
        var header = new byte[24];
        await input.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("CrAU"u8)
            || BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(4, 8)) is not (2 or 3))
        {
            throw new InvalidDataException("Replacement Payload has an invalid CrAU header or version.");
        }
        var manifestLength = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12, 8));
        var signatureLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
        var metadataSize = checked(24UL + manifestLength + signatureLength);
        if (manifestLength == 0 || metadataSize > MaximumPayloadMetadataLength || metadataSize > (ulong)input.Length)
        {
            throw new InvalidDataException("Replacement Payload metadata length is outside the supported range.");
        }

        input.Position = 0;
        using var metadataHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await CopyExactlyAsync(input, new HashingStream(metadataHash), checked((long)metadataSize), cancellationToken)
            .ConfigureAwait(false);
        input.Position = 0;
        var fileHash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        var text =
            $"FILE_HASH={Convert.ToBase64String(fileHash)}\n" +
            $"FILE_SIZE={input.Length}\n" +
            $"METADATA_HASH={Convert.ToBase64String(metadataHash.GetHashAndReset())}\n" +
            $"METADATA_SIZE={metadataSize}\n";
        return (text, metadataSize);
    }

    private static string ReadMetadataBase(string inputPath)
    {
        using var archive = ZipFile.OpenRead(inputPath);
        var entry = archive.GetEntry("META-INF/com/android/metadata");
        if (entry is null)
        {
            return "ota-type=AB\n";
        }
        if (entry.Length > 1024 * 1024)
        {
            throw new InvalidDataException("OTA metadata exceeds 1 MiB.");
        }
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith("ota-streaming-property-files=", StringComparison.Ordinal)
                && !line.StartsWith("ota-property-files=", StringComparison.Ordinal));
        return string.Join('\n', lines) + "\n";
    }

    private static string BuildMetadata(string metadataBase, string streaming, string all)
    {
        if (streaming.Length > PropertyRangeReservation || all.Length > PropertyRangeReservation)
        {
            throw new InvalidDataException("OTA property ranges exceed reserved metadata space.");
        }
        return metadataBase
            + "ota-streaming-property-files=" + streaming.PadRight(PropertyRangeReservation) + "\n"
            + "ota-property-files=" + all.PadRight(PropertyRangeReservation) + "\n";
    }

    private static (long Offset, ulong Size) RequiredRange(
        IReadOnlyDictionary<string, (long Offset, ulong Size)> ranges,
        string entry)
        => ranges.TryGetValue(entry, out var range)
            ? range
            : throw new InvalidDataException($"Rebuilt OTA is missing required stored entry: {entry}");

    private static string NormalizeEntryName(string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"Unsafe OTA ZIP entry name: {name}");
        return normalized;
    }

    private static Dictionary<string, string> ParseProperties(string content)
        => content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

    private static async Task CopyFileAsync(string path, Stream destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(destination, 65536, cancellationToken).ConfigureAwait(false);
    }

    private static void SkipExactly(Stream input, long count)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0) throw new EndOfStreamException("Unexpected end of Payload while skipping CrAU header.");
            count -= read;
        }
    }

    private static void CopyExactly(Stream input, Stream output, long count)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0) throw new EndOfStreamException("Unexpected end of Payload while reading manifest.");
            output.Write(buffer, 0, read);
            count -= read;
        }
    }

    private static async Task CopyExactlyAsync(
        Stream input,
        Stream output,
        long count,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of Payload while hashing metadata.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            count -= read;
        }
    }

    private sealed class HashingStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            hash.AppendData(buffer, offset, count);
            return Task.CompletedTask;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            hash.AppendData(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }
}