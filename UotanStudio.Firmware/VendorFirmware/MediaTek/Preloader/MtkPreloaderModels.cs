namespace UotanStudio.Firmware.VendorFirmware.MediaTek.Preloader;

/// <summary>
/// Boot storage kind declared by the first bytes of a MediaTek preloader region.
/// <para>MediaTek 预加载器区域首字节声明的引导存储类型。</para>
/// </summary>
public enum MtkPreloaderStorage
{
    /// <summary>The magic is not one of the supported boot-region markers.<para>魔数不属于受支持的引导区标记。</para></summary>
    Unknown,

    /// <summary><c>EMMC_BOOT</c>: an eMMC boot partition dump.<para><c>EMMC_BOOT</c>：eMMC 引导分区转储。</para></summary>
    EmmcBoot,

    /// <summary><c>UFS_BOOT</c>: a UFS boot LUN dump.<para><c>UFS_BOOT</c>：UFS 引导 LUN 转储。</para></summary>
    UfsBoot,

    /// <summary><c>COMBO_BOOT</c>: a combined UFS boot region.<para><c>COMBO_BOOT</c>：组合式 UFS 引导区。</para></summary>
    ComboBoot,

    /// <summary><c>MMM</c>: a raw preloader image, which carries no boot-region offset header.<para><c>MMM</c>：裸预加载器镜像，不含引导区偏移头。</para></summary>
    Raw,
}

/// <summary>
/// The <c>AND_ROMINFO_v</c> flag block located inside a boot-region preloader dump.
/// <para>位于引导区预加载器转储内的 <c>AND_ROMINFO_v</c> 标志块。</para>
/// </summary>
/// <param name="Offset">Absolute offset of the block.<para>块的绝对偏移。</para></param>
/// <param name="Length">Block length in bytes.<para>块字节长度。</para></param>
/// <param name="LockState">The fastboot lock-state byte.<para>fastboot 锁状态字节。</para></param>
public sealed record MtkPreloaderFlagBlock(long Offset, int Length, byte LockState)
{
    /// <summary>Gets whether the block reports a locked fastboot (<c>0x22</c>).<para>获取标志块是否报告 fastboot 已锁定（<c>0x22</c>）。</para></summary>
    public bool IsLocked => LockState == 0x22;

    /// <summary>Gets whether the block reports a hard lock (<c>0x11</c>).<para>获取标志块是否报告硬锁（<c>0x11</c>）。</para></summary>
    public bool IsHardLocked => LockState == 0x11;
}

/// <summary>
/// Parsed description of a MediaTek preloader boot region.
/// <para>MediaTek 预加载器引导区的解析描述。</para>
/// </summary>
public sealed class MtkPreloaderImage
{
    /// <summary>Initializes the parsed preloader.<para>初始化已解析的预加载器。</para></summary>
    /// <param name="filePath">Source path.<para>源路径。</para></param>
    /// <param name="fileSize">Source length in bytes.<para>源字节长度。</para></param>
    /// <param name="storage">Declared boot storage kind.<para>声明的引导存储类型。</para></param>
    /// <param name="codeOffset">Declared code offset read from <c>0x20D</c>.<para>从 <c>0x20D</c> 读出的代码偏移。</para></param>
    /// <param name="flagBlock">The flag block, when present.<para>标志块；不存在时为 null。</para></param>
    public MtkPreloaderImage(
        string filePath,
        long fileSize,
        MtkPreloaderStorage storage,
        long codeOffset,
        MtkPreloaderFlagBlock? flagBlock)
    {
        FilePath = filePath;
        FileSize = fileSize;
        Storage = storage;
        CodeOffset = codeOffset;
        FlagBlock = flagBlock;
    }

    /// <summary>Gets the source path.<para>获取源路径。</para></summary>
    /// <value>The full path supplied to the reader.<para>传给读取器的完整路径。</para></value>
    public string FilePath { get; }

    /// <summary>Gets the source length in bytes.<para>获取源字节长度。</para></summary>
    /// <value>The source stream length.<para>源流长度。</para></value>
    public long FileSize { get; }

