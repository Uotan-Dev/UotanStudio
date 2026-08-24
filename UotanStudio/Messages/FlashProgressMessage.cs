namespace UotanStudio.Messages;

/// <summary>
/// Flash progress message, reporting partition flash progress to the output panel.
/// <para>刷写进度消息，向输出面板报告分区刷写进度。</para>
/// </summary>
public class FlashProgressMessage(string? partition, int percent, string status)
{
    /// <summary>
    /// Gets the partition being flashed; null when not partition-specific.
    /// <para>正在刷写的分区；非分区级时为 null。</para>
    /// </summary>
    public string? Partition { get; } = partition;

    /// <summary>
    /// Gets the progress percent (0-100).
    /// <para>进度百分比（0-100）。</para>
    /// </summary>
    public int Percent { get; } = percent;

    /// <summary>
    /// Gets the status text.
    /// <para>状态文本。</para>
    /// </summary>
    public string Status { get; } = status;
}