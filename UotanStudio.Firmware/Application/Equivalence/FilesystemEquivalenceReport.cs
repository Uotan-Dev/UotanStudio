using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.Application.Equivalence;

/// <summary>
/// Read-only façade that reports the four equivalence dimensions of a rebuilt or edited firmware image
/// separately: byte identity against the recorded source digest, inode structure, file content, and
/// capacity against the source filesystem size. Each dimension carries its own
/// <see cref="FirmwareEquivalenceState"/>, and the façade never collapses them into one boolean: an
/// image that was edited is normally byte-different yet structure- and content-equivalent, and a
/// byte-identical image is a distinct, stronger statement. Dimensions are parsed from the equivalence
/// metadata emitted by the preserved filesystem rebuild for the EXT4, EROFS, F2FS and Android sparse
/// container formats; a missing flag means the corresponding check never ran and is reported as
/// <see cref="FirmwareEquivalenceState.NotEvaluated"/>, never as equivalent. Formats without preserved
/// rebuild machinery — LP metadata partitions, AVB-signed partitions, boot images, plain raw-to-sparse
/// conversion and fresh directory builds — have no equivalence computation to surface, so their reports
/// legitimately carry four <see cref="FirmwareEquivalenceState.NotEvaluated"/> dimensions instead of a
/// fabricated pass.
/// <para>只读门面，独立报告重建或编辑后固件镜像的四个等价维度：与记录的源摘要比对的字节一致性、inode 结构、
/// 文件内容、以及相对源文件系统容量的容量。每个维度各自携带一个 <see cref="FirmwareEquivalenceState"/>，
/// 门面绝不把它们折叠成单个布尔值：编辑过的镜像通常字节不同但结构与内容等价，而字节完全一致是另一层更强的
/// 断言。各维度从受保留的文件系统重建为 EXT4、EROFS、F2FS 与 Android 稀疏容器格式发出的等价元数据中解析；
/// 标记缺失意味着对应检查从未执行，报告为不评估而非等价。没有受保留重建机制的格式——LP 元数据分区、
/// AVB 签名分区、启动镜像、纯 raw 转稀疏以及全新目录构建——不存在可呈现的等价计算，因此其四个维度如实报告
/// 为不评估，而不是伪造通过。</para>
/// </summary>
public sealed class FilesystemEquivalenceReport
{
    /// <summary>Metadata flag name carrying the byte-identity verdict emitted by the preserved rebuild path.
    /// <para>受保留重建路径写出的字节一致判定所使用的元数据标记名。</para></summary>
    public const string ByteEquivalenceKey = "EquivalentBytes";

    /// <summary>Metadata flag name carrying the inode-structure verdict emitted by the preserved rebuild path.
    /// <para>受保留重建路径写出的 inode 结构判定所使用的元数据标记名。</para></summary>
    public const string StructureEquivalenceKey = "EquivalentStructure";

    /// <summary>Metadata flag name carrying the file-content verdict emitted by the preserved rebuild path.
    /// <para>受保留重建路径写出的文件内容判定所使用的元数据标记名。</para></summary>
    public const string ContentEquivalenceKey = "EquivalentContent";

    /// <summary>Metadata flag name carrying the capacity verdict; the same key predates this façade and is kept
    /// for result-dictionary compatibility.
    /// <para>容量判定所使用的元数据标记名；该键在本门面出现之前已存在，为兼容结果字典保持不变。</para></summary>
    public const string CapacityEquivalenceKey = "FitsSourceFilesystemSize";

    /// <summary>Byte identity of the produced image against the recorded source digest and length.
    /// <para>产出镜像相对记录的源摘要与长度的字节一致性。</para></summary>
    /// <value><see cref="FirmwareEquivalenceState.Equivalent"/> only when the whole produced image hashes to the
    /// recorded source SHA-256 with the recorded length, <see cref="FirmwareEquivalenceState.NotEquivalent"/>
    /// when either differs, and <see cref="FirmwareEquivalenceState.NotEvaluated"/> when no source identity was
    /// recorded. <para>仅当整份产出镜像的 SHA-256 与长度同记录的源一致时为等价，任一不同为不等价，未记录源身份
    /// 时为不评估。</para></value>
    public FirmwareEquivalenceState ByteEquivalence { get; init; } = FirmwareEquivalenceState.NotEvaluated;