    /// <summary>Gets the declared boot storage kind.<para>获取声明的引导存储类型。</para></summary>
    /// <value>A value of <see cref="MtkPreloaderStorage"/>.<para><see cref="MtkPreloaderStorage"/> 的取值。</para></value>
    public MtkPreloaderStorage Storage { get; }

    /// <summary>Gets the declared code offset (<c>0x20D * 256</c>).<para>获取声明的代码偏移（<c>0x20D * 256</c>）。</para></summary>
    /// <value>The byte offset the reference script rearranges code from.<para>参考脚本据以重排代码的字节偏移。</para></value>
    public long CodeOffset { get; }

    /// <summary>Gets the flag block, when present.<para>获取标志块；不存在时为 null。</para></summary>
    /// <value>The located block, or <see langword="null"/>.<para>已定位的块，或 <see langword="null"/>。</para></value>
    public MtkPreloaderFlagBlock? FlagBlock { get; }

    /// <summary>Gets whether the patch can be applied: a boot-region magic, a flag block and a code offset below <c>0x2000</c>.<para>获取是否可应用补丁：引导区魔数、标志块，且代码偏移低于 <c>0x2000</c>。</para></summary>
    /// <value><see langword="true"/> when every prerequisite holds.<para>全部前提成立时为 <see langword="true"/>。</para></value>
    public bool CanPatch => Storage is MtkPreloaderStorage.EmmcBoot or MtkPreloaderStorage.UfsBoot or MtkPreloaderStorage.ComboBoot
        && FlagBlock is not null
        && CodeOffset < MtkPreloaderConstants.CodeTarget;
}

/// <summary>
/// Options for a preloader patch run.
/// <para>预加载器修补运行的选项。</para>
/// </summary>
public sealed record MtkPreloaderPatchOptions
{
    /// <summary>Gets or sets a value indicating whether the flag-block unlock byte is cleared.<para>获取或设置是否清零标志块的解锁字节。</para></summary>
    public bool UnlockFastboot { get; init; } = true;

    /// <summary>Gets or sets a value indicating whether a backup of the source is created.<para>获取或设置是否创建源备份。</para></summary>
    public bool CreateBackup { get; init; } = true;
}

/// <summary>
/// Result of a preloader patch run.
/// <para>预加载器修补运行的结果。</para>
/// </summary>
/// <param name="OutputPath">Written output path.<para>已写输出版本路径。</para></param>
/// <param name="BackupPath">Backup path, when written.<para>已写备份路径；未写时为 null。</para></param>
/// <param name="Notes">Non-localized notes describing each change.<para>描述每项变更的未本地化说明。</para></param>
public sealed record MtkPreloaderPatchResult(string OutputPath, string? BackupPath, IReadOnlyList<string> Notes);

internal static class MtkPreloaderConstants
{
    public const int CodeTarget = 0x2000;

    public const long DefaultBootRegionSize = 4L * 1024 * 1024;

    public const long MaximumRegionBytes = 64L * 1024 * 1024;

    public const int FlagBlockLength = 0x78;

    public const int LockStateOffsetInBlock = 0x4C;

    public const int FlagBlockTarget = 0x1000;

    public const int LockByteWriteOffset = 0x104C;

    public const int CodeOffsetByte = 0x20D;

    public const int CodeOffsetHighByte = 0x21D;

    public const int CodeOffsetLow1 = 0x211;

    public const int CodeOffsetLow2 = 0x212;

    public const int CodeOffsetLow3 = 0x221;

    public const int CodeOffsetLow4 = 0x222;

    public const int TrailerReserve = 0x3000;

    public static ReadOnlySpan<byte> FlagMagic => "AND_ROMINFO_v"u8;

    public static ReadOnlySpan<byte> EmmcBootMagic => "EMMC_BOOT"u8;

    public static ReadOnlySpan<byte> UfsBootMagic => "UFS_BOOT"u8;

    public static ReadOnlySpan<byte> ComboBootMagic => "COMBO_BOOT"u8;

    public static ReadOnlySpan<byte> RawMagic => [0x4D, 0x4D, 0x4D, 0x01];
}