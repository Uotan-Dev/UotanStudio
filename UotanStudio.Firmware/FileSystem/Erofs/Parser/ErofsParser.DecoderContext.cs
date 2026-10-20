using UotanStudio.Firmware.FileSystem.Erofs.Models;

namespace UotanStudio.Firmware.FileSystem.Erofs.Parser;

/// <summary>
/// Decoder-state plumbing shared by the compressed read paths: every pcluster decode borrows the parser's
/// retained ZSTD decompressor or MicroLZMA decoder, so a decode no longer pays for the decoder workspace a
/// fresh instance would allocate per pcluster.
/// <para>压缩读取路径共享的解码器状态接线：每次 pcluster 解码都借用解析器保留的 ZSTD 解压器或 MicroLZMA
/// 解码器，使解码不必再付出新建实例为每个 pcluster 分配的解码器工作区。</para>
/// </summary>
public sealed partial class ErofsParser
{
    /// <summary>
    /// Decodes one pcluster through the parser's reusable decoder state, borrowing a context for this call and
    /// returning it afterwards. The decoded bytes, the destination bounds and every exception the codec raises
    /// are exactly those of the stateless dispatch.
    /// <para>经解析器可复用的解码器状态解码一个 pcluster，本次调用借出上下文并在结束后归还。解码字节、目标边界与
    /// 编解码器抛出的每个异常都与无状态分派完全相同。</para>
    /// </summary>
    /// <param name="algorithm">Codec branch the pcluster's index head declares.
    /// <para>该 pcluster 索引头声明的编解码分支。</para></param>
    /// <param name="source">Buffer holding the compressed bytes. <para>存放压缩字节的缓冲区。</para></param>
    /// <param name="sourceIndex">Offset in <paramref name="source"/> where the frame starts.
    /// <para><paramref name="source"/> 中帧的起始偏移。</para></param>
    /// <param name="sourceLength">Compressed bytes offered to the decoder.
    /// <para>交给解码器的压缩字节数。</para></param>
    /// <param name="target">Buffer receiving the decoded bytes, starting at offset zero.
    /// <para>接收解码字节的缓冲区，自偏移零起写入。</para></param>
    /// <param name="targetLength">Exact expected decoded length. <para>期望的解码长度。</para></param>
    /// <returns>The number of decoded bytes written. <para>写入的解码字节数。</para></returns>
    private int DecompressPcluster(ErofsAlgorithm algorithm, byte[] source, int sourceIndex, int sourceLength,
        byte[] target, int targetLength)
    {
        var context = _decoderStates.Rent();
        try
        {
            return ErofsCompression.AlgorithmDecompress(
                algorithm, source, sourceIndex, sourceLength, target, 0, targetLength, 0, 0, context);
        }
        finally
        {
            _decoderStates.Return(context);
        }
    }
}