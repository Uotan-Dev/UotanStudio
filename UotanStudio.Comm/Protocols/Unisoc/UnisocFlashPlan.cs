using UotanStudio.Firmware.VendorFirmware.Pac;

namespace UotanStudio.Comm.Protocols.Unisoc;

/// <summary>
/// One flashable partition image inside the plan. <para>计划中一个可刷写的分区镜像。</para>
/// </summary>
public sealed class UnisocFlashImage
{
    internal UnisocFlashImage(string name, long length, PacEntry entry)
    {
        Name = name;
        Length = length;
        Entry = entry;
    }

    /// <summary>Gets the partition name the image targets, taken from the PAC entry identifier.
    /// <para>获取镜像指向的分区名，取自 PAC 条目标识。</para></summary>
    public string Name { get; }

    /// <summary>Gets the image length in bytes. <para>获取镜像长度（字节）。</para></summary>
    public long Length { get; }

    internal PacEntry Entry { get; }
}

/// <summary>
/// Flash plan built from one SPRD/UNISOC PAC package: the confirmed two-stage loaders plus every flashable
/// partition image. The plan streams image bytes straight from the package at write time and never stages them.
/// <para>由一个 SPRD/展锐 PAC 包构建的刷写计划：确认的两级加载器加全部可刷写的分区镜像。写入时镜像字节直接从
/// 包内流式读取，从不落地暂存。</para>
/// </summary>
/// <remarks>
/// Loader roles (<c>FDL</c>/<c>FDL1</c>/<c>FDL2</c>) and XML/certificate entries are excluded from the image list;
/// duplicate image names are rejected because a flash target must be unambiguous.
/// <para>加载器角色（<c>FDL</c>/<c>FDL1</c>/<c>FDL2</c>）与 XML/证书条目不进入镜像列表；镜像名重复会被拒绝，
/// 因为刷写目标必须无歧义。</para>
/// </remarks>
public sealed class UnisocFlashPlan : IDisposable
{
    private readonly PacImage _image;

    private UnisocFlashPlan(string packagePath, PacImage image, SprdPacLoaderSet loaders,
        IReadOnlyList<UnisocFlashImage> images)
    {
        PackagePath = packagePath;
        _image = image;
        Loaders = loaders;
        Images = images;
    }

    /// <summary>Gets the package path the plan was built from. <para>获取计划来源的包路径。</para></summary>
    public string PackagePath { get; }

    /// <summary>Gets the confirmed two-stage loader set. <para>获取确认的两级加载器集合。</para></summary>
    public SprdPacLoaderSet Loaders { get; }

    /// <summary>Gets the flashable partition images in package order.
    /// <para>按包顺序获取可刷写的分区镜像。</para></summary>
    public IReadOnlyList<UnisocFlashImage> Images { get; }

    /// <summary>
    /// Loads and validates a PAC package as a flash plan. Loader resolution failures happen here, before any
    /// device is contacted.
    /// <para>加载并校验一个 PAC 包为刷写计划。加载器解析失败在此发生，早于接触任何设备。</para>
    /// </summary>
    /// <param name="packagePath">Path of the PAC package. <para>PAC 包路径。</para></param>
    /// <param name="cancellationToken">Cancels the load. <para>取消加载。</para></param>
    /// <returns>The loaded plan; dispose it to close the package.
    /// <para>已加载的计划；释放它以关闭包。</para></returns>
    /// <exception cref="InvalidDataException">The package header, loaders or image names fail validation.
    /// <para>包头、加载器或镜像名未通过校验。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public static UnisocFlashPlan Load(string packagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packagePath);
        cancellationToken.ThrowIfCancellationRequested();
        var image = PacImage.Open(packagePath);
        try
        {
            var loaders = SprdPacLoaders.Resolve(image);
            var loaderIdentifiers = new[] { loaders.FirstStage.Id, loaders.SecondStage.Id, "XML" };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var images = new List<UnisocFlashImage>();
            foreach (var entry in image.Entries)
            {
                if (entry.Type != PacFileType.Image || entry.Length <= 0)
                {
                    continue;
                }

                if (loaderIdentifiers.Any(identifier =>
                        string.Equals(entry.Id, identifier, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!seen.Add(entry.Id))
                {
                    throw new InvalidDataException($"The PAC package declares duplicate flash image '{entry.Id}'.");
                }

                images.Add(new UnisocFlashImage(entry.Id, entry.Length, entry));
            }

            return new UnisocFlashPlan(packagePath, image, loaders, images);
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the bounded stream of one image for a write pass.
    /// <para>打开一个镜像的有界流供写入流程使用。</para>
    /// </summary>
    /// <param name="image">An image of this plan. <para>本计划中的镜像。</para></param>
    /// <returns>The read-only stream positioned at the image start.
    /// <para>定位在镜像起点的只读流。</para></returns>
    /// <exception cref="ArgumentException">The image does not belong to this plan.
    /// <para>镜像不属于本计划。</para></exception>
    public Stream OpenImage(UnisocFlashImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return _image.OpenEntryStream(image.Entry);
    }

    /// <summary>Opens the bounded stream of one loader or image entry directly.
    /// <para>直接打开一个加载器或镜像条目的有界流。</para></summary>
    /// <param name="entry">An entry of this package. <para>本包中的条目。</para></param>
    /// <returns>The read-only stream positioned at the entry start.
    /// <para>定位在条目起点的只读流。</para></returns>
    internal Stream OpenEntry(PacEntry entry) => _image.OpenEntryStream(entry);

    /// <summary>Releases the underlying package. <para>释放底层包。</para></summary>
    public void Dispose() => _image.Dispose();
}