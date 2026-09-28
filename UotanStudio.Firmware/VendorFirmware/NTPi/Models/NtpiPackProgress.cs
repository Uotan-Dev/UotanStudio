namespace UotanStudio.Firmware.VendorFirmware.NTPi.Models;

/// <summary>
/// Immutable snapshot of an NTPI pack run, reported through <c>IProgress&lt;NtpiPackProgress&gt;</c> as packing advances.
/// <para>NTPI 打包过程的不可变进度快照，随打包推进通过 <c>IProgress&lt;NtpiPackProgress&gt;</c> 上报。</para>
/// </summary>
public class NtpiPackProgress
{
    /// <summary>Total number of file entries being packed. <para>本次打包的文件条目总数。</para></summary>
    /// <value>The file count for the whole run. <para>整个运行的文件数量。</para></value>
    public int TotalFiles { get; init; }

    /// <summary>Files finished in the current reporting phase; reset to 0 when the Region6 write phase starts.
    /// <para>当前上报阶段已完成的文件数；进入 Region6 写入阶段时归零。</para></summary>
    /// <value>The completed-file count for the current phase. <para>当前阶段的已完成文件数。</para></value>
    public int CompletedFiles { get; init; }

    /// <summary>Name of the file being processed; empty while regions or the header are written.
    /// <para>当前正在处理的文件名；写入区域或头部时为空字符串。</para></summary>
    /// <value>The current file name, or <see cref="string.Empty"/>. <para>当前文件名，或为 <see cref="string.Empty"/>。</para></value>
    public string CurrentFileName { get; init; } = string.Empty;

    /// <summary>Stage of the pack run this snapshot refers to. <para>本快照对应的打包运行阶段。</para></summary>
    /// <value>The current phase. <para>当前阶段。</para></value>
    public NtpiPackPhase Phase { get; init; }
}

/// <summary>
/// Ordered stages an NTPI pack run reports: measure files, write header and encrypted regions, write Region6 payloads, then complete.
/// <para>NTPI 打包运行按序上报的阶段：度量文件、写入头部与加密区域、写入 Region6 载荷，随后完成。</para>
/// </summary>
public enum NtpiPackPhase
{
    /// <summary>Measuring each file and computing hashes and layout. <para>正在逐个度量文件并计算哈希与布局。</para></summary>
    ProcessingFiles = 0,

    /// <summary>Writing the package header and the encrypted regions to the temporary output. <para>正在向临时输出写入包头部与加密区域。</para></summary>
    WritingRegions = 1,

    /// <summary>Encoding each file and appending it as Region6 payload data. <para>正在逐个编码文件并作为 Region6 载荷追加。</para></summary>
    WritingRegion6 = 2,

    /// <summary>The output was flushed to disk and the temporary file moved into the final path. <para>输出已刷盘，临时文件已移动到最终路径。</para></summary>
    Completed = 3
}