namespace UotanStudio.Firmware.Oppo.Models;

/// <summary>
/// <para>报告 Oppo 固件提取操作的进度。</para>
/// Reports progress of an Oppo firmware extraction operation.
/// </summary>
/// <param name="TotalEntries"><para>总条目数。</para>Total number of entries.</param>
/// <param name="CompletedEntries"><para>已完成条目数。</para>Number of completed entries.</param>
/// <param name="CurrentEntryName"><para>当前正在处理的条目名称。</para>Name of the entry currently being processed.</param>
public sealed record OppExtractProgress(int TotalEntries, int CompletedEntries, string CurrentEntryName);