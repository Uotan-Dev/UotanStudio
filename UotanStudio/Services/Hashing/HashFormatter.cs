using UotanStudio.Hash.Digests;

namespace UotanStudio.Services.Hashing;

/// <summary>
/// Hashes an in-memory buffer and renders the digest as lowercase hexadecimal, selected by
/// algorithm name.
/// <para>对内存中的缓冲区做哈希，并按算法名以小写十六进制输出摘要。</para>
/// </summary>
internal static class HashFormatter
{
    /// <summary>
    /// Returns the lowercase hex digest for <paramref name="algorithm"/> (md5/sha1/sha256/sha384/sha512),
    /// or <see langword="null"/> when the name is unsupported.
    /// <para>返回 <paramref name="algorithm"/>（md5/sha1/sha256/sha384/sha512）的小写十六进制摘要；名称不受支持时返回 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="data">The bytes to hash.<para>要哈希的字节。</para></param>
    /// <param name="algorithm">The digest algorithm name.<para>摘要算法名称。</para></param>
    /// <returns>The lowercase hex digest, or null when unsupported.<para>小写十六进制摘要，不受支持时为 null。</para></returns>
    internal static string? ComputeHex(ReadOnlySpan<byte> data, string algorithm) => algorithm switch
    {
        "md5" => Md5.ComputeHexLower(data),
        "sha1" => Sha1.ComputeHexLower(data),
        "sha256" => Sha256.ComputeHexLower(data),
        "sha384" => Sha384.ComputeHexLower(data),
        "sha512" => Sha512.ComputeHexLower(data),
        _ => null,
    };
}