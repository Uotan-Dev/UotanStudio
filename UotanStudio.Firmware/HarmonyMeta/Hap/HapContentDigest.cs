using System.Buffers.Binary;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.HarmonyMeta.Hap;

/// <summary>
/// Which signature algorithm a HAP content digest is computed for; the identifier is embedded in the digest message
/// and verified by the device against the CMS signature algorithm.
/// <para>HAP 内容摘要所用的签名算法；该标识写入摘要消息并由设备与 CMS 签名算法核对。</para>
/// </summary>
public enum HapDigestAlgorithm : uint
{
    /// <summary>ECDSA P-256 with SHA-256. <para>ECDSA P-256 + SHA-256。</para></summary>
    EcdsaSha256 = HapSignatureConstants.AlgorithmEcdsaSha256,

    /// <summary>ECDSA with SHA-384. <para>ECDSA + SHA-384。</para></summary>
    EcdsaSha384 = HapSignatureConstants.AlgorithmEcdsaSha384,

    /// <summary>ECDSA with SHA-512. <para>ECDSA + SHA-512。</para></summary>
    EcdsaSha512 = HapSignatureConstants.AlgorithmEcdsaSha512,

    /// <summary>RSA-PSS with SHA-256. <para>RSA-PSS + SHA-256。</para></summary>
    RsaPssSha256 = HapSignatureConstants.AlgorithmRsaPssSha256,

    /// <summary>RSA-PSS with SHA-384. <para>RSA-PSS + SHA-384。</para></summary>
    RsaPssSha384 = HapSignatureConstants.AlgorithmRsaPssSha384,

    /// <summary>RSA-PSS with SHA-512. <para>RSA-PSS + SHA-512。</para></summary>
    RsaPssSha512 = HapSignatureConstants.AlgorithmRsaPssSha512,
}

/// <summary>
/// Streaming HAP content digest over the four logical ZIP sections. The hash input is
/// <c>0x5a | u32_LE(total chunk count) | per-chunk SHA-256(0xa5 | u32_LE(chunk length) | chunk) | optional block values</c>
/// where the optional values are appended verbatim after the chunk records in signing-block order.
/// <para>跨四个逻辑 ZIP 区段的流式 HAP 内容摘要。哈希输入为
/// <c>0x5a | u32_LE(总块数) | 逐块 SHA-256(0xa5 | u32_LE(块长) | 块) | 可选块值</c>，
/// 可选块值按签名块顺序逐字接在块记录之后。</para>
/// </summary>
/// <remarks>
/// The four sections are the ZIP entries region, the signing-block section (empty while signing, filled while
/// verifying), the central directory and the EOCD whose central-directory offset field is set to the signing block
/// offset. Each section's trailing partial chunk is finalized at the section boundary, so a section always ends a
/// chunk and an empty section contributes no chunk. All hashes go through <see cref="Sha256"/> per the repository
/// digest boundary.
/// <para>四个区段为 ZIP 条目区、签名块区段（签名时为空、验签时为实块）、中央目录与 EOCD（其 CD 偏移字段
/// 指向签名块偏移）。每个区段的尾部不足块的部分在区段边界处定稿，因此区段总是终结一个分块，空区段不产生
/// 任何分块。按仓库摘要边界，全部哈希经 <see cref="Sha256"/> 计算。</para>
/// </remarks>
internal sealed class HapContentDigestComputer
{
    private readonly DigestAccumulator _top;
    private readonly HapDigestAlgorithm _algorithm;
    private readonly DigestAccumulator _chunk;
    private readonly byte[] _pending;
    private int _pendingLength;
    private uint _chunkCount;

    private HapContentDigestComputer(HapDigestAlgorithm algorithm, uint totalChunkCount)
    {
        _algorithm = algorithm;
        _top = Sha256.CreateAccumulator();
        _chunk = Sha256.CreateAccumulator();
        _pending = new byte[HapSignatureConstants.DigestChunkSize];
        Span<byte> head = stackalloc byte[5];
        head[0] = HapSignatureConstants.DigestTopPrefix;
        BinaryPrimitives.WriteUInt32LittleEndian(head[1..], totalChunkCount);
        _top.Append(head);
    }

    /// <summary>
    /// Creates a computer for the supplied section lengths; the total chunk count is precomputed so the first
    /// bytes of the hash input are known before any section is fed.
    /// <para>按给定区段长度创建计算器；总块数预先算出，使哈希输入的开头在任何区段输入前即已确定。</para>
    /// </summary>
    /// <param name="sectionLengths">Logical section lengths in feed order. <para>按输入顺序的逻辑区段长度。</para></param>
    /// <param name="algorithm">Digest algorithm identifier. <para>摘要算法标识。</para></param>
    /// <returns>A fresh computer. <para>新计算器。</para></returns>
    public static HapContentDigestComputer Create(ReadOnlySpan<int> sectionLengths, HapDigestAlgorithm algorithm)
    {
        uint total = 0;
        foreach (var length in sectionLengths)
        {
            total += (uint)((length + HapSignatureConstants.DigestChunkSize - 1) / HapSignatureConstants.DigestChunkSize);
        }

        return new HapContentDigestComputer(algorithm, total);
    }