    /// <summary>Structure equivalence: modes, owners, flags, symlinks, device nodes, extended attributes,
    /// timestamps, generation and hardlink relationships preserved by the rebuild.
    /// <para>结构等价性：重建是否保留模式、属主、标志、符号链接、设备节点、扩展属性、时间戳、代次与硬链接关系。</para></summary>
    /// <value><see cref="FirmwareEquivalenceState.Equivalent"/> when the post-rebuild structure verification ran
    /// and passed, <see cref="FirmwareEquivalenceState.NotEquivalent"/> when a verification reported failure, and
    /// <see cref="FirmwareEquivalenceState.NotEvaluated"/> when the verification never ran.
    /// <para>重建后结构校验执行且通过时为等价，校验报告失败时为不等价，校验从未执行时为不评估。</para></value>
    public FirmwareEquivalenceState StructureEquivalence { get; init; } = FirmwareEquivalenceState.NotEvaluated;

    /// <summary>Content equivalence: every regular-file payload in the produced image hashes to the same
    /// SHA-256 as its captured source content.
    /// <para>内容等价性：产出镜像中每个常规文件载荷的 SHA-256 是否与其采集的源内容一致。</para></summary>
    /// <value><see cref="FirmwareEquivalenceState.Equivalent"/> when the post-rebuild content verification ran
    /// and passed, <see cref="FirmwareEquivalenceState.NotEquivalent"/> when a verification reported failure, and
    /// <see cref="FirmwareEquivalenceState.NotEvaluated"/> when the verification never ran.
    /// <para>重建后内容校验执行且通过时为等价，校验报告失败时为不等价，校验从未执行时为不评估。</para></value>
    public FirmwareEquivalenceState ContentEquivalence { get; init; } = FirmwareEquivalenceState.NotEvaluated;

    /// <summary>Capacity equivalence: whether the produced image still fits the source filesystem extent.
    /// <para>容量等价性：产出镜像是否仍能容纳于源文件系统的容量范围。</para></summary>
    /// <value><see cref="FirmwareEquivalenceState.Equivalent"/> when the produced size fits the recorded baseline,
    /// <see cref="FirmwareEquivalenceState.NotEquivalent"/> when it exceeds that baseline, and
    /// <see cref="FirmwareEquivalenceState.NotEvaluated"/> when no capacity baseline was recorded. For sparse
    /// containers the comparison uses the logical filesystem extent, not the packed container length.
    /// <para>产出大小不超过记录的基线时为等价，超出基线时为不等价，未记录容量基线时为不评估。对稀疏容器而言，
    /// 比较使用逻辑文件系统范围而非打包容器长度。</para></value>
    public FirmwareEquivalenceState CapacityEquivalence { get; init; } = FirmwareEquivalenceState.NotEvaluated;

    /// <summary>The source metadata dictionary this report was parsed from, for diagnostics such as recorded
    /// digests, sizes and the capacity excess.
    /// <para>本报告所解析的源元数据字典，供诊断使用，例如记录的摘要、大小与容量超出量。</para></summary>
    /// <value>A read-only dictionary; empty when the report was built without metadata.
    /// <para>只读字典；报告在无元数据情况下构建时为空。</para></value>
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();

    /// <summary>A report whose four dimensions are all <see cref="FirmwareEquivalenceState.NotEvaluated"/>,
    /// for callers that hold no equivalence evidence at all.
    /// <para>四个维度均为不评估的报告，供完全没有任何等价证据的调用方使用。</para></summary>
    /// <value>A new immutable all-unevaluated report on every access. <para>每次访问都返回全新的全不评估报告。</para></value>
    public static FilesystemEquivalenceReport AllNotEvaluated => new();

