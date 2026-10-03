using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>A validated GPT observation taken through the Legacy download agent.
/// <para>经 Legacy 下载代理取得的已校验 GPT 观测结果。</para></summary>
/// <remarks>
/// <para>It binds the table to the Legacy inventory and region it was read from, so a later plan cannot
/// silently address a different storage observation. Like the XFlash snapshot, it does not authorize later
/// writes and does not prove the table is still current.</para>
/// <para>它把分区表绑定到读取时所用的 Legacy 存储清单与区域，后续计划因此不能悄悄指向另一份存储观测。与 XFlash
/// 快照一样，它不授权后续写入，也不能证明该表仍然最新。</para>
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