using System.Collections.Frozen;

namespace UotanStudio.Firmware.Radio.Qcn;

/// <summary>
/// A curated QPST-style NV item dictionary entry: the English technical name and the QPST category.
/// <para>精选的 QPST 风格 NV 条目字典项：英文技术名称与 QPST 分类。</para>
/// </summary>
public sealed class QcnKnownItem
{
    internal QcnKnownItem(string name, string category)
    {
        Name = name;
        Category = category;
    }

    /// <summary>
    /// Gets the English item name, taken from the QPST NV dictionary.
    /// <para>获取条目英文名称，取自 QPST NV 字典。</para>
    /// </summary>
    /// <value>
    /// The English item name.
    /// <para>条目英文名称。</para>
    /// </value>
    public string Name { get; }

    /// <summary>
    /// Gets the category name from the QPST dictionary.
    /// <para>获取 QPST 字典中的分类名。</para>
    /// </summary>
    /// <value>
    /// The QPST category name.
    /// <para>QPST 分类名。</para>
    /// </value>
    public string Category { get; }
}

/// <summary>
/// Curated dictionary of well-known numbered NV items (id to English name and QPST category),
/// extracted from the QPST NV dictionary. Ids outside this table are displayed as raw numbers by
/// the consumers of this library.
/// <para>已知编号 NV 条目的精选字典（id 到英文名与 QPST 分类），提取自 QPST NV 字典。表外的 id
/// 由本库的使用方以原始数字显示。</para>
/// </summary>
public static class QcnKnownItems
{
    private static readonly FrozenDictionary<ushort, QcnKnownItem> Items = new Dictionary<ushort, QcnKnownItem>
    {
        [0] = new("ESN", "Security"),
        [1] = new("IMSI_M", "CDMA"),
        [10] = new("MIN", "CDMA"),
        [85] = new("Security Code (SPC)", "Security"),
        [447] = new("Bluetooth Device Address", "Security"),
        [457] = new("IMEI", "Security"),
        [458] = new("IMEI Checksum", "Security"),
        [550] = new("UE IMEI", "Security"),
        [551] = new("UE IMEI 2", "Security"),
        [1943] = new("MEID", "Security"),
        [1992] = new("MEID Check Digit", "Security"),
        [5153] = new("UE IMEI Software Version Number", "Security"),
        [5597] = new("ESN ME", "Security"),
        [5598] = new("MEID ME", "Security"),
        [4678] = new("WLAN MAC Address", "Security")
    }.ToFrozenDictionary();

    /// <summary>
    /// Looks up the dictionary entry of a numbered NV item.
    /// <para>查询编号 NV 条目的字典项。</para>
    /// </summary>
    /// <param name="id">
    /// <para>NV 条目 id。</para>
    /// The NV item id.
    /// </param>
    /// <param name="item">
    /// <para>命中的字典项；未命中时为 <c>null</c>。</para>
    /// The matching entry; <c>null</c> when not found.
    /// </param>
    /// <returns>
    /// <para>命中返回 <c>true</c>。</para>
    /// <c>true</c> when the id is known.
    /// </returns>
    public static bool TryGet(ushort id, out QcnKnownItem? item)
    {
        if (Items.TryGetValue(id, out var found))
        {
            item = found;
            return true;
        }

        item = null;
        return false;
    }
}