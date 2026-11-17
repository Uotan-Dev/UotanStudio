namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// One progress report of a Tegra flash run.
/// <para>一次 Tegra 烧录运行的单条进度报告。</para>
/// </summary>
/// <param name="Phase">Run phase: <c>handshake</c>, <c>miniloader</c>, <c>nv3p</c>, <c>bct</c>, <c>bootloader</c>,
/// <c>verify</c>, <c>reboot</c> or <c>done</c>. <para>运行阶段：<c>handshake</c>、<c>miniloader</c>、<c>nv3p</c>、
/// <c>bct</c>、<c>bootloader</c>、<c>verify</c>、<c>reboot</c> 或 <c>done</c>。</para></param>
/// <param name="Target">Step the report belongs to, such as a file name; <see langword="null"/> for run-level
/// reports. <para>报告所属的步骤，例如文件名；运行级报告为 <see langword="null"/>。</para></param>
/// <param name="BytesCompleted">Cumulative payload bytes completed for the current transfer;
/// <see langword="null"/> when not applicable. <para>当前传输已完成的累计负载字节；不适用时为
/// <see langword="null"/>。</para></param>
/// <param name="BytesTotal">Total payload bytes of the current transfer; <see langword="null"/> when unknown.
/// <para>当前传输的负载总字节；未知时为 <see langword="null"/>。</para></param>
/// <param name="Message">Human-readable status line. <para>人类可读的状态行。</para></param>
public sealed record TegraFlashProgress(
    string Phase,
    string? Target,
    long? BytesCompleted,
    long? BytesTotal,
    string Message);