using System.Security.Cryptography;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Represents a single entry in a digest table: the sector offset, the SHA256 digest of the
/// data block, and an optional signature for Verified Image Programming (VIP).
/// <para>表示摘要表中的单个条目：扇区偏移、数据块的 SHA256 摘要，以及用于验证镜像编程
/// （VIP）的可选签名。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DigestEntry"/> class.
/// <para>初始化 <see cref="DigestEntry"/> 类的新实例。</para>
/// </remarks>
/// <param name="sectorOffset">The sector offset for this entry. 此条目的扇区偏移。</param>
/// <param name="digest">The SHA256 digest of the data block. 数据块的 SHA256 摘要。</param>
/// <param name="signature">Optional signature of the digest. 摘要的可选签名。</param>
public sealed class DigestEntry(long sectorOffset, byte[] digest, byte[]? signature = null)
{

    /// <summary>
    /// Gets the sector offset where this digest entry applies.
    /// <para>获取此摘要条目适用的扇区偏移。</para>
    /// </summary>
    public long SectorOffset { get; } = sectorOffset;

    /// <summary>
    /// Gets the SHA256 hash of the data block at this sector offset.
    /// <para>获取此扇区偏移处数据块的 SHA256 哈希值。</para>
    /// </summary>
    public byte[] Digest { get; } = digest ?? throw new ArgumentNullException(nameof(digest));

    /// <summary>
    /// Gets the optional signature of the digest for verified image programming.
    /// <para>获取用于验证镜像编程的摘要可选签名。</para>
    /// </summary>
    public byte[]? Signature { get; } = signature;
}

/// <summary>
/// A digest table for Verified Image Programming (VIP): a list of SHA256 digest entries with
/// optional chaining (cumulative digests over chain segments) and signature support.
/// <para>用于验证镜像编程（VIP）的摘要表：SHA256 摘要条目列表，支持可选的链式（链段的累积
/// 摘要）与签名。</para>
/// </summary>
public sealed class DigestTable
{
    /// <summary>
    /// Gets the list of digest entries in this table.
    /// <para>获取此表中的摘要条目列表。</para>
    /// </summary>
    public List<DigestEntry> Entries { get; } = [];

    /// <summary>
    /// Gets or sets whether this is a chained digest table where chain segments include a
    /// cumulative digest over all data in the segment.
    /// <para>获取或设置此是否为链式摘要表：链段包含对该段全部数据的累积摘要。</para>
    /// </summary>
    public bool IsChained { get; set; }

    /// <summary>
    /// Gets or sets the size of the signature in bytes for entries that include signatures.
    /// <para>获取或设置包含签名的条目的签名大小（字节）。</para>
    /// </summary>
    public int SignatureSize { get; set; }

    /// <summary>
    /// Gets or sets the number of data bytes covered by each digest entry (0 = sector
    /// granularity, one digest per sector).
    /// <para>获取或设置每个摘要条目覆盖的数据字节数（0 = 扇区粒度，每扇区一个摘要）。</para>
    /// </summary>
    public long ChunkSizeInBytes { get; set; }

    /// <summary>
    /// Computes the SHA256 digest for a data block.
    /// <para>计算数据块的 SHA256 摘要。</para>
    /// </summary>
    /// <param name="data">The data buffer. 数据缓冲。</param>
    /// <param name="offset">The starting offset. 起始偏移。</param>
    /// <param name="count">The number of bytes. 字节数。</param>
    /// <returns>The 32-byte SHA256 hash. 32 字节的 SHA256 哈希。</returns>
    public static byte[] ComputeDigest(byte[] data, int offset, int count)
    {
        return data == null
            ? throw new ArgumentNullException(nameof(data))
            : offset < 0 || offset > data.Length
            ? throw new ArgumentOutOfRangeException(nameof(offset))
            : count < 0 || offset + count > data.Length
            ? throw new ArgumentOutOfRangeException(nameof(count))
            : ComputeSha256(data, offset, count);
    }

