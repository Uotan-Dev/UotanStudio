using System.Security.Cryptography;

namespace UotanStudio.Hash.Digests;

/// <summary>
/// SHA-1 preset for the formats and keystore records that still name SHA-1 (DEX signatures, JKS entries, vendor
/// boot images, block-OTA region digests). It is an identity label for those layouts, not a collision-resistant
/// choice.
/// <para>SHA-1 预设，用于仍以 SHA-1 命名的格式与密钥库记录（DEX 签名、JKS 条目、厂商启动镜像、块级 OTA 区域摘要）。
/// 对这些布局它是身份标记，而非抗碰撞选择。</para>
/// </summary>
public static class Sha1
{
    /// <summary>
    /// Width of a SHA-1 digest in bytes.
    /// <para>SHA-1 摘要的字节宽度。</para>
    /// </summary>
    /// <value><c>20</c>. <para><c>20</c>。</para></value>
    public const int HashSizeInBytes = 20;

    /// <summary>
    /// Computes the SHA-1 of the data.
    /// <para>计算数据的 SHA-1。</para>
    /// </summary>
    /// <param name="data">Bytes to digest. <para>待摘要的字节。</para></param>
    /// <returns>A new 20-byte digest. <para>新分配的 20 字节摘要。</para></returns>
    public static byte[] ComputeHash(ReadOnlySpan<byte> data) => SHA1.HashData(data);

    /// <summary>
    /// Computes the SHA-1 of the data into a caller-owned buffer.
    /// <para>把数据的 SHA-1 计算到调用方拥有的缓冲区。</para>
    /// </summary>
    /// <param name="data">Bytes to digest. <para>待摘要的字节。</para></param>
    /// <param name="destination">Buffer receiving the digest; at least <see cref="HashSizeInBytes"/> long.
    /// <para>接收摘要的缓冲区；长度不得小于 <see cref="HashSizeInBytes"/>。</para></param>
    /// <returns>The number of bytes written. <para>写入的字节数。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short. <para><paramref name="destination"/> 短于摘要长度。</para></exception>
    public static int ComputeHash(ReadOnlySpan<byte> data, Span<byte> destination) => SHA1.HashData(data, destination);

    /// <summary>
    /// Computes the SHA-1 and renders it as lowercase hexadecimal without separators.
    /// <para>计算 SHA-1 并以无分隔的小写十六进制呈现。</para>
    /// </summary>
    /// <param name="data">Bytes to digest. <para>待摘要的字节。</para></param>
    /// <returns>A 40-character lowercase hex string. <para>40 字符的小写十六进制字符串。</para></returns>
    public static string ComputeHexLower(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA1.HashData(data));

    /// <summary>
    /// Computes the SHA-1 of a stream without buffering the whole content.
    /// <para>对流计算 SHA-1，不缓冲整个内容。</para>
    /// </summary>
    /// <param name="source">Stream read to the end; it is left open. <para>读取到末尾的流；保持打开。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消读取。</para></param>
    /// <returns>A task yielding the 20-byte digest. <para>产出 20 字节摘要的任务。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
    public static ValueTask<byte[]> ComputeHashAsync(Stream source, CancellationToken cancellationToken = default) =>
        SHA1.HashDataAsync(source, cancellationToken);

    /// <summary>
    /// Computes the SHA-1 of a stream synchronously, without buffering the whole content.
    /// <para>同步地对流计算 SHA-1，不缓冲整个内容。</para>
    /// </summary>
    /// <param name="source">Stream read to the end; it is left open. <para>读取到末尾的流；保持打开。</para></param>
    /// <returns>A new 20-byte digest. <para>新分配的 20 字节摘要。</para></returns>
    public static byte[] ComputeHash(Stream source) => SHA1.HashData(source);

    /// <summary>
    /// Creates a resumable SHA-1 accumulator.
    /// <para>创建可续算的 SHA-1 累加器。</para>
    /// </summary>
    /// <returns>An accumulator fixed to SHA-1. <para>固定为 SHA-1 的累加器。</para></returns>
    public static DigestAccumulator CreateAccumulator() => new(HashAlgorithmName.SHA1, HashSizeInBytes);
}