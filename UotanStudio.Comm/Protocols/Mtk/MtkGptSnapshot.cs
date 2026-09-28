using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>A validated GPT observation. It does not authorize later writes or prove the table is still current.
/// <para>已校验的 GPT 观测结果。它不授权后续写入，也不能证明该表仍然最新。</para></summary>
public sealed class MtkGptSnapshot
{
    internal MtkGptSnapshot(MtkXFlashStorageInfo storage, uint partitionId, GptTable table)
    {
        Storage = storage;
        PartitionId = partitionId;
        Table = table;
    }

    /// <summary>Gets the exact discovered storage snapshot used for the read.
    /// <para>获取该次读取所使用的、精确的已发现存储快照。</para></summary>
    /// <value>The immutable storage geometry observation the table was read under.
    /// <para>读取该表时所依据的不可变存储几何观测。</para></value>
    public MtkXFlashStorageInfo Storage { get; }
    /// <summary>Gets the physical region containing the table.
    /// <para>获取包含该表的物理区域。</para></summary>
    /// <value>The XFlash physical partition identifier holding both GPT copies.
    /// <para>持有两份 GPT 副本的 XFlash 物理分区标识符。</para></value>
    public uint PartitionId { get; }
    /// <summary>Gets the validated partition metadata.
    /// <para>获取已校验的分区元数据。</para></summary>
    /// <value>The parsed GPT table with its metadata digest, not a live device view.
    /// <para>带元数据摘要的已解析 GPT 表，并非设备的实时视图。</para></value>
    public GptTable Table { get; }
}