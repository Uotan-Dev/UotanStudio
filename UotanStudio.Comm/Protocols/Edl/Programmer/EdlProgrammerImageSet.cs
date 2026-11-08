namespace UotanStudio.Comm.Protocols.Edl.Programmer;

/// <summary>
/// The programmer inputs one EDL session can upload: either one Sahara image or the multi-image manifest that
/// spreads the programmer across several Sahara image identifiers.
/// <para>一次 EDL 会话可上传的 programmer 输入：单个 Sahara 镜像，或把 programmer 分散到多个 Sahara 镜像标识上的
/// 多镜像清单。</para>
/// </summary>
/// <remarks>
/// A single image answers the <c>Firehose</c> identifier, which is the id the vendor single-image flow uses. A
/// manifest carries its own identifiers and the target requests them in the order it chooses, so the session
/// uploads whichever of them it asks for.
/// <para>单镜像应答 <c>Firehose</c> 标识，即厂商单镜像流程使用的标识。清单自带标识，目标按其选择的顺序请求，
/// 因此会话按目标请求应答其中任意一个。</para>
/// </remarks>
public sealed class EdlProgrammerImageSet
{
    private EdlProgrammerImageSet(string sourcePath, bool isManifest, IReadOnlyList<SaharaProgrammerImage> images)
    {
        SourcePath = sourcePath;
        IsManifest = isManifest;
        Images = images;
    }

    /// <summary>
    /// Gets the absolute path the set was opened from.
    /// <para>获取该集合被打开时的绝对路径。</para>
    /// </summary>
    /// <value>A non-empty full path. <para>非空的完整路径。</para></value>
    public string SourcePath { get; }

    /// <summary>
    /// Gets whether the source is a multi-image manifest rather than one image file.
    /// <para>获取来源是多镜像清单还是单个镜像文件。</para>
    /// </summary>
    /// <value><see langword="true"/> for a manifest. <para>清单为 <see langword="true"/>。</para></value>
    public bool IsManifest { get; }

    /// <summary>
    /// Gets the images the session may upload, in manifest document order or as the single image.
    /// <para>获取会话可上传的镜像，按清单文档顺序排列，或就是那一个单镜像。</para>
    /// </summary>
    /// <value>A never-empty image list. <para>从不为空的镜像列表。</para></value>
    public IReadOnlyList<SaharaProgrammerImage> Images { get; }

    /// <summary>
    /// Opens one programmer source as an image set.
    /// <para>把一个 programmer 来源打开为镜像集合。</para>
    /// </summary>
    /// <param name="path">Path of a <c>.xml</c> manifest or any other existing programmer image file.
    /// <para><c>.xml</c> 清单或任意其他已存在的 programmer 镜像文件路径。</para></param>
    /// <returns>The image set, with one image for a plain file.
    /// <para>镜像集合；普通文件只含一个镜像。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.
    /// <para><paramref name="path"/> 为空。</para></exception>
    /// <exception cref="InvalidDataException">The manifest is unusable, or the single file is missing, empty or above
    /// the per-image ceiling.<para>清单不可用，或单文件缺失、为空或超过单镜像上限。</para></exception>
    public static EdlProgrammerImageSet Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A programmer path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (SaharaProgrammerManifest.IsManifestPath(fullPath))
        {
            var images = SaharaProgrammerManifest.Read(fullPath);
            return new EdlProgrammerImageSet(fullPath, isManifest: true, images);
        }

        if (!File.Exists(fullPath))
        {
            throw new InvalidDataException($"The programmer image does not exist: {fullPath}.");
        }

        var length = new FileInfo(fullPath).Length;
        if (length is 0 || length > SaharaProgrammerManifest.MaximumImageBytes)
        {
            throw new InvalidDataException(
                $"The programmer image must be non-empty and at most {SaharaProgrammerManifest.MaximumImageBytes} bytes: {fullPath}.");
        }

