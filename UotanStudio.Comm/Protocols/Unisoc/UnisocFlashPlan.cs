using UotanStudio.Firmware.VendorFirmware.Pac;

namespace UotanStudio.Comm.Protocols.Unisoc;

/// <summary>
/// One flashable partition image inside the plan. <para>计划中一个可刷写的分区镜像。</para>
/// </summary>
public sealed class UnisocFlashImage
{
    internal UnisocFlashImage(string name, string partition, long length, PacEntry? entry,
        bool eraseOnly = false, string alias = "", string operation = "", long offset = 0,
        long declaredSize = 0, int flashType = 0, bool mandatory = false)
    {
        Name = name;
        Partition = partition;
        Length = length;
        Entry = entry;
        EraseOnly = eraseOnly;
        Alias = alias;
        Operation = operation;
        Offset = offset;
        DeclaredSize = declaredSize;
        FlashType = flashType;
        IsMandatory = mandatory;
    }

    /// <summary>Gets the scheme <c>IDAlias</c> the official product list shows, or an empty string when the scheme
    /// declares none. <para>获取官方产品列表显示的 scheme <c>IDAlias</c>；scheme 未声明时为空字符串。</para></summary>
    public string Alias { get; }

    /// <summary>Gets the scheme <c>Type</c>, such as <c>CODE2</c> or <c>EraseFlash2</c>, or an empty string when the
    /// scheme declares none. <para>获取 scheme 的 <c>Type</c>，例如 <c>CODE2</c> 或 <c>EraseFlash2</c>；scheme 未声明时为空字符串。</para></summary>
    public string Operation { get; }

    /// <summary>Gets the <c>Block/Base</c> offset the scheme declares for this entry, or <c>0</c> when it declares none.
    /// <para>获取 scheme 为该条目声明的 <c>Block/Base</c> 偏移；未声明时为 <c>0</c>。</para></summary>
    public long Offset { get; }

    /// <summary>Gets the <c>Block/Size</c> the scheme declares, or <c>0</c> when it declares none or declares
    /// <c>0x0</c>, which leaves the extent open-ended. <para>获取 scheme 声明的 <c>Block/Size</c>；未声明或声明为
    /// <c>0x0</c>（表示范围开放）时为 <c>0</c>。</para></summary>
    public long DeclaredSize { get; }

    /// <summary>Gets the declared <c>FlashTypeID</c> of the package: <c>0</c> is Nor flash and <c>1</c> is Nand flash
    /// per the official XML header. <para>获取包声明的 <c>FlashTypeID</c>：按官方 XML 头注释 <c>0</c> 为 Nor flash、
    /// <c>1</c> 为 Nand flash。</para></summary>
    public int FlashType { get; }

    /// <summary>Gets the packaged file name the entry carries, or an empty string for an operation without a payload.
    /// <para>获取条目携带的包内文件名；无负载的操作条目为空字符串。</para></summary>
    public string PackagedFileName => Entry?.Name ?? string.Empty;

    /// <summary>Gets the scheme identifier of the image, taken from the <c>BMAConfig</c> scheme <c>ID</c>.
    /// <para>获取镜像的 scheme 标识，取自 <c>BMAConfig</c> scheme 的 <c>ID</c>。</para></summary>
    public string Name { get; }

    /// <summary>Gets the block identifier the scheme targets for this image, which is the partition name the
    /// official tool writes to. <para>获取 scheme 为该镜像指定的块标识，也就是官方工具写入的分区名。</para></summary>
    public string Partition { get; }

    /// <summary>Gets the image length in bytes. <para>获取镜像长度（字节）。</para></summary>
    public long Length { get; }

    /// <summary>Gets whether the scheme marks this image as mandatory (<c>CheckFlag=1</c>): the official tool forces
    /// it selected and refuses the download when it is missing.
    /// <para>获取 scheme 是否把该镜像标为必须执行（<c>CheckFlag=1</c>）：官方工具强制勾选它，缺失时拒绝下载。</para></summary>
    public bool IsMandatory { get; }

    /// <summary>Gets whether the entry erases its partition instead of writing a payload; such an entry carries no
    /// image bytes and never reaches <see cref="UnisocFlashPlan.OpenImage"/>.
    /// <para>获取该条目是否擦除其分区而不是写入负载；这类条目不含镜像字节，永远不进入
    /// <see cref="UnisocFlashPlan.OpenImage"/>。</para></summary>
    public bool EraseOnly { get; }

    internal PacEntry? Entry { get; }
}

/// <summary>
/// Flash plan built from one SPRD/UNISOC PAC package: the confirmed two-stage loaders plus every flashable
/// partition image. The plan streams image bytes straight from the package at write time and never stages them.
/// <para>由一个 SPRD/展锐 PAC 包构建的刷写计划：确认的两级加载器加全部可刷写的分区镜像。写入时镜像字节直接从
/// 包内流式读取，从不落地暂存。</para>
/// </summary>
/// <remarks>
/// The image list follows the <c>BMAConfig</c> scheme rather than the package directory: an image is listed when the
/// scheme declares a <c>File</c> step that carries a payload, and a payload-free step whose <c>Type</c> is an
/// <c>EraseFlash</c> variant is listed as an erase-only entry with no bytes. The order and the scope therefore match
/// what the official tool executes. A package may carry payload entries the scheme never references, and those are
/// deliberately not listed. Loader roles (<c>FDL</c>/<c>FDL1</c>/<c>FDL2</c>) are reported separately and never appear.
/// <para>镜像列表以 <c>BMAConfig</c> scheme 为准而不是包目录：scheme 声明了携带负载的 <c>File</c> 步骤时该镜像被列出，
/// 而 <c>Type</c> 为 <c>EraseFlash</c> 变体的无负载步骤被列为不含字节的仅擦除条目，因此顺序与范围都与官方工具执行的一致。
/// 包内可能带有 scheme 从未引用的负载条目，它们被有意排除。加载器角色（<c>FDL</c>/<c>FDL1</c>/<c>FDL2</c>）单独上报，
/// 绝不作为镜像出现。</para>
/// </remarks>
public sealed class UnisocFlashPlan : IDisposable
{
    private readonly PacImage _image;

