using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>A validated GPT observation taken through the Legacy download agent.
/// <para>经 Legacy 下载代理取得的已校验 GPT 观测结果。</para></summary>
/// <remarks>
/// <para>It binds the table to the Legacy inventory and region it was read from, so a later plan cannot
/// silently address a different storage observation. Like the XFlash snapshot, it does not authorize later
/// writes and does not prove the table is still current.</para>
/// <para>The write-back that consumes this snapshot is the Legacy <c>writeflash</c> exchange
/// (Library/DA/legacy/dalegacy_lib.py:947) pointed at the region-relative metadata ranges, because the
/// reference agent declares dedicated GPT opcodes (Library/DA/legacy/dalegacy_param.py:15-16 for
/// <c>UFS_READ_GPT_CMD</c>/<c>UFS_WRITE_GPT_CMD</c> and Library/DA/legacy/dalegacy_param.py:54 for
/// <c>SDMMC_WRITE_GPT_CMD</c>) that no upstream client ever sends, so their payload layout is not provable
/// and is deliberately not implemented here.</para>
/// <para>它把分区表绑定到读取时所用的 Legacy 存储清单与区域，后续计划因此不能悄悄指向另一份存储观测。与 XFlash
/// 快照一样，它不授权后续写入，也不能证明该表仍然最新。</para>
/// <para>消费本快照的写回是把 Legacy <c>writeflash</c> 交换（Library/DA/legacy/dalegacy_lib.py:947）指向区域内
/// 的元数据范围；参考代理虽然声明了专用的 GPT 操作码（Library/DA/legacy/dalegacy_param.py:15-16 的
/// <c>UFS_READ_GPT_CMD</c> 与 <c>UFS_WRITE_GPT_CMD</c>、Library/DA/legacy/dalegacy_param.py:54 的
/// <c>SDMMC_WRITE_GPT_CMD</c>），但上游任何客户端都从未发送它们，其载荷布局无法证实，因此此处刻意不实现。</para>
/// </remarks>
public sealed class MtkLegacyGptSnapshot
{
    internal MtkLegacyGptSnapshot(MtkLegacyStorageInfo storage, byte partitionId, GptTable table)
    {
        Storage = storage;
        PartitionId = partitionId;
        Table = table;
    }

    /// <summary>Gets the exact discovered Legacy storage inventory the table was read under.
    /// <para>获取读取该表时所依据的、精确的已发现 Legacy 存储清单。</para></summary>
    /// <value>The immutable inventory observation. <para>不可变的存储清单观测。</para></value>
    public MtkLegacyStorageInfo Storage { get; }

    /// <summary>Gets the physical region containing both GPT copies.
    /// <para>获取持有两份 GPT 副本的物理区域。</para></summary>
    /// <value>A discovered Legacy region identifier, 1 through 8 for eMMC.
    /// <para>已发现的 Legacy 区域标识符，eMMC 为 1 至 8。</para></value>
    public byte PartitionId { get; }

    /// <summary>Gets the validated partition metadata.
    /// <para>获取已校验的分区元数据。</para></summary>
    /// <value>The parsed GPT table with its metadata digest, not a live device view.
    /// <para>带元数据摘要的已解析 GPT 表，并非设备的实时视图。</para></value>
    public GptTable Table { get; }
}