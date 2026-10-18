using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.AndroidMeta.Apk.Signing;

/// <summary>
/// Computes the two-level chunked content digests protecting the ZIP entries, central directory
/// and EOCD sections of an APK (official v2/v3 digest algorithm).
/// <para>按官方 v2/v3 摘要算法计算保护 ZIP 条目、中央目录与 EOCD 的两级分块内容摘要。</para>
/// </summary>
internal static class ApkContentDigest
{
    /// <summary>Computes the full APK digest over the ZIP content plus EOCD of an APK file. The EOCD central-directory offset field is patched to the signing-block offset during hashing without mutating the source file.
    /// <para>计算 APK 的完整内容摘要（ZIP 条目加 EOCD）。哈希期间 EOCD 的中央目录偏移字段被替换为签名块偏移，不改动源文件。</para></summary>
    /// <param name="apkStream">Readable, seekable APK stream.<para>可读可定位的 APK 流。</para></param>
    /// <param name="zipContentLength">Length of ZIP entries from offset 0 to the signing block.
    /// <para>从偏移 0 到签名块的 ZIP 条目长度。</para></param>
    /// <param name="centralDirectoryReadOffset">Central-directory offset in the input file (where to read from).
    /// <para>输入文件中中央目录的偏移（读取位置）。</para></param>
    /// <param name="patchedCentralDirectoryOffset">Central-directory offset written into the EOCD while hashing (signing-block based).
    /// <para>哈希期间写入 EOCD 的中央目录偏移（按签名块位置）。</para></param>
    /// <param name="centralDirectoryLength">Central directory length.<para>中央目录长度。</para></param>
    /// <param name="eocd">Raw EOCD bytes.<para>EOCD 原始字节。</para></param>
    /// <param name="hashName">Hash algorithm name ("SHA256"/"SHA512").
    /// <para>哈希算法名称（"SHA256"/"SHA512"）。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>The top-level content digest over all per-chunk digests; its length equals the hash output width (32 bytes for SHA256, 64 for SHA512).
    /// <para>对所有分块摘要计算得到的顶层内容摘要；长度即哈希输出宽度（SHA256 为 32 字节，SHA512 为 64 字节）。</para></returns>
    /// <exception cref="InvalidDataException">A length or offset is negative, exceeds <c>uint.MaxValue</c> or the stream bounds, or the EOCD is shorter than 22 or longer than 65 557 bytes.
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

        using var builder = new ApkContentDigestBuilder(hashName);
        AppendStreamSection(builder, apkStream, 0, zipContentLength, cancellationToken);
        builder.BeginSection();
        AppendStreamSection(builder, apkStream, centralDirectoryReadOffset, centralDirectoryLength, cancellationToken);
        builder.BeginSection();
        builder.Append(patchedEocd);
        return builder.Complete();
    }

    /// <summary>
    /// Creates the accumulator whose algorithm matches the APK signer's hash name, keeping the
    /// unsupported-name failure identical for every digest consumer.
    /// <para>创建与 APK 签名器哈希名称同算法的累加器，使所有摘要调用方得到一致的不支持名称失败。</para>
    /// </summary>
    /// <param name="hashName">Hash algorithm name ("SHA256"/"SHA512").
    /// <para>哈希算法名称（"SHA256"/"SHA512"）。</para></param>
    /// <returns>An accumulator fixed to that algorithm. <para>固定为该算法的累加器。</para></returns>
    /// <exception cref="CryptographicException">The name is outside the known hash algorithm set.
    /// <para>该名称不在已知哈希算法集合内。</para></exception>
    internal static DigestAccumulator CreateAccumulator(string hashName) => hashName switch
    {
        "MD5" => Md5.CreateAccumulator(),
        "SHA1" => Sha1.CreateAccumulator(),
        "SHA256" => Sha256.CreateAccumulator(),
        "SHA384" => Sha384.CreateAccumulator(),
        "SHA512" => Sha512.CreateAccumulator(),
        _ => throw new CryptographicException($"'{hashName}' is not a known hash algorithm."),
    };

    private static void AppendStreamSection(
        ApkContentDigestBuilder builder, Stream stream, long offset, long length, CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, remaining)));
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected end of stream while hashing APK sections. 计算 APK 区段摘要时流意外结束。");
                }

                builder.Append(buffer.AsSpan(0, read));
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}