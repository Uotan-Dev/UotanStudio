namespace UotanStudio.Comm.Protocols.Sunxi;

/// <summary>
/// Boot file mode the FES service reports through <see cref="SunxiFesSession.QuerySecureAsync"/>; it selects
/// which boot chain variant a package must provide.
/// <para>FES 服务经 <see cref="SunxiFesSession.QuerySecureAsync"/> 上报的启动文件模式，决定固件包必须提供
/// 哪一套引导链变体。</para>
/// </summary>
public enum SunxiBootFileMode : uint
{
    /// <summary>Normal boot chain. <para>正常引导链。</para></summary>
    Normal = 0,

    /// <summary>TOC trust-chain boot. <para>TOC 信任链启动。</para></summary>
    Toc = 1,

    /// <summary>Packaged boot chain. <para>打包引导链。</para></summary>
    Package = 4
}

/// <summary>
/// Storage type word the FES service reports through <see cref="SunxiFesSession.QueryStorageAsync"/>.
/// <para>FES 服务经 <see cref="SunxiFesSession.QueryStorageAsync"/> 上报的存储类型字。</para>
/// </summary>
public enum SunxiStorageKind
{
    /// <summary>Raw NAND. <para>raw NAND。</para></summary>
    Nand = 0,

    /// <summary>SD card. <para>SD 卡。</para></summary>
    Sdcard = 1,

    /// <summary>eMMC. <para>eMMC。</para></summary>
    Emmc = 2,

    /// <summary>SPI NOR. <para>SPI NOR。</para></summary>
    Spinor = 3,

    /// <summary>eMMC v3. <para>eMMC v3。</para></summary>
    Emmc3 = 4,

    /// <summary>SPI NAND. <para>SPI NAND。</para></summary>
    Spinand = 5,

    /// <summary>SD card slot 1. <para>SD 卡槽 1。</para></summary>
    Sd1 = 6,

    /// <summary>eMMC slot 0. <para>eMMC 槽 0。</para></summary>
    Emmc0 = 7,

    /// <summary>UFS. <para>UFS。</para></summary>
    Ufs = 8
}

/// <summary>
/// Boot image role a package entry can fill for one boot mode and storage type.
/// <para>固件包条目在某种启动模式与存储类型下可承担的一种引导镜像角色。</para>
/// </summary>
public enum SunxiBootImageRole
{
    /// <summary>Normal or packaged preboot image. <para>正常或打包启动的预启动镜像。</para></summary>
    PrebootNormal,

    /// <summary>TOC preboot image. <para>TOC 预启动镜像。</para></summary>
    PrebootToc,

    /// <summary>Normal or packaged NAND/SPI NAND boot0. <para>正常或打包启动的 NAND/SPI NAND boot0。</para></summary>
    Boot0NormalNand,

    /// <summary>Normal or packaged SD/eMMC boot0. <para>正常或打包启动的 SD/eMMC boot0。</para></summary>
    Boot0NormalCard,

    /// <summary>Normal or packaged SPI NOR boot0. <para>正常或打包启动的 SPI NOR boot0。</para></summary>
    Boot0NormalNor,

    /// <summary>Normal or packaged UFS boot0. <para>正常或打包启动的 UFS boot0。</para></summary>
    Boot0NormalUfs,

    /// <summary>TOC boot0 for an unrecognized storage type. <para>存储类型未识别时使用的 TOC boot0。</para></summary>
    Boot0TocDefault,

    /// <summary>TOC SD boot0. <para>TOC SD boot0。</para></summary>
    Boot0TocSd,

    /// <summary>TOC NAND boot0. <para>TOC NAND boot0。</para></summary>
    Boot0TocNand,

    /// <summary>TOC SPI NOR boot0. <para>TOC SPI NOR boot0。</para></summary>
    Boot0TocNor,

    /// <summary>TOC UFS boot0. <para>TOC UFS boot0。</para></summary>
    Boot0TocUfs,

    /// <summary>Normal boot1 (U-Boot). <para>正常启动的 boot1（U-Boot）。</para></summary>
    Boot1Normal,

    /// <summary>TOC boot1. <para>TOC boot1。</para></summary>
    Boot1Toc,

    /// <summary>Packaged boot1 for non-SPI-NOR storage. <para>非 SPI NOR 存储的打包 boot1。</para></summary>
    Boot1Package,

    /// <summary>Packaged boot1 for SPI NOR storage. <para>SPI NOR 存储的打包 boot1。</para></summary>
    Boot1PackageNor
}

