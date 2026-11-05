using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Edl.Programmer;

/// <summary>
/// One image a Qualcomm multi-image programmer manifest declares: the Sahara image identifier the target will
/// request and the resolved file that answers it.
/// <para>高通多镜像 programmer 清单声明的一个镜像：目标将要请求的 Sahara 镜像标识，以及应答该请求的已解析文件。</para>
/// </summary>
/// <param name="ImageId">The Sahara <c>image_id</c> the target requests.
/// <para>目标请求的 Sahara <c>image_id</c>。</para></param>
/// <param name="Path">The resolved absolute path of the image file.
/// <para>镜像文件解析后的绝对路径。</para></param>
/// <param name="LengthBytes">The file length in bytes, validated against the per-image ceiling.
/// <para>文件字节数，已按单镜像上限校验。</para></param>
public sealed record SaharaProgrammerImage(uint ImageId, string Path, long LengthBytes);

/// <summary>
/// Reads the Qualcomm multi-image programmer manifest (<c>qsahara_device_programmer.xml</c>) into the bounded image
/// list a Sahara upload needs, so one programmer can span several image identifiers.
/// <para>读取高通多镜像 programmer 清单（<c>qsahara_device_programmer.xml</c>），把它转换成 Sahara 上传所需的
/// 有界镜像列表，使一个 programmer 可以跨多个镜像标识。</para>
/// </summary>
/// <remarks>
/// The manifest names files but carries no bytes of its own; every entry is resolved against the manifest's own
/// directory first and, like the vendor tool, may also be given as a usable absolute or working-directory path. A
/// manifest is untrusted input: the document size, element depth, element and attribute counts, image count, per-image
/// size and combined size are all bounded before any file is opened for upload.
/// <para>清单只声明文件名而不携带自身字节；每个条目先按清单所在目录解析，并且与厂商工具一样，也接受可直接使用的
/// 绝对路径或工作目录路径。清单是不可信输入：文档大小、元素深度、元素与属性数量、镜像数量、单镜像大小与合计大小
/// 都在任何文件被打开上传之前受限。</para>
/// </remarks>
public static class SaharaProgrammerManifest
{
    /// <summary>
    /// Gets the largest number of images one manifest may declare.
    /// <para>获取单个清单可声明的最大镜像数。</para>
    /// </summary>
    /// <value>A bounded image count that covers every shipping programmer manifest.
    /// <para>有界的镜像数，足以覆盖现存的 programmer 清单。</para></value>
    public const int MaximumImages = 64;

    /// <summary>
    /// Gets the largest manifest document this reader accepts.
    /// <para>获取本读取器接受的最大清单文档字节数。</para>
    /// </summary>
    /// <value>A 512 KiB ceiling, far above any observed manifest and far below a payload.
    /// <para>512 KiB 上限，远高于任何已见清单，且远低于任何负载。</para></value>
    public const long MaximumManifestBytes = 512L * 1024;

    /// <summary>
    /// Gets the largest single image a manifest may reference.
    /// <para>获取清单可引用的单个镜像最大字节数。</para>
    /// </summary>
    /// <value>A 256 MiB ceiling, mirroring the programmer folder scan bound.
    /// <para>256 MiB 上限，与 programmer 目录扫描的上限一致。</para></value>
    public const long MaximumImageBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Gets the largest combined size of every image a manifest references.
    /// <para>获取清单引用的全部镜像的合计大小上限。</para>
    /// </summary>
    /// <value>A 512 MiB ceiling for the whole upload.
    /// <para>整个上传的 512 MiB 上限。</para></value>
    public const long MaximumTotalImageBytes = 512L * 1024 * 1024;

    private const int MaximumDocumentCharacters = 1024 * 1024;
    private const int MaximumElements = 4096;
    private const int MaximumAttributes = 16;
    private const int MaximumDepth = 4;