    /// <summary>
    /// Builds a digest table from a data stream, computing SHA256 digests for each
    /// sector-aligned block (or each chunk when <paramref name="chunkSizeInBytes"/> is greater
    /// than zero).
    /// <para>从数据流构建摘要表，为每个扇区对齐的块（当 <paramref name="chunkSizeInBytes"/>
    /// 大于 0 时按块）计算 SHA256 摘要。</para>
    /// </summary>
    /// <param name="data">The stream containing the data. 包含数据的流。</param>
    /// <param name="sectorSize">The sector size in bytes. 扇区大小（字节）。</param>
    /// <param name="numSectors">The number of sectors to process. 待处理的扇区数量。</param>
    /// <param name="signatureSize">The size of the signature in bytes (0 if none). 签名大小（字节），无则为 0。</param>
    /// <param name="chunkSizeInBytes">Bytes per digest entry (0 = sector granularity). 每个摘要条目覆盖的字节数（0 = 扇区粒度）。</param>
    /// <returns>The computed digest table. 计算所得的摘要表。</returns>
    public static DigestTable BuildDigestTable(Stream data, long sectorSize, long numSectors, int signatureSize, long chunkSizeInBytes = 0)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (sectorSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorSize));
        }

        if (numSectors < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numSectors));
        }

        if (signatureSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(signatureSize));
        }

        var blockSize = ResolveBlockSize(sectorSize, chunkSizeInBytes);

        var table = new DigestTable
        {
            IsChained = false,
            SignatureSize = signatureSize,
            ChunkSizeInBytes = blockSize
        };

        var buffer = new byte[blockSize];
        var totalBytes = sectorSize * numSectors;
        long bytesProcessed = 0;

        while (bytesProcessed < totalBytes)
        {
            var toRead = (int)Math.Min(blockSize, totalBytes - bytesProcessed);
            if (!ReadBlock(data, buffer, toRead, out _))
            {
                break;
            }

            var digest = ComputeSha256(buffer, 0, toRead);
            table.Entries.Add(new DigestEntry(bytesProcessed / sectorSize, digest));
            bytesProcessed += toRead;
        }

        return table;
    }

    /// <summary>
    /// Builds a chained digest table where every chain segment has a cumulative digest
    /// computed over all data in the segment.
    /// <para>构建链式摘要表：每个链段都有一个对该段全部数据计算的累积摘要。</para>
    /// </summary>
    /// <param name="data">The stream containing the data. 包含数据的流。</param>
    /// <param name="sectorSize">The sector size in bytes. 扇区大小（字节）。</param>
    /// <param name="numSectors">The number of sectors to process. 待处理的扇区数量。</param>
    /// <param name="signatureSize">The size of the signature in bytes (0 if none). 签名大小（字节），无则为 0。</param>
    /// <param name="chainSize">The number of blocks per chain segment. 每个链段的块数量。</param>
    /// <param name="chunkSizeInBytes">Bytes per digest entry (0 = sector granularity). 每个摘要条目覆盖的字节数（0 = 扇区粒度）。</param>
    /// <returns>The chained digest table. 链式摘要表。</returns>
    public static DigestTable BuildChainedDigestTable(Stream data, long sectorSize, long numSectors, int signatureSize, int chainSize, long chunkSizeInBytes = 0)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (sectorSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorSize));
        }

        if (numSectors < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numSectors));
        }

        if (signatureSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(signatureSize));
        }

        if (chainSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chainSize));
        }

        var blockSize = ResolveBlockSize(sectorSize, chunkSizeInBytes);

        var table = new DigestTable
        {
            IsChained = true,
            SignatureSize = signatureSize,
            ChunkSizeInBytes = blockSize
        };

        var buffer = new byte[blockSize];
        var chainBuffer = new byte[(int)(blockSize * chainSize)];
        var chainOffset = 0;
        var totalBytes = sectorSize * numSectors;
        long bytesProcessed = 0;

        while (bytesProcessed < totalBytes)
        {
            var toRead = (int)Math.Min(blockSize, totalBytes - bytesProcessed);
            if (!ReadBlock(data, buffer, toRead, out _))
            {
                break;
            }

            Array.Copy(buffer, 0, chainBuffer, chainOffset, toRead);
            chainOffset += toRead;

            var digest = ComputeSha256(buffer, 0, toRead);
            table.Entries.Add(new DigestEntry(bytesProcessed / sectorSize, digest));
            bytesProcessed += toRead;

            if (chainOffset >= chainBuffer.Length || bytesProcessed >= totalBytes)
            {
                if (chainOffset > 0)
                {
                    var chainDigest = ComputeSha256(chainBuffer, 0, chainOffset);
                    table.Entries.Add(new DigestEntry((bytesProcessed - chainOffset) / sectorSize, chainDigest));
                }

                chainOffset = 0;
            }
        }

        return table;
    }

    /// <summary>
    /// Computes a SHA256 hash over the specified buffer range.
    /// <para>计算指定缓冲区范围的 SHA256 哈希。</para>
    /// </summary>
    /// <param name="data">The data buffer. 数据缓冲。</param>
    /// <param name="offset">The starting offset. 起始偏移。</param>
    /// <param name="count">The number of bytes. 字节数。</param>
    /// <returns>The 32-byte SHA256 hash. 32 字节的 SHA256 哈希。</returns>
    internal static byte[] ComputeSha256(byte[] data, int offset, int count) => SHA256.HashData(new ReadOnlySpan<byte>(data, offset, count));

    /// <summary>
    /// Converts a digest to its lowercase hex string form.
    /// <para>将摘要转换为其小写十六进制字符串形式。</para>
    /// </summary>
    /// <param name="digest">The digest bytes. 摘要字节。</param>
    /// <returns>The hex string. 十六进制字符串。</returns>
    internal static string ToHexString(byte[] digest) => Convert.ToHexString(digest).ToLowerInvariant();

    /// <summary>
    /// Compares two hashes in fixed Time.
    /// <para>以固定时间比较两个哈希。</para>
    /// </summary>
    /// <param name="left">The first hash. 第一个哈希。</param>
    /// <param name="right">The second hash. 第二个哈希。</param>
    /// <returns><c>true</c> when the hashes are equal. 哈希相等时返回 <c>true</c>。</returns>
    internal static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < left.Length; i++)
        {
            diff |= left[i] ^ right[i];
        }

        return diff == 0;
    }

    /// <summary>
    /// Reads one block, zero-filling a short read so digests always cover the full block at
    /// file end.
    /// <para>读取一个数据块，短读时补零，保证文件末尾的摘要始终覆盖完整块。</para>
    /// </summary>
    private static bool ReadBlock(Stream data, byte[] buffer, int toRead, out int bytesRead)
    {
        bytesRead = data.Read(buffer, 0, toRead);
        if (bytesRead == 0)
        {
            return false;
        }

        if (bytesRead < toRead)
        {
            Array.Clear(buffer, bytesRead, toRead - bytesRead);
        }

        return true;
    }

    /// <summary>
    /// Resolves the digest block size: the chunk size when greater than zero (must be a
    /// multiple of the sector size), else sector granularity.
    /// <para>解析摘要块大小：chunkSizeInBytes 大于 0 时使用该值（须为扇区大小整数倍），否则
    /// 回退为扇区粒度。</para>
    /// </summary>
    private static long ResolveBlockSize(long sectorSize, long chunkSizeInBytes)
    {
        return chunkSizeInBytes <= 0
            ? sectorSize
            : chunkSizeInBytes % sectorSize != 0
            ? throw new ArgumentOutOfRangeException(nameof(chunkSizeInBytes),
                "chunkSizeInBytes must be a multiple of sectorSize.")
            : chunkSizeInBytes;
    }
}