        return new EdlProgrammerImageSet(fullPath, isManifest: false,
            [new SaharaProgrammerImage((uint)SaharaImageId.Firehose, fullPath, length)]);
    }

    /// <summary>
    /// Builds an image set from an in-memory id-to-file mapping, e.g. a programmer mapping inferred from
    /// file-name conventions when a folder carries no vendor manifest.
    /// <para>由内存中的 id 到文件映射构建镜像集合，例如目录没有厂商清单时按文件名约定推断出的 programmer 映射。</para>
    /// </summary>
    /// <remarks>
    /// The mapping is validated like a manifest before any upload: duplicate identifiers, missing files,
    /// empty or oversized images and an over-ceiling combined size are all rejected here. Each image is
    /// re-measured from disk, so a stale declared length cannot poison the upload evidence.
    /// <para>映射在上传前按清单同等规则校验：重复标识、缺失文件、空镜像或超限镜像以及超合计上限都在此拒绝。
    /// 每个镜像按磁盘重新测量，因此过期的声明长度不会污染上传证据。</para></remarks>
    /// <param name="sourcePath">The path the mapping is attributed to, typically the scanned folder; it is
    /// recorded as the set source for evidence and logs.
    /// <para>映射归属的路径，通常为扫描目录；作为集合来源记录进证据与日志。</para></param>
    /// <param name="images">The candidate image mappings in rule order.
    /// <para>按规则顺序排列的候选镜像映射。</para></param>
    /// <returns>The validated image set, marked as a manifest whenever it carries more than one image.
    /// <para>校验通过的镜像集合；多于一个镜像时按清单形态标记。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="sourcePath"/> is blank, or an image carries no path.
    /// <para><paramref name="sourcePath"/> 为空，或某个镜像没有路径。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="images"/> is null.
    /// <para><paramref name="images"/> 为 null。</para></exception>
    /// <exception cref="InvalidDataException">The mapping is empty or above the image-count ceiling, repeats an
    /// image identifier, or any image is missing, empty, oversized or pushes the combined size over the ceiling.
    /// <para>映射为空或超过镜像数量上限、重复镜像标识，或任一镜像缺失、为空、超限或使合计大小超过上限。</para></exception>
    public static EdlProgrammerImageSet FromImages(string sourcePath, IReadOnlyList<SaharaProgrammerImage> images)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count is 0 or > SaharaProgrammerManifest.MaximumImages)
        {
            throw new InvalidDataException(
                $"A programmer set carries between 1 and {SaharaProgrammerManifest.MaximumImages} images.");
        }

        var seen = new HashSet<uint>();
        var total = 0L;
        var measured = new List<SaharaProgrammerImage>(images.Count);
        foreach (var image in images)
        {
            if (!seen.Add(image.ImageId))
            {
                throw new InvalidDataException($"The programmer set repeats image_id {image.ImageId}.");
            }

            if (string.IsNullOrWhiteSpace(image.Path))
            {
                throw new InvalidDataException($"The image for image_id {image.ImageId} carries no path.");
            }

            var fullPath = Path.GetFullPath(image.Path);
            if (!File.Exists(fullPath))
            {
                throw new InvalidDataException($"The programmer image does not exist: {fullPath}.");
            }

            var length = new FileInfo(fullPath).Length;
            if (length is 0 || length > SaharaProgrammerManifest.MaximumImageBytes)
            {
                throw new InvalidDataException(
                    $"The programmer image must be non-empty and at most {SaharaProgrammerManifest.MaximumImageBytes} bytes: {fullPath}.");
            }

            total += length;
            if (total > SaharaProgrammerManifest.MaximumTotalImageBytes)
            {
                throw new InvalidDataException(
                    $"The programmer images total more than {SaharaProgrammerManifest.MaximumTotalImageBytes} bytes: {fullPath}.");
            }

            measured.Add(new SaharaProgrammerImage(image.ImageId, fullPath, length));
        }

        return new EdlProgrammerImageSet(Path.GetFullPath(sourcePath), isManifest: measured.Count > 1, measured);
    }
}