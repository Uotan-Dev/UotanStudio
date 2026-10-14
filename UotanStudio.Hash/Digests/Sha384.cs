using System.Security.Cryptography;

namespace UotanStudio.Hash.Digests;

/// <summary>
/// SHA-384 preset for the AVB hash descriptor variant that declares SHA-384.
/// <para>SHA-384 预设，用于声明 SHA-384 的 AVB 哈希描述符变体。</para>
/// </summary>
public static class Sha384
{
    /// <summary>
    /// Width of a SHA-384 digest in bytes.
    /// <para>SHA-384 摘要的字节宽度。</para>
    /// </summary>
    /// <value><c>48</c>. <para><c>48</c>。</para></value>
    public const int HashSizeInBytes = 48;

    /// <summary>
    /// Computes the SHA-384 of the data.
    /// <para>计算数据的 SHA-384。</para>
    /// </summary>
    /// <param name="data">Bytes to digest. <para>待摘要的字节。</para></param>
    /// <returns>A new 48-byte digest. <para>新分配的 48 字节摘要。</para></returns>
    public static byte[] ComputeHash(ReadOnlySpan<byte> data) => SHA384.HashData(data);

    /// <summary>
    /// Computes the SHA-384 of the data into a caller-owned buffer.
    /// <para>把数据的 SHA-384 计算到调用方拥有的缓冲区。</para>
    /// </summary>
    /// <param name="data">Bytes to digest. <para>待摘要的字节。</para></param>
    /// <param name="destination">Buffer receiving the digest; at least <see cref="HashSizeInBytes"/> long.
    /// <para>接收摘要的缓冲区；长度不得小于 <see cref="HashSizeInBytes"/>。</para></param>
    /// <returns>The number of bytes written. <para>写入的字节数。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short. <para><paramref name="destination"/> 短于摘要长度。</para></exception>
    public static int ComputeHash(ReadOnlySpan<byte> data, Span<byte> destination) => SHA384.HashData(data, destination);

    /// <summary>
    /// Computes the SHA-384 and renders it as lowercase hexadecimal without separators.
    /// <para>计算 SHA-384 并以无分隔的小写十六进制呈现。</para>
    /// </summary>
    /// <param name="data">Bytes to digest. <para>待摘要的字节。</para></param>
    /// <returns>A 96-character lowercase hex string. <para>96 字符的小写十六进制字符串。</para></returns>
    public static string ComputeHexLower(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA384.HashData(data));

    /// <summary>
    /// Computes the SHA-384 of a stream without buffering the whole content.
    /// <para>对流计算 SHA-384，不缓冲整个内容。</para>
    /// </summary>
    /// <param name="source">Stream read to the end; it is left open. <para>读取到末尾的流；保持打开。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消读取。</para></param>
    /// <returns>A task yielding the 48-byte digest. <para>产出 48 字节摘要的任务。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
    public static ValueTask<byte[]> ComputeHashAsync(Stream source, CancellationToken cancellationToken = default) =>
        SHA384.HashDataAsync(source, cancellationToken);

    /// <summary>
    /// Computes the SHA-384 of a stream synchronously, without buffering the whole content.
    /// <para>同步地对流计算 SHA-384，不缓冲整个内容。</para>
    /// </summary>
    /// <param name="source">Stream read to the end; it is left open. <para>读取到末尾的流；保持打开。</para></param>
    /// <returns>A new 48-byte digest. <para>新分配的 48 字节摘要。</para></returns>
    public static byte[] ComputeHash(Stream source) => SHA384.HashData(source);

    /// <summary>
    /// Creates a resumable SHA-384 accumulator.
    /// <para>创建可续算的 SHA-384 累加器。</para>
    /// </summary>
    /// <returns>An accumulator fixed to SHA-384. <para>固定为 SHA-384 的累加器。</para></returns>
    public static DigestAccumulator CreateAccumulator() => new(HashAlgorithmName.SHA384, HashSizeInBytes);
}