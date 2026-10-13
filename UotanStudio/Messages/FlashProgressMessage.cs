namespace UotanStudio.Messages;

/// <summary>
/// Flash progress message carrying the current partition, percent and status text.
/// <para>刷写进度消息，携带当前分区、进度百分比与状态文本。</para>
/// </summary>
/// <param name="partition">Partition being flashed; null when not partition-specific. <para>正在刷写的分区；非分区级时为 null。</para></param>
/// <param name="percent">Progress percent in the 0-100 range. <para>0-100 范围内的进度百分比。</para></param>
/// <param name="status">Status text describing the current step. <para>描述当前步骤的状态文本。</para></param>
public class FlashProgressMessage(string? partition, int percent, string status)
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
}