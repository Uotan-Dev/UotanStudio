using System.Security.Cryptography;

namespace UotanStudio.Comm.Protocols.Edl.Programmer;

/// <summary>
/// Streams a programmer image through SHA-256 without retaining the whole file in memory.
/// <para>以流式方式对 programmer 镜像计算 SHA-256，不把整个文件驻留内存。</para>
/// The stream position is restored after hashing so the same stream can be uploaded afterwards;
/// a non-seekable stream is rejected before any device interaction because Sahara requires it.
/// <para>哈希后恢复流位置，以便同一文件流随后仍可上传；不可定位的流会在任何设备交互之前被拒绝，
/// 因为 Sahara 要求可定位。</para>
/// </summary>
public static class EdlProgrammerHash
{
    private const int ChunkBytes = 64 * 1024;

    /// <summary>
    /// Computes the lowercase hexadecimal SHA-256 digest of the whole stream.
    /// <para>计算整个流的小写十六进制 SHA-256 摘要。</para>
    /// </summary>
    /// <param name="image">
    /// The readable, seekable image stream owned by the caller; its position is restored before return.
    /// <para>调用方持有的可读、可定位镜像流；返回前恢复其位置。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation checked between chunks. <para>在数据块之间检查的取消令牌。</para>
    /// </param>
    /// <returns>
    /// The 64-character lowercase hex digest. <para>64 位小写十六进制摘要。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="image"/> is <see langword="null"/>. <para><paramref name="image"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The stream cannot be read or cannot seek. <para>流不可读或不可定位。</para>
    /// </exception>
    public static string ComputeSha256Hex(Stream image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.CanRead || !image.CanSeek)
        {
            throw new ArgumentException(
                "Programmer images must be readable and seekable so the hash preflight can restore the upload position.",
                nameof(image));
        }

        var origin = image.Position;
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[ChunkBytes];
            int read;
            while ((read = image.Read(buffer, 0, ChunkBytes)) > 0)
            {
                hasher.AppendData(buffer.AsSpan(0, read));
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Convert.ToHexStringLower(hasher.GetHashAndReset());
        }
        finally
        {
            image.Position = origin;
        }
    }
}