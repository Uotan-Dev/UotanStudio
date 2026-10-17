namespace UotanStudio.Firmware.Core;

/// <summary>
/// Metadata flag names of the four equivalence dimensions a rebuild reports separately. The names live in the
/// shared kernel so a bounded context can emit them without reaching into the application composition region,
/// while <c>UotanStudio.Firmware.Application.Equivalence.FilesystemEquivalenceReport</c> reads the same names.
/// <para>重建分别上报的四个等价维度所使用的元数据标记名。名称放在共享内核中，使限界上下文无需触达应用组合区域
/// 即可写出这些标记，而 <c>UotanStudio.Firmware.Application.Equivalence.FilesystemEquivalenceReport</c> 读取同一组名称。</para>
/// </summary>
public static class EquivalenceMetadataKeys
{
    /// <summary>Gets the flag carrying the byte-identity verdict.
    /// <para>获取承载字节一致判定的标记名。</para></summary>
    /// <value><c>EquivalentBytes</c>. <para><c>EquivalentBytes</c>。</para></value>
    public const string Byte = "EquivalentBytes";

    /// <summary>Gets the flag carrying the inode-structure verdict.
    /// <para>获取承载 inode 结构判定的标记名。</para></summary>
    /// <value><c>EquivalentStructure</c>. <para><c>EquivalentStructure</c>。</para></value>
    public const string Structure = "EquivalentStructure";

    /// <summary>Gets the flag carrying the file-content verdict.
    /// <para>获取承载文件内容判定的标记名。</para></summary>
    /// <value><c>EquivalentContent</c>. <para><c>EquivalentContent</c>。</para></value>
    public const string Content = "EquivalentContent";

    /// <summary>Gets the flag carrying the capacity verdict; the same key predates the equivalence façade and is
    /// kept for result-dictionary compatibility.
    /// <para>获取承载容量判定的标记名；该键在等价门面出现之前已存在，为兼容结果字典保持不变。</para></summary>
    /// <value><c>FitsSourceFilesystemSize</c>. <para><c>FitsSourceFilesystemSize</c>。</para></value>
    public const string Capacity = "FitsSourceFilesystemSize";
}
