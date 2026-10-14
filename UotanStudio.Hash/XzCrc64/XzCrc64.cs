namespace UotanStudio.Hash.XzCrc64;

/// <summary>
/// XZ's CRC-64: reflected polynomial <c>0xc96c5795d7870f42</c>, all-one register initialization and a final
/// complement. <c>System.IO.Hashing.Crc64</c> implements the non-reflected ECMA-182 variant, so it cannot stand in
/// for the check field an <c>.xz</c> container stores.
/// <para>XZ 使用的 CRC-64：反射多项式 <c>0xc96c5795d7870f42</c>、寄存器全 1 初值并做最终取反。
/// <c>System.IO.Hashing.Crc64</c> 实现的是非反射的 ECMA-182 变体，因此无法代替 <c>.xz</c> 容器所存储的校验字段。</para>
/// </summary>
public sealed class XzCrc64
{
    private static readonly ulong[] Table = CreateTable();
    private ulong _state = ulong.MaxValue;

    /// <summary>
    /// Folds more bytes into the running checksum, continuing from whatever has been appended so far.
    /// <para>把更多字节并入进行中的校验和，从此前已累加的位置继续。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to fold; an empty span leaves the state unchanged.
    /// <para>要并入的字节；空区间不改变状态。</para>
    /// </param>
    public void Append(ReadOnlySpan<byte> data)
    {
        var state = _state;
        foreach (var value in data)
        {
            state = Table[(byte)(state ^ value)] ^ (state >> 8);
        }

        _state = state;
    }

    /// <summary>
    /// Returns the finalized checksum without changing the accumulated state, so more bytes may still be appended.
    /// <para>返回定稿校验和且不改变已累加状态，因此仍可继续并入字节。</para>
    /// </summary>
    /// <returns>
    /// The complement of the register, which is the value the XZ container stores.
    /// <para>寄存器取反值，即 XZ 容器所存储的数值。</para>
    /// </returns>
    public ulong GetCurrentHashAsUInt64() => ~_state;

    /// <summary>
    /// Computes the XZ CRC-64 of a complete buffer in one call.
    /// <para>对完整缓冲区一次性计算 XZ CRC-64。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to checksum; an empty span yields <c>0</c>.
    /// <para>参与校验的字节；空区间得到 <c>0</c>。</para>
    /// </param>
    /// <returns>
    /// The stored checksum value. <para>容器所存储的校验和值。</para>
    /// </returns>
    public static ulong HashToUInt64(ReadOnlySpan<byte> data)
    {
        var crc = new XzCrc64();
        crc.Append(data);
        return crc.GetCurrentHashAsUInt64();
    }

    private static ulong[] CreateTable()
    {
        const ulong reflectedPolynomial = 0xc96c5795d7870f42;
        var table = new ulong[256];
        for (var i = 0; i < table.Length; i++)
        {
            var value = (ulong)i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value >> 1) ^ ((value & 1) != 0 ? reflectedPolynomial : 0);
            }

            table[i] = value;
        }

        return table;
    }
}