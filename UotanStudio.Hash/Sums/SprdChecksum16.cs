namespace UotanStudio.Hash.Sums;

/// <summary>
/// Ones-complement word checksum of the UNISOC (Spreadtrum) FDL link: the bytes are summed as little-endian
/// 16-bit words (an odd trailing byte contributes its plain value), the carry of the 32-bit accumulator is folded
/// back, and the result is complemented. The check value over the ASCII bytes <c>123456789</c> (odd length) is
/// <c>0x2AF6</c>, over <c>12345678</c> (even length) is <c>0x2B2F</c>, and empty data yields <c>0xFFFF</c>.
/// <para>紫光展锐（Spreadtrum）FDL 链路的一补数求和校验：字节按小端 16 位字累加（奇数尾字节按其原值单独累加），
/// 32 位累加器的进位折叠回 16 位后取反。对 ASCII 字节 <c>123456789</c>（奇长度）的校验值为 <c>0x2AF6</c>，
/// 对 <c>12345678</c>（偶长度）为 <c>0x2B2F</c>，空数据结果为 <c>0xFFFF</c>。</para>
/// </summary>
/// <remarks>
/// The byte order of the value on the wire is direction-dependent and part of the protocol: the host writes
/// <see cref="ComputeTransmit"/> as a big-endian field (an even-length region is swapped, an odd-length region is
/// not), while a frame received from the device must be validated against the byte-swapped
/// <see cref="ComputeReceive"/> before the big-endian field comparison. Both rules reproduce the device-side
/// zero-sum verification, where the checksum bytes read back as little-endian words must cancel the region sum.
/// <para>该值在线上的字节序按方向属于协议本身：主机发送时将 <see cref="ComputeTransmit"/> 以大端字段写出
/// （偶长度区间交换字节、奇长度区间不交换），而从设备接收的帧必须先与字节交换后的 <see cref="ComputeReceive"/>
/// 比较再对照大端字段。两条规则共同复现设备端"校验字节按小端读回后须抵消区间和"的零和校验。</para>
/// <para>This is a distinct type from <see cref="WordSum32LittleEndian"/> even though both accumulate little-endian
/// words: the FDL variant folds and complements the accumulator, and conflating them would silently turn every
/// frame checksum into an unrelated value instead of failing a round-trip test.</para>
/// <para>它与 <see cref="WordSum32LittleEndian"/> 虽然都在累加小端字，也必须是两个类型：FDL 变体要对累加器做
/// 折叠与取反，合并后只会让每条帧校验都变成无关值，而不会让往返测试变红。</para>
/// </remarks>
public static class SprdChecksum16
{
    /// <summary>
    /// Computes the folded, complemented word sum of the data (the raw value before any wire byte order rule).
    /// <para>计算数据的折叠取反字和（未应用任何线上字节序规则的原值）。</para>
    /// </summary>
    /// <param name="data">
    /// Bytes to sum; an empty span yields <c>0xFFFF</c>.
    /// <para>待累加的字节；空区间返回 <c>0xFFFF</c>。</para>
    /// </param>
    /// <returns>
    /// The complemented 16-bit ones-complement sum.
    /// <para>取反后的 16 位一补数和。</para>
    /// </returns>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var offset = 0;
        for (; offset + 2 <= data.Length; offset += 2)
        {
            sum += data[offset] | (uint)(data[offset + 1] << 8);
        }

        if (offset < data.Length)
        {
            sum += data[offset];
        }

        sum = (sum >> 16) + (sum & 0xFFFF);
        sum += sum >> 16;
        return (ushort)~sum;
    }

    /// <summary>
    /// Computes the checksum the host transmits: <see cref="Compute"/> byte-swapped when the region length is even
    /// and unmodified when it is odd, matching the device-side zero-sum verification for both parities.
    /// <para>计算主机发送方向的校验值：区间长度为偶数时字节交换 <see cref="Compute"/> 的结果，奇数时原样返回，
    /// 两种奇偶都与设备端零和校验一致。</para>
    /// </summary>
    /// <param name="data">
    /// The checksummed region (command, length and payload bytes, excluding the checksum field itself).
    /// <para>参与校验的区间（命令、长度与负载字节，不含校验字段自身）。</para>
    /// </param>
    /// <returns>
    /// The value to write as the big-endian checksum field.
    /// <para>应作为大端校验字段写出的值。</para>
    /// </returns>
    public static ushort ComputeTransmit(ReadOnlySpan<byte> data)
    {
        var value = Compute(data);
        return (data.Length & 1) == 0 ? Reverse(value) : value;
    }

    /// <summary>
    /// Computes the checksum a device-originated frame must match: <see cref="Compute"/> byte-swapped
    /// unconditionally, because the device complement-folds its region and writes the swapped result.
    /// <para>计算设备发出帧应匹配的校验值：无条件字节交换 <see cref="Compute"/> 的结果，因为设备端对区间
    /// 折叠取反后先交换字节再写出。</para>
    /// </summary>
    /// <param name="data">
    /// The checksummed region of the received frame (excluding the checksum field itself).
    /// <para>接收帧中参与校验的区间（不含校验字段自身）。</para>
    /// </param>
    /// <returns>
    /// The value to compare against the frame's big-endian checksum field.
    /// <para>应与帧的大端校验字段比较的值。</para>
    /// </returns>
    public static ushort ComputeReceive(ReadOnlySpan<byte> data) => Reverse(Compute(data));

    private static ushort Reverse(ushort value) => (ushort)((value << 8) | (value >> 8));
}