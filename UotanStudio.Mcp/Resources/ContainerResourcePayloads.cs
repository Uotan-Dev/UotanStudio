using System.Text.Json.Serialization;

namespace UotanStudio.Mcp.Resources;

/// <summary>
/// One line of the static overview resource: a URI template and what reading it returns.
/// <para>静态概览资源中的一行：URI 模板及其读取内容。</para>
/// </summary>
/// <param name="UriTemplate"><para>客户端可直接读取或填充的 URI 模板。</para>The URI template a client reads directly or fills in.</param>
/// <param name="Purpose"><para>该资源返回的内容说明。</para>What the resource yields.</param>
internal sealed record ResourceSchemeDescriptor(string UriTemplate, string Purpose);

/// <summary>
/// Overview document served at the static resource URI: sandbox shape plus the readable templates.
/// <para>发布在静态资源 URI 上的概览文档：沙箱形态与可读模板清单。</para>
/// </summary>
/// <param name="Server"><para>服务器标识名。</para>The server identity name.</param>
/// <param name="Root"><para>沙箱根目录；不受限沙箱时为 <see langword="null"/>。</para>Sandbox root; <see langword="null"/> when unrestricted.</param>
/// <param name="Unrestricted"><para>路径是否不受根目录约束。</para>Whether paths escape the root confinement.</param>
/// <param name="MaximumContainerEntries"><para>容器条目清单的返回上限。</para>Return cap for container entry listings.</param>
/// <param name="MaximumFilesystemEntries"><para>文件系统条目清单的返回上限。</para>Return cap for filesystem entry listings.</param>
/// <param name="MaximumMetadataEntries"><para>镜像元数据明细行的返回上限。</para>Return cap for image metadata detail lines.</param>
/// <param name="Resources"><para>可读资源与资源模板清单。</para>The readable resources and resource templates.</param>
internal sealed record SandboxOverviewDocument(
    string Server,
    string? Root,
    bool Unrestricted,
    int MaximumContainerEntries,
    int MaximumFilesystemEntries,
    int MaximumMetadataEntries,
    IReadOnlyList<ResourceSchemeDescriptor> Resources);

/// <summary>
/// One inner entry of a super image, OTA payload or ZIP container.
/// <para>super 镜像、OTA payload 或 ZIP 容器中的一个内部条目。</para>
/// </summary>
/// <param name="Name"><para>分区名或 ZIP 内条目的完整路径。</para>Partition name or full ZIP member path.</param>
/// <param name="Format"><para>条目的格式列文本。</para>Format text reported for the entry.</param>
/// <param name="SizeBytes"><para>条目大小；未知时为 0。</para>Entry size in bytes; 0 when unknown.</param>
/// <param name="FromPayload"><para>该条目是否来自内嵌 payload 分区。</para>Whether the entry is an embedded payload partition.</param>
internal sealed record ContainerEntryRecord(
    string Name,
    string Format,
    long SizeBytes,
    bool FromPayload);

/// <summary>
/// Container listing served at <c>uotan://container/{path}/entries</c>.
/// <para>发布在 <c>uotan://container/{path}/entries</c> 的容器条目清单。</para>
/// </summary>
/// <param name="Uri"><para>可再次读取本清单的规范 URI。</para>Canonical URI that re-reads this listing.</param>
/// <param name="Path"><para>沙箱相对路径，使用 '/' 分隔。</para>Sandbox-relative path using '/' separators.</param>
/// <param name="Kind"><para>容器类型名（Super、Payload、Zip）。</para>Container kind name (Super, Payload, Zip).</param>
/// <param name="TotalEntries"><para>解析得到的条目总数。</para>Total entries the parser produced.</param>
/// <param name="ReturnedEntries"><para>本文档实际返回的条目数。</para>Entries actually returned in this document.</param>
/// <param name="Truncated"><para>条目清单是否被返回上限截断。</para>Whether the listing hit the return cap.</param>
/// <param name="Entries"><para>条目清单。</para>The entry listing.</param>
internal sealed record ContainerEntriesDocument(
    string Uri,
    string Path,
    string Kind,
    int TotalEntries,
    int ReturnedEntries,
    bool Truncated,
    IReadOnlyList<ContainerEntryRecord> Entries);

/// <summary>
/// One named artifact reported by a format handler's metadata read.
/// <para>格式处理器元数据读取所上报的一个已命名产物。</para>
/// </summary>
/// <param name="Name"><para>组件名称。</para>Component name.</param>
/// <param name="SizeBytes"><para>组件字节大小；未测量时为 0。</para>Component size in bytes; 0 when unmeasured.</param>
internal sealed record FirmwareComponentRecord(string Name, long SizeBytes);

