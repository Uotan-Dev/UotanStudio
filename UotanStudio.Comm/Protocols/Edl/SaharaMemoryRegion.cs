namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// One region advertised by a target in Sahara memory-debug mode, parsed from the debug region table
/// whose layout is fixed by the reference protocol header
/// (QSaharaServer/src/sahara_protocol.h:463-479).
/// <para>Sahara 内存调试模式下的目标所声明的一个区域，解析自调试区域表；其布局由参考协议头固定
/// （QSaharaServer/src/sahara_protocol.h:463-479）。</para>
/// </summary>
public sealed class SaharaMemoryRegion
{
    /// <summary>
    /// Gets the record's leading save-preference word.
    /// <para>获取记录开头的保存优先级字段。</para>
    /// </summary>
    /// <value>The <c>save_pref</c> field; the reference header defines the field but assigns it no
    /// protocol meaning (QSaharaServer/src/sahara_protocol.h:466 and :474), so it is preserved
    /// verbatim. <para><c>save_pref</c> 字段；参考头文件定义了该字段但未赋予任何协议含义
    /// （QSaharaServer/src/sahara_protocol.h:466 与 :474），因此原样保留。</para></value>
    public ulong SavePreference { get; init; }

    /// <summary>
    /// Gets the target address the region starts at.
    /// <para>获取区域起始的目标地址。</para>
    /// </summary>
    /// <value>The <c>mem_base</c> field in bytes
    /// (QSaharaServer/src/sahara_protocol.h:467 and :475).
    /// <para><c>mem_base</c> 字段，单位为字节
    /// （QSaharaServer/src/sahara_protocol.h:467 与 :475）。</para></value>
    public ulong Address { get; init; }

    /// <summary>
    /// Gets the length of the region in bytes.
    /// <para>获取区域的字节长度。</para>
    /// </summary>
    /// <value>The <c>length</c> field; zero means the target declared an empty region, which the
    /// reference server skips (QSaharaServer/src/sahara_protocol.c:886-887).
    /// <para><c>length</c> 字段；为零表示目标声明了一个空区域，参考服务端会跳过它
    /// （QSaharaServer/src/sahara_protocol.c:886-887）。</para></value>
    public ulong Length { get; init; }

    /// <summary>
    /// Gets the region description supplied by the target.
    /// <para>获取目标提供的区域描述。</para>
    /// </summary>
    /// <value>The <c>desc</c> field decoded as ASCII and truncated at its first NUL; empty when the
    /// target left the field blank (QSaharaServer/src/sahara_protocol.h:469 and :477).
    /// <para>以 ASCII 解码并在首个 NUL 处截断的 <c>desc</c> 字段；目标留空时为空字符串
    /// （QSaharaServer/src/sahara_protocol.h:469 与 :477）。</para></value>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the file name proposed by the target for this region.
    /// <para>获取目标为该区域建议的文件名。</para>
    /// </summary>
    /// <value>The <c>filename</c> field decoded as ASCII and truncated at its first NUL
    /// (QSaharaServer/src/sahara_protocol.h:470 and :478). Treat it as untrusted input and validate
    /// it with <see cref="SaharaProtocol.IsSafeMemoryDumpFileName"/> before opening it.
    /// <para>以 ASCII 并在首个 NUL 处截断的 <c>filename</c> 字段
    /// （QSaharaServer/src/sahara_protocol.h:470 与 :478）。应将其视为不可信输入，并在打开之前用
    /// <see cref="SaharaProtocol.IsSafeMemoryDumpFileName"/> 校验。</para></value>
    public required string FileName { get; init; }

    /// <summary>Returns the region's address range and proposed name. 返回区域的地址范围与建议名称。</summary>
    /// <returns>A single-line diagnostic string. <para>单行诊断字符串。</para></returns>
    public override string ToString() => $"SaharaMemoryRegion(0x{Address:X16}+0x{Length:X}, {FileName})";
}