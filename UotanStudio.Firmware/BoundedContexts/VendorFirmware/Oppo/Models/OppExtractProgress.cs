namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;

/// <summary>
/// Progress snapshot reported for each entry while an Oppo archive is being extracted.
/// <para>Oppo 归档提取过程中为每个条目报告的进度快照。</para>
/// </summary>
/// <param name="TotalEntries">
/// Total number of entries considered in the run; entries excluded by <c>SelectedEntries</c> still count towards it.
/// <para>本次运行所考虑的全部条目数；被 <c>SelectedEntries</c> 排除的条目仍计入其中。</para>
/// </param>
/// <param name="CompletedEntries">
/// Number of entries already handled, including those skipped for zero size or because the output file exists without overwrite permission.
/// <para>已处理完成的条目数，包含因大小为零或输出已存在且未允许覆盖而被跳过的条目。</para>
/// </param>
/// <param name="CurrentEntryName">
/// Raw archive entry name the snapshot refers to; reported once before the entry is written and once after it completes.
/// <para>快照对应的归档原始条目名；在条目写入前和完成后各报告一次。</para>
/// </param>
public sealed record OppExtractProgress(int TotalEntries, int CompletedEntries, string CurrentEntryName);