    /// <summary>
    /// Feeds one logical section; the section's trailing partial chunk is finalized here, so a section always
    /// ends a chunk and an empty section contributes nothing.
    /// <para>输入一个逻辑区段；区段尾部不足块的部分在此定稿，因此区段总是终结一个分块，空区段不产生任何输入。</para>
    /// </summary>
    /// <param name="section">Section bytes. <para>区段字节。</para></param>
    public void UpdateSection(ReadOnlySpan<byte> section)
    {
        BeginSection();
        UpdateSectionPiece(section);
        EndSection();
    }

    /// <summary>
    /// Opens a section scope: pieces fed through <see cref="UpdateSectionPiece"/> are chunked as one contiguous
    /// section until <see cref="EndSection"/> finalizes the trailing partial chunk.
    /// <para>打开区段作用域：经 <see cref="UpdateSectionPiece"/> 输入的片段按一个连续区段分块，
    /// 直到 <see cref="EndSection"/> 定稿尾部不足块的部分。</para>
    /// </summary>
    public void BeginSection()
    {
    }

    /// <summary>
    /// Feeds bytes into the open section. <para>向已打开的区段输入字节。</para></summary>
    /// <param name="piece">Bytes of the current section. <para>当前区段的字节。</para></param>
    public void UpdateSectionPiece(ReadOnlySpan<byte> piece)
    {
        var offset = 0;
        while (offset < piece.Length)
        {
            var take = Math.Min(HapSignatureConstants.DigestChunkSize - _pendingLength, piece.Length - offset);
            piece.Slice(offset, take).CopyTo(_pending.AsSpan(_pendingLength));
            _pendingLength += take;
            offset += take;
            if (_pendingLength == HapSignatureConstants.DigestChunkSize)
            {
                FlushChunk();
            }
        }
    }

    /// <summary>
    /// Finalizes the open section: the trailing partial chunk becomes its own chunk and an empty section
    /// contributes nothing. <para>定稿已打开的区段：尾部不足块的部分自成一块，空区段不产生任何输入。</para>
    /// </summary>
    public void EndSection()
    {
        if (_pendingLength > 0)
        {
            FlushChunk();
        }
    }

    /// <summary>
    /// Appends one optional signing-block value verbatim after the chunk records; no chunk framing is applied.
    /// <para>把一个可选签名块值逐字接在块记录之后；不加任何分块框架。</para>
    /// </summary>
    /// <param name="value">Block value bytes. <para>块值字节。</para></param>
    public void UpdateOptionalBlock(ReadOnlySpan<byte> value)
    {
        _top.Append(value);
    }

    /// <summary>Finalizes the digest; every section has already flushed its trailing chunk.
    /// <para>定稿摘要；每个区段的尾部分块均已冲刷。</para></summary>
    /// <returns>The digest bytes. <para>摘要字节。</para></returns>
    public byte[] Finalize() => _top.GetHashAndReset();

    private void FlushChunk()
    {
        Span<byte> prefix = stackalloc byte[5];
        prefix[0] = HapSignatureConstants.DigestChunkPrefix;
        BinaryPrimitives.WriteUInt32LittleEndian(prefix[1..], (uint)_pendingLength);
        _chunk.Append(prefix);
        _chunk.Append(_pending.AsSpan(0, _pendingLength));
        var digest = _chunk.GetHashAndReset();
        _top.Append(digest);
        _chunkCount++;
        _pendingLength = 0;
    }

    /// <summary>
    /// Builds the digest message stored as the CMS attached content: version, single digest block, algorithm and
    /// the digest itself, all little-endian.
    /// <para>构造作为 CMS attached 内容的摘要消息：版本、单一摘要块、算法与摘要本体，全部小端。</para>
    /// </summary>
    /// <param name="algorithm">Digest algorithm identifier. <para>摘要算法标识。</para></param>
    /// <param name="digest">Content digest bytes. <para>内容摘要字节。</para></param>
    /// <returns>The encoded digest message. <para>编码后的摘要消息。</para></returns>
    public static byte[] BuildDigestMessage(HapDigestAlgorithm algorithm, ReadOnlySpan<byte> digest)
    {
        var message = new byte[20 + digest.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(message, HapSignatureConstants.DigestMessageVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 8 + (uint)digest.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(12), (uint)algorithm);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(16), (uint)digest.Length);
        digest.CopyTo(message.AsSpan(20));
        return message;
    }
}