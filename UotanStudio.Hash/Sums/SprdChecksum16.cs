namespace UotanStudio.Hash.Sums;

/// <summary>
/// Ones-complement checksum of the UNISOC (Spreadtrum) FDL link, matching the official flash tool's frame routine
/// (<c>CBMPkg::Assemble</c> / <c>frm_chk</c>): even-length regions are summed as big-endian 16-bit words, an odd
/// trailing byte is placed in the high byte of a pending word, the carry is folded back twice, the result is
/// complemented and then byte-swapped for the big-endian wire field. Empty data yields <c>0xFFFF</c>.
/// <para>紫光展锐（Spreadtrum）FDL 链路的一补数校验和，与官方刷机工具的组帧例程（<c>CBMPkg::Assemble</c> /
/// <c>frm_chk</c>）一致：偶数长度区间按大端 16 位字累加，奇数尾字节放在待累加字的高字节位，32 位累加器
/// 的进位折叠两次后取反，最后交换字节写入大端校验字段。空数据结果为 <c>0xFFFF</c>。</para>
/// </summary>
/// <remarks>
/// The official routine accumulates even pairs as <c>(p[0] &lt;&lt; 8) | p[1]</c>. For an odd region it parks the
/// last byte in <c>cZero[0]</c>, i.e. in the high byte of the pending 16-bit word, and folds that word last. The
/// equivalent formulation used here sums little-endian words with a plain trailing byte and swaps the final
/// value; the two agree for every length (verified exhaustively), while treating the trailing byte as a plain
/// low-order byte diverges on every odd-length region.
/// <para>官方例程按 <c>(p[0] &lt;&lt; 8) | p[1]</c> 累加偶数对；奇数区间把最后一字节放入 <c>cZero[0]</c>，
/// 即待累加 16 位字的高字节，再折叠该字。本实现采用等价写法：累加小端字、尾字节按原值累加，最后交换结果；
/// 两者对所有长度完全一致（已穷举验证），而把尾字节当作低字节的写法会在每个奇数长度区间上产生偏差。</para>
/// <para>The value is direction-independent: the encoder writes it as a big-endian field and the decoder compares
/// the same value against a received frame, so no per-direction byte order rule exists.</para>
/// <para>该值与方向无关：编码器按大端字段写出，解码器用同一个值比对收到的帧，因此不存在按方向区分的字节序规则。</para>
/// <para>This is a distinct type from <see cref="WordSum32LittleEndian"/> even though both accumulate 16-bit words:
/// the FDL variant folds, complements and swaps the accumulator, and conflating them would silently turn every
/// frame checksum into an unrelated value instead of failing a round-trip test.</para>
/// <para>它与 <see cref="WordSum32LittleEndian"/> 虽然都在累加 16 位字，也必须是两个类型：FDL 变体要对累加器做
/// 折叠、取反与交换，合并后只会让每条帧校验都变成无关值，而不会让往返测试变红。</para>
/// </remarks>
public static class SprdChecksum16
{
    /// <summary>
    /// Computes the folded, complemented and byte-swapped word sum of <paramref name="data"/>.
    /// <para>计算 <paramref name="data"/> 的折叠、取反并交换字节后的字和。</para>
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
            sum += (uint)((data[offset + 1] << 8) | data[offset]);
        }

        if (offset < data.Length)
        {
            sum += data[offset];
        }

        sum = (sum >> 16) + (sum & 0xFFFF);
        sum += sum >> 16;
        var complemented = (ushort)~sum;
        return (ushort)((complemented >> 8) | (complemented << 8));
    }
}