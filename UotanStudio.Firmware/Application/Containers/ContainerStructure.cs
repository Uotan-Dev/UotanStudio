namespace UotanStudio.Firmware.Application.Containers;

/// <summary>
/// <para>容器类型：无 / super / payload.bin / ZIP 归档。</para>
/// Container kind: none / super / payload.bin / ZIP archive.
/// </summary>
public enum ContainerKind
{
    /// <summary><para>不是可分析的容器。</para>Not an analyzable container.</summary>
    None = 0,

    /// <summary><para>Android super 镜像（LP 元数据）。</para>Android super image (LP metadata).</summary>
    Super,

    /// <summary><para>Android OTA payload（payload.bin 或 ZIP 包裹）。</para>Android OTA payload (payload.bin or ZIP-wrapped).</summary>
    Payload,

    /// <summary><para>ZIP 归档，可包含 Payload 分区。</para>ZIP archive, optionally containing Payload partitions.</summary>
    Zip
}

/// <summary>
/// <para>容器内部条目：分区名或 ZIP 内文件，供新建项目列表勾选。</para>
/// A container inner entry: a partition name or a ZIP member, selectable in the new-project list.
/// </summary>
/// <param name="Name">
/// <para>显示名称：super/payload 分区名或 ZIP 条目完整路径。</para>
/// Display name: the super/payload partition name or the full ZIP entry path.
/// </param>
/// <param name="Format">
/// <para>格式列显示文本（如 payload / super / 镜像扩展名）。</para>
/// Format column text (e.g. payload / super / image extension).
/// </param>
/// <param name="SizeBytes">
/// <para>条目大小（字节）；无法确定时为 0。</para>
/// Entry size in bytes; 0 when unknown.
/// </param>
/// <param name="FromPayload">
/// <para>是否来自 payload 内部分区（ZIP 容器时用于区分 ZIP 条目与 payload 分区）。</para>
/// Whether the entry is a payload partition (distinguishes ZIP members from payload partitions inside a ZIP).
/// </param>
public sealed record ContainerEntry(
    string Name,
    string Format,
    long SizeBytes,
    bool FromPayload)
{
    /// <summary>Full member path of the containing Payload inside a ZIP, when present.</summary>
    public string? PayloadMemberPath { get; init; }
}

/// <summary>
/// <para>容器结构分析结果。</para>
/// Container structure analysis result.
/// </summary>
/// <param name="Kind"><para>容器类型。</para>The container kind.</param>
/// <param name="Entries"><para>内部条目列表（按展示顺序）。</para>Inner entries in display order.</param>
public sealed record ContainerStructure(
    ContainerKind Kind,
    IReadOnlyList<ContainerEntry> Entries)
{
    /// <summary><para>空结果（非容器或解析失败）。</para>Empty result (not a container or parse failed).</summary>
    public static ContainerStructure Empty { get; } = new(ContainerKind.None, []);
}