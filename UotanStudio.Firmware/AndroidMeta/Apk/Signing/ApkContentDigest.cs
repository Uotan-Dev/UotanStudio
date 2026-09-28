using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UotanStudio.Firmware.AndroidMeta.Apk.Signing;

/// <summary>
/// Computes the two-level chunked content digests protecting the ZIP entries, central directory
/// and EOCD sections of an APK (official v2/v3 digest algorithm).
/// <para>按官方 v2/v3 摘要算法计算保护 ZIP 条目、中央目录与 EOCD 的两级分块内容摘要。</para>
/// </summary>
internal static class ApkContentDigest
{
    /// <summary>
    /// Computes the full APK digest over the ZIP content plus EOCD of an APK file.
    /// The EOCD central-directory offset field is patched to the signing-block offset during hashing
    /// without mutating the source file.
    /// <para>计算 APK 的完整内容摘要（ZIP 条目加 EOCD）。哈希期间 EOCD 的中央目录偏移字段被替换为签名块偏移，不改动源文件。</para>
    /// </summary>
    /// <param name="apkStream">Readable, seekable APK stream. <para>可读可定位的 APK 流。</para></param>
    /// <param name="zipContentLength">Length of ZIP entries from offset 0 to the signing block.
    /// <para>从偏移 0 到签名块的 ZIP 条目长度。</para></param>
    /// <param name="centralDirectoryReadOffset">
    /// Central-directory offset in the input file (where to read from).
    /// <para>输入文件中中央目录的偏移（读取位置）。</para>
    /// </param>
    /// <param name="patchedCentralDirectoryOffset">
    /// Central-directory offset written into the EOCD while hashing (signing-block based).
    /// <para>哈希期间写入 EOCD 的中央目录偏移（按签名块位置）。</para>
    /// </param>
    /// <param name="centralDirectoryLength">Central directory length. <para>中央目录长度。</para></param>
    /// <param name="eocd">Raw EOCD bytes. <para>EOCD 原始字节。</para></param>
    /// <param name="hashName">Hash algorithm name ("SHA256"/"SHA512").
    /// <para>哈希算法名称（"SHA256"/"SHA512"）。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The top-level content digest over all per-chunk digests; its length equals the hash
    /// output width (32 bytes for SHA256, 64 for SHA512).
    /// <para>对所有分块摘要计算得到的顶层内容摘要；长度即哈希输出宽度（SHA256 为 32 字节，SHA512 为 64 字节）。</para></returns>
    /// <exception cref="InvalidDataException">A length or offset is negative, exceeds <c>uint.MaxValue</c>
    /// or the stream bounds, or the EOCD is shorter than 22 or longer than 65 557 bytes.
    /// <para>某个长度或偏移为负、超过 <c>uint.MaxValue</c> 或流边界，或 EOCD 短于 22 字节、长于 65 557 字节。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled before or during chunk hashing.
    /// <para>在分块哈希开始前或期间令牌被取消。</para></exception>
    public static byte[] ComputeApkDigest(
        Stream apkStream,
        long zipContentLength,
        long centralDirectoryReadOffset,
        long patchedCentralDirectoryOffset,
        long centralDirectoryLength,
        byte[] eocd,
        string hashName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!apkStream.CanSeek || !apkStream.CanRead || zipContentLength < 0 || zipContentLength > uint.MaxValue
            || centralDirectoryLength < 0 || centralDirectoryLength > uint.MaxValue
            || patchedCentralDirectoryOffset < 0 || patchedCentralDirectoryOffset > uint.MaxValue
            || centralDirectoryReadOffset < zipContentLength || centralDirectoryReadOffset > apkStream.Length
            || zipContentLength > apkStream.Length || centralDirectoryLength > apkStream.Length - centralDirectoryReadOffset
            || eocd.Length > 22 + ushort.MaxValue)
            throw new InvalidDataException("Invalid APK digest ranges. APK 摘要范围无效。");
        if (eocd.Length < 22)
        {
            throw new InvalidDataException("EOCD record is too short. EOCD 记录过短。");
        }

        var patchedEocd = (byte[])eocd.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(patchedEocd.AsSpan(16, 4), (uint)patchedCentralDirectoryOffset);

        var chunkCountZip = CountChunks(zipContentLength);
        var chunkCountCd = CountChunks(centralDirectoryLength);
        var chunkCountEocd = CountChunks(patchedEocd.Length);
        var totalChunks = checked((int)(chunkCountZip + chunkCountCd + chunkCountEocd));

