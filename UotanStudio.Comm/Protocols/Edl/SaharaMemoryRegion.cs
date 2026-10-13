namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// One region advertised by a target in Sahara memory-debug mode, parsed from the debug region table.
/// <para>Sahara 内存调试模式下的目标所声明的一个区域，解析自调试区域表。</para>
/// </summary>
public sealed class SaharaMemoryRegion
{
    /// <summary>
    /// Gets the record's leading save-preference word.
    /// <para>获取记录开头的保存优先级字段。</para>
    /// </summary>
    /// <value>The <c>save_pref</c> field; the protocol header defines the field but assigns it no
    /// protocol meaning, so it is preserved
    /// verbatim. <para><c>save_pref</c> 字段；协议头定义了该字段但未赋予任何协议含义
    /// ，因此原样保留。</para></value>
    public ulong SavePreference { get; init; }

    /// <summary>
    /// Gets the target address the region starts at.
    /// <para>获取区域起始的目标地址。</para>
    /// </summary>
    /// <value>The <c>mem_base</c> field in bytes
    ///.
    /// <para><c>mem_base</c> 字段，单位为字节
    /// 。</para></value>
    public ulong Address { get; init; }

    /// <summary>
    /// Gets the length of the region in bytes.
    /// <para>获取区域的字节长度。</para>
    /// </summary>
    /// <value>The <c>length</c> field; zero means the target declared an empty region, which is
    /// skipped.
    /// <para><c>length</c> 字段；为零表示目标声明了一个空区域，会被跳过。</para></value>
    public ulong Length { get; init; }

    /// <summary>
    /// Gets the region description supplied by the target.
    /// <para>获取目标提供的区域描述。</para>
    /// </summary>
    /// <value>The <c>desc</c> field decoded as ASCII and truncated at its first NUL; empty when the
    /// target left the field blank.
    /// <para>以 ASCII 解码并在首个 NUL 处截断的 <c>desc</c> 字段；目标留空时为空字符串
    /// 。</para></value>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the file name proposed by the target for this region.
    /// <para>获取目标为该区域建议的文件名。</para>
    /// </summary>
    /// <value>The <c>filename</c> field decoded as ASCII and truncated at its first NUL
    ///. Treat it as untrusted input and validate
    /// it with <see cref="SaharaProtocol.IsSafeMemoryDumpFileName"/> before opening it.
    /// <para>以 ASCII 并在首个 NUL 处截断的 <c>filename</c> 字段
    /// 。应将其视为不可信输入，并在打开之前用
    /// <see cref="SaharaProtocol.IsSafeMemoryDumpFileName"/> 校验。</para></value>
    public required string FileName { get; init; }

    /// <summary>
    /// Returns the region's address range and proposed name.
    /// <para>返回区域的地址范围与建议名称。</para>
    /// </summary>
    /// <returns>A single-line diagnostic string. <para>单行诊断字符串。</para></returns>
    public override string ToString() => $"SaharaMemoryRegion(0x{Address:X16}+0x{Length:X}, {FileName})";
}