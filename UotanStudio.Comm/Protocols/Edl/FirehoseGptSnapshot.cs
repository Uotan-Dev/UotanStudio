using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Validated GPT observation bound to one protocol instance, physical region and configured slot.
/// <para>绑定到某一协议实例、物理区域与已配置槽位的已校验 GPT 观测。</para>
/// </summary>
public sealed class FirehoseGptSnapshot
{
    internal FirehoseGptSnapshot(FirehoseProtocol owner, FirehoseStorageGeometry geometry, int slot, GptTable table)
    {
        Owner = owner;
        Geometry = geometry;
        Slot = slot;
        Table = table;
    }

    internal FirehoseProtocol Owner { get; }
    /// <summary>
    /// Gets the physical region and geometry reported for this observation.
    /// <para>获取本次观测所报告的物理区域与几何信息。</para>
    /// </summary>
    /// <value>
    /// The target-reported geometry captured while the table was read.
    /// <para>读取 GPT 期间由目标上报并被一同捕获的几何信息。</para>
    /// </value>
    public FirehoseStorageGeometry Geometry { get; }
    /// <summary>
    /// Gets the configured slot used during acquisition; zero means no slot attribute.
    /// <para>获取采集时所使用的配置槽位；零表示未附带槽位属性。</para>
    /// </summary>
    /// <value>
    /// The <see cref="FirehoseConfiguration"/> slot value in effect when the table was read.
    /// <para>读取 GPT 时生效的 <see cref="FirehoseConfiguration"/> 槽位值。</para>
    /// </value>
    public int Slot { get; }
    /// <summary>
    /// Gets both validated GPT copies represented as one table, without repair or fallback.
    /// <para>获取以单张表呈现的两份已校验 GPT 副本，不含修复或回退。</para>
    /// </summary>
    /// <value>
    /// The merged table whose primary and backup copies matched during validation.
    /// <para>主备副本在校验时完全一致、已合并的分区表。</para>
    /// </value>
    public GptTable Table { get; }
}