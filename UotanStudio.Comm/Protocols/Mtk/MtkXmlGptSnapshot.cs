using UotanStudio.Firmware.BlockImage.Gpt;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>A validated GPT observation taken through the XML download agent.
/// <para>经 XML 下载代理取得的已校验 GPT 观测结果。</para></summary>
/// <remarks>
/// <para>It binds the table to the XML geometry and the named physical region it was read from, so a later
/// plan cannot silently address a different storage observation; XML regions are named tokens rather than the
/// numeric identifiers used by XFlash. Like the other snapshots, it does not authorize later writes and does
/// not prove the table is still current.</para>
/// <para>它把分区表绑定到读取时所用的 XML 几何信息与命名物理区域，后续计划因此不能悄悄指向另一份存储观测；
/// XML 区域使用名称标记而非 XFlash 的数值标识符。与其他快照一样，它不授权后续写入，也不能证明该表仍然最新。</para>
/// </remarks>
public sealed class MtkXmlGptSnapshot
{
    internal MtkXmlGptSnapshot(MtkXmlStorageInfo storage, string regionName, GptTable table)
    {
        Storage = storage;
        RegionName = regionName;
        Table = table;
    }

    /// <summary>Gets the exact discovered XML geometry the table was read under.
    /// <para>获取读取该表时所依据的、精确的已发现 XML 几何信息。</para></summary>
    /// <value>The immutable geometry observation. <para>不可变的几何信息观测。</para></value>
    public MtkXmlStorageInfo Storage { get; }

    /// <summary>Gets the named physical region holding both GPT copies.
    /// <para>获取持有两份 GPT 副本的命名物理区域。</para></summary>
    /// <value>An exact XML region token, such as <c>EMMC-USER</c>.
    /// <para>精确的 XML 区域标记，例如 <c>EMMC-USER</c>。</para></value>
    public string RegionName { get; }

    /// <summary>Gets the validated partition metadata.
    /// <para>获取已校验的分区元数据。</para></summary>
    /// <value>The parsed GPT table with its metadata digest, not a live device view.
    /// <para>带元数据摘要的已解析 GPT 表，并非设备的实时视图。</para></value>
    public GptTable Table { get; }
}