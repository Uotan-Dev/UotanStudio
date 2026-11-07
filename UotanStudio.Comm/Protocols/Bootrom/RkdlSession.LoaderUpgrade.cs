using UotanStudio.Comm.Diagnostics;

namespace UotanStudio.Comm.Protocols.Bootrom;

public sealed partial class RkdlSession
{
    /// <summary>
    /// Upgrades the loader on a mask ROM device by building the RK28 ID block from the loader image and writing it at
    /// LBA 64, the way <c>upgrade_loader</c> does.
    /// <para>按 <c>upgrade_loader</c> 的方式，从 loader 镜像构建 RK28 ID block 并写入 LBA 64，在 Mask ROM 设备上升级
    /// loader。</para>
    /// </summary>
    /// <remarks>
    /// The image is parsed and CRC-validated first, then the <c>FlashBoot</c> and <c>FlashData</c> entries are required
    /// and <c>FlashHead</c> is optional. With a head entry the new-ID-block layout is used, which additionally requires
    /// the device to answer READ_CAPABILITY with bit 0 of the second capability byte set (<c>capability[1] &amp; 1</c>);
    /// without it the legacy four-header-sector layout is built. Both layouts store the entries' <c>ucRc4Flag</c>
    /// verbatim as <c>uiRc4Flag</c> and RC4-encrypt the payloads when that flag is non-zero. The device is not
    /// reset afterwards.
    /// <para>镜像先解析并通过 CRC 校验，随后要求存在 <c>FlashBoot</c> 与 <c>FlashData</c> 条目，<c>FlashHead</c> 为可选。
    /// 存在头部条目时使用新 ID block 布局，该布局还要求设备在 READ_CAPABILITY 应答中把第二个能力字节的 bit 0 置位
    /// （<c>capability[1] &amp; 1</c>）；否则构建旧版四个头部扇区的布局。两种布局都把条目的 <c>ucRc4Flag</c> 原样写入
    /// <c>uiRc4Flag</c>，并在该标志非 0 时对载荷施加 RC4。完成后不复位设备。</para>
    /// </remarks>
    /// <param name="loaderImage">The complete RKBOOT loader image (the .bin a mask ROM device is loaded with).
    /// <para>完整的 RKBOOT loader 镜像（用于加载 Mask ROM 设备的 .bin）。</para></param>
    /// <returns>The number of sectors the ID block occupies at LBA 64. <para>ID block 在 LBA 64 占用的扇区数。</para></returns>
    /// <exception cref="RkdlException">The device already reports loader mode, the image fails parsing or CRC
    /// validation, a required entry is missing or its data leaves the image, the head layout is requested but the device
    /// does not report new-ID-block support, the sector layout does not fit the device's 16-bit fields, or the WRITE_LBA
    /// transaction failed. <para>设备已报告 loader 模式、镜像解析或 CRC 校验失败、缺少必需条目或其数据越过镜像、
    /// 需要头部布局但设备未报告新 ID block 支持、扇区布局装不进设备的 16 位字段，或 WRITE_LBA 事务失败。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="loaderImage"/> is <see langword="null"/>.
    /// <para><paramref name="loaderImage"/> 为 <see langword="null"/>。</para></exception>
    public int UpgradeLoader(byte[] loaderImage)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(loaderImage);
        if (Mode == RkdlDeviceMode.Loader)
        {
            throw new RkdlException(RkdlError.NotSupported,
                "Loader upgrade rewrites the ID block and is mask ROM only, but this device already reports loader mode.");
        }

        var image = RkBootImage.Parse(loaderImage);
        var loaderCode = RequireLoaderEntry(loaderImage, image, "FlashBoot");
        var loaderData = RequireLoaderEntry(loaderImage, image, "FlashData");
        var loaderHead = FindLoaderEntry(loaderImage, image, "FlashHead");

        var flashDataSectors = Rk28IdBlock.SectorsFor(loaderData.Length);
        var flashBootSectors = Rk28IdBlock.SectorsFor(loaderCode.Length);

