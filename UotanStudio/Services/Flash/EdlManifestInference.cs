using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Programmer;

namespace UotanStudio.Services.Flash;

/// <summary>
/// One package folder's multi-image programmer mapping inferred from file-name conventions alone: the Sahara
/// image identifiers each programmer file answers, resolved and bounded the same way a real manifest would be.
/// <para>仅凭文件名约定从包目录推断出的多镜像 programmer 映射：每个 programmer 文件应答的 Sahara 镜像标识，
/// 解析与限额方式与真实清单一致。</para>
/// </summary>
/// <param name="Directory">The folder the mapping was inferred from.
/// <para>推断该映射所用目录。</para></param>
/// <param name="Images">The inferred image mappings in rule order.
/// <para>按规则顺序排列的推断镜像映射。</para></param>
public sealed record EdlInferredProgrammerManifest(
    string Directory,
    IReadOnlyList<SaharaProgrammerImage> Images);

/// <summary>
/// Infers a Qualcomm multi-image programmer mapping for a folder that carries the well-known programmer scatter
/// files but no <c>qsahara_device_programmer.xml</c> manifest: the vendor manifest's image identifiers are
/// recovered from file-name conventions in memory, never written to disk, and the result stays a candidate the
/// ordinary validation and programmer selection still decide about.
/// <para>为带有知名 programmer 散件但没有 <c>qsahara_device_programmer.xml</c> 清单的目录推断高通多镜像
/// programmer 映射：按文件名约定在内存中恢复厂商清单的镜像标识，不落盘、不写临时清单，结果仍只是候选，
/// 由既有校验与 programmer 选择决定取舍。</para>
/// </summary>
/// <remarks>
/// The rules generalize the vendor manifest contents observed in the reference tooling: the kaanapali
/// eight-image set (<c>multi_image_qti.mbn</c>, <c>multi_image.mbn</c>, <c>xbl_sc.elf</c>,
/// <c>signed_firmware_soc_view.elf</c>, <c>sequencer_ram.elf</c>, <c>tme_config.elf</c>,
/// <c>prog_firehose_ddr.elf</c>, <c>xbl_config_devprg.elf</c>) with image identifiers 36/37/21/60/59/61/13/38.
/// Exact vendor names win, well-known stem prefixes generalize to variant suffixes, and the Firehose member
/// accepts the same <c>prog_*firehose*</c> naming the single-image flow already scans for. A folder that does
/// not present a Firehose member plus at least one auxiliary member infers nothing, so the caller keeps the
/// previous behavior instead of guessing.
/// <para>规则泛化自参考工具中观察到的厂商清单内容：kaanapali 八镜像套件（<c>multi_image_qti.mbn</c>、
/// <c>multi_image.mbn</c>、<c>xbl_sc.elf</c>、<c>signed_firmware_soc_view.elf</c>、<c>sequencer_ram.elf</c>、
/// <c>tme_config.elf</c>、<c>prog_firehose_ddr.elf</c>、<c>xbl_config_devprg.elf</c>），镜像标识为
/// 36/37/21/60/59/61/13/38。厂商精确名优先，知名主干前缀泛化到变体后缀，Firehose 成员沿用单镜像流程
/// 已扫描的 <c>prog_*firehose*</c> 命名。目录未同时给出 Firehose 成员与至少一个辅助成员时不做推断，
/// 调用方保持原状而不是猜测。</para>
/// </remarks>
public static class EdlManifestInference
{
    /// <summary>
    /// Gets the largest number of programmer files one inference admits; the excess is never inspected.
    /// <para>获取单次推断接纳的 programmer 文件数量上限；超出部分不再检查。</para>
    /// </summary>
    /// <value>A bounded candidate count far above any observed programmer scatter.
    /// <para>有界的候选数量，远高于任何已见 programmer 散件数量。</para></value>
    public const int MaximumCandidateFiles = 256;

    /// <summary>
    /// Gets the smallest number of inferred images a usable mapping carries: the Firehose member plus at least
    /// one auxiliary member.
    /// <para>获取可用映射至少要推断出的镜像数：Firehose 成员加至少一个辅助成员。</para>
    /// </summary>
    /// <value>Two images. <para>两个镜像。</para></value>
    public const int MinimumImages = 2;

    private static readonly string[] ProgrammerExtensions = [".bin", ".mbn", ".elf", ".melf"];

    /// <summary>
    /// The per-image rules in claim order: exact vendor stems first, then a well-known stem prefix, and the
    /// Firehose identifier resolved by the <c>firehose</c> name token.
    /// <para>按认领顺序排列的单镜像规则：先是厂商精确主干名，再是知名主干前缀，Firehose 标识按
    /// <c>firehose</c> 名称词解析。</para>
    /// </summary>
    private static readonly (uint ImageId, string ExactStem, string? StemPrefix, string? ExcludedPrefix)[] Rules =
    [
        (36, "multi_image_qti", "multi_image_qti", null),
        (37, "multi_image", "multi_image", "multi_image_qti"),
        (21, "xbl_sc", "xbl_sc", null),
        (60, "signed_firmware_soc_view", "signed_firmware_soc_view", null),
        (59, "sequencer_ram", "sequencer_ram", null),
        (61, "tme_config", "tme_config", null),
        ((uint)SaharaImageId.Firehose, "prog_firehose_ddr", null, null),
        (38, "xbl_config_devprg", "xbl_config", null)
    ];

