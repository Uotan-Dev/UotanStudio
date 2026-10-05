using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.F2fs;

namespace UotanStudio.Mcp.Resources;

/// <summary>
/// Builds the read-only MCP resources that let a client inspect firmware containers, images and
/// filesystem entries without asking a tool to unpack anything first.
/// <para>构建只读 MCP 资源，让客户端无需先调用解包工具即可检查固件容器、镜像与文件系统条目。</para>
/// Every URI is <c>uotan://</c> with a fixed first segment naming the projection and a reserved
/// <c>{+path}</c> template variable carrying the sandbox-relative file path:
/// <para>每个 URI 都以 <c>uotan://</c> 开头，首段指明投影类型，保留的 <c>{+path}</c> 模板变量承载
/// 沙箱相对文件路径：</para>
/// <list type="bullet">
///   <item><description><c>uotan://resources/overview</c> — static document describing the sandbox and
///   the readable templates. <para>描述沙箱与可读模板的静态文档。</para></description></item>
///   <item><description><c>uotan://container/{+path}/entries</c> — inner partition or ZIP member names
///   of a super image, OTA payload or ZIP container. <para>super 镜像、OTA payload 或 ZIP 容器的内部分区
///   或包内成员名。</para></description></item>
///   <item><description><c>uotan://image/{+path}/metadata</c> — format metadata from the registered
///   handler that claims the file. <para>来自认领该文件的已注册处理器的格式元数据。</para></description></item>
///   <item><description><c>uotan://filesystem/{+path}/entries</c> — entry listing of an EXT4, EROFS or
///   F2FS image, which is how a client sees filesystem entries without an unpack.
///   <para>EXT4、EROFS 或 F2FS 镜像的条目清单，客户端由此在不解包时看到文件系统条目。</para></description></item>
/// </list>
/// <c>{+path}</c> is RFC 6570 reserved expansion, so a multi-segment path such as
/// <c>uotan://container/images/system.img/entries</c> binds as a single value. Percent-encoding is
/// decoded by the MCP server before the value reaches a handler, so <c>%2e%2e</c> becomes <c>..</c>
/// and is then rejected by <see cref="PathSandbox"/> exactly like a literal traversal.
/// <para><c>{+path}</c> 为 RFC 6570 保留扩展，因此 <c>uotan://container/images/system.img/entries</c>
/// 这类多段路径会绑定为单个值。百分号编码会在值到达处理器之前由 MCP 服务器解码，因此 <c>%2e%2e</c>
/// 会还原为 <c>..</c>，并像字面目录穿越一样被 <see cref="PathSandbox"/> 拒绝。</para>
/// Registration is explicit through <see cref="McpServerResource.Create(Delegate, McpServerResourceCreateOptions)"/>
/// rather than attribute reflection, matching the tool factories and keeping the assembly trim and
/// Native-AOT safe.
/// <para>注册通过 <see cref="McpServerResource.Create(Delegate, McpServerResourceCreateOptions)"/> 显式完成
/// 而非特性反射，与工具工厂一致并保持程序集的裁剪与 Native AOT 安全。</para>
/// </summary>
internal static class ContainerResourceFactory
{
    /// <summary>Scheme every resource URI uses. <para>所有资源 URI 使用的协议名。</para></summary>
    /// <value>The constant <c>uotan</c>. <para>常量 <c>uotan</c>。</para></value>
    internal const string Scheme = "uotan";

    /// <summary>URI of the static overview resource listed by <c>resources/list</c>.
    /// <para>由 <c>resources/list</c> 列出的静态概览资源 URI。</para></summary>
    /// <value>The constant <c>uotan://resources/overview</c>. <para>常量 <c>uotan://resources/overview</c>。</para></value>
    internal const string OverviewUri = Scheme + "://resources/overview";

    /// <summary>Template listing inner entries of a super, payload or ZIP container.
    /// <para>列出 super、payload 或 ZIP 容器内部条目的模板。</para></summary>
    /// <value>The constant <c>uotan://container/{+path}/entries</c>. <para>常量 <c>uotan://container/{+path}/entries</c>。</para></value>
    internal const string ContainerUriTemplate = Scheme + "://container/{+path}/entries";

    /// <summary>Template exposing the detected format handler's metadata for one file.
    /// <para>输出单个文件所匹配格式处理器元数据的模板。</para></summary>
    /// <value>The constant <c>uotan://image/{+path}/metadata</c>. <para>常量 <c>uotan://image/{+path}/metadata</c>。</para></value>
    internal const string ImageUriTemplate = Scheme + "://image/{+path}/metadata";

