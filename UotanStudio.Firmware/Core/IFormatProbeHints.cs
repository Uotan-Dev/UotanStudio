namespace UotanStudio.Firmware.Core;

/// <summary>
/// Optional capability a format handler can publish so the registry can make a low-cost structural
/// magic confirmation beyond its extension candidates. Unlike <see cref="IReadonlyFormatHandler.CanHandle"/>,
/// which re-opens the file, this checks a header the caller already read with
/// <see cref="FormatDetector.ReadHeader(string, int)"/>; that header can be shorter than the offset where
/// a magic sits, so an implementation must return <see langword="false"/> for any length (including zero)
/// it cannot confidently confirm and must never perform IO.
/// <para>格式处理器可选发布的能力，供注册表在扩展名候选之外做低成本的结构性魔数确认。与会重新打开文件的
/// <see cref="IReadonlyFormatHandler.CanHandle"/> 不同，本方法只检查调用方经 <see cref="FormatDetector.ReadHeader(string, int)"/>
/// 读好的头部；该头部可能短于魔数所在偏移，因此实现必须对任意长度（含 0）在无法确信时返回 <see langword="false"/>，
/// 且绝不执行 IO。</para>
/// </summary>
public interface IFormatProbeHints
{
    /// <summary>
    /// Confirms whether the handler owns the format from a pre-read header, without touching the file.
    /// <para>依据预先读取的头部确认处理器是否负责该格式，不触碰文件。</para>
    /// </summary>
    /// <param name="header">
    /// Leading bytes read by the caller; may be shorter than the magic offset, and an empty span is a
    /// valid input that must yield <see langword="false"/> unless nothing is required to confirm.
    /// <para>调用方读入的起始字节；可能短于魔数偏移；空 span 是合法输入，除非确认无需任何字节，否则必须返回
    /// <see langword="false"/>。</para>
    /// </param>
    /// <param name="path">
    /// Path the header was read from, used only for cheap name-based disambiguation; the value is never
    /// opened or stat-ed by an implementation.
    /// <para>读取该头部所用的路径，仅用于廉价的名字消歧；实现绝不打开或 stat 它。</para>
    /// </param>
    /// <returns><see langword="true"/> only when the header carries this format's structural magic;
    /// <see langword="false"/> for any shorter, ambiguous or unrelated header.
    /// <para>仅当头部携带本格式的结构魔数时返回 <see langword="true"/>；对更短、含糊或无关的头部返回 <see langword="false"/>。</para></returns>
    bool MatchesMagic(ReadOnlySpan<byte> header, string path);
}