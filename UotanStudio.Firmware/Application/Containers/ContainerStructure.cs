namespace UotanStudio.Firmware.Application.Containers;

/// <summary>
/// Container kind: none / super / payload.bin / ZIP archive / HarmonyOS update.bin.
/// <para>容器类型：无 / super / payload.bin / ZIP 归档 / HarmonyOS update.bin。</para>
/// </summary>
public enum ContainerKind
{
    /// <summary>Not an analyzable container. <para>不是可分析的容器。</para></summary>
    None = 0,

    /// <summary>Android super image (LP metadata). <para>Android super 镜像（LP 元数据）。</para></summary>
    Super,

    /// <summary>Android OTA payload (payload.bin or ZIP-wrapped). <para>Android OTA payload（payload.bin 或 ZIP 包裹）。</para></summary>
    Payload,

    /// <summary>ZIP archive, optionally containing Payload partitions. <para>ZIP 归档，可包含 Payload 分区。</para></summary>
    Zip,

    /// <summary>HarmonyOS update.bin package (L1/L2 component table). <para>HarmonyOS update.bin 升级包（L1/L2 组件表）。</para></summary>
    UpdateBin
}

/// <summary>
/// A container inner entry: a partition name, a HarmonyOS <c>update.bin</c> component or a ZIP member,
/// selectable in the new-project list.
/// <para>容器内部条目：分区名、HarmonyOS <c>update.bin</c> 组件或 ZIP 内文件，供新建项目列表勾选。</para>
/// </summary>
/// <param name="Name">
/// Display name: the super/payload partition name, the update.bin component name or the full ZIP entry path.
/// <para>显示名称：super/payload 分区名、update.bin 组件名或 ZIP 条目完整路径。</para>
/// </param>
/// <param name="Format">
/// Format column text (e.g. payload / super / image extension / update.bin component extension).
/// <para>格式列显示文本（如 payload / super / 镜像扩展名 / update.bin 组件扩展名）。</para>
/// </param>
/// <param name="SizeBytes">
/// Entry size in bytes; 0 when unknown.
/// <para>条目大小（字节）；无法确定时为 0。</para>
/// </param>
/// <param name="FromPayload">
/// Whether the entry is a payload partition (distinguishes ZIP members from payload partitions inside a ZIP).
/// <para>是否来自 payload 内部分区（ZIP 容器时用于区分 ZIP 条目与 payload 分区）。</para>
/// </param>
public sealed record ContainerEntry(
    string Name,
    string Format,
    long SizeBytes,
    bool FromPayload)
{
    /// <summary>
    /// Full member path of the containing Payload inside a ZIP, when present.
    /// <para>当分区条目来自 ZIP 内包裹的 Payload 时，记录该 Payload 成员在包内的完整路径。</para>
    /// </summary>
    /// <value>
    /// The ZIP member path of the containing Payload entry, or <see langword="null"/>
    /// when the entry was not listed from an embedded Payload.
    /// <para>所属 Payload 成员的 ZIP 路径；条目并非来自内嵌 Payload 时为 <see langword="null"/>。</para>
    /// </value>
    public string? PayloadMemberPath { get; init; }
}

/// <summary>
/// Container structure analysis result.
/// <para>容器结构分析结果。</para>
/// </summary>
/// <param name="Kind">The container kind. <para>容器类型。</para></param>
/// <param name="Entries">Inner entries in display order. <para>内部条目列表（按展示顺序）。</para></param>
public sealed record ContainerStructure(
    ContainerKind Kind,
    IReadOnlyList<ContainerEntry> Entries)
{
    /// <summary>Empty result (not a container or parse failed). <para>空结果（非容器或解析失败）。</para></summary>
    /// <value>A shared instance with <see cref="ContainerKind.None"/> and an empty entry list.
    /// <para>共享实例，类型为 <see cref="ContainerKind.None"/> 且条目列表为空。</para></value>
    public static ContainerStructure Empty { get; } = new(ContainerKind.None, []);
}