    /// <summary>Template listing entries inside an EXT4, EROFS or F2FS image.
    /// <para>列出 EXT4、EROFS 或 F2FS 镜像内部条目的模板。</para></summary>
    /// <value>The constant <c>uotan://filesystem/{+path}/entries</c>. <para>常量 <c>uotan://filesystem/{+path}/entries</c>。</para></value>
    internal const string FilesystemUriTemplate = Scheme + "://filesystem/{+path}/entries";

    /// <summary>Upper bound on container entries returned by one read.
    /// <para>单次读取返回的容器条目上限。</para></summary>
    /// <value><c>4096</c>; larger listings are truncated and flagged. <para><c>4096</c>；超出部分会被截断并标记。</para></value>
    internal const int MaximumContainerEntries = 4096;

    /// <summary>Upper bound on filesystem entries returned by one read.
    /// <para>单次读取返回的文件系统条目上限。</para></summary>
    /// <value><c>1000</c>; larger listings are truncated and flagged. <para><c>1000</c>；超出部分会被截断并标记。</para></value>
    internal const int MaximumFilesystemEntries = 1000;

    /// <summary>Upper bound on metadata detail lines and components returned by one read.
    /// <para>单次读取返回的元数据明细行与组件上限。</para></summary>
    /// <value><c>1000</c>. <para><c>1000</c>。</para></value>
    internal const int MaximumMetadataEntries = 1000;

    /// <summary>Upper bound on metadata key/value pairs returned by one read.
    /// <para>单次读取返回的元数据键值对上限。</para></summary>
    /// <value><c>256</c>. <para><c>256</c>。</para></value>
    internal const int MaximumMetadataFields = 256;

    /// <summary>Upper bound on any single untrusted string copied into a resource document.
    /// <para>写入资源文档的每个不可信字符串的长度上限。</para></summary>
    /// <value><c>512</c> characters. <para><c>512</c> 个字符。</para></value>
    internal const int MaximumTextCharacters = 512;

    /// <summary>Upper bound on a URI-supplied path before it is handed to the sandbox.
    /// <para>URI 提供的路径交给沙箱前的长度上限。</para></summary>
    /// <value><c>4096</c> characters. <para><c>4096</c> 个字符。</para></value>
    internal const int MaximumPathCharacters = 4096;

    /// <summary>Directory entries the EXT4 and EROFS walkers may visit for one read.
    /// <para>单次读取中 EXT4 与 EROFS 遍历器可访问的目录条目预算。</para></summary>
    /// <value><c>100000</c> entries; a busier image fails with an explicit budget message instead of
    /// growing without bound. <para><c>100000</c> 条；更繁忙的镜像会以明确的预算错误失败，而不是无限增长。</para></value>
    internal const int FilesystemEntryBudget = 100_000;

    /// <summary>Summed path characters the EXT4 and EROFS walkers may accumulate for one read.
    /// <para>单次读取中 EXT4 与 EROFS 遍历器可累计的路径字符预算。</para></summary>
    /// <value><c>12000000</c> characters. <para><c>12000000</c> 个字符。</para></value>
    internal const long FilesystemPathCharacterBudget = 12_000_000;

    /// <summary>MIME type reported for every resource document.
    /// <para>每个资源文档上报的 MIME 类型。</para></summary>
    /// <value>The constant <c>application/json</c>. <para>常量 <c>application/json</c>。</para></value>
    internal const string JsonMimeType = "application/json";

    /// <summary>
    /// Builds the resource collection for one sandbox.
    /// <para>为单个沙箱构建资源集合。</para>
    /// </summary>
    /// <param name="sandbox">Confinement shared with the file-touching tools; must not be <see langword="null"/>.
    /// <para>与文件类工具共用的路径限制；不得为 <see langword="null"/>。</para></param>
    /// <returns>The overview resource plus the container, image and filesystem templates, all confined to
    /// <paramref name="sandbox"/>. <para>概览资源与容器、镜像、文件系统模板，全部限制在 <paramref name="sandbox"/> 内。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="sandbox"/> is <see langword="null"/>.
    /// <para><paramref name="sandbox"/> 为 <see langword="null"/>。</para></exception>
    internal static IReadOnlyList<McpServerResource> CreateResources(PathSandbox sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);

