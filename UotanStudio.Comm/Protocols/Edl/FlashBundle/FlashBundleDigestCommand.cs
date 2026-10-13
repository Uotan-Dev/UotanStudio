using System.Security.Cryptography;

namespace UotanStudio.Comm.Protocols.Edl.FlashBundle;

/// <summary>
/// A manifest program entry bound to a write digest, after the bytes it covers have been hashed and compared.
/// A write is bound to its digest in two steps: the program operation writes the range, and a
/// <c>&lt;getsha256digest&gt;</c> operation over the same range is dispatched right after it, so the target
/// reports the digest of what it actually stored. This type performs the
/// first half of that pairing: it hashes exactly the byte sequence the program path will put on the wire, in
/// the same write chunks, and refuses the entry when the manifest digest does not match, so nothing is written
/// on a mismatch.
/// <para>表示与写入摘要绑定的清单 program 条目，且其所覆盖的字节已完成哈希与比对。写入与其摘要分两步绑定：
/// program 操作先写入该范围，紧接着对同一范围派发 <c>&lt;getsha256digest&gt;</c>，
/// 由目标上报它实际存储内容的摘要。本类型负责该配对的前半部分：
/// 它对编程路径即将上线的精确字节序列做哈希，并采用相同的写入分块，一旦清单摘要不匹配就拒绝该条目，
/// 从而在不匹配时完全不写入。</para>
/// </summary>
/// <param name="Digest">The SHA-256 of the exact wire bytes.
/// <para>即将上线精确字节的 SHA-256。</para></param>
/// <param name="DigestHex">The lowercase hexadecimal form of <paramref name="Digest"/>.
/// <para><paramref name="Digest"/> 的小写十六进制形式。</para></param>
/// <param name="ByteCount">The checked byte length of the programmed range.
/// <para>待编程范围的经校验字节长度。</para></param>
/// <param name="PacketCount">The number of write chunks the wire sequence holds.
/// <para>上线序列所包含的写入块数量。</para></param>
/// <param name="CommandXml">The digest-bound program document produced for the range.
/// <para>为本区域生成的摘要绑定编程文档。</para></param>
internal sealed record FlashBundleDigestCommand(byte[] Digest, string DigestHex, long ByteCount, int PacketCount,
    string CommandXml)
{
    /// <summary>
    /// The refusal recorded when a digest-bound entry declares no signature for its digest.
    /// <para>摘要绑定条目声明了摘要却没有为其声明签名时应记录的拒绝理由。</para>
    /// </summary>
    /// <value>Stable refusal text naming the missing signature.</value>
    internal const string MissingSignatureReason =
        "A digest-bound program entry must declare digest_signature and signature_size; nothing was written.";

    /// <summary>
    /// The refusal recorded when the declared digest does not equal the bytes the program path would send.
    /// <para>声明的摘要与编程路径即将发送的字节不一致时应记录的拒绝理由。</para>
    /// </summary>
    /// <value>Stable refusal text naming the digest mismatch.</value>
    internal const string DigestMismatchReason =
        "The declared write digest does not match the bytes this program entry would write; nothing was written.";

    /// <summary>
    /// The refusal recorded when the write sequence needs more packet digests than the digest tables can
    /// carry (<see cref="EdlConstants.MaximumPacketDigestCount"/>).
    /// <para>写入序列所需包摘要数量超过摘要表容量
    ///（<see cref="EdlConstants.MaximumPacketDigestCount"/>）时应记录的拒绝理由。</para>
    /// </summary>
    /// <value>Stable refusal text naming the digest-table capacity.</value>
    internal const string PacketCapacityReason =
        "The write sequence exceeds the packet digests one signed digest table set can carry; nothing was written.";

    /// <summary>
    /// The refusal recorded when the range, the chunk bound or the address grammar is out of bounds.
    /// <para>范围、写入块上限或地址文法越界时应记录的拒绝理由。</para>
    /// </summary>
    /// <value>Stable refusal text naming the rejected range.</value>
    internal const string RangeReason =
        "The digest-bound program range is outside the validated write bounds; nothing was written.";

    /// <summary>
    /// The refusal recorded when the declared digest is not one SHA-256 width in hexadecimal.
    /// <para>声明的摘要不是十六进制表示的单个 SHA-256 宽度时应记录的拒绝理由。</para>
    /// </summary>
    /// <value>Stable refusal text naming the malformed digest.</value>
    internal const string DigestFormatReason =
        "The declared write digest is not 64 hexadecimal characters; nothing was written.";

    /// <summary>
    /// The refusal recorded when the declared signature is not well-formed base64 of the declared size.
    /// <para>声明的签名不是与声明大小相符的合法 Base64 时应记录的拒绝理由。</para>
    /// </summary>
    /// <value>Stable refusal text naming the malformed signature.</value>
    internal const string SignatureFormatReason =
        "The declared digest signature does not decode to the declared signature size; nothing was written.";

    /// <summary>
    /// Hashes the exact wire byte sequence of a digest-bound program entry and builds its command document,
    /// reporting a refusal instead of throwing so the caller can record the item and write nothing.
    /// <para>对摘要绑定 program 条目的上线字节序列做哈希并构造其命令文档，报告拒绝而不抛出异常，
    /// 使调用方能够记录该条目且不写入任何内容。</para>
    /// </summary>
    /// <param name="entry">The parsed program entry that declares a write digest.
    /// <para>声明了写入摘要的已解析 program 条目。</para></param>
    /// <param name="source">The source stream positioned at the first byte the program path reads; its position
    /// is restored before returning. <para>定位在编程路径首个读取字节的源流，返回前会恢复其位置。</para></param>
    /// <param name="sectorSize">The sector size the range was planned with.
    /// <para>本范围规划所用的扇区大小。</para></param>
    /// <param name="numSectors">The resolved number of sectors to program.
    /// <para>解析出的待编程扇区数。</para></param>
    /// <param name="startSector">The absolute starting sector. <para>绝对起始扇区。</para></param>
    /// <param name="payloadChunkBytes">The effective write chunk, from
    /// <see cref="EffectiveChunkBytes"/>. <para>实际写入块大小，取自
    /// <see cref="EffectiveChunkBytes"/>。</para></param>
    /// <param name="slot">The slot emitted with the command. <para>随命令输出的槽位。</para></param>
    /// <param name="cancellationToken">Cancellation observed while the source is hashed.
    /// <para>哈希源期间观察到的取消令牌。</para></param>
    /// <returns>A task whose result reports whether the command was built, carrying the prepared command when every
    /// check passed and the refusal reason when one failed.
    /// <para>任务结果指示命令是否构造成功：全部校验通过时携带已准备的命令，校验失败时携带拒绝理由。</para></returns>
    /// <exception cref="OperationCanceledException">The token was cancelled while hashing the source.
    /// <para>哈希源期间令牌被取消。</para></exception>
    internal static async Task<(bool Prepared, FlashBundleDigestCommand? Command, string? Refusal)> TryPrepareAsync(
        RawProgramEntry entry, Stream source, long sectorSize, long numSectors, long startSector,
        long payloadChunkBytes, int? slot, CancellationToken cancellationToken)
    {
        if (entry.DigestSignature is not { Length: > 0 } declaredSignature
            || entry.SignatureSize is < 1 or > int.MaxValue)
        {
            return (false, null, MissingSignatureReason);
        }

        long byteCount;
        try
        {
            byteCount = FirehoseXmlBuilder.ValidateProgramRange(sectorSize, numSectors,
                entry.PhysicalPartitionNumber, startSector, slot);
        }
        catch (ArgumentOutOfRangeException)
        {
            return (false, null, RangeReason);
        }
        catch (OverflowException)
        {
            return (false, null, RangeReason);
        }

        if (payloadChunkBytes <= 0 || payloadChunkBytes > 16777216 || payloadChunkBytes % sectorSize != 0)
        {
            return (false, null, RangeReason);
        }

        var position = source.Position;
        var available = source.Length - position;
        long tailPaddingBytes;
        try
        {
            tailPaddingBytes = FlashProgramLength.ResolveTailPadding(available, numSectors, sectorSize);
        }
        catch (ArgumentOutOfRangeException)
        {
            source.Position = position;
            return (false, null, RangeReason);
        }
        catch (OverflowException)
        {
            source.Position = position;
            return (false, null, RangeReason);
        }

        var chunk = new byte[(int)payloadChunkBytes];
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var dataBytes = byteCount - tailPaddingBytes;
        long written = 0;
        var packets = 0;
        try
        {
            while (written < byteCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(payloadChunkBytes, byteCount - written);
                var readable = (int)Math.Min(toRead, dataBytes - written);
                if (readable > 0)
                {
                    await source.ReadExactlyAsync(chunk.AsMemory(0, readable), cancellationToken)
                        .ConfigureAwait(false);
                }

                if (readable < toRead)
                {
                    chunk.AsSpan(readable, toRead - readable).Clear();
                }

                hasher.AppendData(chunk, 0, toRead);
                written += toRead;
                packets++;
                if (packets > EdlConstants.MaximumPacketDigestCount)
                {
                    return (false, null, PacketCapacityReason);
                }
            }
        }
        finally
        {
            source.Position = position;
        }

        var digest = hasher.GetHashAndReset();
        var digestHex = Convert.ToHexStringLower(digest);
        if (!string.Equals(digestHex, entry.Digest, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, DigestMismatchReason);
        }

        try
        {
            return (true, new FlashBundleDigestCommand(digest, digestHex, byteCount, packets,
                FirehoseXmlBuilder.BuildProgramWithDigest(sectorSize, numSectors, entry.Filename,
                    entry.PhysicalPartitionNumber, startSector, digestHex, declaredSignature,
                    (int)entry.SignatureSize, string.IsNullOrEmpty(entry.Label) ? null : entry.Label, slot)), null);
        }
        catch (ArgumentOutOfRangeException)
        {
            return (false, null, DigestFormatReason);
        }
        catch (FormatException)
        {
            return (false, null, SignatureFormatReason);
        }
        catch (OverflowException)
        {
            return (false, null, RangeReason);
        }
    }

    /// <summary>
    /// Derives the write chunk the program path uses from the negotiated payload and the explicit write-chunk
    /// bound, rounded down to a whole sector, so a digest computed here covers the same packet boundaries the
    /// transfer stage produces.
    /// <para>依据协商负载与显式写入块上限推导编程路径使用的写入块，并向下取整到整扇区，使此处计算的
    /// 摘要与传输阶段产生的包边界一致。</para>
    /// </summary>
    /// <param name="configuration">The live Firehose configuration. <para>当前 Firehose 配置。</para></param>
    /// <returns>The effective chunk in bytes. <para>实际写入块大小（字节）。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The configuration carries no usable sector size.
    /// <para>配置未提供可用的扇区大小。</para></exception>
    internal static long EffectiveChunkBytes(FirehoseConfiguration configuration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuration.SectorSize);
        var bounded = Math.Min(configuration.MaxPayloadSizeToTarget, configuration.MaxProgramChunkBytes);
        if (bounded < configuration.SectorSize)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration),
                "The effective write chunk is smaller than one sector.");
        }

        return checked(bounded - (bounded % configuration.SectorSize));
    }
}