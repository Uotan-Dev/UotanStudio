namespace UotanStudio.Firmware.Nb0;

/// <summary>
/// <para>表示 NB0 固件文件校验操作的进度信息。</para>
/// Represents progress information for an NB0 firmware file verification operation.
/// </summary>
public sealed class Nb0CheckProgress
{
    /// <summary>
    /// <para>待校验的条目总数。</para>
    /// The total number of entries to verify.
    /// </summary>
    public int TotalEntries { get; set; }

    /// <summary>
    /// <para>已完成校验的条目数。</para>
    /// The number of entries that have been verified.
    /// </summary>
    public int CompletedEntries { get; set; }

    /// <summary>
    /// <para>当前正在校验的条目名称。</para>
    /// The name of the entry currently being verified.
    /// </summary>
    public string CurrentEntryName { get; set; } = string.Empty;
}

/// <summary>
/// <para>表示 NB0 固件文件提取操作的进度信息。</para>
/// Represents progress information for an NB0 firmware file extraction operation.
/// </summary>
public sealed class Nb0ExtractionProgress
{
    /// <summary>
    /// <para>待提取的条目总数。</para>
    /// The total number of entries to extract.
    /// </summary>
    public int TotalEntries { get; set; }

    /// <summary>
    /// <para>已完成提取的条目数。</para>
    /// The number of entries that have been extracted.
    /// </summary>
    public int CompletedEntries { get; set; }

    /// <summary>
    /// <para>当前正在提取的条目名称。</para>
    /// The name of the entry currently being extracted.
    /// </summary>
    public string CurrentEntryName { get; set; } = string.Empty;
}

/// <summary>
/// <para>表示 NB0 固件文件打包操作的进度信息。</para>
/// Represents progress information for an NB0 firmware file packing operation.
/// </summary>
public sealed class Nb0PackProgress
{
    /// <summary>
    /// <para>待打包的条目总数。</para>
    /// The total number of entries to pack.
    /// </summary>
    public int TotalEntries { get; set; }

    /// <summary>
    /// <para>已完成打包的条目数。</para>
    /// The number of entries that have been packed.
    /// </summary>
    public int CompletedEntries { get; set; }

    /// <summary>
    /// <para>当前正在打包的条目名称。</para>
    /// The name of the entry currently being packed.
    /// </summary>
    public string CurrentEntryName { get; set; } = string.Empty;
}