    /// <summary>
    /// Parses the four equivalence dimensions out of a rebuild metadata dictionary; the flag names are the
    /// documented keys of this type. A value that is neither <c>True</c> nor <c>False</c> is treated as an
    /// absent check rather than guessed, and a missing flag is never interpreted as equivalent.
    /// <para>从重建元数据字典解析四个等价维度；标记名即本类型已记录的键。既非 <c>True</c> 也非 <c>False</c>
    /// 的值按“检查缺失”处理而不做猜测，缺失的标记绝不会被当作等价。</para>
    /// </summary>
    /// <param name="metadata">Rebuild metadata as surfaced by the format handler, or <see langword="null"/>
    /// for an all-unevaluated report. <para>格式处理器呈现的重建元数据；传 <see langword="null"/> 时得到全不评估报告。</para></param>
    /// <returns>A report with each dimension independently evaluated from its flag.
    /// <para>各维度按其标记独立解析后的报告。</para></returns>
    public static FilesystemEquivalenceReport FromMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        var details = metadata ?? new Dictionary<string, string>();
        return new FilesystemEquivalenceReport
        {
            ByteEquivalence = ParseState(details, ByteEquivalenceKey),
            StructureEquivalence = ParseState(details, StructureEquivalenceKey),
            ContentEquivalence = ParseState(details, ContentEquivalenceKey),
            CapacityEquivalence = ParseState(details, CapacityEquivalenceKey),
            Details = details,
        };
    }

    /// <summary>
    /// Parses the equivalence dimensions out of a firmware operation result's metadata, so callers holding only
    /// an operation outcome can still obtain the separated four-dimension report.
    /// <para>从固件操作结果的元数据解析等价维度，使仅持有操作结果的调用方也能得到分离的四维度报告。</para>
    /// </summary>
    /// <param name="result">Operation result to read; <see langword="null"/> yields an all-unevaluated report.
    /// <para>待读取的操作结果；为 <see langword="null"/> 时得到全不评估报告。</para></param>
    /// <returns>A report parsed from <see cref="FirmwareOpResult.Metadata"/>.
    /// <para>依据 <see cref="FirmwareOpResult.Metadata"/> 解析出的报告。</para></returns>
    public static FilesystemEquivalenceReport FromOperationResult(FirmwareOpResult? result)
        => FromMetadata(result?.Metadata);

    /// <summary>
    /// Streams the produced image through a SHA-256 digest and compares it against the recorded source
    /// identity, evaluating only the byte dimension. The length is checked before the digest is trusted, the
    /// read buffer stays bounded, and the token is observed after every block.
    /// <para>将产出镜像流式计入 SHA-256 摘要并与记录的源身份比对，仅评估字节维度。长度先于摘要被检查，
    /// 读取缓冲保持有界，且每个数据块之后都会观察取消令牌。</para>
    /// </summary>
    /// <param name="producedImage">Readable stream positioned at the start of the produced image; the caller
    /// keeps ownership. <para>定位在产出镜像起始处的可读流；所有权仍归调用方。</para></param>
    /// <param name="expectedSha256Hex">Recorded source digest as 64 hexadecimal characters, either case.
    /// <para>以 64 个十六进制字符记录的源摘要，大小写均可。</para></param>
    /// <param name="expectedLength">Recorded source length in bytes; a seekable stream whose length already
    /// disagrees short-circuits to <see cref="FirmwareEquivalenceState.NotEquivalent"/>.
    /// <para>记录的源字节长度；长度已经不一致的可寻址流直接短路为不等价。</para></param>
    /// <param name="cancellationToken">Cancels the digest loop between blocks.
    /// <para>在摘要循环的各数据块之间取消。</para></param>
    /// <returns><see cref="FirmwareEquivalenceState.Equivalent"/> or
    /// <see cref="FirmwareEquivalenceState.NotEquivalent"/>; this evaluator always runs, so it never returns
    /// <see cref="FirmwareEquivalenceState.NotEvaluated"/>.
    /// <para>返回等价或不等价；本评估器总会执行，因此绝不返回不评估。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="producedImage"/> or
    /// <paramref name="expectedSha256Hex"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="expectedSha256Hex"/> is not exactly 64 hexadecimal
    /// characters, or <paramref name="producedImage"/> cannot be read.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedLength"/> is negative.
    /// <para><paramref name="expectedLength"/> 为负数。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public static FirmwareEquivalenceState EvaluateByteEquivalence(
        Stream producedImage,
        string expectedSha256Hex,
        long expectedLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(producedImage);
        ArgumentNullException.ThrowIfNull(expectedSha256Hex);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);
        if (expectedSha256Hex.Length != 64 || !expectedSha256Hex.All(static character => char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("The expected digest must be exactly 64 hexadecimal characters.", nameof(expectedSha256Hex));
        }
        if (producedImage.CanSeek && producedImage.Length != expectedLength)
        {
            return FirmwareEquivalenceState.NotEquivalent;
        }
        long total = 0;
        var buffer = new byte[65536];
        using var hashing = new HashingReadStream(producedImage, leaveOpen: true);
        int count;
        while ((count = hashing.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total = checked(total + count);
        }

        return total == expectedLength && string.Equals(hashing.GetHashHex(), expectedSha256Hex, StringComparison.OrdinalIgnoreCase)
            ? FirmwareEquivalenceState.Equivalent
            : FirmwareEquivalenceState.NotEquivalent;
    }

    private static FirmwareEquivalenceState ParseState(IReadOnlyDictionary<string, string> details, string key)
        => !details.TryGetValue(key, out var value)
            || !bool.TryParse(value, out var verdict)
                ? FirmwareEquivalenceState.NotEvaluated
                : verdict ? FirmwareEquivalenceState.Equivalent : FirmwareEquivalenceState.NotEquivalent;
}