using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace UotanStudio.Core.Verification;

/// <summary>
/// <para>哈希服务：基于 BCL SHA256 提供文件哈希计算。</para>
/// Hash service: computes file hashes using the BCL SHA256.
/// </summary>
public static class HashService
{
    /// <summary>
    /// <para>计算指定文件的 SHA-256，返回小写十六进制字符串。</para>
    /// Computes the SHA-256 of the specified file and returns a lower-case hex string.
    /// </summary>
    /// <param name="path">
    /// <para>文件路径。</para>
    /// The file path.
    /// </param>
    /// <param name="cancellationToken">
    /// <para>取消令牌。</para>
    /// The cancellation token.
    /// </param>
    /// <returns>
    /// <para>小写 SHA-256 十六进制字符串。</para>
    /// The lower-case SHA-256 hex string.
    /// </returns>
    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}