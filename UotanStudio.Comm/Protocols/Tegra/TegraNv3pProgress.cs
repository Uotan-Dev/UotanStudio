namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// One progress report of an nv3p transfer.
/// <para>一次 nv3p 传输的单条进度报告。</para>
/// </summary>
/// <param name="Phase">Transfer phase: <c>download</c>, <c>upload</c> or <c>status</c>.
/// <para>传输阶段：<c>download</c>、<c>upload</c> 或 <c>status</c>。</para></param>
/// <param name="BytesCompleted">Payload bytes transferred so far. <para>已传输的负载字节。</para></param>
/// <param name="BytesTotal">Total payload bytes of the transfer, which is zero when the device declares the
/// length only while the transfer runs. <para>本次传输的负载总字节；设备仅在传输过程中才声明长度时为零。</para></param>
/// <param name="Message">Human-readable status line. <para>人类可读的状态行。</para></param>
public sealed record TegraNv3pProgress(string Phase, long BytesCompleted, long BytesTotal, string Message);