        var overview = McpServerResource.Create(
            (CancellationToken cancellationToken) => RunGuardedAsync(
                token => ReadOverviewAsync(sandbox, token), cancellationToken),
            new McpServerResourceCreateOptions
            {
                Name = "sandbox-overview",
                UriTemplate = OverviewUri,
                MimeType = JsonMimeType,
                Description = "Describe the MCP path sandbox and list the readable uotan:// resource "
                    + "templates, including the entry and metadata caps applied to every read."
            });

        var containerEntries = McpServerResource.Create(
            ([Description("Sandbox-relative or sandbox-contained absolute container path.")] string path,
             CancellationToken cancellationToken) => RunGuardedAsync(
                token => ReadContainerEntriesAsync(sandbox, path, token), cancellationToken),
            new McpServerResourceCreateOptions
            {
                Name = "container-entries",
                UriTemplate = ContainerUriTemplate,
                MimeType = JsonMimeType,
                Description = "List the inner partitions of a super image or an OTA payload, or the image "
                    + "members of a ZIP (plus its embedded payload partitions), without extracting anything. "
                    + "Pass the reported names to extract_container_entries to unpack selected entries."
            });

        var imageMetadata = McpServerResource.Create(
            ([Description("Sandbox-relative or sandbox-contained absolute firmware file path.")] string path,
             CancellationToken cancellationToken) => RunGuardedAsync(
                token => ReadImageMetadataAsync(sandbox, path, token), cancellationToken),
            new McpServerResourceCreateOptions
            {
                Name = "image-metadata",
                UriTemplate = ImageUriTemplate,
                MimeType = JsonMimeType,
                Description = "Read the metadata the registered firmware format handler reports for a file "
                    + "(header geometry, UUID, feature flags, partition or component lines) without unpacking it."
            });

        var filesystemEntries = McpServerResource.Create(
            ([Description("Sandbox-relative or sandbox-contained absolute filesystem image path.")] string path,
             CancellationToken cancellationToken) => RunGuardedAsync(
                token => ReadFilesystemEntriesAsync(sandbox, path, token), cancellationToken),
            new McpServerResourceCreateOptions
            {
                Name = "filesystem-entries",
                UriTemplate = FilesystemUriTemplate,
                MimeType = JsonMimeType,
                Description = "Walk an EXT4, EROFS or F2FS image and list its directories and files with "
                    + "size, inode and type, without extracting the image. A filesystem nested in a super "
                    + "image or payload must be extracted first."
            });

