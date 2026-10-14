namespace UotanStudio.Hash.Crc32Ieee;

/// <summary>
/// Carries the standard CRC-32 across chunks, so a writer or reader that emits a checksum field after streaming a
/// whole image folds the same value <see cref="Crc32Ieee"/> would return for one buffer.
/// <para>跨数据块承载标准 CRC-32，使流式写出或读取校验字节的调用方，最终得到与
/// <see cref="Crc32Ieee"/> 对单块缓冲区一致的值。</para>
/// </summary>
/// <remarks>
/// The accumulator cannot be seeded: a format that resumes from an arbitrary register value needs
/// <see cref="UotanStudio.Hash.Crc32Register.Crc32Register"/> with the previous result as its seed instead.
/// <para>本累加器不接受种子：需要从任意寄存器值续算的格式，应改为把上次结果作为 seed 传给
/// <see cref="UotanStudio.Hash.Crc32Register.Crc32Register"/>。</para>
/// </remarks>
public sealed class Crc32IeeeAccumulator
{
    private readonly System.IO.Hashing.Crc32 _hash = new();

    /// <summary>
    /// Folds more bytes into the running checksum.
    /// <para>把更多字节并入进行中的校验和。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to fold; an empty span leaves the state unchanged.
    /// <para>要并入的字节；空区间不改变状态。</para>
    /// </param>
    public void Append(ReadOnlySpan<byte> data) => _hash.Append(data);

    /// <summary>
    /// Returns the finalized checksum without consuming the accumulated bytes, so appending may continue.
    /// <para>返回定稿校验和且不消费已累加字节，因此可以继续并入。</para>
    /// </summary>
    /// <returns>
    /// The standard CRC-32 of everything appended so far. <para>此前全部已并入字节的标准 CRC-32。</para>
    /// </returns>
    public uint GetCurrentHash() => _hash.GetCurrentHashAsUInt32();

    /// <summary>
    /// Discards the accumulated state and restarts from the standard initial register value.
    /// <para>丢弃已累加状态，并从标准初始寄存器值重新开始。</para>
    /// </summary>
    public void Reset() => _hash.Reset();
}