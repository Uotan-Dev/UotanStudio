using System.Buffers.Binary;

namespace UotanStudio.Firmware.VendorFirmware.MediaTek.Lk;

/// <summary>
/// Kind of a MediaTek LK image entry: an application/modem payload or an attached certificate.
/// <para>MediaTek LK 镜像条目的种类：应用/调制解调器负载或附加证书。</para>
/// </summary>
public enum LkImageEntryKind
{
    /// <summary>Application processor payload (for example the <c>lk</c> partition).<para>应用处理器负载（例如 <c>lk</c> 分区）。</para></summary>
    Application,

    /// <summary>Modem payload.<para>调制解调器负载。</para></summary>
    Modem,

    /// <summary>Certificate payload (<c>cert1</c>/<c>cert2</c>).<para>证书负载（<c>cert1</c>/<c>cert2</c>）。</para></summary>
    Certificate,
}

/// <summary>
/// One parsed entry of a MediaTek LK image, with its header geometry and the certificate bound to it.
/// <para>MediaTek LK 镜像的一个已解析条目，包含头部几何信息与绑定到它的证书。</para>
/// </summary>
/// <param name="Name">The 32-byte partition name, trimmed of NUL padding.<para>去除 NUL 填充后的 32 字节分区名。</para></param>
/// <param name="Kind">The entry kind derived from the image type group.<para>由镜像类型分组得出的条目种类。</para></param>
/// <param name="HeaderOffset">Absolute offset of the 512-byte header.<para>512 字节头部的绝对偏移。</para></param>
/// <param name="DataOffset">Absolute offset of the payload.<para>负载的绝对偏移。</para></param>
/// <param name="DataSize">Declared payload length in bytes.<para>声明的负载字节数。</para></param>
/// <param name="MemoryAddress">Declared load address.<para>声明的加载地址。</para></param>
/// <param name="AddressingMode">Declared addressing mode word.<para>声明的寻址模式字。</para></param>
/// <param name="IsExtended">Whether the header uses the extended 64-bit layout.<para>头部是否使用扩展的 64 位布局。</para></param>
/// <param name="HeaderVersion">Declared header version.<para>声明的头部版本。</para></param>
/// <param name="Alignment">Declared alignment.<para>声明的对齐值。</para></param>
/// <param name="IsLast">Whether the header terminates the entry chain.<para>头部是否终止条目链。</para></param>
/// <param name="CertificateName">Name of the attached certificate entry, if any.<para>附加证书条目的名称；没有时为 null。</para></param>
/// <param name="CertificateSize">Declared certificate payload length.<para>声明的证书负载字节数。</para></param>
public sealed record LkImageEntry(
    string Name,
    LkImageEntryKind Kind,
    long HeaderOffset,
    long DataOffset,
    long DataSize,
    uint MemoryAddress,
    uint AddressingMode,
    bool IsExtended,
    uint HeaderVersion,
    uint Alignment,
    bool IsLast,
    string? CertificateName,
    long CertificateSize)
{
    /// <summary>
    /// Gets whether this entry carries a certificate whose digests can be inspected.
    /// <para>获取本条目是否带有可检查摘要的证书。</para>
    /// </summary>
    public bool HasCertificate => CertificateName is not null;
}

/// <summary>
/// The parsed LK image: the ordered entry chain plus the source facts the patch view presents.
/// <para>已解析的 LK 镜像：有序条目链以及修补视图呈现的源信息。</para>
/// </summary>
public sealed class LkImage
{
    /// <summary>Initializes the image from a completed parse.<para>用已完成的解析结果初始化镜像。</para></summary>
    /// <param name="filePath">Source path.<para>源路径。</para></param>
    /// <param name="fileSize">Source length in bytes.<para>源字节长度。</para></param>
    /// <param name="entries">Entries in chain order.<para>按链顺序排列的条目。</para></param>
    /// <param name="fingerprint">Image fingerprint.<para>镜像指纹。</para></param>
    public LkImage(string filePath, long fileSize, IReadOnlyList<LkImageEntry> entries, string fingerprint)
    {
        FilePath = filePath;
        FileSize = fileSize;
        Entries = entries;
        Fingerprint = fingerprint;
    }

    /// <summary>Gets the source image path.<para>获取源镜像路径。</para></summary>
    /// <value>The full path supplied to the reader.<para>传给读取器的完整路径。</para></value>
    public string FilePath { get; }