    private UnisocFlashPlan(string packagePath, PacImage image, SprdPacLoaderSet loaders,
        IReadOnlyList<UnisocFlashImage> images, IReadOnlyList<SprdPacPartition> partitions, byte[]? extTable = null)
    {
        PackagePath = packagePath;
        _image = image;
        Loaders = loaders;
        Images = images;
        Partitions = partitions;
        ExtTable = extTable;
    }

    /// <summary>Gets the partition table the package declares, in declaration order, each entry carrying the offset
    /// derived by accumulating the declared sizes. The table describes the layout the official tool rebuilds on the
    /// device, so these offsets are what the rebuilt table would use; the flash pass itself still addresses partitions
    /// by name and never forces a position. <para>获取包声明的分区表，按声明顺序排列，每条带按声明大小累加推导出的偏移。
    /// 该表描述官方工具在设备上重建的布局，因此这些偏移就是重建后使用的值；烧录流程本身仍按分区名寻址，绝不强制位置。</para></summary>
    public IReadOnlyList<SprdPacPartition> Partitions { get; }

    /// <summary>Gets the package path the plan was built from. <para>获取计划来源的包路径。</para></summary>
    public string PackagePath { get; }

    /// <summary>Gets the serialized vendor ExtTable the package declares for repartitioning, or
    /// <see langword="null"/> when the package declares none.
    /// <para>获取包为重建分区表声明的厂商 ExtTable 字节；包未声明时为 <see langword="null"/>。</para></summary>
    public byte[]? ExtTable { get; }

    /// <summary>Gets the confirmed two-stage loader set. <para>获取确认的两级加载器集合。</para></summary>
    public SprdPacLoaderSet Loaders { get; }

    /// <summary>Gets the plan entries in scheme declaration order: payload images plus the erase operations the scheme
    /// declares without a payload. <para>按 scheme 声明顺序获取计划条目：负载镜像，以及 scheme 声明的不含负载的擦除操作。</para></summary>
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
        PacImage.ValidateCrc(packagePath, cancellationToken);
        var image = PacImage.Open(packagePath);
        try
        {
            var loaders = SprdPacLoaders.Resolve(image);
            var layout = SprdPacLoaders.ResolveLayout(image);
            var images = new List<UnisocFlashImage>();
            foreach (var step in layout.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (step.Entry is not { } entry)
                {
                    // Only erase operations become plan entries without a payload; other payload-free steps are
                    // inspections the official tool runs around the write sequence and never as a partition job.
                    // 只有擦除操作成为不含负载的计划条目；其它无负载步骤是官方工具在写入顺序之外的检查动作。
                    if (step.IsErase && step.Partition.Length > 0)
                    {
                        images.Add(new UnisocFlashImage(step.Identifier, step.Partition, 0, null,
                            eraseOnly: true, step.Alias, step.Operation, step.BlockBase, step.BlockSize,
                            layout.FlashType, step.IsMandatory));
                    }

                    continue;
                }

                images.Add(new UnisocFlashImage(step.Identifier, step.Partition, entry.Length, entry,
                    eraseOnly: false, step.Alias, step.Operation, step.BlockBase, step.BlockSize,
                    layout.FlashType, step.IsMandatory));
            }

            return new UnisocFlashPlan(packagePath, image, loaders, images, layout.Partitions, layout.ExtTable);
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
    /// <exception cref="ArgumentException">The image is an erase-only entry, which carries no bytes to open.
    /// <para>该镜像是仅擦除的条目，没有可打开的字节。</para></exception>
    public Stream OpenImage(UnisocFlashImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Entry is not { } entry)
        {
            throw new ArgumentException(
                $"The plan entry '{image.Name}' is an erase operation and carries no image bytes.", nameof(image));
        }

        return _image.OpenEntryStream(entry);
    }

    /// <summary>Opens the bounded stream of one of the plan's two loader entries, for tools that drive the
    /// two-stage handshake without executing a flash plan.
    /// <para>直接打开本计划两个加载器条目之一的有界流，供不执行刷写计划、只驱动两级握手的工具使用。</para></summary>
    /// <param name="entry">A loader entry of this package. <para>本包中的一个加载器条目。</para></param>
    /// <returns>The read-only stream positioned at the entry start.
    /// <para>定位在条目起点的只读流。</para></returns>
    public Stream OpenLoader(PacEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!ReferenceEquals(entry, Loaders.FirstStage) && !ReferenceEquals(entry, Loaders.SecondStage))
        {
            throw new ArgumentException("The entry is not one of the plan's loader entries.", nameof(entry));
        }

        return _image.OpenEntryStream(entry);
    }

    internal Stream OpenEntry(PacEntry entry) => _image.OpenEntryStream(entry);

    /// <summary>Releases the underlying package. <para>释放底层包。</para></summary>
    public void Dispose() => _image.Dispose();
}