/// <summary>
/// Maps the security and storage answers of a FES service to the boot image roles a package must provide.
/// <para>把 FES 服务的安全与存储应答映射为固件包必须提供的引导镜像角色。</para>
/// </summary>
public static class SunxiBootImageSelector
{
    /// <summary>
    /// Converts a security answer to its boot file mode.
    /// <para>把安全应答转换为启动文件模式。</para>
    /// </summary>
    /// <param name="secureWord">Answer of <see cref="SunxiFesSession.QuerySecureAsync"/>.
    /// <para><see cref="SunxiFesSession.QuerySecureAsync"/> 的应答。</para></param>
    /// <returns>The mode, or <see langword="null"/> for an unknown word.
    /// <para>启动文件模式；未知字时为 <see langword="null"/>。</para></returns>
    public static SunxiBootFileMode? BootFileModeFromWord(uint secureWord) => secureWord switch
    {
        0 => SunxiBootFileMode.Normal,
        1 => SunxiBootFileMode.Toc,
        4 => SunxiBootFileMode.Package,
        _ => null
    };

    /// <summary>
    /// Converts a storage answer to its storage kind.
    /// <para>把存储应答转换为存储类型。</para>
    /// </summary>
    /// <param name="storageWord">Answer of <see cref="SunxiFesSession.QueryStorageAsync"/>.
    /// <para><see cref="SunxiFesSession.QueryStorageAsync"/> 的应答。</para></param>
    /// <returns>The storage kind, or <see langword="null"/> for a word outside the known range.
    /// <para>存储类型；超出已知范围时为 <see langword="null"/>。</para></returns>
    public static SunxiStorageKind? StorageKindFromWord(uint storageWord) =>
        storageWord <= (uint)SunxiStorageKind.Ufs ? (SunxiStorageKind)storageWord : null;

    /// <summary>
    /// Reports whether one role fills the boot slot for the reported mode and storage.
    /// <para>判断某个角色是否满足上报的启动模式与存储类型。</para>
    /// </summary>
    /// <param name="role">Role the package entry provides. <para>包条目提供的角色。</para></param>
    /// <param name="mode">Boot file mode, or <see langword="null"/> when the word is unknown.
    /// <para>启动文件模式；字未知时为 <see langword="null"/>。</para></param>
    /// <param name="storage">Storage kind, or <see langword="null"/> when the word is unknown.
    /// <para>存储类型；字未知时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when the role is the variant to download.
    /// <para>该角色为应下载的变体时为 <see langword="true"/>。</para></returns>
    public static bool Matches(SunxiBootImageRole role, SunxiBootFileMode? mode, SunxiStorageKind? storage) =>
        role switch
        {
            SunxiBootImageRole.PrebootNormal => mode is SunxiBootFileMode.Normal or SunxiBootFileMode.Package,
            SunxiBootImageRole.PrebootToc => mode == SunxiBootFileMode.Toc,
            SunxiBootImageRole.Boot0NormalNand => IsNormalOrPackage(mode)
                && storage is SunxiStorageKind.Nand or SunxiStorageKind.Spinand,
            SunxiBootImageRole.Boot0NormalCard => IsNormalOrPackage(mode)
                && storage is null or SunxiStorageKind.Sdcard or SunxiStorageKind.Emmc
                    or SunxiStorageKind.Emmc3 or SunxiStorageKind.Emmc0 or SunxiStorageKind.Sd1,
            SunxiBootImageRole.Boot0NormalNor => IsNormalOrPackage(mode) && storage == SunxiStorageKind.Spinor,
            SunxiBootImageRole.Boot0NormalUfs => IsNormalOrPackage(mode) && storage == SunxiStorageKind.Ufs,
            SunxiBootImageRole.Boot0TocDefault => mode is not SunxiBootFileMode.Normal
                and not SunxiBootFileMode.Package && storage is null,
            SunxiBootImageRole.Boot0TocSd => IsTocOrUnreported(mode)
                && storage is SunxiStorageKind.Sdcard or SunxiStorageKind.Sd1,
            SunxiBootImageRole.Boot0TocNand => IsTocOrUnreported(mode)
                && storage is SunxiStorageKind.Nand or SunxiStorageKind.Spinand,
            SunxiBootImageRole.Boot0TocNor => IsTocOrUnreported(mode) && storage == SunxiStorageKind.Spinor,
            SunxiBootImageRole.Boot0TocUfs => IsTocOrUnreported(mode) && storage == SunxiStorageKind.Ufs,
            SunxiBootImageRole.Boot1Normal => mode == SunxiBootFileMode.Normal,
            SunxiBootImageRole.Boot1Toc => mode == SunxiBootFileMode.Toc,
            SunxiBootImageRole.Boot1Package => mode == SunxiBootFileMode.Package
                && storage != SunxiStorageKind.Spinor,
            SunxiBootImageRole.Boot1PackageNor => mode == SunxiBootFileMode.Package
                && storage == SunxiStorageKind.Spinor,
            _ => false
        };

    private static bool IsNormalOrPackage(SunxiBootFileMode? mode) =>
        mode is SunxiBootFileMode.Normal or SunxiBootFileMode.Package;

    private static bool IsTocOrUnreported(SunxiBootFileMode? mode) => mode is not SunxiBootFileMode.Normal
        and not SunxiBootFileMode.Package;
}