    /// <summary>Gets the source image length in bytes.<para>获取源镜像字节长度。</para></summary>
    /// <value>The source stream length.<para>源流长度。</para></value>
    public long FileSize { get; }

    /// <summary>Gets the entries in chain order.<para>获取按链顺序排列的条目。</para></summary>
    /// <value>The immutable entry list.<para>不可变条目列表。</para></value>
    public IReadOnlyList<LkImageEntry> Entries { get; }

    /// <summary>Gets the image fingerprint: entry names, sizes and load addresses in chain order.<para>获取镜像指纹：按链顺序排列的条目名、大小与加载地址。</para></summary>
    /// <value>A stable, non-localized identifier.<para>稳定的、未本地化的标识。</para></value>
    public string Fingerprint { get; }

    /// <summary>Finds one entry by its exact partition name.<para>按精确分区名查找一个条目。</para></summary>
    /// <param name="name">Partition name to match.<para>要匹配的分区名。</para></param>
    /// <returns>The entry, or <see langword="null"/> when absent.<para>条目；不存在时为 <see langword="null"/>。</para></returns>
    public LkImageEntry? Find(string name)
    {
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }
}

/// <summary>
/// A single binary substitution applied to an LK partition payload.
/// <para>应用于 LK 分区负载的一次二进制替换。</para>
/// </summary>
/// <param name="Category">Patch category (<c>fastboot</c>, <c>dm_verity</c>, ...).<para>补丁类别（<c>fastboot</c>、<c>dm_verity</c> 等）。</para></param>
/// <param name="Needle">Hex needle that must be present exactly once.<para>必须恰好出现一次的十六进制 needle。</para></param>
/// <param name="Replacement">Hex replacement bytes with the same length as the needle.<para>与 needle 等长的十六进制替换字节。</para></param>
public sealed record LkBinaryPatch(string Category, string Needle, string Replacement);

/// <summary>
/// Options controlling an LK patch run.
/// <para>控制 LK 修补运行的选项。</para>
/// </summary>
public sealed record LkPatchOptions
{
    /// <summary>
    /// Gets or sets the patch categories to apply; empty applies every category in the default set.
    /// <para>获取或设置要应用的补丁类别；为空时应用默认集中的全部类别。</para>
    /// </summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the <c>cert2</c> digest override is applied.
    /// <para>获取或设置是否应用 <c>cert2</c> 摘要覆盖。</para>
    /// </summary>
    public bool ApplyCertBypass { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether a backup of the source image is created next to it.
    /// <para>获取或设置是否在源镜像旁创建备份。</para>
    /// </summary>
    public bool CreateBackup { get; init; } = true;
}

/// <summary>
/// One reported change of an LK patch run.
/// <para>LK 修补运行的一项已报告变更。</para>
/// </summary>
/// <param name="Category">Patch category or operation name.<para>补丁类别或操作名称。</para></param>
/// <param name="EntryName">Entry the change belongs to, or <c>-</c> for chain-level facts.<para>变更所属条目；链级事实为 <c>-</c>。</para></param>
/// <param name="Offset">Absolute image offset, or -1 for entry-level facts.<para>绝对镜像偏移；条目级事实为 -1。</para></param>
/// <param name="Detail">Human-readable, non-localized detail.<para>人类可读、未本地化的细节。</para></param>
public sealed record LkPatchChange(string Category, string EntryName, long Offset, string Detail);

/// <summary>
/// Result of an LK patch run.
/// <para>LK 修补运行的结果。</para>
/// </summary>
/// <param name="OutputPath">Path of the written patched image.<para>已写出修补镜像的路径。</para></param>
/// <param name="BackupPath">Path of the backup, when one was written.<para>已写备份的路径；未写时为 null。</para></param>
/// <param name="Changes">Reported changes.<para>已报告变更。</para></param>
public sealed record LkPatchResult(string OutputPath, string? BackupPath, IReadOnlyList<LkPatchChange> Changes);

internal static class LkImageConstants
{
    public const uint Magic = 0x58881688;

    public const uint ExtendedMagic = 0x58891689;

    public const int HeaderSize = 512;

    public const int NameLength = 32;

    public const long MaximumEntries = 64;

    public const long MaximumImageBytes = 512L * 1024 * 1024;

    public const byte GroupApplication = 0x0;

    public const byte GroupModem = 0x1;

    public const byte GroupCertificate = 0x2;

    public static uint ReadUInt32(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
}