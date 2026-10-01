using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace UotanStudio.Services.Checksum;

/// <summary>
/// <para>一次校验的算法勾选集合。</para>
/// The algorithm selection for one verification run.
/// </summary>
/// <param name="Md5"><para>是否计算 MD5。</para>Whether to compute MD5.</param>
/// <param name="Sha1"><para>是否计算 SHA1。</para>Whether to compute SHA1.</param>
/// <param name="Sha256"><para>是否计算 SHA256。</para>Whether to compute SHA256.</param>
/// <param name="Crc32"><para>是否计算 CRC-32。</para>Whether to compute CRC-32.</param>
internal readonly record struct ChecksumAlgorithms(bool Md5, bool Sha1, bool Sha256, bool Crc32)
{
    /// <summary>是否勾选了任一算法。 Whether any algorithm is selected.</summary>
    public bool Any => Md5 || Sha1 || Sha256 || Crc32;
}

/// <summary>
/// <para>一次校验的结果；未参与的算法为空字符串。</para>
/// The result of one verification run; algorithms that were not requested are empty strings.
/// </summary>
/// <param name="Md5"><para>MD5（小写十六进制）。</para>MD5 (lowercase hex).</param>
/// <param name="Sha1"><para>SHA1（小写十六进制）。</para>SHA1 (lowercase hex).</param>
/// <param name="Sha256"><para>SHA256（小写十六进制）。</para>SHA256 (lowercase hex).</param>
/// <param name="Crc32"><para>CRC-32（8 位小写十六进制）。</para>CRC-32 (8-digit lowercase hex).</param>
internal readonly record struct ChecksumResult(string Md5, string Sha1, string Sha256, string Crc32);

/// <summary>
/// <para>文件校验后端：单次流式读取同时计算所有勾选算法，不把整个文件读入内存。</para>
/// File checksum backend: one streaming pass computes every selected algorithm without loading
/// the whole file into memory.
/// </summary>
internal static class ChecksumService
{
    private const int BufferLength = 1024 * 1024;

    /// <summary>
    /// <para>流式计算文件校验值；取消时抛出 <see cref="OperationCanceledException"/>，绝不返回部分结果。</para>
    /// Streams the file and computes the checksums; throws <see cref="OperationCanceledException"/> on
    /// cancellation and never returns partial results.
    /// </summary>
    /// <param name="filePath"><para>文件路径，调用方须保证文件存在。</para>The file path; the caller guarantees it exists.</param>
    /// <param name="algorithms"><para>参与计算的算法集合。</para>The algorithms to compute.</param>
    /// <param name="token"><para>取消令牌。</para>Cancellation token.</param>
    internal static async Task<ChecksumResult> ComputeAsync(string filePath, ChecksumAlgorithms algorithms, CancellationToken token)
    {
        var file = new FileInfo(filePath);
        await using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferLength, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var md5 = algorithms.Md5 ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
        using var sha1 = algorithms.Sha1 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
        using var sha256 = algorithms.Sha256 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var crc32 = algorithms.Crc32 ? new Crc32() : (Crc32?)null;

        var buffer = new byte[BufferLength];
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            var chunk = buffer.AsSpan(0, read);
            md5?.AppendData(chunk);
            sha1?.AppendData(chunk);
            sha256?.AppendData(chunk);
            crc32?.Append(chunk);
        }

        if (new FileInfo(filePath).Length != file.Length)
        {
            throw new IOException("File changed while hashing. 校验期间文件发生变化。");
        }

        return new ChecksumResult(
            Md5: HashToHex(md5),
            Sha1: HashToHex(sha1),
            Sha256: HashToHex(sha256),
            Crc32: crc32 is null ? "" : BinaryPrimitives.ReadUInt32LittleEndian(crc32.GetCurrentHash()).ToString("x8"));
    }

    /// <summary>未参与时返回空串，否则输出全库统一的小写十六进制。 Empty when absent; otherwise the repo-wide lowercase hex.</summary>
    private static string HashToHex(IncrementalHash? hash)
        => hash is null ? "" : Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
}