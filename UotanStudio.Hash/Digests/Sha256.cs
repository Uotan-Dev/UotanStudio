using System.Security.Cryptography;

namespace UotanStudio.Hash.Digests;

/// <summary>
/// SHA-256 preset: one-shot, destination-buffer, lowercase-hex and asynchronous stream forms, plus a resumable
/// accumulator. Every digest field the product writes or compares goes through a type in this namespace so the
/// algorithm choice and the rendering convention live in one place.
/// <para>SHA-256 预设：一次性、目标缓冲区、小写十六进制与异步流四种形态，另可创建续算累加器。
/// 产品中写入或比较的每个摘要字段都经由本命名空间的类型，使算法选择与呈现约定集中在一处。</para>
/// </summary>
public static class Sha256
{
    /// <summary>
    /// Width of a SHA-256 digest in bytes.
    /// <para>SHA-256 摘要的字节宽度。</para>
    /// </summary>
    /// <value><c>32</c>. <para><c>32</c>。</para></value>
    public const int HashSizeInBytes = 32;

    /// <summary>
    /// Computes the SHA-256 of the data.
    /// <para>计算数据的 SHA-256。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to digest. <para>待摘要的字节。</para>
    /// </param>
    /// <returns>
    /// A new 32-byte digest. <para>新分配的 32 字节摘要。</para>
    /// </returns>
    public static byte[] ComputeHash(ReadOnlySpan<byte> data) => SHA256.HashData(data);

    /// <summary>Computes the SHA-256 of the data into a caller-owned buffer.
    /// <para>把数据的 SHA-256 计算到调用方拥有的缓冲区。</para></summary>
    /// <param name="data">Bytes to digest.<para>待摘要的字节。</para></param>
    /// <param name="destination">Buffer receiving the digest; it must be at least <see cref="HashSizeInBytes"/> long.
    /// <para>接收摘要的缓冲区；长度不得小于 <see cref="HashSizeInBytes"/>。</para></param>
    /// <returns>The number of bytes written.<para>写入的字节数。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short to hold the digest.
    /// <para><paramref name="destination"/> 短于摘要长度。</para></exception>
    public static int ComputeHash(ReadOnlySpan<byte> data, Span<byte> destination) => SHA256.HashData(data, destination);

    /// <summary>
    /// Computes the SHA-256 and renders it as lowercase hexadecimal without separators.
    /// <para>计算 SHA-256 并以无分隔的小写十六进制呈现。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to digest. <para>待摘要的字节。</para>
    /// </param>
    /// <returns>
    /// A 64-character lowercase hex string. <para>64 字符的小写十六进制字符串。</para>
    /// </returns>
    public static string ComputeHexLower(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Computes the SHA-256 of a stream without buffering the whole content.
    /// <para>对流计算 SHA-256，不缓冲整个内容。</para></summary>
    /// <param name="source">Stream read to the end; it is left open.<para>读取到末尾的流；保持打开。</para></param>
    /// <param name="cancellationToken">Cancels the read.<para>取消读取。</para></param>
    /// <returns>A task yielding the 32-byte digest.<para>产出 32 字节摘要的任务。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested.
    /// <para>请求了取消。</para></exception>
    public static ValueTask<byte[]> ComputeHashAsync(Stream source, CancellationToken cancellationToken = default) =>
        SHA256.HashDataAsync(source, cancellationToken);

    /// <summary>
    /// Computes the SHA-256 of a stream synchronously, without buffering the whole content.
    /// <para>同步地对流计算 SHA-256，不缓冲整个内容。</para>
    /// </summary>
    /// <param name="source">
    /// Stream read to the end; it is left open. <para>读取到末尾的流；保持打开。</para>
    /// </param>
    /// <returns>
    /// A new 32-byte digest. <para>新分配的 32 字节摘要。</para>
    /// </returns>
    public static byte[] ComputeHash(Stream source) => SHA256.HashData(source);

    /// <summary>
    /// Creates a resumable SHA-256 accumulator.
    /// <para>创建可续算的 SHA-256 累加器。</para>
    /// </summary>
    /// <returns>
    /// An accumulator fixed to SHA-256. <para>固定为 SHA-256 的累加器。</para>
    /// </returns>
    public static DigestAccumulator CreateAccumulator() => new(HashAlgorithmName.SHA256, HashSizeInBytes);
}