namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// One progress report of an RCM (APX) transfer.
/// <para>一次 RCM（APX）传输的单条进度报告。</para>
/// </summary>
/// <param name="Phase">Phase of the RCM exchange: <c>query</c>, <c>sync</c>, <c>download</c> or <c>done</c>.
/// <para>RCM 交互阶段：<c>query</c>、<c>sync</c>、<c>download</c> 或 <c>done</c>。</para></param>
/// <param name="BytesCompleted">Cumulative payload bytes completed for the current transfer;
/// <see langword="null"/> when the phase moves no payload.
/// <para>当前传输已完成的累计负载字节；该阶段不移动负载时为 <see langword="null"/>。</para></param>
/// <param name="BytesTotal">Total payload bytes of the current transfer; <see langword="null"/> when unknown.
/// <para>当前传输的负载总字节；未知时为 <see langword="null"/>。</para></param>
/// <param name="Message">Human-readable status line.
/// <para>人类可读的状态行。</para></param>
public sealed record TegraRcmProgress(
    string Phase,
    long? BytesCompleted,
    long? BytesTotal,
    string Message);