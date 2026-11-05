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
}