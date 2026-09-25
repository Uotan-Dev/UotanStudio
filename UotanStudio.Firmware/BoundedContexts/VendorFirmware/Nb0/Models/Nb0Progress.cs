namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Nb0.Models;

/// <summary>
/// Checkpoint a checksum verification run reports after each archive entry it processes. Instances are snapshots: a
/// report carries the entry name of the pass it belongs to and is never mutated afterwards.
/// <para>校验和验证每处理完一个归档条目时上报的快照。实例是一次性快照：上报携带所属流程中该条目的名称，之后不会被修改。</para>
/// </summary>
public sealed class Nb0CheckProgress
{
    /// <summary>
    /// Entries in the parsed metadata, counted before verification starts, including a trailing <c>.md5</c> entry.
    /// Zero for an archive that declares no entries, in which case no report is emitted.
    /// <para>验证开始前统计出的已解析元数据条目数，包含末尾的 <c>.md5</c> 条目。归档未声明条目时为零，此时不会发出任何上报。</para>
    /// </summary>
    /// <value>Entry count the verification loop walks. <para>验证循环所遍历的条目数。</para></value>
    public int TotalEntries { get; set; }

    /// <summary>
    /// Entries handled so far, incremented for each entry whether its digest matched, mismatched, or had no record at
    /// all; it reaches <see cref="TotalEntries"/> when the loop finishes without throwing.
    /// <para>已处理的条目数：无论摘要匹配、不匹配还是缺少记录都会递增；循环未抛出异常时最终等于 <see cref="TotalEntries"/>。</para>
    /// </summary>
    /// <value>Entries walked so far, never above <see cref="TotalEntries"/>. <para>已遍历的条目数，不会超过 <see cref="TotalEntries"/>。</para></value>
    public int CompletedEntries { get; set; }

    /// <summary>
    /// Name of the entry just handled, taken verbatim from the archive header table, so it may be empty or contain
    /// characters that are invalid in a file name.
    /// <para>刚处理完的条目名称，原样取自归档头表，因此可能为空或包含在文件名中非法的字符。</para>
    /// </summary>
    /// <value>Name behind the current report. <para>当前上报对应的条目名。</para></value>
    public string CurrentEntryName { get; set; } = string.Empty;
}

/// <summary>
/// Checkpoint an extraction run reports after each entry it actually writes out. Instances are snapshots and are never
/// mutated after being reported.
/// <para>提取流程每真正写出一个条目后上报的快照。实例是一次性快照，上报后不会被修改。</para>
/// </summary>
public sealed class Nb0ExtractionProgress
{
    /// <summary>
    /// Entries in the parsed metadata, counted before extraction starts, including a trailing <c>.md5</c> entry. Zero
    /// for an archive that declares no entries, in which case no report is emitted.
    /// <para>提取开始前统计出的已解析元数据条目数，包含末尾的 <c>.md5</c> 条目。归档未声明条目时为零，此时不会发出任何上报。</para>
    /// </summary>
    /// <value>Entry count the extraction loop walks. <para>提取循环所遍历的条目数。</para></value>
    public int TotalEntries { get; set; }

    /// <summary>
    /// Entries handled so far, including ones excluded by a filter or left untouched because the target exists and
    /// overwriting is disabled. Those skips emit no report, so this value can jump between consecutive reports.
    /// <para>已处理的条目数，包含被过滤器排除、以及目标已存在且禁用覆盖而保持原样的条目。这些跳过的条目不上报，因此相邻两次上报之间该值可能突然增大。</para>
    /// </summary>
    /// <value>Entries walked so far, counting skipped ones, never above <see cref="TotalEntries"/>. <para>已遍历的条目数（含被跳过的条目），不会超过 <see cref="TotalEntries"/>。</para></value>
    public int CompletedEntries { get; set; }

    /// <summary>
    /// Name of the entry just handled, taken verbatim from the archive header table rather than the sanitized name used
    /// for the output file, and a failing entry is reported too, since its error is collected instead of thrown.
    /// <para>刚处理完的条目名称，原样取自归档头表，而非用于输出文件的净化名称；写出失败的条目同样会上报，因为其错误是被收集而非抛出。</para>
    /// </summary>
    /// <value>Name behind the current report, which trails the counter when entries were skipped. <para>当前上报对应的条目名；有条目被跳过时它会落后于计数器。</para></value>
    public string CurrentEntryName { get; set; } = string.Empty;
}

/// <summary>
/// Checkpoint a packing run reports after each entry whose data has been written to the output. Instances are
/// snapshots and are never mutated after being reported.
/// <para>打包流程每写完一个条目的数据后上报的快照。实例是一次性快照，上报后不会被修改。</para>
/// </summary>
public sealed class Nb0PackProgress
{
    /// <summary>
    /// Total number of entries being packed, materialized before the header table is written. Zero for an empty entry
    /// list, in which case no report is emitted.
    /// <para>本次打包的条目总数，在写出条目头表之前就已确定。条目列表为空时为零，此时不会发出任何上报。</para>
    /// </summary>
    /// <value>Entry count the packer writes data for. <para>打包器需要为其写出数据的条目数。</para></value>
    public int TotalEntries { get; set; }

    /// <summary>
    /// Entries whose data has been written so far, padded to a 4-byte boundary. A size mismatch or a source file that
    /// ends early aborts the run at that entry, so this value can stay below <see cref="TotalEntries"/>.
    /// <para>已写出数据并完成 4 字节对齐填充的条目数。大小不匹配或源文件提前结束会在该条目处中断整个流程，因此该值可能始终小于 <see cref="TotalEntries"/>。</para>
    /// </summary>
    /// <value>Entries whose payload and padding reached the output. <para>正文与填充字节已写入输出流的条目数。</para></value>
    public int CompletedEntries { get; set; }

    /// <summary>
    /// Name recorded in the header of the entry just written. Directory-based packing stores relative paths with
    /// <c>/</c> separators, so the value identifies an archive member, not an operating-system path.
    /// <para>刚写入条目头部中记录的名称。按目录打包时名称使用以 <c>/</c> 分隔的相对路径，因此该值标识归档成员，而非操作系统路径。</para>
    /// </summary>
    /// <value>Header name of the entry just written. <para>刚写入条目的头部名称。</para></value>
    public string CurrentEntryName { get; set; } = string.Empty;
}