        using var topLevel = IncrementalHash.CreateHash(new HashAlgorithmName(hashName));
        var digestSize = topLevel.HashLengthInBytes;
        var perChunkDigests = new byte[checked(totalChunks * digestSize)];
        var filled = 0;

        filled = AppendSectionDigests(apkStream, 0, zipContentLength, hashName, perChunkDigests, filled, cancellationToken);
        filled = AppendSectionDigests(apkStream, centralDirectoryReadOffset, centralDirectoryLength, hashName, perChunkDigests, filled, cancellationToken);
        filled = AppendDigestOf(patchedEocd, 0, patchedEocd.Length, hashName, perChunkDigests, filled);
        if (filled != perChunkDigests.Length)
        {
            throw new InvalidOperationException("Chunk digest buffer was not filled exactly. 分块摘要缓冲区未被精确填满。");
        }

        topLevel.AppendData(TopPrefix(totalChunks));
        topLevel.AppendData(perChunkDigests);
        return topLevel.GetHashAndReset();
    }

    private static long CountChunks(long length) =>
        (length + ApkSignatureConstants.ContentDigestChunkSize - 1) / ApkSignatureConstants.ContentDigestChunkSize;

    private static byte[] TopPrefix(int count)
    {
        var prefix = new byte[5];
        prefix[0] = 0x5a;
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(1, 4), (uint)count);
        return prefix;
    }

    /// <summary>
    /// Appends per-chunk digests of a stream section into the digest buffer, returning the new fill position.
    /// <para>把流区段的各分块摘要写入摘要缓冲区，返回新的填充位置。</para>
    /// </summary>
    private static int AppendSectionDigests(
        Stream stream, long offset, long length, string hashName, byte[] digests, int filled, CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            return filled;
        }

        const long chunkSize = ApkSignatureConstants.ContentDigestChunkSize;
        var buffer = new byte[chunkSize];
        long remaining = length;
        long position = offset;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkLength = (int)Math.Min(buffer.Length, remaining);
            filled = AppendChunkDigest(stream, position, chunkLength, hashName, digests, filled, buffer);
            position += chunkLength;
            remaining -= chunkLength;
        }

        return filled;
    }

    /// <summary>
    /// Appends per-chunk digests of an in-memory section into the digest buffer.
    /// <para>把内存区段的各分块摘要写入摘要缓冲区。</para>
    /// </summary>
    private static int AppendDigestOf(
        byte[] data, long offset, long length, string hashName, byte[] digests, int filled)
    {
        if (length == 0)
        {
            return filled;
        }

        const long chunkSize = ApkSignatureConstants.ContentDigestChunkSize;
        long remaining = length;
        long position = offset;
        while (remaining > 0)
        {
            var chunkLength = (int)Math.Min(chunkSize, remaining);
            filled = AppendChunkDigest(data, position, chunkLength, hashName, digests, filled);
            position += chunkLength;
            remaining -= chunkLength;
        }

        return filled;
    }

    private static int AppendChunkDigest(
        Stream stream, long offset, int chunkLength, string hashName, byte[] digests, int filled, byte[]? buffer = null)
    {
        using var hash = IncrementalHash.CreateHash(new HashAlgorithmName(hashName));
        hash.AppendData(ChunkPrefix(chunkLength));
        if (chunkLength > 0)
        {
            var scratch = buffer ?? new byte[chunkLength];
            stream.Seek(offset, SeekOrigin.Begin);
            stream.ReadExactly(scratch.AsSpan(0, chunkLength));
            hash.AppendData(scratch.AsSpan(0, chunkLength));
        }

        var written = hash.GetHashAndReset();
        written.CopyTo(digests, filled);
        return checked(filled + written.Length);
    }

    private static int AppendChunkDigest(
        byte[] data, long offset, int chunkLength, string hashName, byte[] digests, int filled)
    {
        using var hash = IncrementalHash.CreateHash(new HashAlgorithmName(hashName));
        hash.AppendData(ChunkPrefix(chunkLength));
        if (chunkLength > 0)
        {
            hash.AppendData(data.AsSpan(checked((int)offset), chunkLength));
        }

        var written = hash.GetHashAndReset();
        written.CopyTo(digests, filled);
        return checked(filled + written.Length);
    }

    private static byte[] ChunkPrefix(int length)
    {
        var prefix = new byte[5];
        prefix[0] = 0xa5;
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(1, 4), (uint)length);
        return prefix;
    }
}