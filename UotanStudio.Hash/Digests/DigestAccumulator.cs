using System.Security.Cryptography;

namespace UotanStudio.Hash.Digests;

/// <summary>
/// Resumable digest computation over an arbitrary number of byte chunks, created by one of the
/// <c>UotanStudio.Hash.Digests</c> preset types so the algorithm is fixed at creation and cannot drift between
/// appends.
/// <para>可对任意数量字节块续算的摘要计算，由 <c>UotanStudio.Hash.Digests</c> 下的预设类型创建，
/// 因此算法在创建时即固定，不会在多次追加之间漂移。</para>
/// </summary>
/// <remarks>
/// <see cref="GetHashAndReset()"/> finalizes and restarts, matching the BCL contract the flashing and repacking paths
/// already depend on; <see cref="TryGetHashAndReset(Span{byte}, out int)"/> avoids the allocation when the caller
/// owns a buffer of exactly <c>HashSizeInBytes</c>.
/// <para><see cref="GetHashAndReset()"/> 定稿并重新开始，与刷写和重打包路径已经依赖的 BCL 契约一致；
/// <see cref="TryGetHashAndReset(Span{byte}, out int)"/> 在调用方自带恰好 <c>HashSizeInBytes</c> 缓冲区时避免分配。</para>
/// </remarks>
public sealed class DigestAccumulator : IDisposable
{
    private readonly IncrementalHash _hash;

    internal DigestAccumulator(HashAlgorithmName algorithm, int hashLengthInBytes)
    {
        _hash = IncrementalHash.CreateHash(algorithm);
        HashLengthInBytes = hashLengthInBytes;
    }

    /// <summary>
    /// Digest length in bytes produced by this accumulator's algorithm.
    /// <para>本累加器所用算法产出的摘要字节长度。</para>
    /// </summary>
    /// <value>The fixed digest width, e.g. <c>32</c> for SHA-256. <para>固定摘要宽度，例如 SHA-256 为 <c>32</c>。</para></value>
    public int HashLengthInBytes { get; }

    /// <summary>
    /// Feeds more bytes into the digest.
    /// <para>向摘要继续输入更多字节。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to append; an empty span changes nothing.
    /// <para>要追加的字节；空区间不产生变化。</para>
    /// </param>
    public void Append(ReadOnlySpan<byte> data) => _hash.AppendData(data);

    /// <summary>
    /// Finalizes the digest and resets the accumulator for the next run.
    /// <para>定稿摘要并把累加器重置到下一次计算。</para>
    /// </summary>
    /// <returns>
    /// A newly allocated digest of the algorithm's fixed length. <para>新分配的、算法固定长度的摘要。</para>
    /// </returns>
    public byte[] GetHashAndReset() => _hash.GetHashAndReset();

    /// <summary>
    /// Returns the digest of everything appended so far **without** resetting, so the same running state can keep
    /// accepting bytes. Payload signing uses this to sign a header+manifest digest and then continue folding the
    /// data region into the same accumulator.
    /// <para>返回此前已输入字节的摘要且**不**重置，因此同一累进状态可以继续接收字节。Payload 签名用它在对
    /// header+manifest 摘要签名后，继续把数据区并入同一累加器。</para>
    /// </summary>
    /// <returns>
    /// A newly allocated digest of the algorithm's fixed length. <para>新分配的、算法固定长度的摘要。</para>
    /// </returns>
    public byte[] GetCurrentHash() => _hash.GetCurrentHash();

    /// <summary>Finalizes the digest into a caller-owned buffer and resets the accumulator.
    /// <para>把摘要定稿到调用方拥有的缓冲区，并重置累加器。</para></summary>
    /// <param name="destination">Buffer receiving the digest; it must be exactly <see cref="HashLengthInBytes"/> long.
    /// <para>接收摘要的缓冲区；长度必须恰好等于 <see cref="HashLengthInBytes"/>。</para></param>
    /// <returns>The number of bytes written.<para>写入的字节数。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short to hold the digest.
    /// <para><paramref name="destination"/> 短于摘要长度。</para></exception>
    public int GetHashAndReset(Span<byte> destination) => _hash.GetHashAndReset(destination);

    /// <summary>Finalizes the digest into a caller-owned buffer and resets the accumulator.
    /// <para>把摘要定稿到调用方拥有的缓冲区，并重置累加器。</para></summary>
    /// <param name="destination">Buffer receiving the digest; it must be at least the algorithm's digest length.
    /// <para>接收摘要的缓冲区；长度不得小于算法的摘要长度。</para></param>
    /// <param name="bytesWritten">Number of bytes written when this method returns <see langword="true"/>; undefined otherwise.
    /// <para>方法返回 <see langword="true"/> 时写入的字节数；否则未定义。</para></param>
    /// <returns><see langword="true"/> when the buffer was large enough; <see langword="false"/> leaves the accumulator carrying the same state so the caller may retry with a larger buffer.
    /// <para>缓冲区足够大时为 <see langword="true"/>；为 <see langword="false"/> 时累加器保持原状态， 调用方可用更大的缓冲区重试。</para></returns>
    public bool TryGetHashAndReset(Span<byte> destination, out int bytesWritten) =>
        _hash.TryGetHashAndReset(destination, out bytesWritten);

    /// <summary>
    /// Releases the native hashing context held by the accumulator.
    /// <para>释放累加器持有的原生哈希上下文。</para>
    /// </summary>
    public void Dispose() => _hash.Dispose();
}