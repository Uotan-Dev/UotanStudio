namespace UotanStudio.Firmware.Radio.Qcn;

/// <summary>
/// Parsed content of a Qualcomm QCN NV backup file: the container file version, the mobile property
/// summary and every numbered NV item record found across all <c>NV_ITEM_ARRAY</c> streams.
/// <para>解析后的 Qualcomm QCN NV 备份文件内容：容器文件版本、移动设备属性摘要，以及所有
/// <c>NV_ITEM_ARRAY</c> 流中找到的全部编号 NV 条目记录。</para>
/// </summary>
public sealed class QcnDocument
{
    internal QcnDocument(IReadOnlyList<ushort> fileVersion, QcnMobilePropertyInfo? mobilePropertyInfo, IReadOnlyList<QcnNvItem> items, IReadOnlyList<string> nvItemArrayStreamPaths)
    {
        FileVersion = fileVersion;
        MobilePropertyInfo = mobilePropertyInfo;
        Items = items;
        NvItemArrayStreamPaths = nvItemArrayStreamPaths;
    }

    /// <summary>
    /// Contents of the <c>File_Version</c> stream (three <c>uint16</c> values, typically
    /// <c>{2, 0, 0}</c>); empty when the stream is absent.
    /// <para><c>File_Version</c> 流的内容（三个 <c>uint16</c>，通常为 <c>{2, 0, 0}</c>）；流缺失时为空。</para>
    /// </summary>
    /// <value>
    /// The file version components, or an empty list when absent.
    /// <para>文件版本分量；缺失时为空列表。</para>
    /// </value>
    public IReadOnlyList<ushort> FileVersion { get; }

    /// <summary>
    /// Parsed <c>Mobile_Property_Info</c> stream; <c>null</c> when the stream is absent or malformed.
    /// <para>解析出的 <c>Mobile_Property_Info</c> 流；流缺失或畸形时为 <c>null</c>。</para>
    /// </summary>
    /// <value>
    /// The mobile property info, or <c>null</c>.
    /// <para>移动设备属性信息；或 <c>null</c>。</para>
    /// </value>
    public QcnMobilePropertyInfo? MobilePropertyInfo { get; }

    /// <summary>
    /// All numbered NV item records in file order.
    /// <para>按文件顺序排列的全部编号 NV 条目记录。</para>
    /// </summary>
    /// <value>
    /// Read-only list of NV items.
    /// <para>NV 条目的只读列表。</para>
    /// </value>
    public IReadOnlyList<QcnNvItem> Items { get; }

    /// <summary>
    /// Full paths of the <c>NV_ITEM_ARRAY</c> streams the records were merged from; a QCN may carry
    /// records in more than one of these streams.
    /// <para>记录合并自的 <c>NV_ITEM_ARRAY</c> 流的完整路径；一个 QCN 可能同时在多个此类流中携带记录。</para>
    /// </summary>
    /// <value>
    /// Read-only list of stream paths.
    /// <para>流路径的只读列表。</para>
    /// </value>
    public IReadOnlyList<string> NvItemArrayStreamPaths { get; }
}

/// <summary>
/// Decoded <c>Mobile_Property_Info</c> stream describing the mobile model and the software/QPST
/// versions recorded when the backup was taken.
/// <para>解码后的 <c>Mobile_Property_Info</c> 流，描述机型以及备份时记录的软件与 QPST 版本。</para>
/// </summary>
public sealed class QcnMobilePropertyInfo
{
    internal QcnMobilePropertyInfo(uint efs, ushort mobileModelNo, byte versionMajor, byte versionMinor, string swVersion, string qpstVersion)
    {
        Efs = efs;
        MobileModelNo = mobileModelNo;
        VersionMajor = versionMajor;
        VersionMinor = versionMinor;
        SwVersion = swVersion;
        QpstVersion = qpstVersion;
    }

    /// <summary>
    /// Gets the EFS identifier field.
    /// <para>获取 EFS 标识字段。</para>
    /// </summary>
    /// <value>
    /// The EFS identifier value.
    /// <para>EFS 标识值。</para>
    /// </value>
    public uint Efs { get; }

    /// <summary>
    /// Gets the mobile model number.
    /// <para>获取移动设备型号编号。</para>
    /// </summary>
    /// <value>
    /// The mobile model number.
    /// <para>移动设备型号编号。</para>
    /// </value>
    public ushort MobileModelNo { get; }

    /// <summary>
    /// Gets the major version number.
    /// <para>获取主版本号。</para>
    /// </summary>
    /// <value>
    /// The major version number.
    /// <para>主版本号。</para>
    /// </value>
    public byte VersionMajor { get; }

    /// <summary>
    /// Gets the minor version number.
    /// <para>获取次版本号。</para>
    /// </summary>
    /// <value>
    /// The minor version number.
    /// <para>次版本号。</para>
    /// </value>
    public byte VersionMinor { get; }

    /// <summary>
    /// Gets the length-prefixed software version string.
    /// <para>获取长度前缀式软件版本字符串。</para>
    /// </summary>
    /// <value>
    /// The software version string.
    /// <para>软件版本字符串。</para>
    /// </value>
    public string SwVersion { get; }

    /// <summary>
    /// Gets the length-prefixed QPST application version string.
    /// <para>获取长度前缀式 QPST 应用版本字符串。</para>
    /// </summary>
    /// <value>
    /// The QPST application version string.
    /// <para>QPST 应用版本字符串。</para>
    /// </value>
    public string QpstVersion { get; }
}