    /// <summary>
    /// Infers the multi-image programmer mapping of one folder from its file names, without touching the files
    /// beyond metadata reads and without writing anything.
    /// <para>仅按文件名为单个目录推断多镜像 programmer 映射，除元数据读取外不触碰文件，也不写入任何内容。</para>
    /// </summary>
    /// <param name="directory">Folder holding the programmer scatter files; it must exist.
    /// <para>存放 programmer 散件的目录；必须已存在。</para></param>
    /// <param name="cancellationToken">Token cancelling the inference.
    /// <para>取消推断的令牌。</para></param>
    /// <returns>The inferred mapping, or <see langword="null"/> when no Firehose member, no auxiliary member,
    /// an overlarge file or the size ceilings leave nothing safe to infer.
    /// <para>推断出的映射；没有 Firehose 成员、没有辅助成员、文件过大或超出大小上限而无法安全推断时为
    /// <see langword="null"/>。</para></returns>
    public static EdlInferredProgrammerManifest? TryInfer(string directory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        List<string> candidates = [];
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => ProgrammerExtensions.Contains(
                Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            if (candidates.Count >= MaximumCandidateFiles)
            {
                break;
            }

            candidates.Add(path);
        }

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var images = new List<SaharaProgrammerImage>(Rules.Length);
        long total = 0;
        var firehoseClaimed = false;
        foreach (var (imageId, exactStem, stemPrefix, excludedPrefix) in Rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MatchRule(candidates, claimed, exactStem, stemPrefix, excludedPrefix) is not { } match)
            {
                if (imageId == (uint)SaharaImageId.Firehose)
                {
                    return null;
                }

                continue;
            }

            long length;
            try
            {
                length = new FileInfo(match).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (imageId == (uint)SaharaImageId.Firehose)
                {
                    return null;
                }

                continue;
            }

            if (length is 0 or > SaharaProgrammerManifest.MaximumImageBytes ||
                total + length > SaharaProgrammerManifest.MaximumTotalImageBytes ||
                images.Count >= SaharaProgrammerManifest.MaximumImages)
            {
                return null;
            }

            claimed.Add(match);
            total += length;
            firehoseClaimed |= imageId == (uint)SaharaImageId.Firehose;
            images.Add(new SaharaProgrammerImage(imageId, match, length));
        }

        return firehoseClaimed && images.Count >= MinimumImages
            ? new EdlInferredProgrammerManifest(Path.GetFullPath(directory), images)
            : null;
    }

    /// <summary>
    /// Picks the one file a rule claims: an unclaimed exact vendor stem wins, otherwise an unclaimed file whose
    /// stem carries the rule's well-known prefix, and the Firehose token rule always falls back to names that
    /// contain <c>firehose</c> ranked the way the single-image flow ranks them.
    /// <para>挑出规则认领的唯一文件：未被认领的厂商精确主干名优先，否则取主干带该规则知名前缀的未认领文件；
    /// Firehose 词规则始终回退到包含 <c>firehose</c> 的名称，并按单镜像流程的顺序排名。</para>
    /// </summary>
    /// <param name="candidates">The bounded candidate paths. <para>有界的候选路径。</para></param>
    /// <param name="claimed">Paths already claimed by earlier rules. <para>已被先前规则认领的路径。</para></param>
    /// <param name="exactStem">The exact vendor stem. <para>厂商精确主干名。</para></param>
    /// <param name="stemPrefix">The well-known stem prefix, or <see langword="null"/> to fall straight to the
    /// Firehose token rule. <para>知名主干前缀；为 <see langword="null"/> 时直接落入 Firehose 词规则。</para></param>
    /// <param name="excludedPrefix">A stem prefix the rule must never claim, or <see langword="null"/>.
    /// <para>规则绝不认领的主干前缀；可为 <see langword="null"/>。</para></param>
    /// <returns>The claimed path, or <see langword="null"/> when nothing matches.
    /// <para>被认领的路径；无匹配时为 <see langword="null"/>。</para></returns>
    private static string? MatchRule(IReadOnlyList<string> candidates, HashSet<string> claimed,
        string exactStem, string? stemPrefix, string? excludedPrefix)
    {
        string? exact = null;
        string? prefixed = null;
        string? token = null;
        foreach (var path in candidates)
        {
            if (claimed.Contains(path))
            {
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(path);
            if (stem.Equals(exactStem, StringComparison.OrdinalIgnoreCase))
            {
                exact ??= path;
                continue;
            }

            if (stemPrefix is not null && stem.StartsWith(stemPrefix, StringComparison.OrdinalIgnoreCase) &&
                !(excludedPrefix is not null && stem.StartsWith(excludedPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                prefixed ??= path;
                continue;
            }

            if (stemPrefix is null && stem.Contains("firehose", StringComparison.OrdinalIgnoreCase))
            {
                token ??= path;
            }
        }

        if (exact is not null || prefixed is not null || token is not null)
        {
            return exact ?? prefixed ?? token;
        }

        return null;
    }
}