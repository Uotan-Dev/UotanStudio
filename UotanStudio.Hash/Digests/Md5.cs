using System.Security.Cryptography;

namespace UotanStudio.Hash.Digests;

/// <summary>
/// MD5 preset for legacy vendor containers that store an MD5 in a header field or a sidecar. This is an integrity
/// label of those formats, not a security claim.
/// <para>MD5 预设，用于在头部字段或旁挂文件里存储 MD5 的旧厂商容器；它表达的是这些格式的完整性标记，不构成安全性声明。</para>
/// </summary>
public static class Md5
{
    /// <summary>
    /// Width of an MD5 digest in bytes.
    /// <para>MD5 摘要的字节宽度。</para>
    /// </summary>
    /// <value><c>16</c>. <para><c>16</c>。</para></value>
    public const int HashSizeInBytes = 16;

    /// <summary>
    /// Computes the MD5 of the data.
    /// <para>计算数据的 MD5。</para>
    /// </summary>
    /// <param name="data">Bytes to digest. <para>待摘要的字节。</para></param>
    /// <returns>A new 16-byte digest. <para>新分配的 16 字节摘要。</para></returns>
    public static byte[] ComputeHash(ReadOnlySpan<byte> data) => MD5.HashData(data);

    /// <summary>
    /// Computes the MD5 of the data into a caller-owned buffer.
    /// <para>把数据的 MD5 计算到调用方拥有的缓冲区。</para>
    /// </summary>
    /// <param name="data">Bytes to digest. <para>待摘要的字节。</para></param>
    /// <param name="destination">Buffer receiving the digest; at least <see cref="HashSizeInBytes"/> long.
    /// <para>接收摘要的缓冲区；长度不得小于 <see cref="HashSizeInBytes"/>。</para></param>
    /// <returns>The number of bytes written. <para>写入的字节数。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short. <para><paramref name="destination"/> 短于摘要长度。</para></exception>
    public static int ComputeHash(ReadOnlySpan<byte> data, Span<byte> destination) => MD5.HashData(data, destination);

    /// <summary>
    /// Computes the MD5 and renders it as lowercase hexadecimal without separators.
    /// <para>计算 MD5 并以无分隔的小写十六进制呈现。</para>
    /// </summary>
    /// <param name="data">Bytes to digest. <para>待摘要的字节。</para></param>
    /// <returns>A 32-character lowercase hex string. <para>32 字符的小写十六进制字符串。</para></returns>
    public static string ComputeHexLower(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(MD5.HashData(data));

    /// <summary>
    /// Computes the MD5 of a stream without buffering the whole content.
    /// <para>对流计算 MD5，不缓冲整个内容。</para>
    /// </summary>
    /// <param name="source">Stream read to the end; it is left open. <para>读取到末尾的流；保持打开。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消读取。</para></param>
    /// <returns>A task yielding the 16-byte digest. <para>产出 16 字节摘要的任务。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
    public static ValueTask<byte[]> ComputeHashAsync(Stream source, CancellationToken cancellationToken = default) =>
        MD5.HashDataAsync(source, cancellationToken);

    /// <summary>
    /// Computes the MD5 of a stream synchronously, without buffering the whole content.
    /// <para>同步地对流计算 MD5，不缓冲整个内容。</para>
    /// </summary>
    /// <param name="source">Stream read to the end; it is left open. <para>读取到末尾的流；保持打开。</para></param>
    /// <returns>A new 16-byte digest. <para>新分配的 16 字节摘要。</para></returns>
    public static byte[] ComputeHash(Stream source) => MD5.HashData(source);

    /// <summary>
    /// Creates a resumable MD5 accumulator.
    /// <para>创建可续算的 MD5 累加器。</para>
    /// </summary>
    /// <returns>An accumulator fixed to MD5. <para>固定为 MD5 的累加器。</para></returns>
    public static DigestAccumulator CreateAccumulator() => new(HashAlgorithmName.MD5, HashSizeInBytes);
}