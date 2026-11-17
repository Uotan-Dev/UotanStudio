namespace UotanStudio.Comm.Protocols.Unisoc;

/// <summary>
/// Phase of one flash pass. <para>一次刷写流程所处的阶段。</para>
/// </summary>
public enum UnisocFlashPhase
{
    /// <summary>Bringing the link up to FDL2. <para>把链路提升到 FDL2。</para></summary>
    Connect = 0,

    /// <summary>Erasing partitions. <para>擦除分区。</para></summary>
    Erase = 1,

    /// <summary>Writing partition images. <para>写入分区镜像。</para></summary>
    Flash = 2,

    /// <summary>A plan image has no matching device partition. <para>计划镜像没有对应的设备分区。</para></summary>
    Skip = 3,

    /// <summary>The pass completed. <para>流程完成。</para></summary>
    Done = 4,
}

/// <summary>
/// One progress record of a flash pass. <para>刷写流程的一条进度记录。</para>
/// </summary>
/// <param name="Phase">Current phase. <para>当前阶段。</para></param>
/// <param name="Target">Partition or loader the record refers to, when applicable.
/// <para>记录指向的分区或加载器；不适用时为 <see langword="null"/>。</para></param>
/// <param name="BytesCompleted">Bytes transferred so far in the current target.
/// <para>当前目标已传输的字节数。</para></param>
/// <param name="Total">Total bytes of the current target. <para>当前目标的总字节数。</para></param>
/// <param name="Message">Human-readable detail, when applicable. <para>人类可读的细节；不适用时为 <see langword="null"/>。</para></param>
public sealed record UnisocFlashProgress(
    UnisocFlashPhase Phase,
    string? Target,
    long BytesCompleted,
    long Total,
    string? Message = null);