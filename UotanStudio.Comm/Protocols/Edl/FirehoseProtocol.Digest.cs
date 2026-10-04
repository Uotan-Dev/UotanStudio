using System.Security.Cryptography;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// On-target SHA256 digest queries (<c>&lt;getsha256digest&gt;</c>) used to verify or skip programming
/// without reading the region back. Some programmers reject the command; a rejection or a missing
/// digest is reported as <see langword="null"/> rather than as a session fault, mirroring the reference
/// behaviour where the caller keeps programming normally.
/// <para>目标侧 SHA256 摘要查询（<c>&lt;getsha256digest&gt;</c>），用于在不回读区域的情况下校验或跳过
/// 编程。部分 programmer 会拒绝该命令；拒绝或缺少摘要时返回 <see langword="null"/> 而不是判定会话失效，
/// 与参考实现中“调用方继续正常编程”的行为一致。</para>
/// </summary>
public sealed partial class FirehoseProtocol
{
    /// <summary>
    /// Requests the SHA256 digest the target computes for a flash range.
    /// <para>请求目标为一段闪存范围计算并上报的 SHA256 摘要。</para>
    /// </summary>
    /// <param name="startSector">The absolute starting sector. <para>绝对起始扇区。</para></param>
    /// <param name="numSectors">The number of sectors to hash; must be positive. <para>待计算哈希的扇区数；必须为正。</para></param>
    /// <param name="physicalPartition">The physical storage region or LUN. <para>物理存储区域或 LUN。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>
    /// The 32-byte digest, or <see langword="null"/> when the target rejects the command or reports no digest.
    /// <para>32 字节摘要；目标拒绝该命令或未上报摘要时为 <see langword="null"/>。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="numSectors"/> is not positive, or the
    /// range leaves the configured storage geometry. <para><paramref name="numSectors"/> 非正，或范围超出
    /// 已配置的存储几何。</para></exception>
    /// <exception cref="EdlException">The transport fails or times out. <para>传输层失败或超时。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled. <para>令牌被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The protocol has been disposed. <para>协议已被释放。</para></exception>
    public async Task<byte[]?> GetSha256DigestAsync(long startSector, long numSectors, int physicalPartition = 0,
        CancellationToken cancellationToken = default)
    {
        using var operation = await EnterOperationAsync(cancellationToken).ConfigureAwait(false);
        return await GetSha256DigestCoreAsync(startSector, numSectors, physicalPartition, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Digest fast path for an already-owned operation lease: hashes the source locally and compares it
    /// with the target-reported digest. Returns <see langword="true"/> only on a verified match; any
    /// unsupported or mismatched outcome returns <see langword="false"/> so the caller can fall back to
    /// the authoritative read-back comparison.
    /// <para>在已持有的操作租约上的摘要快速校验路径：本地计算源哈希并与目标上报的摘要比对。仅在验证
    /// 匹配时返回 <see langword="true"/>；任何不支持或不匹配的结果都返回 <see langword="false"/>，
    /// 由调用方回退到权威的回读比对。</para>
    /// </summary>
    /// <param name="dataStream">The seekable source stream, read from its start. <para>可寻址的源流，从起始处读取。</para></param>
    /// <param name="startSector">The absolute starting sector. <para>绝对起始扇区。</para></param>
    /// <param name="numSectors">The number of programmed sectors. <para>已编程的扇区数。</para></param>
    /// <param name="physicalPartition">The physical storage region or LUN. <para>物理存储区域或 LUN。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>
    /// <see langword="true"/> when the on-target digest matches the locally hashed source.
    /// <para>目标侧摘要与本地哈希匹配时为 <see langword="true"/>。</para>
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

            dataStream.Seek(0, SeekOrigin.Begin);
            byte[] local;
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536];
                int read;
                while ((read = await dataStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                }

                local = hasher.GetHashAndReset();
            }

            var remote = await GetSha256DigestCoreAsync(startSector, numSectors, physicalPartition, cancellationToken)
                .ConfigureAwait(false);
            if (remote is null || local.AsSpan().SequenceEqual(remote))
            {
                return remote is not null;
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
    /// Sends <c>&lt;getsha256digest&gt;</c> on an owned lease and extracts the digest from the target
    /// logs or response attributes. A NAK or a missing digest yields <see langword="null"/> and keeps
    /// the session usable.
    /// <para>在已持有的租约上发送 <c>&lt;getsha256digest&gt;</c>，并从目标日志或响应属性中提取摘要。
    /// NAK 或缺少摘要时返回 <see langword="null"/>，会话保持可用。</para>
    /// </summary>
    internal async Task<byte[]?> GetSha256DigestCoreAsync(long startSector, long numSectors,
        int physicalPartition, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(startSector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(numSectors);
        _ = ValidateStorageRange(startSector, numSectors, physicalPartition);
        var logs = new List<string>();
        var response = await SendCommandAndWaitResponseAsync(
            FirehoseXmlBuilder.BuildGetSha256Digest(Configuration.SectorSize, numSectors, physicalPartition,
                startSector),
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
    /// the reference target emits it as <c>Digest &lt;hex&gt;</c> inside a log node.
    /// <para>从自由格式的日志文本或属性值中提取 64 字符的十六进制摘要；参考目标以日志节点内的
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
            if (System.Uri.IsHexDigit(text[start]) && System.Uri.IsHexDigit(text[start + 63]))
            {
                var candidate = text.AsSpan(start, 64);
                var allHex = true;
                foreach (var character in candidate)
                {
                    if (!System.Uri.IsHexDigit(character))
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