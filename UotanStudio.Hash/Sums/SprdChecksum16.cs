namespace UotanStudio.Hash.Sums;

/// <summary>
/// Ones-complement checksum of the UNISOC (Spreadtrum) FDL link: the bytes are summed as big-endian 16-bit words
/// (an odd trailing byte contributes its plain value), the carry of the 32-bit accumulator is folded back twice,
/// and the result is complemented. Empty data yields <c>0xFFFF</c>.
/// <para>紫光展锐（Spreadtrum）FDL 链路的一补数求和校验：字节按大端 16 位字累加（奇数尾字节按其原值累加），
/// 把 32 位累加器的进位折叠两次后取反。空数据结果为 <c>0xFFFF</c>。</para>
/// </summary>
/// <remarks>
/// The value is direction-independent: the frame encoder writes it as a big-endian field and the frame decoder
/// compares a device frame against that same value, so no per-direction byte order rule exists. The word order is
/// the vendor transport's, not a post-hoc byte swap; that distinction only disappears when the summed region has
/// an even length.
/// <para>该值与方向无关：帧编码器把它按大端字段写出，帧解码器用同一个值比对公司设备的帧，因此不存在按方向区分的
/// 字节序规则。字序取厂商传输层的原始字序，而不是事后交换字节；这一差别只在被求和区间长度为偶数时消失。</para>
/// <para>Accumulating little-endian words and byte-swapping the result produces the same value only for even-length
/// regions: a byte swap is multiplication by 256 in the end-around-carry ring that the fold implements, while an
/// odd trailing byte is added outside that structure and is therefore not carried along by the swap. Ports that
/// swap unconditionally, or that swap only per direction, diverge from the vendor on every odd-length region, so
/// this type keeps the vendor word order instead of adding a swap rule.</para>
/// <para>改成"累加小端字再交换结果"只在偶数长度区间上等价：字节交换在折叠所实现的循环进位环上等于乘 256，而奇数
/// 尾字节是在该结构之外相加的，不会被交换带走。因此无条件交换、或按方向分别交换的移植都会在每个奇数长度区间上
/// 与厂商产生偏差，本类型因此保留厂商字序而不追加交换规则。</para>
/// <para>This is a distinct type from <see cref="WordSum32LittleEndian"/> even though both accumulate 16-bit words:
/// the FDL variant folds and complements the accumulator, and conflating them would silently turn every frame
/// checksum into an unrelated value instead of failing a round-trip test.</para>
/// <para>它与 <see cref="WordSum32LittleEndian"/> 虽然都在累加 16 位字，也必须是两个类型：FDL 变体要对累加器做
/// 折叠与取反，合并后只会让每条帧校验都变成无关值，而不会让往返测试变红。</para>
/// </remarks>
public static class SprdChecksum16
{
    /// <summary>
    /// Computes the folded, complemented big-endian word sum of the data; this single value serves both the
    /// transmitted and the received direction.
    /// <para>计算数据的折叠取反大端字和；发送与接收两个方向都用这一个值。</para>
    /// </summary>
    /// <param name="data">
    /// The checksummed region (command, length and payload bytes, excluding the checksum field itself); an empty
    /// span yields <c>0xFFFF</c>.
    /// <para>参与校验的区间（命令、长度与负载字节，不含校验字段自身）；空区间返回 <c>0xFFFF</c>。</para>
    /// </param>
    /// <returns>
    /// The value to write as the big-endian checksum field and to compare against a received frame's field.
    /// <para>应作为大端校验字段写出、并与接收帧字段比较的值。</para>
    /// </returns>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var offset = 0;
        for (; offset + 2 <= data.Length; offset += 2)
        {
            sum += (uint)((data[offset] << 8) | data[offset + 1]);
        }

        if (offset < data.Length)
        {
            sum += data[offset];
        }

        sum = (sum >> 16) + (sum & 0xFFFF);
        sum += sum >> 16;
        return (ushort)~sum;
    }
}