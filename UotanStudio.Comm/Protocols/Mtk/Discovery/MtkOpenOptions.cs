namespace UotanStudio.Comm.Protocols.Mtk.Discovery;

/// <summary>
/// Caller-supplied expectations applied by <c>MtkSession.OpenUsbAsync</c> before any protocol byte
/// is written, so a wrong-stage or logging-only endpoint is rejected without device I/O.
/// <para>调用方提供的期望条件，由 <c>MtkSession.OpenUsbAsync</c> 在写出任何协议字节之前应用，
/// 从而在无设备 I/O 的情况下拒绝错误阶段或仅日志的端点。</para>
/// </summary>
public sealed record MtkOpenOptions
{
    /// <summary>
    /// Gets the stage the caller expects the endpoint to present; <see cref="MtkUsbMode.Unknown"/>
    /// imposes no expectation and keeps the previous open behaviour.
    /// <para>获取调用方期望端点呈现的阶段；<see cref="MtkUsbMode.Unknown"/> 表示不设期望，保持原有打开行为。</para>
    /// </summary>
    /// <value>
    /// The expected stage; defaults to <see cref="MtkUsbMode.Unknown"/>.
    /// <para>期望阶段；默认为 <see cref="MtkUsbMode.Unknown"/>。</para>
    /// </value>
    public MtkUsbMode ExpectedMode { get; init; } = MtkUsbMode.Unknown;

    /// <summary>
    /// Gets an optional explicit command interface class code overriding the vendor-specific
    /// <c>0xFF</c> default used during interface selection.
    /// <para>获取可选的显式命令接口类代码，用于覆盖接口选择时默认的厂商特定类 <c>0xFF</c>。</para>
    /// </summary>
    /// <value>
    /// The required interface class, or <see langword="null"/> to require <c>0xFF</c>.
    /// <para>要求的接口类；为 <see langword="null"/> 时要求 <c>0xFF</c>。</para>
    /// </value>
    public byte? CommandInterfaceClass { get; init; }

    /// <summary>
    /// Gets an optional explicit command interface subclass filter; when several vendor bulk pairs
    /// differ only by subclass, this is the disambiguator.
    /// <para>获取可选的命令接口子类过滤；当多个厂商 bulk 对仅在子类上不同时，以此消除歧义。</para>
    /// </summary>
    /// <value>
    /// The required subclass, or <see langword="null"/> to accept any subclass.
    /// <para>要求的子类；为 <see langword="null"/> 时接受任意子类。</para>
    /// </value>
    public byte? CommandInterfaceSubClass { get; init; }
}