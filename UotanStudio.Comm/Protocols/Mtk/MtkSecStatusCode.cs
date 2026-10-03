namespace UotanStudio.Comm.Protocols.Mtk.Security;

/// <summary>Names for the download-agent status codes that a security operation can receive.
/// <para>安全操作可能收到的下载代理状态码名称。</para></summary>
/// <remarks>
/// Only codes catalogued by the reference implementation are named here, and each name cites the
/// table entry it comes from (Library/error.py:549-655). An unnamed code is reported as a raw value;
/// the client never infers a meaning for a status it cannot point at.
/// <para>此处只命名参考实现已编目的状态码，每个名称都注明其来源表项（Library/error.py:549-655）。
/// 未命名的状态码按原始值上报，客户端绝不为无法指认的状态码推断含义。</para>
/// </remarks>
public static class MtkSecStatusCode
{
    /// <summary>The success value the reference table names as <c>OK</c> (Library/error.py:550).
    /// <para>参考表命名为 <c>OK</c> 的成功值（Library/error.py:550）。</para></summary>
    public const uint Ok = 0;

    /// <summary>Names a download-agent status code, or null when the evidence has no entry for it.
    /// <para>为下载代理状态码命名；证据中没有该条目时返回 null。</para></summary>
    /// <param name="status">The raw status value reported by the agent. 代理上报的原始状态值。</param>
    /// <returns>The documented name, or <see langword="null"/> when the value is not catalogued.
    /// <para>已记载的名称；取值未被编目时为 <see langword="null"/>。</para></returns>
    public static string? TryDescribe(uint status) => status switch
    {
        0x00000000 => "OK",
        0xc0010003 => "Unsupported command",
        0xc0010004 => "Unsupported ctrl code",
        0xc002002e => "SECCFG not found",
        0xc002002f => "SECCFG magic incorrect",
        0xc0020030 => "SECCFG invalid",
        0xc0020031 => "Cipher mode invalid",
        0xc0020032 => "Cipher key invalid",
        0xc0020033 => "Cipher data unaligned",
        0xc0020042 => "Ext AllInOne Signature rejected",
        0xc0020043 => "Ext AllInOne Signature missing",
        0xc0020044 => "Comm Key is not set",
        0xc0020049 => "Remote Security policy disabled",
        0xc002004d => "Get OTP value fail",
        0xc0020051 => "Otp table not initialized",
        0xc0020055 => "Security level unsupported",
        0xc0020056 => "Security level mismatch",
        0xc002005a => "Security config is formatted",
        0xc002005b => "Security config unknown error",
        0xc002005c => "Lockstate seccfg fail",
        0xc002005d => "Lockstate custom fail",
        0xc002005e => "Lockstate inconsistent",
        _ => null
    };

    /// <summary>Formats a status value with its documented name when one exists.
    /// <para>在存在已记载名称时，连同该名称一起格式化状态值。</para></summary>
    /// <param name="status">The raw status value reported by the agent. 代理上报的原始状态值。</param>
    /// <returns>A localization-neutral description of the status. 与本地化无关的状态描述。</returns>
    public static string Describe(uint status) =>
        TryDescribe(status) is { } name ? $"0x{status:X8} ({name})" : $"0x{status:X8}";
}

/// <summary>Names for the reply codes of a replay-protected block transfer.
/// <para>重放保护块传输回复码的名称。</para></summary>
/// <remarks>
/// The reference client unpacks the first reply of a replay-protected read or write as one
/// little-endian 16-bit word and indexes a fixed table with it, where zero means success
/// (Library/DA/xflash/extension/xflash.py:29-38, :395-430). Codes beyond that table are reported
/// unchanged.
/// <para>参考客户端把重放保护读或写的首个回复按一个小端 16 位字解包，并用它索引固定表，其中零表示成功
/// （Library/DA/xflash/extension/xflash.py:29-38、:395-430）。超出该表的取值原样上报。</para>
/// </remarks>
public static class MtkSecRpmbStatusCode
{
    /// <summary>The value that means the transfer completed (Library/DA/xflash/extension/xflash.py:30).
    /// <para>表示传输完成取值（Library/DA/xflash/extension/xflash.py:30）。</para></summary>
    public const ushort Ok = 0;

    /// <summary>Names a replay-protected block reply code, or null when the table has no entry.
    /// <para>为重放保护回复码命名；表中没有该条目时返回 null。</para></summary>
    /// <param name="status">The raw reply word. 原始回复字。</param>
    /// <returns>The documented text, or <see langword="null"/> for an uncatalogued value.
    /// <para>已记载的文本；未编目取值返回 <see langword="null"/>。</para></returns>
    public static string? TryDescribe(ushort status) => status switch
    {
        0 => "OK",
        1 => "General failure",
        2 => "Authentication failure",
        3 => "Counter failure",
        4 => "Address failure",
        5 => "Write failure",
        6 => "Read failure",
        7 => "Authentication key not yet programmed",
        _ => null
    };

    /// <summary>Formats a replay-protected block reply code with its documented name when available.
    /// <para>在存在已记载名称时，连同该名称一起格式化重放保护回复码。</para></summary>
    /// <param name="status">The raw reply word. 原始回复字。</param>
    /// <returns>A localization-neutral description of the reply. 与本地化无关的回复描述。</returns>
    public static string Describe(ushort status) =>
        TryDescribe(status) is { } name ? $"0x{status:X4} ({name})" : $"0x{status:X4}";
}