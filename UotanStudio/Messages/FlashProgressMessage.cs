namespace UotanStudio.Messages;

/// <summary>
/// Flash progress message carrying the current partition, percent and status text, plus the optional structured
/// byte counters the per-chunk EDL transfers report.
/// <para>刷写进度消息，携带当前分区、进度百分比与状态文本，以及逐块 EDL 传输上报的可选结构化字节计数。</para>
/// </summary>
/// <param name="partition">Partition being flashed; null when not partition-specific. <para>正在刷写的分区；非分区级时为 null。</para></param>
/// <param name="percent">Progress percent in the 0-100 range. <para>0-100 范围内的进度百分比。</para></param>
/// <param name="status">Status text describing the current step. <para>描述当前步骤的状态文本。</para></param>
/// <param name="entryKey">Bundle entry key (manifest label or file name) the progress belongs to, or null when the
/// step has no bundle entry. <para>进度所属的包条目键（清单标签或文件名）；该步骤没有包条目时为 null。</para></param>
/// <param name="bytesWritten">Cumulative bytes accepted by the current transfer, or null when byte counters are
/// unavailable. <para>当前传输已累计接受的字节数；无字节计数时为 null。</para></param>
/// <param name="totalBytes">Total bytes the current transfer covers, or null when byte counters are unavailable.
/// <para>当前传输覆盖的总字节数；无字节计数时为 null。</para></param>
public class FlashProgressMessage(string? partition, int percent, string status,
    string? entryKey = null, long? bytesWritten = null, long? totalBytes = null)
{
    /// <summary>
    /// Gets the partition being flashed; null when not partition-specific.
    /// <para>正在刷写的分区；非分区级时为 null。</para>
    /// </summary>
    /// <value>The partition name or null. <para>分区名或 null。</para></value>
    public string? Partition { get; } = partition;

    /// <summary>
    /// Gets the progress percent (0-100).
    /// <para>进度百分比（0-100）。</para>
    /// </summary>
    /// <value>The percent reached by the current flash. <para>当前刷写已达到的百分比。</para></value>
    public int Percent { get; } = percent;

    /// <summary>
    /// Gets the status text.
    /// <para>状态文本。</para>
    /// </summary>
    /// <value>The text describing the current step. <para>描述当前步骤的文本。</para></value>
    public string Status { get; } = status;

    /// <summary>
    /// Gets the bundle entry key the progress belongs to; null when the step has no bundle entry.
    /// <para>获取进度所属的包条目键；该步骤没有包条目时为 null。</para>
    /// </summary>
    /// <value>The manifest label or file name of the current bundle entry, or null.
    /// <para>当前包条目的清单标签或文件名，或 null。</para></value>
    public string? EntryKey { get; } = entryKey;

    /// <summary>
    /// Gets the cumulative bytes accepted by the current transfer; null when byte counters are unavailable.
    /// <para>获取当前传输已累计接受的字节数；无字节计数时为 null。</para>
    /// </summary>
    /// <value>The accepted byte count, or null. <para>已接受的字节数，或 null。</para></value>
    public long? BytesWritten { get; } = bytesWritten;

    /// <summary>
    /// Gets the total bytes the current transfer covers; null when byte counters are unavailable.
    /// <para>获取当前传输覆盖的总字节数；无字节计数时为 null。</para>
    /// </summary>
    /// <value>The total byte count, or null. <para>总字节数，或 null。</para></value>
    public long? TotalBytes { get; } = totalBytes;
}