namespace UotanStudio.Firmware.NTPi.Models;

/// <summary>
/// <para>表示 NTPI 打包操作的进度信息。</para>
/// Represents progress information for an NTPI pack operation.
/// </summary>
public class NtpiPackProgress
{
    /// <summary>
    /// <para>待打包的文件总数。</para>
    /// The total number of files to be packed.
    /// </summary>
    public int TotalFiles { get; init; }

    /// <summary>
    /// <para>已完成压缩和加密的文件数。</para>
    /// The number of files that have been compressed and encrypted.
    /// </summary>
    public int CompletedFiles { get; init; }

    /// <summary>
    /// <para>当前正在处理的文件名。</para>
    /// The name of the file currently being processed.
    /// </summary>
    public string CurrentFileName { get; init; } = string.Empty;

    /// <summary>
    /// <para>当前打包阶段。</para>
    /// The current packing phase.
    /// </summary>
    public NtpiPackPhase Phase { get; init; }
}

/// <summary>
/// <para>表示 NTPI 打包操作的阶段。</para>
/// Represents the phase of an NTPI pack operation.
/// </summary>
public enum NtpiPackPhase
{
    /// <summary>
    /// <para>正在压缩和加密文件。</para>
    /// Compressing and encrypting files.
    /// </summary>
    ProcessingFiles = 0,

    /// <summary>
    /// <para>正在写入区域数据。</para>
    /// Writing region data.
    /// </summary>
    WritingRegions = 1,

    /// <summary>
    /// <para>正在写入 Region6 数据。</para>
    /// Writing Region6 data.
    /// </summary>
    WritingRegion6 = 2,

    /// <summary>
    /// <para>打包完成。</para>
    /// Packing completed.
    /// </summary>
    Completed = 3
}