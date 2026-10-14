using System.Security.Cryptography;

namespace UotanStudio.Hash.Digests;

/// <summary>
/// SHA-512 preset for the AVB hash descriptor variants and vendor images that declare SHA-512.
/// <para>SHA-512 预设，用于声明 SHA-512 的 AVB 哈希描述符变体与厂商镜像。</para>
/// </summary>
public static class Sha512
{
    /// <summary>
    /// Width of a SHA-512 digest in bytes.
    /// <para>SHA-512 摘要的字节宽度。</para>
    /// </summary>
    /// <value><c>64</c>. <para><c>64</c>。</para></value>
    public const int HashSizeInBytes = 64;

    /// <summary>
    /// Computes the SHA-512 of the data.
    /// <para>计算数据的 SHA-512。</para>
    /// </summary>
    /// <param name="data">Bytes to digest. <para>待摘要的字节。</para></param>
    /// <returns>A new 64-byte digest. <para>新分配的 64 字节摘要。</para></returns>
    public static byte[] ComputeHash(ReadOnlySpan<byte> data) => SHA512.HashData(data);

    /// <summary>
    /// Computes the SHA-512 of the data into a caller-owned buffer.
    /// <para>把数据的 SHA-512 计算到调用方拥有的缓冲区。</para>
    /// </summary>
    /// <param name="data">Bytes to digest. <para>待摘要的字节。</para></param>
    /// <param name="destination">Buffer receiving the digest; at least <see cref="HashSizeInBytes"/> long.
    /// <para>接收摘要的缓冲区；长度不得小于 <see cref="HashSizeInBytes"/>。</para></param>
    /// <returns>The number of bytes written. <para>写入的字节数。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short. <para><paramref name="destination"/> 短于摘要长度。</para></exception>
    public static int ComputeHash(ReadOnlySpan<byte> data, Span<byte> destination) => SHA512.HashData(data, destination);

    /// <summary>
    /// Computes the SHA-512 and renders it as lowercase hexadecimal without separators.
    /// <para>计算 SHA-512 并以无分隔的小写十六进制呈现。</para>
    /// </summary>
    /// <param name="data">Bytes to digest. <para>待摘要的字节。</para></param>
    /// <returns>A 128-character lowercase hex string. <para>128 字符的小写十六进制字符串。</para></returns>
    public static string ComputeHexLower(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA512.HashData(data));

    /// <summary>
    /// Computes the SHA-512 of a stream without buffering the whole content.
    /// <para>对流计算 SHA-512，不缓冲整个内容。</para>
    /// </summary>
    /// <param name="source">Stream read to the end; it is left open. <para>读取到末尾的流；保持打开。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消读取。</para></param>
    /// <returns>A task yielding the 64-byte digest. <para>产出 64 字节摘要的任务。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
    public static ValueTask<byte[]> ComputeHashAsync(Stream source, CancellationToken cancellationToken = default) =>
        SHA512.HashDataAsync(source, cancellationToken);

    /// <summary>
    /// Computes the SHA-512 of a stream synchronously, without buffering the whole content.
    /// <para>同步地对流计算 SHA-512，不缓冲整个内容。</para>
    /// </summary>
    /// <param name="source">Stream read to the end; it is left open. <para>读取到末尾的流；保持打开。</para></param>
    /// <returns>A new 64-byte digest. <para>新分配的 64 字节摘要。</para></returns>
    public static byte[] ComputeHash(Stream source) => SHA512.HashData(source);

    /// <summary>
    /// Creates a resumable SHA-512 accumulator.
    /// <para>创建可续算的 SHA-512 累加器。</para>
    /// </summary>
    /// <returns>An accumulator fixed to SHA-512. <para>固定为 SHA-512 的累加器。</para></returns>
    public static DigestAccumulator CreateAccumulator() => new(HashAlgorithmName.SHA512, HashSizeInBytes);
}