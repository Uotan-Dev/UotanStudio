using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Edl.FlashBundle.FactoryBuild;

/// <summary>
/// One image inside a Qualcomm factory build: its partition name, file name, flavor restrictions
/// and optional disk slot.
/// <para>Qualcomm 工厂构建中的一个镜像：分区名、文件名、flavor 限制与可选磁盘槽位。</para>
/// </summary>
/// <param name="Name">The partition name. <para>分区名。</para></param>
/// <param name="FileName">The image file name inside the build. <para>构建内的镜像文件名。</para></param>
/// <param name="Flavors">Flavor tags that must be present when a flavor filter is applied; empty
/// means the image ships for every flavor. <para>应用 flavor 过滤时必须命中的 flavor 标签；空表示
/// 该镜像面向所有 flavor。</para></param>
/// <param name="Slot">The boot slot the image is bound to, if any.
/// <para>镜像绑定的启动槽位；可为空。</para></param>
public sealed record FactoryBuildImage(string Name, string FileName, IReadOnlyList<string> Flavors, string? Slot)
{
    /// <summary>
    /// Whether the image survives the given flavor filter.
    /// <para>该镜像是否通过给定 flavor 过滤。</para>
    /// </summary>
    /// <param name="flavor">The selected flavor; <see langword="null"/> keeps everything.
    /// <para>选中的 flavor；<see langword="null"/> 保留全部。</para></param>
    /// <returns><see langword="true"/> when the image ships for the flavor.
    /// <para>镜像面向该 flavor 时为 <see langword="true"/>。</para></returns>
    public bool MatchesFlavor(string? flavor) => flavor is null || Flavors.Count == 0
        || Flavors.Any(candidate => candidate.Equals(flavor, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A parsed Qualcomm factory metabuild (<c>contents.xml</c>): chip id, build id, flavor list and
/// every image with its partition binding.
/// <para>解析后的 Qualcomm 工厂元构建（<c>contents.xml</c>）：芯片号、构建号、flavor 列表与全部
/// 镜像及其分区绑定。</para>
/// </summary>
public sealed record FactoryBuildContents
{
    /// <summary>Accepted image file size bound (1 GiB), far above any single factory image.</summary>
    internal const long MaxImageFileBytes = 1024L * 1024 * 1024;
    /// <summary>Required chip id string from the metabuild header.
    /// <para>元构建头要求的芯片号字符串。</para></summary>
    public required string ChipId { get; init; }

    /// <summary>Required build id string from the metabuild header.
    /// <para>元构建头要求的构建号字符串。</para></summary>
    public required string BuildId { get; init; }

    /// <summary>Flavor names declared by the metabuild; may be empty.
    /// <para>元构建声明的 flavor 名；可为空。</para></summary>
    public IReadOnlyList<string> Flavors { get; init; } = [];

    /// <summary>All parsed images in document order. <para>按文档顺序解析的全部镜像。</para></summary>
    public IReadOnlyList<FactoryBuildImage> Images { get; init; } = [];

    /// <summary>The metabuild file the contents were parsed from.
    /// <para>解析出这些内容的元构建文件。</para></summary>
    public required string SourcePath { get; init; }

    /// <summary>
    /// Flattens the metabuild for one flavor into the ordered program/erase plan: images that carry
    /// no filename erase their partition, matching the flash-bundle semantics already used here.
    /// <para>把元构建按单个 flavor 展平成有序的刷写/擦除计划：无文件名的镜像擦除其分区，与
    /// 本库既有的 flash-bundle 语义一致。</para>
    /// </summary>
    /// <param name="flavor">The selected flavor, or <see langword="null"/> to keep every image.
    /// <para>选中的 flavor；<see langword="null"/> 保留全部镜像。</para></param>
    /// <param name="buildRoot">The directory resolving relative file names.
    /// <para>解析相对文件名的目录。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The flattened plan. <para>展平后的计划。</para></returns>
    public async Task<FactoryBuildPlan> FlattenAsync(string? flavor, string buildRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildRoot);
        if (flavor is { Length: > 0 } && !Flavors.Any(candidate => candidate.Equals(flavor, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"The metabuild does not declare the flavor '{flavor}'.", nameof(flavor));
        }

        var operations = new List<FactoryBuildOperation>();
        foreach (var image in Images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!image.MatchesFlavor(flavor))
            {
                continue;
            }

            if (image.FileName.Length == 0)
            {
                operations.Add(new FactoryBuildOperation(image.Name, null, 0, image.Slot));
                continue;
            }

            var path = Path.Combine(buildRoot, image.FileName.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new FileNotFoundException($"The metabuild image '{image.FileName}' is missing.", path);
            }

            if (info.Length > MaxImageFileBytes)
            {
                throw new InvalidDataException($"The metabuild image '{image.FileName}' exceeds the accepted size.");
            }

            operations.Add(new FactoryBuildOperation(image.Name, path, info.Length, image.Slot));
        }

        if (operations.Count == 0)
        {
            throw new InvalidDataException("The flavor filter left no image in the metabuild plan.");
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return new FactoryBuildPlan(this, flavor, operations);
    }
}

/// <summary>
/// One flattened operation: program the image file when present, erase the partition when not.
/// <para>单个展平操作：有镜像文件时刷写，无文件时擦除分区。</para>
/// </summary>
public sealed record FactoryBuildOperation(string Partition, string? ImagePath, long ImageBytes, string? Slot);

/// <summary>
/// The flattened result of one metabuild and flavor.
/// <para>一个元构建与 flavor 的展平结果。</para>
/// </summary>
public sealed record FactoryBuildPlan(
    FactoryBuildContents Contents,
    string? Flavor,
    IReadOnlyList<FactoryBuildOperation> Operations)
{
    /// <summary>Total image bytes the plan programs. <para>该计划刷写的镜像总字节数。</para></summary>
    public long TotalImageBytes => Operations.Sum(operation => operation.ImageBytes);
}

/// <summary>
/// Parser for Qualcomm factory metabuild <c>contents.xml</c> documents. The document is untrusted
/// input: parsing is bounded and every image entry is validated before it is accepted.
/// <para>Qualcomm 工厂元构建 <c>contents.xml</c> 文档解析器。文档按不可信输入处理：解析有界，
/// 每个镜像条目先校验再接受。</para>
/// </summary>
public static class FactoryBuildContentsParser
{
    private const int MaxImageEntries = 512;
    private const int MaxNameLength = 128;



    /// <summary>
    /// Parses the metabuild document at the given path.
    /// <para>解析指定路径的元构建文档。</para>
    /// </summary>
    /// <param name="path">The <c>contents.xml</c> path. <para><c>contents.xml</c> 路径。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The parsed metabuild. <para>解析后的元构建。</para></returns>
    /// <exception cref="InvalidDataException">The document is not a well-formed metabuild.
    /// <para>文档不是合法的元构建。</para></exception>
    public static Task<FactoryBuildContents> ParseAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ParseCoreAsync(path, cancellationToken);
    }

    private static async Task<FactoryBuildContents> ParseCoreAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        XDocument document;
        try
        {
            document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            throw new InvalidDataException($"'{path}' is not a well-formed metabuild document.", ex);
        }

        var root = document.Root;
        if (root is null || !root.Name.LocalName.Equals("contents", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"'{path}' does not carry a <contents> root element.");
        }

        var header = root.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals("header", StringComparison.OrdinalIgnoreCase));
        var chipId = ReadRequiredText(header, "chip_id");
        var buildId = ReadRequiredText(header, "build_id");
        var flavors = header?.Elements()
            .Where(element => element.Name.LocalName.Equals("flavor", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .ToList() ?? [];

        var images = new List<FactoryBuildImage>();
        foreach (var element in root.Elements())
        {
            if (element is null || element.Name.LocalName.Equals("header", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (images.Count >= MaxImageEntries)
            {
                throw new InvalidDataException($"'{path}' declares more than {MaxImageEntries} images.");
            }

            var name = ValidateName(element.Attribute("name")?.Value, path, "partition");
            var fileName = element.Attribute("filename")?.Value ?? string.Empty;
            if (fileName.Length > MaxNameLength)
            {
                throw new InvalidDataException($"'{path}' declares an over-long image file name.");
            }

            if (fileName.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"'{path}' declares an image file name with parent segments.");
            }

            var imageFlavors = element.Attribute("flavor")?.Value
                .Split([';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            var slot = element.Attribute("slot")?.Value;
            images.Add(new FactoryBuildImage(name, fileName, imageFlavors, string.IsNullOrEmpty(slot) ? null : slot));
        }

        return new FactoryBuildContents
        {
            ChipId = chipId,
            BuildId = buildId,
            Flavors = flavors,
            Images = images,
            SourcePath = path,
        };
    }

    private static string ReadRequiredText(XElement? parent, string localName)
    {
        var value = parent?.Elements()
            .FirstOrDefault(element => element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidDataException($"The metabuild header does not declare <{localName}>.");
        }

        return value!;
    }

    private static string ValidateName(string? value, string path, string what)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxNameLength)
        {
            throw new InvalidDataException($"'{path}' declares an invalid {what} name.");
        }

        return value;
    }
}