/// <summary>
/// Image metadata served at <c>uotan://image/{path}/metadata</c>.
/// <para>发布在 <c>uotan://image/{path}/metadata</c> 的镜像元数据。</para>
/// </summary>
/// <param name="Uri"><para>可再次读取本文档的规范 URI。</para>Canonical URI that re-reads this document.</param>
/// <param name="Path"><para>沙箱相对路径，使用 '/' 分隔。</para>Sandbox-relative path using '/' separators.</param>
/// <param name="Format"><para>所选格式处理器的名称。</para>Name of the selected format handler.</param>
/// <param name="Summary"><para>处理器给出的一行摘要。</para>One-line summary from the handler.</param>
/// <param name="TotalEntries"><para>处理器上报的明细行总数。</para>Total detail lines the handler reported.</param>
/// <param name="ReturnedEntries"><para>本文档实际返回的明细行数。</para>Detail lines actually returned.</param>
/// <param name="Truncated"><para>明细或键值对是否触及返回上限。</para>Whether lines or key/value pairs hit a cap.</param>
/// <param name="Entries"><para>明细行（分区、条目或组件名）。</para>Detail lines (partitions, entries or component names).</param>
/// <param name="Metadata"><para>格式专属键值字段。</para>Format-specific key/value fields.</param>
/// <param name="Components"><para>处理器列出的组件。</para>Components listed by the handler.</param>
internal sealed record ImageMetadataDocument(
    string Uri,
    string Path,
    string Format,
    string Summary,
    int TotalEntries,
    int ReturnedEntries,
    bool Truncated,
    IReadOnlyList<string> Entries,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<FirmwareComponentRecord> Components);

/// <summary>
/// One filesystem image entry served at <c>uotan://filesystem/{path}/entries</c>.
/// <para>发布在 <c>uotan://filesystem/{path}/entries</c> 处的文件系统镜像条目。</para>
/// </summary>
/// <param name="Path"><para>镜像内路径，使用 '/' 分隔；根目录为 <c>.</c>。</para>Image-internal path with '/' separators; the root is <c>.</c>.</param>
/// <param name="Size"><para>条目字节大小。</para>Entry size in bytes.</param>
/// <param name="Kind"><para>条目类型：<c>dir</c>、<c>file</c>、<c>symlink</c> 或 <c>special</c>。</para>Entry kind: <c>dir</c>, <c>file</c>, <c>symlink</c> or <c>special</c>.</param>
/// <param name="Inode"><para>磁盘 inode 编号。</para>On-disk inode number.</param>
/// <param name="Target"><para>符号链接目标；未知或不适用时为 <see langword="null"/>。</para>Symlink target; <see langword="null"/> when unavailable.</param>
internal sealed record FilesystemEntryRecord(
    string Path,
    long Size,
    string Kind,
    ulong Inode,
    string? Target);

/// <summary>
/// Filesystem entry listing served at <c>uotan://filesystem/{path}/entries</c>.
/// <para>发布在 <c>uotan://filesystem/{path}/entries</c> 的文件系统条目清单。</para>
/// </summary>
/// <param name="Uri"><para>可再次读取本清单的规范 URI。</para>Canonical URI that re-reads this listing.</param>
/// <param name="Path"><para>镜像文件的沙箱相对路径。</para>Sandbox-relative path of the image file.</param>
/// <param name="Filesystem"><para>文件系统格式名（EXT4、EROFS、F2FS）。</para>Filesystem format name (EXT4, EROFS, F2FS).</param>
/// <param name="TotalEntries"><para>遍历得到的条目总数。</para>Total entries the walk produced.</param>
/// <param name="ReturnedEntries"><para>本文档实际返回的条目数。</para>Entries actually returned.</param>
/// <param name="Truncated"><para>条目清单是否被返回上限截断。</para>Whether the listing hit the return cap.</param>
/// <param name="Entries"><para>条目清单。</para>The entry listing.</param>
internal sealed record FilesystemEntriesDocument(
    string Uri,
    string Path,
    string Filesystem,
    int TotalEntries,
    int ReturnedEntries,
    bool Truncated,
    IReadOnlyList<FilesystemEntryRecord> Entries);

/// <summary>
/// Source-generated JSON contracts for every resource document, so resource text stays trimming and
/// Native-AOT safe without touching the tool context.
/// <para>全部资源文档的 source-gen JSON 契约，使资源文本在不改动工具上下文的前提下保持裁剪与 Native AOT 安全。</para>
/// </summary>
/// <remarks>
/// The tools publish their payloads through <see cref="UotanStudio.Mcp.Tools.McpToolJsonContext"/>, which
/// lives in the <c>Tools</c> folder and is owned by the tool factories. Resource documents are registered
/// here instead so the two surfaces can evolve independently; both use the same camel-case, null-skipping
/// generation options.
/// <para>工具经 <see cref="UotanStudio.Mcp.Tools.McpToolJsonContext"/> 发布负载，该上下文位于 <c>Tools</c>
/// 目录并由工具工厂持有。资源文档因此注册在本文，使两面可以独立演进；两者采用相同的小驼峰、忽略 null 的生成选项。</para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SandboxOverviewDocument))]
[JsonSerializable(typeof(ContainerEntriesDocument))]
[JsonSerializable(typeof(ImageMetadataDocument))]
[JsonSerializable(typeof(FilesystemEntriesDocument))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<FilesystemEntryRecord>))]
[JsonSerializable(typeof(List<ContainerEntryRecord>))]
[JsonSerializable(typeof(List<FirmwareComponentRecord>))]
[JsonSerializable(typeof(List<ResourceSchemeDescriptor>))]
internal sealed partial class McpResourceJsonContext : JsonSerializerContext;