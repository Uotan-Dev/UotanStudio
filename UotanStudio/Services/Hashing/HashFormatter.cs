using System.Security.Cryptography;

namespace UotanStudio.Services.Hashing;

/// <summary>
/// Hashes an in-memory buffer and renders the digest as lowercase hexadecimal, selected by
/// algorithm name. File/stream hashing belongs to <see cref="Checksum.ChecksumService"/>; this
/// covers the small node-graph payloads that are already fully materialized.
/// <para>对内存中的缓冲区做哈希并以小写十六进制输出，按算法名选择。文件/流哈希属于
/// <see cref="Checksum.ChecksumService"/>；此处覆盖已完整驻留的较小节点图载荷。</para>
/// </summary>
internal static class HashFormatter
{
    /// <summary>
    /// Returns the lowercase hex digest for <paramref name="algorithm"/> (md5/sha1/sha256/sha384/sha512),
    /// or <see langword="null"/> when the name is unsupported.
    /// <para>返回 <paramref name="algorithm"/>（md5/sha1/sha256/sha384/sha512）的小写十六进制摘要；名称不受支持时返回 <see langword="null"/>。</para>
    /// </summary>
    internal static string? ComputeHex(ReadOnlySpan<byte> data, string algorithm) => algorithm switch
    {
        "md5" => Convert.ToHexStringLower(MD5.HashData(data)),
        "sha1" => Convert.ToHexStringLower(SHA1.HashData(data)),
        "sha256" => Convert.ToHexStringLower(SHA256.HashData(data)),
        "sha384" => Convert.ToHexStringLower(SHA384.HashData(data)),
        "sha512" => Convert.ToHexStringLower(SHA512.HashData(data)),
        _ => null,
    };
}