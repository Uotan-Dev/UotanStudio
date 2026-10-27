using UotanStudio.Hash.Digests;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// On-target SHA256 digest queries (<c>&lt;getsha256digest&gt;</c>) used to verify or skip programming
/// without reading the region back. Some programmers reject the command; a rejection or a missing
/// digest is reported as <see langword="null"/> rather than as a session fault, so the caller keeps
/// programming normally.
/// <para>目标侧 SHA256 摘要查询（<c>&lt;getsha256digest&gt;</c>），用于在不回读区域的情况下校验或跳过
/// 编程。部分 programmer 会拒绝该命令；拒绝或缺少摘要时返回 <see langword="null"/> 而不是判定会话失效，
/// 调用方可继续正常编程。</para>
/// </summary>
public sealed partial class FirehoseProtocol
{
    private ProgramPayloadDigest? _programPayloadDigest;

    private sealed record ProgramPayloadDigest(long SourceLength, long StartSector, long NumSectors,
        int PhysicalPartition, byte[] Digest);

    /// <summary>Requests the SHA256 digest the target computes for a flash range.
    /// <para>请求目标为一段闪存范围计算并上报的 SHA256 摘要。</para></summary>
    /// <param name="startSector">The absolute starting sector.<para>绝对起始扇区。</para></param>
    /// <param name="numSectors">The number of sectors to hash; must be positive.
    /// <para>待计算哈希的扇区数；必须为正。</para></param>
    /// <param name="physicalPartition">The physical storage region or LUN.
    /// <para>物理存储区域或 LUN。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The 32-byte digest, or <see langword="null"/> when the target rejects the command or reports no digest.
    /// <para>32 字节摘要；目标拒绝该命令或未上报摘要时为 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="numSectors"/> is not positive, or the range leaves the configured storage geometry.
    /// <para><paramref name="numSectors"/> 非正，或范围超出 已配置的存储几何。</para></exception>
    /// <exception cref="EdlException">The transport fails or times out.
    /// <para>传输层失败或超时。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled.
    /// <para>令牌被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The protocol has been disposed.
    /// <para>协议已被释放。</para></exception>
    public async Task<byte[]?> GetSha256DigestAsync(long startSector, long numSectors, int physicalPartition = 0,
        CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await GetSha256DigestCoreAsync(startSector, numSectors, physicalPartition, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Digest fast path for an already-owned operation lease: queries the on-target digest and compares it with the
    /// digest recorded while the same source was programmed, hashing the source again only when no recorded digest
    /// matches. Returns <see langword="true"/> only on a verified match; any unsupported or mismatched outcome
    /// returns <see langword="false"/> so the caller can fall back to the authoritative read-back comparison.
    /// <para>在已持有的操作租约上的摘要快速校验路径：查询目标侧摘要，并与同一源编程时记录的摘要比对，仅在没有任何
    /// 已记录摘要匹配时才重新哈希源。仅在验证匹配时返回 <see langword="true"/>；任何不支持或不匹配的结果都返回
    /// <see langword="false"/>，由调用方回退到权威的回读比对。</para>
    /// </summary>
    /// <param name="dataStream">The seekable source stream, read from its start. <para>可寻址的源流，从起始处读取。</para></param>
    /// <param name="startSector">The absolute starting sector. <para>绝对起始扇区。</para></param>
    /// <param name="numSectors">The number of programmed sectors. <para>已编程的扇区数。</para></param>
    /// <param name="physicalPartition">The physical storage region or LUN. <para>物理存储区域或 LUN。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>
    /// <see langword="true"/> when the on-target digest matches the source's digest.
    /// <para>目标侧摘要与源的摘要匹配时为 <see langword="true"/>。</para>
    /// </returns>
    internal async Task<bool> TryVerifyProgramWithTargetDigestOwnedAsync(Stream dataStream, long startSector,
        long numSectors, int physicalPartition, CancellationToken cancellationToken)
    {
        if (Configuration.StorageType is FirehoseStorageType.Nand || !dataStream.CanSeek)
        {
            return false;
        }

        try
        {
            var regionBytes = checked(numSectors * Configuration.SectorSize);
            if (dataStream.Length != regionBytes)
            {
                return false;
            }

            var remote = await GetSha256DigestCoreAsync(startSector, numSectors, physicalPartition, cancellationToken)
                .ConfigureAwait(false);
            if (remote is null)
            {
                return false;
            }

            var local = TryGetRecordedProgramDigest(dataStream.Length, startSector, numSectors, physicalPartition)
                ?? await ComputeSourceDigestAsync(dataStream, cancellationToken).ConfigureAwait(false);
            if (local.AsSpan().SequenceEqual(remote))
            {
                return true;
            }

            Log("On-target digest mismatch; falling back to read-back verification.");
            return false;
        }
        catch (EdlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Hashes the seekable source from its start with a bounded buffer; used when the send did not record a digest for
    /// this range.
    /// <para>以有界缓冲从起始处哈希可寻址源；当发送期间没有为该范围记录摘要时使用。</para>
    /// </summary>
    /// <param name="dataStream">The seekable source stream. <para>可寻址的源流。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The SHA-256 of the complete source. <para>完整源的 SHA-256。</para></returns>
    private static async Task<byte[]> ComputeSourceDigestAsync(Stream dataStream, CancellationToken cancellationToken)
    {
        dataStream.Seek(0, SeekOrigin.Begin);
        using var hasher = Sha256.CreateAccumulator();
        var buffer = new byte[65536];
        int read;
        while ((read = await dataStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hasher.Append(buffer.AsSpan(0, read));
        }

        return hasher.GetHashAndReset();
    }

    /// <summary>
    /// Records the digest accumulated while programming so a later verification of the same range reuses it instead of
    /// reading the source again. A disk-relative address or a payload that did not start at position zero cannot be
    /// matched against an absolute digest query and is discarded.
    /// <para>记录编程期间累计的摘要，使之后对同一范围的校验直接复用它而无需再次读取源。磁盘相对地址或并非从位置零
    /// 开始的载荷无法与绝对摘要查询匹配，因此会被丢弃。</para>
    /// </summary>
    /// <param name="source">The stream whose bytes were sent; only its length is retained for matching.
    /// <para>其字节已被发送的流；仅保留其长度用于匹配。</para></param>
    /// <param name="startSector">The programmed start address. <para>已编程的起始地址。</para></param>
    /// <param name="numSectors">The programmed sector count. <para>已编程的扇区数。</para></param>
    /// <param name="physicalPartition">The physical storage region. <para>物理存储区域。</para></param>
    /// <param name="startPosition">The source position the payload started at. <para>载荷起始时的源位置。</para></param>
    /// <param name="digest">The finalized SHA-256 of every byte sent. <para>全部已发送字节的定稿 SHA-256。</para></param>
    internal void RecordProgramPayloadDigest(Stream source, FirehoseSectorAddress startSector, long numSectors,
        int physicalPartition, long startPosition, byte[] digest)
    {
        _programPayloadDigest = startSector.RelativeToEnd || startPosition != 0
            ? null
            : new ProgramPayloadDigest(source.Length, startSector.Value, numSectors, physicalPartition, digest);
    }

    /// <summary>
    /// Returns the digest recorded for a source of this length and this exact range and partition, or
    /// <see langword="null"/> when the caller must hash the source again.
    /// <para>返回针对该长度、同一范围与分区记录的摘要；调用方需要重新哈希源时返回 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="sourceLength">The source length about to be verified. <para>即将被校验的源长度。</para></param>
    /// <param name="startSector">The absolute starting sector. <para>绝对起始扇区。</para></param>
    /// <param name="numSectors">The programmed sector count. <para>已编程的扇区数。</para></param>
    /// <param name="physicalPartition">The physical storage region. <para>物理存储区域。</para></param>
    /// <returns>The recorded digest, or <see langword="null"/> when no record matches.
    /// <para>已记录的摘要；没有匹配记录时为 <see langword="null"/>。</para></returns>
    private byte[]? TryGetRecordedProgramDigest(long sourceLength, long startSector, long numSectors,
        int physicalPartition)
    {
        if (_programPayloadDigest is not { } recorded
            || recorded.StartSector != startSector
            || recorded.NumSectors != numSectors
            || recorded.PhysicalPartition != physicalPartition
            || recorded.SourceLength != sourceLength)
        {
            return null;
        }

        return recorded.Digest;
    }

    /// <summary>
    /// Sends <c>&lt;getsha256digest&gt;</c> on an owned lease and extracts the digest from the target
    /// logs or response attributes. A NAK or a missing digest yields <see langword="null"/> and keeps
    /// the session usable.
    /// <para>在已持有的租约上发送 <c>&lt;getsha256digest&gt;</c>，并从目标日志或响应属性中提取摘要。
    /// NAK 或缺少摘要时返回 <see langword="null"/>，会话保持可用。</para>
    /// </summary>
    /// <param name="startSector">The absolute starting sector. <para>绝对起始扇区。</para></param>
    /// <param name="numSectors">The number of sectors to hash. <para>待计算哈希的扇区数。</para></param>
    /// <param name="physicalPartition">The physical storage region or LUN. <para>物理存储区域或 LUN。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="slot">
    /// Optional slot number for the <c>slot</c> attribute; <see langword="null"/> or zero omits it, so a
    /// command without a slot keeps the wire form that omits the attribute.
    /// <para>可选的 <c>slot</c> 属性槽位编号；<see langword="null"/> 或零时省略，使不带槽位的命令保持
    /// 省略该属性的线上形式。</para>
    /// </param>
    internal async Task<byte[]?> GetSha256DigestCoreAsync(long startSector, long numSectors,
        int physicalPartition, CancellationToken cancellationToken, int? slot = null)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(startSector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(numSectors);
        _ = ValidateStorageRange(startSector, numSectors, physicalPartition);
        var logs = new List<string>();
        var response = await SendCommandAndWaitResponseAsync(
            FirehoseXmlBuilder.BuildGetSha256Digest(Configuration.SectorSize, numSectors, physicalPartition,
                startSector, slot),
            cancellationToken,
            blockObserver: block => logs.AddRange(block.Logs)).ConfigureAwait(false);
        if (!response.IsAck)
        {
            Log("The target rejected <getsha256digest>; digest verification is unavailable.");
            return null;
        }

        foreach (var log in logs)
        {
            if (TryExtractDigestHex(log) is { } hex)
            {
                return Convert.FromHexString(hex);
            }
        }

        foreach (var name in new[] { "Digest", "SHA256", "sha256" })
        {
            if (TryExtractDigestHex(response[name]) is { } attributeHex)
            {
                return Convert.FromHexString(attributeHex);
            }
        }

        Log("The target acknowledged <getsha256digest> without reporting a digest.");
        return null;
    }

    /// <summary>
    /// Extracts a 64-character hexadecimal digest from free-form log text or an attribute value;
    /// the target emits it as <c>Digest &lt;hex&gt;</c> inside a log node.
    /// <para>从自由格式的日志文本或属性值中提取 64 字符的十六进制摘要；目标以日志节点内的
    /// <c>Digest &lt;hex&gt;</c> 形式输出。</para>
    /// </summary>
    private static string? TryExtractDigestHex(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var start = 0;
        while (start + 64 <= text.Length)
        {
            if (Uri.IsHexDigit(text[start]) && Uri.IsHexDigit(text[start + 63]))
            {
                var candidate = text.AsSpan(start, 64);
                var allHex = true;
                foreach (var character in candidate)
                {
                    if (!Uri.IsHexDigit(character))
                    {
                        allHex = false;
                        break;
                    }
                }

                if (allHex)
                {
                    return new string(candidate);
                }
            }

            start++;
        }

        return null;
    }
}