using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace UotanStudio.Services.Checksum;

/// <summary>
/// The algorithm selection for one verification run.
/// <para>一次校验的算法勾选集合。</para>
/// </summary>
/// <param name="Md5">Whether to compute MD5. <para>是否计算 MD5。</para></param>
/// <param name="Sha1">Whether to compute SHA1. <para>是否计算 SHA1。</para></param>
/// <param name="Sha256">Whether to compute SHA256. <para>是否计算 SHA256。</para></param>
/// <param name="Crc32">Whether to compute CRC-32. <para>是否计算 CRC-32。</para></param>
internal readonly record struct ChecksumAlgorithms(bool Md5, bool Sha1, bool Sha256, bool Crc32)
{
    /// <summary>Whether any algorithm is selected.<para>勾选了任一算法。</para></summary>
    /// <value>True when at least one algorithm is selected.<para>至少勾选一个算法时为 true。</para></value>
    public bool Any => Md5 || Sha1 || Sha256 || Crc32;
}

/// <summary>
/// The result of one verification run; algorithms that were not requested are empty strings.
/// <para>一次校验的结果；未参与的算法为空字符串。</para>
/// </summary>
/// <param name="Md5">MD5 (lowercase hex). <para>MD5（小写十六进制）。</para></param>
/// <param name="Sha1">SHA1 (lowercase hex). <para>SHA1（小写十六进制）。</para></param>
/// <param name="Sha256">SHA256 (lowercase hex). <para>SHA256（小写十六进制）。</para></param>
/// <param name="Crc32">CRC-32 (8-digit lowercase hex). <para>CRC-32（8 位小写十六进制）。</para></param>
internal readonly record struct ChecksumResult(string Md5, string Sha1, string Sha256, string Crc32);

/// <summary>
/// File checksum backend: one streaming pass computes every selected algorithm without loading
/// the whole file into memory.
/// <para>文件校验后端：单次流式读取同时计算所有勾选算法，不把整个文件读入内存。</para>
/// </summary>
internal static class ChecksumService
{
    private const int BufferLength = 1024 * 1024;

    /// <summary>
    /// Streams the file and computes the checksums; throws <see cref="OperationCanceledException"/> on
    /// cancellation and never returns partial results.
    /// <para>流式计算文件校验值；取消时抛出 <see cref="OperationCanceledException"/>，绝不返回部分结果。</para>
    /// </summary>
    /// <param name="filePath">Path of the file to read. <para>待读取文件的路径。</para></param>
    /// <param name="algorithms">The algorithms to compute. <para>参与计算的算法集合。</para></param>
    /// <param name="token">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The computed checksum result.<para>计算得到的校验结果。</para></returns>
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

    /// <summary>Empty when the hash was not requested; otherwise lowercase hexadecimal.<para>未请求该算法时返回空串，否则返回小写十六进制文本。</para></summary>
    /// <param name="hash">The incremental hash, or null when the algorithm was skipped. <para>增量哈希；算法未参与时为 null。</para></param>
    /// <returns>The hex text, empty when the hash is null.<para>十六进制文本；哈希为 null 时为空串。</para></returns>
    private static string HashToHex(IncrementalHash? hash)
        => hash is null ? "" : Convert.ToHexStringLower(hash.GetHashAndReset());
}