        return [overview, containerEntries, imageMetadata, filesystemEntries];
    }

    /// <summary>
    /// Renders the static overview document: sandbox confinement plus the readable templates.
    /// <para>渲染静态概览文档：沙箱限制范围与可读模板。</para>
    /// </summary>
    /// <param name="sandbox">Sandbox described by the document. <para>文档所描述的沙箱。</para></param>
    /// <returns>Camel-case JSON text. <para>小驼峰 JSON 文本。</para></returns>
    internal static string RenderOverview(PathSandbox sandbox)
    {
        ResourceSchemeDescriptor[] resources =
        [
            new(OverviewUri, "Sandbox confinement, caps and the readable resource templates."),
            new(ContainerUriTemplate, "Inner partition or ZIP member names of a container."),
            new(ImageUriTemplate, "Format metadata reported by the handler that claims the file."),
            new(FilesystemUriTemplate, "Directory and file entries inside an EXT4, EROFS or F2FS image.")
        ];

        var version = typeof(McpServerHost).Assembly.GetName().Version?.ToString(3);
        var document = new SandboxOverviewDocument(
            version is { Length: > 0 } ? "uotanstudio/" + version : "uotanstudio",
            sandbox.Root,
            sandbox.IsUnrestricted,
            MaximumContainerEntries,
            MaximumFilesystemEntries,
            MaximumMetadataEntries,
            resources);

        return JsonSerializer.Serialize(document, McpResourceJsonContext.Default.SandboxOverviewDocument);
    }

    /// <summary>
    /// <para>读取静态概览文档；内容完全来自沙箱配置，因此只在进入前检查一次取消。</para>
    /// Reads the static overview document; its content comes entirely from the sandbox configuration, so
    /// cancellation is observed once before the text is rendered.
    /// </summary>
    /// <param name="sandbox"><para>要描述的沙箱。</para>The sandbox to describe.</param>
    /// <param name="cancellationToken"><para>本次读取的取消令牌。</para>Cancellation token for this read.</param>
    /// <returns><para>已渲染的 JSON 文本。</para>The rendered JSON text.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    internal static Task<string> ReadOverviewAsync(PathSandbox sandbox, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RenderOverview(sandbox));
    }

    /// <summary>
    /// Reads a container projection: header-level structure parsing only, never extraction.
    /// <para>读取容器投影：仅做头部级别的结构解析，绝不提取数据。</para>
    /// </summary>
    /// <param name="sandbox">Confinement for <paramref name="path"/>. <para><paramref name="path"/> 的限制范围。</para></param>
    /// <param name="path">Template-supplied container path. <para>模板给出的容器路径。</para></param>
    /// <param name="cancellationToken">Token bound by the MCP server for this read.
    /// <para>MCP 服务器为该次读取绑定的令牌。</para></param>
    /// <returns>JSON text listing the inner entries up to <see cref="MaximumContainerEntries"/>.
    /// <para>列出至多 <see cref="MaximumContainerEntries"/> 个内部条目的 JSON 文本。</para></returns>
    /// <exception cref="McpProtocolException">The path escapes the sandbox, does not exist, or is not a
    /// super, payload or ZIP container. <para>路径逃逸沙箱、不存在，或不是 super、payload、ZIP 容器时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    internal static async Task<string> ReadContainerEntriesAsync(
        PathSandbox sandbox, string path, CancellationToken cancellationToken)
    {
        FirmwareFormatRegistry.RegisterAll();
        var resolved = sandbox.Resolve(RequireCandidatePath(path), mustExist: true);
        var displayPath = ToDisplayPath(sandbox, resolved);
        cancellationToken.ThrowIfCancellationRequested();
        var structure = await Task.Run(
            () => ContainerStructureAnalyzer.Analyze(resolved), cancellationToken).ConfigureAwait(false);
        if (structure.Kind == ContainerKind.None)
        {
            throw InvalidParams($"'{displayPath}' is not a super image, OTA payload or ZIP container.");
        }

        var total = structure.Entries.Count;
        var returned = Math.Min(total, MaximumContainerEntries);
        var entries = new List<ContainerEntryRecord>(returned);
        for (var index = 0; index < returned; index++)
        {
            var entry = structure.Entries[index];
            entries.Add(new ContainerEntryRecord(
                Truncate(entry.Name, MaximumTextCharacters),
                Truncate(entry.Format, MaximumTextCharacters),
                entry.SizeBytes > 0 ? entry.SizeBytes : 0,
                entry.FromPayload));
        }

        var document = new ContainerEntriesDocument(
            BuildUri("container", displayPath, "entries"),
            displayPath,
            structure.Kind.ToString(),
            total,
            returned,
            total > returned,
            entries);
        return JsonSerializer.Serialize(document, McpResourceJsonContext.Default.ContainerEntriesDocument);
    }

    /// <summary>
    /// Reads the image projection through the registered format handler, reusing its bounded metadata
    /// reader instead of duplicating any format probing.
    /// <para>经已注册格式处理器读取镜像投影，复用其有界元数据读取器而不重复任何格式探测。</para>
    /// </summary>
    /// <param name="sandbox">Confinement for <paramref name="path"/>. <para><paramref name="path"/> 的限制范围。</para></param>
    /// <param name="path">Template-supplied file path. <para>模板给出的文件路径。</para></param>
    /// <param name="cancellationToken">Token bound by the MCP server for this read.
    /// <para>MCP 服务器为该次读取绑定的令牌。</para></param>
    /// <returns>JSON text with the handler summary, capped detail lines and capped metadata fields.
    /// <para>含处理器摘要、有界明细行与有界键值字段的 JSON 文本。</para></returns>
    /// <exception cref="McpProtocolException">The path escapes the sandbox, does not exist, no handler
    /// claims it, or the handler could not read it. <para>路径逃逸沙箱、不存在、无处理器认领，或处理器无法读取时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    internal static async Task<string> ReadImageMetadataAsync(
        PathSandbox sandbox, string path, CancellationToken cancellationToken)
    {
        FirmwareFormatRegistry.RegisterAll();
        var resolved = sandbox.Resolve(RequireCandidatePath(path), mustExist: true);
        var displayPath = ToDisplayPath(sandbox, resolved);
        var handler = FormatDetector.Detect(resolved);
        if (handler is null)
        {
            throw InvalidParams($"No registered firmware format handler can process '{displayPath}'.");
        }

        var result = await handler.ReadMetadataAsync(resolved, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new McpProtocolException(
                $"The {handler.FormatName} handler could not read '{displayPath}': {Truncate(result.Summary, MaximumTextCharacters)}",
                McpErrorCode.InternalError);
        }

        var entryTotal = result.Entries.Count;
        var entryReturned = Math.Min(entryTotal, MaximumMetadataEntries);
        var entries = new List<string>(entryReturned);
        for (var index = 0; index < entryReturned; index++)
        {
            entries.Add(Truncate(result.Entries[index], MaximumTextCharacters));
        }

        var metadata = new Dictionary<string, string>();
        foreach (var field in result.Metadata)
        {
            if (metadata.Count == MaximumMetadataFields)
            {
                break;
            }

            metadata[Truncate(field.Key, MaximumTextCharacters)] = Truncate(field.Value, MaximumTextCharacters);
        }

        var componentTotal = result.Components.Count;
        var componentReturned = Math.Min(componentTotal, MaximumMetadataEntries);
        var components = new List<FirmwareComponentRecord>(componentReturned);
        for (var index = 0; index < componentReturned; index++)
        {
            var component = result.Components[index];
            components.Add(new FirmwareComponentRecord(
                Truncate(component.Name, MaximumTextCharacters),
                component.SizeBytes > 0 ? component.SizeBytes : 0));
        }

        var truncated = entryTotal > entryReturned
            || componentTotal > componentReturned
            || result.Metadata.Count > metadata.Count;
        var document = new ImageMetadataDocument(
            BuildUri("image", displayPath, "metadata"),
            displayPath,
            handler.FormatName,
            Truncate(result.Summary, MaximumTextCharacters),
            entryTotal,
            entryReturned,
            truncated,
            entries,
            metadata,
            components);
        return JsonSerializer.Serialize(document, McpResourceJsonContext.Default.ImageMetadataDocument);
    }

    /// <summary>
    /// Reads the filesystem projection: a cancellable, budget-bounded directory walk over the image
    /// itself, so an agent can list EROFS, EXT4 and F2FS contents without an unpack.
    /// <para>读取文件系统投影：对镜像本身执行可取消、受预算限制的目录遍历，使智能体无需解包即可列出
    /// EROFS、EXT4 与 F2FS 内容。</para>
    /// EXT4 and EROFS walks honour <see cref="FilesystemEntryBudget"/> and
    /// <see cref="FilesystemPathCharacterBudget"/>; the F2FS parser exposes no budget knob, so its walk
    /// relies on that parser's own structural limits and only the returned list is capped here.
    /// <para>EXT4 与 EROFS 遍历遵守 <see cref="FilesystemEntryBudget"/> 与
    /// <see cref="FilesystemPathCharacterBudget"/>；F2FS 解析器没有预算参数，其遍历依赖解析器自身的结构限制，
    /// 此处只限制返回清单的长度。</para>
    /// An EROFS image with external devices is walked through its primary device stream only, so
    /// content stored on the extra devices is not listed.
    /// <para>含外部设备的 EROFS 镜像仅按主设备流遍历，因此额外设备上的内容不会列出。</para>
    /// </summary>
    /// <param name="sandbox">Confinement for <paramref name="path"/>. <para><paramref name="path"/> 的限制范围。</para></param>
    /// <param name="path">Template-supplied image path. <para>模板给出的镜像路径。</para></param>
    /// <param name="cancellationToken">Token bound by the MCP server for this read.
    /// <para>MCP 服务器为该次读取绑定的令牌。</para></param>
    /// <returns>JSON text listing entries up to <see cref="MaximumFilesystemEntries"/>.
    /// <para>列出至多 <see cref="MaximumFilesystemEntries"/> 个条目的 JSON 文本。</para></returns>
    /// <exception cref="McpProtocolException">The path escapes the sandbox, does not exist, or is not an
    /// EXT4, EROFS or F2FS image. <para>路径逃逸沙箱、不存在，或不是 EXT4、EROFS、F2FS 镜像时抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    internal static async Task<string> ReadFilesystemEntriesAsync(
        PathSandbox sandbox, string path, CancellationToken cancellationToken)
    {
        FirmwareFormatRegistry.RegisterAll();
        var resolved = sandbox.Resolve(RequireCandidatePath(path), mustExist: true);
        var displayPath = ToDisplayPath(sandbox, resolved);
        cancellationToken.ThrowIfCancellationRequested();
        var (filesystem, total, entries) = await Task.Run(
            () => WalkFilesystem(resolved, displayPath, cancellationToken), cancellationToken).ConfigureAwait(false);

        var returned = entries.Count;
        var document = new FilesystemEntriesDocument(
            BuildUri("filesystem", displayPath, "entries"),
            displayPath,
            filesystem,
            total,
            returned,
            total > returned,
            entries);
        return JsonSerializer.Serialize(document, McpResourceJsonContext.Default.FilesystemEntriesDocument);
    }

    /// <summary>
    /// Selects the filesystem parser through the shared format registry and returns the capped listing.
    /// <para>经共享格式注册表选择文件系统解析器，并返回有上限的清单。</para>
    /// </summary>
    /// <param name="resolved"><para>已解析并确认存在的绝对镜像路径。</para>Resolved absolute image path already confined and checked.</param>
    /// <param name="displayPath"><para>用于错误消息的沙箱相对展示路径。</para>Sandbox-relative path rendered in errors.</param>
    /// <param name="cancellationToken"><para>本次读取的取消令牌；EXT4 与 EROFS 遍历会观察它，F2FS
    /// 解析器不接受令牌，因此其遍历只能在完成后报告取消。</para>Cancellation token for this read; the EXT4 and EROFS
    /// walks observe it, while the F2FS parser accepts no token, so its walk can only report cancellation after finishing.</param>
    /// <returns><para>格式名、遍历得到的条目总数与截断后的清单。</para>The format name, the walked entry count and the capped listing.</returns>
    private static (string Filesystem, int Total, IReadOnlyList<FilesystemEntryRecord> Entries) WalkFilesystem(
        string resolved, string displayPath, CancellationToken cancellationToken)
    {
        var handler = FormatDetector.Detect(resolved);
        return handler switch
        {
            Ext4FormatHandler => WalkExt4(resolved, cancellationToken),
            ErofsFormatHandler => WalkErofs(resolved, cancellationToken),
            F2fsFormatHandler => WalkF2fs(resolved),
            _ => throw InvalidParams(
                $"'{displayPath}' is not an EXT4, EROFS or F2FS image"
                + (handler is null
                    ? "; no registered format handler claims it."
                    : $"; the detected format is {handler.FormatName}."))
        };
    }

    private static (string Filesystem, int Total, IReadOnlyList<FilesystemEntryRecord> Entries) WalkExt4(
        string resolved, CancellationToken cancellationToken)
    {
        using var parser = new Ext4Parser(
            OpenImage(resolved), FilesystemEntryBudget, FilesystemPathCharacterBudget, cancellationToken);
        var walked = parser.ListEntriesWithRoot();
        var records = new List<FilesystemEntryRecord>(Math.Min(walked.Count, MaximumFilesystemEntries));
        foreach (var entry in walked)
        {
            if (records.Count == MaximumFilesystemEntries)
            {
                break;
            }

            records.Add(new FilesystemEntryRecord(
                Truncate(entry.Path, MaximumTextCharacters),
                entry.Size > 0 ? entry.Size : 0,
                entry.Kind switch
                {
                    Ext4Parser.Ext4EntryKind.Directory => "dir",
                    Ext4Parser.Ext4EntryKind.Symlink => "symlink",
                    _ => "file"
                },
                entry.Inode,
                TruncateToNull(entry.SymlinkTarget, MaximumTextCharacters)));
        }

        return ("EXT4", walked.Count, records);
    }

    private static (string Filesystem, int Total, IReadOnlyList<FilesystemEntryRecord> Entries) WalkErofs(
        string resolved, CancellationToken cancellationToken)
    {
        using var image = OpenImage(resolved);
        using var parser = new ErofsParser(
            image, FilesystemEntryBudget, FilesystemPathCharacterBudget, cancellationToken);
        var walked = parser.EnumerateEntries();
        var records = new List<FilesystemEntryRecord>(Math.Min(walked.Count, MaximumFilesystemEntries));
        foreach (var entry in walked)
        {
            if (records.Count == MaximumFilesystemEntries)
            {
                break;
            }

            records.Add(new FilesystemEntryRecord(
                Truncate(entry.Path, MaximumTextCharacters),
                entry.Size > 0 ? entry.Size : 0,
                ErofsKind(entry),
                entry.Nid,
                null));
        }

        return ("EROFS", walked.Count, records);
    }

    private static (string Filesystem, int Total, IReadOnlyList<FilesystemEntryRecord> Entries) WalkF2fs(string resolved)
    {
        using var parser = new F2fsParser(OpenImage(resolved));
        var walked = parser.ListEntriesWithRoot();
        var records = new List<FilesystemEntryRecord>(Math.Min(walked.Count, MaximumFilesystemEntries));
        foreach (var entry in walked)
        {
            if (records.Count == MaximumFilesystemEntries)
            {
                break;
            }

            records.Add(new FilesystemEntryRecord(
                Truncate(entry.Path, MaximumTextCharacters),
                entry.Size > 0 ? entry.Size : 0,
                entry.Kind switch
                {
                    F2fsParser.F2fsEntryKind.Directory => "dir",
                    F2fsParser.F2fsEntryKind.Symlink => "symlink",
                    _ => "file"
                },
                entry.Inode,
                TruncateToNull(entry.SymlinkTarget, MaximumTextCharacters)));
        }

        return ("F2FS", walked.Count, records);
    }

    /// <summary>
    /// <para>把 EROFS inode 模式位翻译为清单里的稳定类型名，避免枚举名出现在对外文档中。</para>
    /// Translates the EROFS inode mode bits into the stable kind names used by the listing, keeping
    /// enum spelling out of the published document.
    /// </summary>
    /// <param name="entry"><para>待分类的 EROFS 条目。</para>The EROFS entry to classify.</param>
    /// <returns><para><c>dir</c>、<c>symlink</c>、<c>special</c> 或 <c>file</c>。</para>One of <c>dir</c>, <c>symlink</c>, <c>special</c> or <c>file</c>.</returns>
    private static string ErofsKind(ErofsFileEntry entry)
    {
        if (entry.IsDirectory)
        {
            return "dir";
        }

        if (entry.IsSymlink)
        {
            return "symlink";
        }

        return entry.IsSpecialFile ? "special" : "file";
    }

    /// <summary>
    /// Opens an image file read-only and shared, matching what the format handlers use.
    /// <para>以只读共享方式打开镜像文件，与格式处理器的打开方式一致。</para>
    /// </summary>
    /// <param name="resolved"><para>已解析的绝对路径。</para>The resolved absolute path.</param>
    /// <returns><para>供解析器读取的文件流。</para>A stream for the parser to read.</returns>
    private static FileStream OpenImage(string resolved) =>
        new(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>
    /// Bounds and sanity-checks the path segment the client supplied in the URI before any filesystem
    /// call is made; containment itself is delegated to <see cref="PathSandbox.Resolve"/>.
    /// <para>在任何文件系统调用前限制并校验客户端在 URI 中给出的路径段；包含性检查本身交给
    /// <see cref="PathSandbox.Resolve"/>。</para>
    /// </summary>
    /// <param name="path"><para>已由 MCP 服务器完成百分号解码的模板变量值。</para>The template variable value already percent-decoded by the MCP server.</param>
    /// <returns><para>校验通过的路径原值。</para>The validated path unchanged.</returns>
    private static string RequireCandidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > MaximumPathCharacters)
        {
            throw InvalidParams($"The resource path exceeds the {MaximumPathCharacters}-character budget.");
        }

        if (path.Contains('\0'))
        {
            throw InvalidParams("The resource path contains a NUL character.");
        }

        return path;
    }

    /// <summary>
    /// Builds the canonical, re-readable URI for a resolved path by escaping each segment.
    /// <para>通过逐段转义为已解析路径构造可再次读取的规范 URI。</para>
    /// </summary>
    /// <param name="projection"><para>URI 首段：container、image 或 filesystem。</para>The URI's first segment: container, image or filesystem.</param>
    /// <param name="displayPath"><para>以 '/' 分隔的沙箱相对路径。</para>The '/' separated sandbox-relative path.</param>
    /// <param name="suffix"><para>URI 末尾的投影名。</para>The projection name closing the URI.</param>
    /// <returns><para>可原样用于 <c>resources/read</c> 的 URI。</para>A URI usable verbatim for <c>resources/read</c>.</returns>
    private static string BuildUri(string projection, string displayPath, string suffix)
    {
        var segments = displayPath.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            segments[index] = Uri.EscapeDataString(segments[index]);
        }

        return Scheme + "://" + projection + "/" + string.Join('/', segments) + "/" + suffix;
    }

    /// <summary>
    /// Renders the resolved path the way the client addressed it: sandbox-relative when confined, full
    /// when the sandbox is unrestricted.
    /// <para>按客户端寻址方式渲染已解析路径：受限沙箱给出相对路径，不受限时给出完整路径。</para>
    /// </summary>
    /// <param name="sandbox"><para>本次读取的沙箱。</para>The sandbox of this read.</param>
    /// <param name="resolved"><para><see cref="PathSandbox"/> 返回的绝对路径。</para>The absolute path returned by <see cref="PathSandbox"/>.</param>
    /// <returns><para>用于文档与错误消息的 '/' 分隔路径。</para>A '/' separated path for documents and error messages.</returns>
    private static string ToDisplayPath(PathSandbox sandbox, string resolved)
    {
        var root = sandbox.Root;
        if (root is null)
        {
            return NormalizeSeparators(resolved);
        }

        var relative = NormalizeSeparators(Path.GetRelativePath(root, resolved));
        return relative == ".."
            || relative.StartsWith("../", StringComparison.Ordinal)
            || relative.Contains("/../", StringComparison.Ordinal)
            ? NormalizeSeparators(resolved)
            : relative;
    }

    private static string NormalizeSeparators(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    /// <summary>
    /// Copies a possibly over-long untrusted string into the bounded document form.
    /// <para>把可能过长的不可信字符串复制为有界文档形式。</para>
    /// </summary>
    /// <param name="value"><para>来自镜像、归档或文件名的原始文本，可能为 <see langword="null"/>。</para>Raw text from an image, archive or file name, possibly <see langword="null"/>.</param>
    /// <param name="maximumCharacters"><para>保留的最大字符数。</para>Maximum characters kept.</param>
    /// <returns><para>截断后的文本；<paramref name="value"/> 为空时返回空字符串。</para>The truncated text; empty when <paramref name="value"/> is null or empty.</returns>
    private static string Truncate(string? value, int maximumCharacters) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    /// <summary>
    /// <para>按上限裁剪可选文本，并在没有内容时返回 <see langword="null"/>，让 JSON 省略该字段而不是写入空串。</para>
    /// Trims optional text to the cap and returns <see langword="null"/> when there is no content, so the
    /// JSON omits the field instead of publishing an empty string.
    /// </summary>
    /// <param name="value"><para>待裁剪的可选文本。</para>The optional text to trim.</param>
    /// <param name="maximumCharacters"><para>保留的最大字符数。</para>Maximum characters kept.</param>
    /// <returns><para>裁剪后的文本，或 <see langword="null"/>。</para>The trimmed text, or <see langword="null"/>.</returns>
    private static string? TruncateToNull(string? value, int maximumCharacters) =>
        string.IsNullOrEmpty(value)
            ? null
            : value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    private static McpProtocolException InvalidParams(string message) => new(message, McpErrorCode.InvalidParams);

    /// <summary>
    /// <para>运行资源读取主体，并把沙箱与格式失败映射为携带原始消息的
    /// <see cref="McpProtocolException"/>；SDK 会替换其他异常类型在线上的消息文本。</para>
    /// Runs one resource read and maps sandbox and format failures onto an
    /// <see cref="McpProtocolException"/> that keeps the original message, because the SDK replaces the
    /// wire message of any other exception type.
    /// </summary>
    /// <param name="body">Read implementation receiving the request's cancellation token.
    /// <para>接收本次请求取消令牌的读取实现。</para></param>
    /// <param name="cancellationToken">Token bound by the MCP server for this read.
    /// <para>MCP 服务器为该次读取绑定的令牌。</para></param>
    /// <returns>The JSON text served as the resource contents. <para>作为资源内容发布的 JSON 文本。</para></returns>
    /// <exception cref="OperationCanceledException">The request was cancelled. <para>请求被取消时抛出。</para></exception>
    /// <exception cref="McpProtocolException">The read failed. <para>读取失败时抛出。</para></exception>
    private static async Task<string> RunGuardedAsync(
        Func<CancellationToken, Task<string>> body,
        CancellationToken cancellationToken)
    {
        try
        {
            return await body(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new McpProtocolException(
                $"{exception.GetType().Name}: {Truncate(exception.Message, MaximumTextCharacters)}",
                exception,
                exception is UnauthorizedAccessException or ArgumentException or FileNotFoundException
                    ? McpErrorCode.InvalidParams
                    : McpErrorCode.InternalError);
        }
    }
}