        byte[] idBlock;
        string layout;
        if (loaderHead is not null)
        {
            var capability = ReadCapability();
            if ((capability.Raw[1] & 1) == 0)
            {
                throw new RkdlException(RkdlError.NotSupported,
                    $"Device does not support the head-based loader ID block (capability byte 1 = 0x{capability.Raw[1]:X2}).");
            }

            var flashHeadSectors = Rk28IdBlock.SectorsFor(loaderHead.Length);
            idBlock = Rk28IdBlock.BuildNewIdBlock(loaderHead, loaderData, loaderCode, flashHeadSectors,
                flashDataSectors, flashBootSectors, image.Rc4DisabledFlag);
            layout = $"head {flashHeadSectors} + data {flashDataSectors} + code {flashBootSectors}";
        }
        else
        {
            idBlock = Rk28IdBlock.Build(loaderData, loaderCode, flashDataSectors, flashBootSectors,
                image.Rc4DisabledFlag);
            layout = $"legacy 4 + data {flashDataSectors} + code {flashBootSectors}";
        }

        using var source = new MemoryStream(idBlock, writable: false);
        WriteLbaCore(RkdlConstants.LoaderIdBlockLba, source, idBlock.Length, null, default, RkdlRwMethod.Image,
            maskromLoaderUpgrade: true);

        var sectors = idBlock.Length / RkdlConstants.SectorSize;
        CommTrace.State("rkdl",
            $"Loader ID block written at LBA {RkdlConstants.LoaderIdBlockLba}: {sectors} sectors ({layout}), rc4Flag={image.Rc4DisabledFlag}.");
        return sectors;
    }

    /// <summary>
    /// Extracts a loader entry the upgrade cannot run without. <para>提取升级不可或缺的 loader 条目。</para>
    /// </summary>
    /// <param name="loaderImage">The whole loader image. <para>完整 loader 镜像。</para></param>
    /// <param name="image">The parsed image. <para>解析后的镜像。</para></param>
    /// <param name="name">The entry name to look up. <para>要查找的条目名。</para></param>
    /// <returns>The entry data. <para>条目数据。</para></returns>
    /// <exception cref="RkdlException">The entry is missing or its data leaves the image.
    /// <para>条目缺失或其数据越过镜像。</para></exception>
    private static byte[] RequireLoaderEntry(byte[] loaderImage, RkBootImage image, string name)
        => FindLoaderEntry(loaderImage, image, name)
            ?? throw new RkdlException(RkdlError.InvalidParameter,
                $"Loader image has no '{name}' loader entry; the ID block cannot be built without it.");

    /// <summary>
    /// Finds a loader entry by name, reproducing the case-insensitive <c>strcasecmp</c> scan of
    /// <c>CRKBoot::GetIndexByName</c>, which returns the first match in table order.
    /// <para>按名称查找 loader 条目，复现 <c>CRKBoot::GetIndexByName</c> 的 <c>strcasecmp</c> 大小写不敏感扫描，
    /// 返回表中首个匹配。</para>
    /// </summary>
    /// <param name="loaderImage">The whole loader image. <para>完整 loader 镜像。</para></param>
    /// <param name="image">The parsed image. <para>解析后的镜像。</para></param>
    /// <param name="name">The entry name to look up. <para>要查找的条目名。</para></param>
    /// <returns>The entry data, or <see langword="null"/> when no entry carries that name.
    /// <para>条目数据；没有条目使用该名称时为 <see langword="null"/>。</para></returns>
    /// <exception cref="RkdlException">The entry data leaves the image. <para>条目数据越过镜像。</para></exception>
    private static byte[]? FindLoaderEntry(byte[] loaderImage, RkBootImage image, string name)
    {
        foreach (var entry in image.LoaderEntries)
        {
            if (!string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var end = (long)entry.DataOffset + entry.DataSize;
            if (entry.DataSize == 0 || entry.DataOffset >= loaderImage.Length || end > loaderImage.Length)
            {
                throw new RkdlException(RkdlError.InvalidParameter,
                    $"Loader entry '{entry.Name}' data is empty or out of range.");
            }

            return SliceEntry(loaderImage, entry);
        }

        return null;
    }
}