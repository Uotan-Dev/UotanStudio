using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace UotanStudio.Hash.Sums;

/// <summary>
/// Reduces the data by XOR-ing successive little-endian 16-bit words from a seed, with no final inversion and no
/// modular reduction beyond the width of the accumulator — the MTK download-agent and security-resource upload
/// checksum, which the host writes big-endian onto the wire after computing this value.
/// <para>以给定种子依次与数据中的小端 16 位字做异或归约，不做最终取反，超出累加器宽度的回卷即其自然溢出——即
/// MTK 下载代理与安全资源上传校验和；主机算出该值后以大端写到线上。</para>
/// </summary>
/// <remarks>
/// The tail rule is part of the protocol: a final odd byte contributes as the low half of a word whose high half is
/// zero, which is the same value as appending one <c>0x00</c> byte before folding. The reference downloader pads the
/// buffer this way before its word loop (Library/mtk_preloader.py:988-996 of the mtkclient tree), and both MTK upload
/// exchanges reuse that helper, so the word order, the zero seed and the padding rule are a single contract rather
/// than two call-site conventions.
/// <para>尾部规则属于协议本身：末尾的奇数字节作为「高字节为 0 的字」的低半部参与异或，等价于在折叠前先追加一个
/// <c>0x00</c> 字节。参考下载器在逐词循环之前就是这样补齐缓冲区的（mtkclient 源码树的
/// Library/mtk_preloader.py:988-996），且两处 MTK 上传交换都走同一个 helper，因此字序、零种子与补齐规则是一条
/// 统一契约，而不是两个调用点各自的约定。</para>
/// Continuing an earlier call requires the next chunk to be word-aligned: <c>Compute(first)</c> then
/// <c>Compute(second, firstResult)</c> equals <c>Compute(concatenation)</c> only when <c>first.Length</c> is even,
/// because an odd-length prefix would have padded its own tail. Upload framing keeps chunks even, so a running
/// value may be carried across packets.
/// <para>续算要求后续分块按字对齐：只有 <c>first.Length</c> 为偶数时，先算 <c>Compute(first)</c> 再算
/// <c>Compute(second, firstResult)</c> 才等于对拼接区间一次性调用，因为奇数长度的前缀会自行补齐尾部。上传分帧保证
/// 每块长度为偶数，因此该值可以跨数据包携带续算。</para>
/// Spans on little-endian processors fold whole words per lane through a vector XOR accumulator before the lane
/// reduction; other byte orders keep the portable path because a native word read there would not be a
/// little-endian read.
/// <para>小端处理器上的区间先在向量通道内按整字异或累加，再做通道归约；其他字节序保留可移植路径，因为在那些
/// 平台上按本机字节序读字不等于按小端读。</para>
/// </remarks>
public static class WordXor16LittleEndian
{
    /// <summary>
    /// Reduces the data from the zero seed.
    /// <para>以零种子对数据做异或归约。</para>
    /// </summary>
    /// <param name="data">Bytes to reduce; an odd length folds its last byte as the low half of a zero-padded word.
    /// <para>待归约的字节；奇数长度时最后一个字节作为「高字节为 0 的字」的低半部参与。</para></param>
    /// <returns>The XOR reduction of the little-endian words; an empty span returns <c>0</c>.
    /// <para>小端字的异或归约值；空区间返回 <c>0</c>。</para></returns>
    public static ushort Compute(ReadOnlySpan<byte> data) => Compute(data, 0);

    /// <summary>
    /// Continues the reduction of a word-aligned chunk from a previous result.
    /// <para>以上一次的结果续算一个按字对齐的分块。</para>
    /// </summary>
    /// <param name="data">The chunk to fold in; every chunk except the last must be word-aligned for the running value to equal a single pass over the concatenated bytes.
    /// <para>待并入的分块；除最后一块外都必须按字对齐，累加值才会等于对拼接字节一次性计算的结果。</para></param>
    /// <param name="previousResult">The value returned by the preceding call; an empty chunk returns it unchanged.
    /// <para>上一次调用返回的值；空区间原样返回该值。</para></param>
    /// <returns>The reduction of the preceding bytes together with this chunk.
    /// <para>先前字节与本块合并后的归约值。</para></returns>
    public static ushort Compute(ReadOnlySpan<byte> data, ushort previousResult)
        => BitConverter.IsLittleEndian
            ? ComputeLittleEndian(data, previousResult)
            : ComputePortable(data, previousResult);

    /// <summary>
    /// The portable form, which reads every word explicitly as little-endian and is therefore correct on any
    /// processor byte order.
    /// <para>可移植形态：显式按小端读取每个字，因此在任意处理器字节序上都正确。</para>
    /// </summary>
    /// <param name="data">Bytes to reduce. <para>待归约的字节。</para></param>
    /// <param name="previousResult">Running value. <para>当前累加值。</para></param>
    /// <returns>The updated reduction. <para>更新后的归约值。</para></returns>
    internal static ushort ComputePortable(ReadOnlySpan<byte> data, ushort previousResult)
    {
        var checksum = previousResult;
        var offset = 0;
        for (; offset + sizeof(ushort) <= data.Length; offset += sizeof(ushort))
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        }

        if (offset < data.Length)
        {
            checksum ^= data[offset];
        }

        return checksum;
    }

    /// <summary>
    /// The little-endian form, which reinterprets the bytes as native words and XOR-folds whole
    /// <see cref="Vector{T}"/> lanes before finishing the remaining words scalarly.
    /// <para>小端形态：把字节重组为本机字，先按整条 <see cref="Vector{T}"/> 通道异或折叠，再以标量收尾剩余字。</para>
    /// </summary>
    /// <param name="data">Bytes to reduce. <para>待归约的字节。</para></param>
    /// <param name="previousResult">Running value. <para>当前累加值。</para></param>
    /// <returns>The updated reduction. <para>更新后的归约值。</para></returns>
    internal static ushort ComputeLittleEndian(ReadOnlySpan<byte> data, ushort previousResult)
    {
        uint checksum = previousResult;
        var words = MemoryMarshal.Cast<byte, ushort>(data);
        var index = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var lanes = Vector<ushort>.Count;
            var limit = words.Length - lanes;
            if (limit >= 0)
            {
                var accumulator = Vector<ushort>.Zero;
                while (index <= limit)
                {
                    accumulator ^= new Vector<ushort>(words[index..]);
                    index += lanes;
                }

                for (var lane = 0; lane < lanes; lane++)
                {
                    checksum ^= accumulator[lane];
                }
            }
        }

        for (; index < words.Length; index++)
        {
            checksum ^= words[index];
        }

        if ((data.Length & 1) != 0)
        {
            checksum ^= data[^1];
        }

        return (ushort)checksum;
    }
}