    private const string RootElementName = "sahara_config";
    private const string ImagesElementName = "images";
    private const string ImageElementName = "image";
    private const string ChipsetElementName = "chipset";
    private const string ImageIdAttribute = "image_id";
    private const string ImagePathAttribute = "image_path";

    /// <summary>
    /// Reports whether a path names a candidate multi-image programmer manifest by extension.
    /// <para>按扩展名报告某个路径是否为候选的多镜像 programmer 清单。</para>
    /// </summary>
    /// <param name="path">Candidate path; may be empty.<para>候选路径，可以为空。</para></param>
    /// <returns><see langword="true"/> for a <c>.xml</c> path regardless of whether the file exists.
    /// <para>对 <c>.xml</c> 路径为 <see langword="true"/>，与文件是否存在无关。</para></returns>
    public static bool IsManifestPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
            && string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads and validates one manifest into its bounded image list.
    /// <para>读取并校验一个清单，输出其有界镜像列表。</para>
    /// </summary>
    /// <param name="path">Path of the manifest file; it is opened read-only.
    /// <para>清单文件路径；以只读方式打开。</para></param>
    /// <returns>The images in document order.<para>按文档顺序排列的镜像。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.
    /// <para><paramref name="path"/> 为空。</para></exception>
    /// <exception cref="InvalidDataException">The document is unreadable, malformed, exceeds a bound, declares no
    /// usable image, or references a file that is missing or outside the per-image ceiling.
    /// <para>文档不可读、格式非法、超过某个上限、未声明可用镜像，或引用的文件缺失或超出单镜像上限。</para></exception>
    public static IReadOnlyList<SaharaProgrammerImage> Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A programmer manifest path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new InvalidDataException($"The programmer manifest does not exist: {fullPath}.");
        }

        var length = new FileInfo(fullPath).Length;
        if (length is 0 || length > MaximumManifestBytes)
        {
            throw new InvalidDataException(
                $"The programmer manifest must be non-empty and at most {MaximumManifestBytes} bytes: {fullPath}.");
        }

        XDocument document;
        try
        {
            using var stream = File.OpenRead(fullPath);
            document = LoadBounded(stream);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"The programmer manifest is not well-formed XML: {exception.Message}", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidDataException($"The programmer manifest could not be read: {exception.Message}", exception);
        }

        var root = document.Root
            ?? throw new InvalidDataException($"The programmer manifest has no root element: {fullPath}.");
        if (!string.Equals(root.Name.LocalName, RootElementName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The programmer manifest root must be <{RootElementName}>: {root.Name.LocalName}.");
        }

        var imagesContainer = root.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, ImagesElementName, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"The programmer manifest has no <{ImagesElementName}> container.");
        var imageElements = imagesContainer.Elements().Where(element =>
            string.Equals(element.Name.LocalName, ImageElementName, StringComparison.Ordinal)).ToList();
        if (imageElements.Count == 0)
        {
            throw new InvalidDataException($"The programmer manifest declares no <{ImageElementName}> entries.");
        }

        if (imageElements.Count > MaximumImages)
        {
            throw new InvalidDataException(
                $"The programmer manifest declares {imageElements.Count} images, above the {MaximumImages} ceiling.");
        }

        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var images = new List<SaharaProgrammerImage>(imageElements.Count);
        var seen = new HashSet<uint>();
        var total = 0L;
        foreach (var element in imageElements)
        {
            var imageId = ReadImageId(element);
            var declaredPath = element.Attribute(ImagePathAttribute)?.Value;
            if (string.IsNullOrWhiteSpace(declaredPath))
            {
                throw new InvalidDataException($"A <{ImageElementName}> entry has no {ImagePathAttribute} value.");
            }

            if (!seen.Add(imageId))
            {
                throw new InvalidDataException($"The programmer manifest repeats image_id {imageId}.");
            }

            var resolved = ResolveImagePath(directory, declaredPath);
            if (!File.Exists(resolved))
            {
                throw new InvalidDataException(
                    $"The programmer manifest references a missing image: {declaredPath} (resolved to {resolved}).");
            }

            var imageLength = new FileInfo(resolved).Length;
            if (imageLength is 0 || imageLength > MaximumImageBytes)
            {
                throw new InvalidDataException(
                    $"The programmer image must be non-empty and at most {MaximumImageBytes} bytes: {resolved}.");
            }

            total += imageLength;
            if (total > MaximumTotalImageBytes)
            {
                throw new InvalidDataException(
                    $"The programmer images total more than {MaximumTotalImageBytes} bytes: {fullPath}.");
            }

            images.Add(new SaharaProgrammerImage(imageId, resolved, imageLength));
        }

        if (images.Count == 0)
        {
            throw new InvalidDataException($"The programmer manifest produced no usable image mappings: {fullPath}.");
        }

        return images;
    }

    /// <remarks>
    /// A parent stack rebuilds the nesting instead of appending every element to the root, because the manifest is
    /// root / images / image and only the second level carries the payload the reader consumes.
    /// <para>用父元素栈重建嵌套，而不是把每个元素都挂到根元素上，因为清单是 root / images / image 三层，只有第二层
    /// 承载读取器要消费的内容。</para>
    /// </remarks>
    private static XDocument LoadBounded(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumDocumentCharacters,
            MaxCharactersFromEntities = 0,
            CloseInput = false,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        });

        var document = new XDocument();
        var parents = new Stack<XElement>();
        var elements = 0;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.Depth >= MaximumDepth)
                    {
                        throw new InvalidDataException(
                            $"The programmer manifest nests deeper than {MaximumDepth} elements.");
                    }

                    if (++elements > MaximumElements)
                    {
                        throw new InvalidDataException(
                            $"The programmer manifest holds more than {MaximumElements} elements.");
                    }

                    if (reader.AttributeCount > MaximumAttributes)
                    {
                        throw new InvalidDataException(
                            $"A programmer manifest element carries more than {MaximumAttributes} attributes.");
                    }

                    var element = new XElement(XName.Get(reader.LocalName, reader.NamespaceURI));
                    if (reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            var name = reader.Name == "xmlns"
                                ? XName.Get("xmlns")
                                : XName.Get(reader.LocalName, reader.NamespaceURI);
                            element.Add(new XAttribute(name, reader.Value));
                        }
                        while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }

                    if (parents.Count == 0)
                    {
                        document.Add(element);
                    }
                    else
                    {
                        parents.Peek().Add(element);
                    }

                    if (!reader.IsEmptyElement)
                    {
                        parents.Push(element);
                    }

                    break;
                case XmlNodeType.EndElement:
                    if (parents.Count > 0)
                    {
                        parents.Pop();
                    }

                    break;
            }
        }

        return document;
    }

    private static uint ReadImageId(XElement element)
    {
        var value = element.Attribute(ImageIdAttribute)?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"A <{ImageElementName}> entry has no {ImageIdAttribute} value.");
        }

        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexId))
            {
                throw new InvalidDataException($"image_id '{value}' is not a hexadecimal integer.");
            }

            return hexId;
        }

        if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            throw new InvalidDataException($"image_id '{value}' is not a decimal integer.");
        }

        return id;
    }

    /// <remarks>
    /// The vendor tool resolves a relative path against the manifest directory and, when that file is absent, also
    /// tries the path as given; this reader keeps both steps so an unchanged vendor manifest folder still works.
    /// <para>厂商工具先按清单所在目录解析相对路径，该文件不存在时再按原样尝试；本读取器保留两步，使未经改动的
    /// 厂商清单目录仍可用。</para>
    /// </remarks>
    private static string ResolveImagePath(string manifestDirectory, string declaredPath)
    {
        if (Path.IsPathRooted(declaredPath))
        {
            return Path.GetFullPath(declaredPath);
        }

        var combined = Path.GetFullPath(Path.Combine(manifestDirectory, declaredPath));
        if (File.Exists(combined))
        {
            return combined;
        }

        return Path.GetFullPath(declaredPath);
    }
}