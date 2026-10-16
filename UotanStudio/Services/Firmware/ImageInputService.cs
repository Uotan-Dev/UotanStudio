using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Text;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.BootSecurity.BootImage.Formats;
using UotanStudio.Firmware.OtaDelta.Dat;
using FormatDetector = UotanStudio.Firmware.Core.FormatDetector;

namespace UotanStudio.Services.Firmware;

/// <summary>Filesystem layout probed from a raw image header.<para>从 raw 镜像头部探测出的文件系统布局。</para></summary>
internal enum RawImageLayout
{
    /// <summary>ext4 filesystem image, magic 0xEF53 at offset 1080.<para>ext4 文件系统镜像，魔数 0xEF53 位于偏移 1080。</para></summary>
    Ext4,

    /// <summary>EROFS filesystem image, magic 0xE0F5E1E2 at offset 1024.<para>EROFS 文件系统镜像，魔数 0xE0F5E1E2 位于偏移 1024。</para></summary>
    Erofs,

    /// <summary>F2FS filesystem image, magic 0xF2F52010 at offset 1024.<para>F2FS 文件系统镜像，魔数 0xF2F52010 位于偏移 1024。</para></summary>
    F2fs,
}

/// <summary>
/// The format identity produced by image probing; persistence reuses the labels mapped by
/// <see cref="ImageInputService.GetFormatLabel"/> and enum names are never display text.
/// <para>镜像探测得到的格式标识。持久化沿用 <see cref="ImageInputService.GetFormatLabel"/>
/// 映射出的既有标签，枚举名本身不作为显示文案。</para>
/// </summary>
internal enum ImageFormatKind
{
    /// <summary>Unrecognized content.<para>未能识别的内容。</para></summary>
    Unknown,

    /// <summary>Raw image without a recognized header.<para>无可识别头部的 raw 镜像。</para></summary>
    Raw,

    /// <summary>Directory entry packed into an image rather than a probe result.<para>打包用的目录条目，而非探测结果。</para></summary>
    Folder,

    /// <summary>OTA block-data transfer list or DAT file.<para>OTA 块数据 transfer list 或 DAT 文件。</para></summary>
    Dat,

    /// <summary>Brotli-compressed block data.<para>Brotli 压缩的块数据。</para></summary>
    Brotli,

    /// <summary>Android logical partition super container.<para>Android 逻辑分区 super 容器。</para></summary>
    Super,

    /// <summary>Android sparse image.<para>Android 稀疏镜像。</para></summary>
    Sparse,

    /// <summary>OTA payload identified by the CrAU magic.<para>以 CrAU 魔数识别的 OTA payload。</para></summary>
    Payload,

    /// <summary>CPIO archive.<para>CPIO 归档。</para></summary>
    Cpio,

    /// <summary>Oppo OFP, OPS or OZIP package.<para>Oppo OFP、OPS 或 OZIP 包。</para></summary>
    Oppo,

    /// <summary>ext4 filesystem image.<para>ext4 文件系统镜像。</para></summary>
    Ext4,

    /// <summary>EROFS filesystem image.<para>EROFS 文件系统镜像。</para></summary>
    Erofs,

    /// <summary>F2FS filesystem image.<para>F2FS 文件系统镜像。</para></summary>
    F2fs,

    /// <summary>FAT12, FAT16 or FAT32 volume.<para>FAT12、FAT16 或 FAT32 卷。</para></summary>
    Fat,

    /// <summary>exFAT volume.<para>exFAT 卷。</para></summary>
    ExFat,

    /// <summary>Android boot image.<para>Android boot 镜像。</para></summary>
    Boot,

    /// <summary>Android init_boot image.<para>Android init_boot 镜像。</para></summary>
    InitBoot,

    /// <summary>Android recovery image.<para>Android recovery 镜像。</para></summary>
    Recovery,

    /// <summary>Vendor boot image.<para>vendor_boot 镜像。</para></summary>
    VendorBoot,

    /// <summary>MediaTek boot image.<para>MediaTek boot 镜像。</para></summary>
    MtkBoot,

    /// <summary>Samsung PXA boot image.<para>三星 PXA boot 镜像。</para></summary>
    SamsungBoot,

    /// <summary>DHTB boot image.<para>DHTB boot 镜像。</para></summary>
    DhtbBoot,

    /// <summary>ChromeOS kernel image.<para>ChromeOS 内核镜像。</para></summary>
    ChromeOsBoot,

    /// <summary>NVIDIA Tegra blob.<para>NVIDIA Tegra blob。</para></summary>
    TegraBlob,

    /// <summary>zImage kernel.<para>zImage 内核。</para></summary>
    ZImage,

    /// <summary>Rockchip RSCE resource container.<para>瑞芯微 RSCE 资源容器。</para></summary>
    RkResource,
}

/// <summary>
/// Image input-side responsibilities: format probing and source-path resolution, returning only plain
/// data such as format identities, labels, layouts and absolute paths.
/// <para>镜像输入侧职责：格式探测与源文件路径解析，只返回格式标识、标签、布局和绝对路径等纯数据。</para>
/// </summary>
internal sealed class ImageInputService
{
    private readonly Func<string, string?> _resolvePath;

    /// <summary>Warning sink taking a category and a message.<para>接收类别与消息的警告输出槽。</para></summary>
    private readonly Action<string, string> _logWarning;

    /// <summary>
    /// Creates the input service bound to a path resolver and a warning sink.
    /// <para>用路径解析委托与警告输出槽创建输入服务。</para>
    /// </summary>
    /// <param name="resolvePath">Maps a project-relative path to an absolute one, or null when unavailable. <para>把工程内相对路径解析为绝对路径，不可用时为 null。</para></param>
    /// <param name="logWarning">Receives a category and message pair for non-fatal problems. <para>接收由类别与消息组成的非致命问题条目。</para></param>
    public ImageInputService(Func<string, string?> resolvePath, Action<string, string> logWarning)
    {
        _resolvePath = resolvePath ?? throw new ArgumentNullException(nameof(resolvePath));
        _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
    }

    /// <summary>
    /// Resolves and validates a source entry, logging a warning and returning null when the relative path
    /// cannot be resolved or the target is missing.
    /// <para>解析并校验源条目：相对路径不可解析或目标不存在时记警告并返回 null。</para>
    /// </summary>
    /// <param name="relativePath">Project-relative path. <para>工程内相对路径。</para></param>
    /// <param name="isDirectory">Whether the entry is a directory (pack mode). <para>是否为目录条目（打包模式）。</para></param>
    /// <param name="operation">Operation name, written into warning text only. <para>操作名，仅写入警告文案。</para></param>
    /// <returns>Absolute source path; null when unavailable. <para>绝对源路径；不可用为 null。</para></returns>
    internal string? ResolveSourcePath(string relativePath, bool isDirectory, string operation)
    {
        var source = _resolvePath(relativePath);
        if (source is null)
        {
            _logWarning("Images", $"{operation} aborted: path not found: {relativePath}  {operation}中止：找不到路径：{relativePath}");
            return null;
        }

        if (isDirectory ? !Directory.Exists(source) : !File.Exists(source))
        {
            _logWarning("Images", $"{operation} aborted: not found: {relativePath}  {operation}中止：找不到：{relativePath}");
            return null;
        }

        return source;
    }

    /// <summary>
    /// Probes the image format and returns the established label string of the detected identity.
    /// <para>探测镜像格式，返回所识别格式标识对应的既有标签字符串。</para>
    /// </summary>
    /// <param name="filePath">Path of the file to probe. <para>待探测的文件路径。</para></param>
    /// <returns>The label produced by <see cref="GetFormatLabel"/>. <para><see cref="GetFormatLabel"/> 给出的标签。</para></returns>
    internal static string DetectFormat(string filePath) => GetFormatLabel(ProbeFormat(filePath));

    /// <summary>
    /// Probes the image format from extension hints and magic numbers found in a 1082-byte header,
    /// reporting the unknown identity when nothing matches.
    /// <para>结合扩展名线索与 1082 字节头部中的魔数探测镜像格式，未命中任何特征时记为未知标识。</para>
    /// </summary>
    /// <param name="filePath">Path of the file to probe. <para>待探测的文件路径。</para></param>
    /// <returns>The detected format identity. <para>探测得到的格式标识。</para></returns>
    internal static ImageFormatKind ProbeFormat(string filePath)
    {
        if (DatFileSet.IsTransferList(filePath)) return ImageFormatKind.Dat;
        if (filePath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) return ImageFormatKind.Dat;
        if (DatFileSet.IsBrotli(filePath)) return ImageFormatKind.Brotli;
        if (IsLpSuper(filePath)) return ImageFormatKind.Super;
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        switch (extension)
        {
            case ".cpio":
                return ImageFormatKind.Cpio;
            case ".ofp":
            case ".ops":
            case ".ozip":
                return ImageFormatKind.Oppo;
            case ".simg":
            case ".sparse":
                return ImageFormatKind.Sparse;
        }

        try
        {
            var header = FormatDetector.ReadHeader(filePath, 1082);

            if (IsElfExecutable(header))
            {
                return ImageFormatKind.Unknown;
            }

            if (IsSparseHeader(header))
            {
                return ImageFormatKind.Sparse;
            }

            if (IsPayloadHeader(header))
            {
                return ImageFormatKind.Payload;
            }

            if (DetectBootFamilyLabel(header, filePath) is { } bootLabel)
            {
                return bootLabel;
            }

            if (IsExt4Superblock(header))
            {
                return ImageFormatKind.Ext4;
            }

            if (header.Length >= 1028
                && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == ErofsMagic)
            {
                return ImageFormatKind.Erofs;
            }

            if (header.Length >= 1028
                && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == F2fsMagic)
            {
                return ImageFormatKind.F2fs;
            }

            if (MatchFatFamilyFormat(header) is { } fatFamily)
            {
                return fatFamily;
            }
        }
        catch
        {
        }

        if (extension is ".img" or ".raw")
        {
            return ImageFormatKind.Raw;
        }

        return extension == ".bin" && Path.GetFileName(filePath).Contains("payload", StringComparison.OrdinalIgnoreCase)
            ? ImageFormatKind.Payload
            : ImageFormatKind.Unknown;
    }

    /// <summary>
    /// Extensions accepted as format-conversion inputs (partition and raw images). Determined by extension only,
    /// so the explorer's menu gate never reads a file header; optical-disc images stay out because DAT/BR targets
    /// Android OTA partitions. OTA artifacts (<c>.dat</c>, <c>.br</c>, <c>.transfer.list</c>) are handled by the
    /// split-volume and open-with entries instead.
    /// <para>被接受为格式转换输入的扩展名（分区镜像与裸镜像）。只按扩展名判定，资源管理器的菜单显隐因此不读文件头；
    /// 光盘镜像不在其内，因为 DAT/BR 的目标是 Android OTA 分区。OTA 工件（<c>.dat</c>、<c>.br</c>、
    /// <c>.transfer.list</c>）改由分卷与打开方式入口处理。</para>
    /// </summary>
    private static readonly FrozenSet<string> ConversionSourceExtensions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ".img", ".raw", ".bin", ".simg", ".sparse", ".super", ".image"
        }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Determines whether the path is an accepted format-conversion input. The click-time conversion validates
    /// the source again against the active sub-project, so this predicate only decides entry visibility.
    /// <para>判断路径是否为可接受的格式转换输入。点击时转换会再对活动子项目校验源，因此本谓词只决定入口显隐。</para>
    /// </summary>
    /// <param name="path">The file path. <para>文件路径。</para></param>
    /// <returns>True for a conversion source extension. <para>是转换源扩展名时为 true。</para></returns>
    internal static bool SupportsFormatConversion(string path)
        => !string.IsNullOrEmpty(path)
            && ConversionSourceExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Maps a format identity to the established label string that is written to persisted state.
    /// <para>把格式标识映射为写入持久化状态的既有标签字符串。</para>
    /// </summary>
    /// <param name="kind">The format identity to label. <para>待映射的格式标识。</para></param>
    /// <returns>The stable label for the identity, or <c>unknown</c>. <para>该标识对应的稳定标签，未列出时为 <c>unknown</c>。</para></returns>
    internal static string GetFormatLabel(ImageFormatKind kind) => kind switch
    {
        ImageFormatKind.Dat => "dat",
        ImageFormatKind.Brotli => "brotli",
        ImageFormatKind.Super => "super",
        ImageFormatKind.Sparse => "sparse",
        ImageFormatKind.Payload => "payload",
        ImageFormatKind.Cpio => "cpio",
        ImageFormatKind.Oppo => "oppo",
        ImageFormatKind.Ext4 => "ext4",
        ImageFormatKind.Erofs => "erofs",
        ImageFormatKind.F2fs => "f2fs",
        ImageFormatKind.Fat => "fat",
        ImageFormatKind.ExFat => "exfat",
        ImageFormatKind.Boot => "boot",
        ImageFormatKind.InitBoot => "init_boot",
        ImageFormatKind.Recovery => "recovery",
        ImageFormatKind.VendorBoot => "vendor_boot",
        ImageFormatKind.MtkBoot => "mtk_boot",
        ImageFormatKind.SamsungBoot => "samsung_boot",
        ImageFormatKind.DhtbBoot => "dhtb_boot",
        ImageFormatKind.ChromeOsBoot => "chromeos_boot",
        ImageFormatKind.TegraBlob => "tegra_blob",
        ImageFormatKind.ZImage => "zimage",
        ImageFormatKind.RkResource => "rk_resource",
        ImageFormatKind.Raw => "raw",
        ImageFormatKind.Folder => "folder",
        _ => "unknown",
    };

    /// <summary>
    /// Reads a persisted format label back into a format identity, treating blank or unrecognized labels
    /// as unknown.
    /// <para>把持久化的格式标签还原为格式标识，空白或未识别的标签按未知处理。</para>
    /// </summary>
    /// <param name="label">The persisted label, possibly null. <para>持久化的标签，可为 null。</para></param>
    /// <returns>The matching identity, or <see cref="ImageFormatKind.Unknown"/>. <para>匹配的格式标识，未命中时为 <see cref="ImageFormatKind.Unknown"/>。</para></returns>
    internal static ImageFormatKind ParseFormatLabel(string? label) => label?.ToLowerInvariant() switch
    {
        "dat" => ImageFormatKind.Dat,
        "brotli" => ImageFormatKind.Brotli,
        "super" => ImageFormatKind.Super,
        "sparse" => ImageFormatKind.Sparse,
        "payload" => ImageFormatKind.Payload,
        "cpio" => ImageFormatKind.Cpio,
        "oppo" => ImageFormatKind.Oppo,
        "ext4" => ImageFormatKind.Ext4,
        "erofs" => ImageFormatKind.Erofs,
        "f2fs" => ImageFormatKind.F2fs,
        "fat" => ImageFormatKind.Fat,
        "exfat" => ImageFormatKind.ExFat,
        "boot" => ImageFormatKind.Boot,
        "init_boot" => ImageFormatKind.InitBoot,
        "recovery" => ImageFormatKind.Recovery,
        "vendor_boot" => ImageFormatKind.VendorBoot,
        "mtk_boot" => ImageFormatKind.MtkBoot,
        "samsung_boot" => ImageFormatKind.SamsungBoot,
        "dhtb_boot" => ImageFormatKind.DhtbBoot,
        "chromeos_boot" => ImageFormatKind.ChromeOsBoot,
        "tegra_blob" => ImageFormatKind.TegraBlob,
        "zimage" => ImageFormatKind.ZImage,
        "rk_resource" => ImageFormatKind.RkResource,
        "raw" => ImageFormatKind.Raw,
        "folder" => ImageFormatKind.Folder,
        _ => ImageFormatKind.Unknown,
    };

    /// <summary>
    /// Decides whether a file is an LP super container through the firmware format registry.
    /// <para>经由固件格式注册表判定文件是否为 LP super 容器。</para>
    /// </summary>
    /// <param name="filePath">Path of the file to probe. <para>待探测的文件路径。</para></param>
    /// <returns><see langword="true"/> when the registered handler is the LP handler. <para>注册表命中的处理器为 LP 处理器时为 <see langword="true"/>。</para></returns>
    private static bool IsLpSuper(string filePath)
    {
        FirmwareFormatRegistry.RegisterAll();
        return FormatDetector.Detect(filePath) is LpFormatHandler;
    }

    /// <summary>
    /// Decides whether a format identity belongs to the boot family.
    /// <para>判定格式标识是否属于 boot 系列。</para>
    /// </summary>
    /// <param name="format">The format identity from <see cref="ProbeFormat"/>. <para><see cref="ProbeFormat"/> 得到的格式标识。</para></param>
    /// <returns><see langword="true"/> for boot-family formats. <para>属于 boot 系列时为 <see langword="true"/>。</para></returns>
    internal static bool IsBootFamilyFormat(ImageFormatKind format) => format is ImageFormatKind.Boot or ImageFormatKind.InitBoot
        or ImageFormatKind.Recovery or ImageFormatKind.VendorBoot or ImageFormatKind.MtkBoot or ImageFormatKind.SamsungBoot
        or ImageFormatKind.DhtbBoot or ImageFormatKind.ChromeOsBoot or ImageFormatKind.TegraBlob or ImageFormatKind.ZImage
        or ImageFormatKind.RkResource;

    /// <summary>
    /// Decides whether an established label string names a boot-family format after it is parsed back
    /// into a format identity.
    /// <para>判定既有格式标签字符串在还原为格式标识后是否属于 boot 系列。</para>
    /// </summary>
    /// <param name="format">The label string (<see cref="DetectFormat"/> or a persisted value). <para>标签字符串（<see cref="DetectFormat"/> 或持久化值）。</para></param>
    /// <returns><see langword="true"/> for boot-family labels. <para>属于 boot 系列时为 <see langword="true"/>。</para></returns>
    internal static bool IsBootFamilyFormat(string format) => IsBootFamilyFormat(ParseFormatLabel(format));

    /// <summary>
    /// Decides the boot-family identity through boot image signature probes, splitting Android boot
    /// images by the partition role taken from the file name.
    /// <para>用 boot 镜像签名探测判定 boot 系列标识，并按文件名的分区角色细分 Android boot 镜像。</para>
    /// </summary>
    /// <param name="header">The already-read image header bytes. <para>已读取的镜像头部字节。</para></param>
    /// <param name="filePath">Path whose file name supplies the partition role. <para>其文件名提供分区角色的路径。</para></param>
    /// <returns>The boot-family identity, or null when no boot signature matches. <para>boot 系列标识，未命中 boot 签名时为 null。</para></returns>
    private static ImageFormatKind? DetectBootFamilyLabel(byte[] header, string filePath)
    {
        if (header.Length < 8)
        {
            return null;
        }

        if (RockchipResourceImage.DetectResource(header) == RockchipResourceImage.BootResourceType.RockchipResource)
        {
            return ImageFormatKind.RkResource;
        }

        return BootImageDetector.Detect(header).Format switch
        {
            BootImageDetector.ImageFormat.AospBoot => Path.GetFileNameWithoutExtension(filePath) switch
            {
                var stem when stem.Contains("init_boot", StringComparison.OrdinalIgnoreCase) => ImageFormatKind.InitBoot,
                var stem when stem.Contains("recovery", StringComparison.OrdinalIgnoreCase) => ImageFormatKind.Recovery,
                _ => ImageFormatKind.Boot
            },
            BootImageDetector.ImageFormat.VendorBoot => ImageFormatKind.VendorBoot,
            BootImageDetector.ImageFormat.MtkBoot => ImageFormatKind.MtkBoot,
            BootImageDetector.ImageFormat.SamsungPxa => ImageFormatKind.SamsungBoot,
            BootImageDetector.ImageFormat.DhtbBoot => ImageFormatKind.DhtbBoot,
            BootImageDetector.ImageFormat.ChromeOs => ImageFormatKind.ChromeOsBoot,
            BootImageDetector.ImageFormat.TegraBlob => ImageFormatKind.TegraBlob,
            BootImageDetector.ImageFormat.ZImage => ImageFormatKind.ZImage,
            _ => null
        };
    }

    /// <summary>
    /// Reads a 1082-byte header and probes the filesystem layout inside a raw image by superblock
    /// structure and magic numbers.
    /// <para>读取 1082 字节头部，按超级块结构与魔数探测 raw 镜像内的文件系统布局。</para>
    /// </summary>
    /// <param name="filePath">Path of the raw image to probe. <para>待探测的 raw 镜像路径。</para></param>
    /// <returns>The matched layout, or null when no magic matches. <para>命中的布局，未命中魔数时为 null。</para></returns>
    internal static RawImageLayout? ProbeFilesystemHeader(string filePath)
    {
        var header = FormatDetector.ReadHeader(filePath, 1082);
        if (IsExt4Superblock(header))
        {
            return RawImageLayout.Ext4;
        }

        if (header.Length >= 1028
            && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == ErofsMagic)
        {
            return RawImageLayout.Erofs;
        }

        if (header.Length >= 1028
            && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == F2fsMagic)
        {
            return RawImageLayout.F2fs;
        }

        return null;
    }

    /// <summary>
    /// Decides by content rather than file extension whether an image carries a filesystem inside it,
    /// treating ELF executables as false.
    /// <para>按内容而非扩展名判定镜像内部是否包含文件系统，ELF 可执行文件一律为假。</para>
    /// </summary>
    /// <param name="filePath">Path of the file to probe. <para>待探测的文件路径。</para></param>
    /// <returns><see langword="true"/> when the content carries a filesystem. <para>内容包含文件系统时为 <see langword="true"/>。</para></returns>
    internal static bool ContainsFileSystem(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var header = FormatDetector.ReadHeader(filePath, 1082);
            if (IsElfExecutable(header))
            {
                return false;
            }

            if (IsFilesystemBootRecord(header) || IsPayloadHeader(header))
            {
                return true;
            }

            if (IsLpSuper(filePath))
            {
                return true;
            }

            return IsSparseHeader(header)
                && IsFilesystemBootRecord(SparseReader.PeekLogicalRange(filePath, 0, 1082));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Determines whether already-read header bytes carry a filesystem volume boot record for ext4,
    /// EROFS, F2FS or the FAT family.
    /// <para>判定已读取的头部字节是否携带 ext4、EROFS、F2FS 或 FAT 系列的文件系统卷引导记录。</para>
    /// </summary>
    /// <param name="header">Header bytes already read from the image. <para>已从镜像读取的头部字节。</para></param>
    /// <returns><see langword="true"/> when a volume boot record matches. <para>命中卷引导记录时为 <see langword="true"/>。</para></returns>
    private static bool IsFilesystemBootRecord(ReadOnlySpan<byte> header) =>
        IsExt4Superblock(header)
        || header.Length >= 1028
        && (BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1024, 4)) == ErofsMagic
            || BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1024, 4)) == F2fsMagic)
        || MatchFatFamilyFormat(header) is not null;

    /// <summary>
    /// Matches a FAT12/FAT16/FAT32 or exFAT volume boot record by the 0x55AA signature and the
    /// filesystem name bytes.
    /// <para>按 0x55AA 签名与文件系统名字节匹配 FAT12/FAT16/FAT32 或 exFAT 卷引导记录。</para>
    /// </summary>
    /// <param name="header">Header bytes already read from the image. <para>已从镜像读取的头部字节。</para></param>
    /// <returns>The matched family, or null when the record does not match. <para>命中的系列，未匹配时为 null。</para></returns>
    private static ImageFormatKind? MatchFatFamilyFormat(ReadOnlySpan<byte> header)
    {
        if (header.Length < 512 || header[510] != 0x55 || header[511] != 0xAA)
        {
            return null;
        }

        var oem12 = Encoding.ASCII.GetString(header.Slice(54, 8)).Trim();
        if (oem12 is "FAT12" or "FAT16")
        {
            return ImageFormatKind.Fat;
        }

        if (header.Length >= 90 && Encoding.ASCII.GetString(header.Slice(82, 8)).Trim() == "FAT32")
        {
            return ImageFormatKind.Fat;
        }

        return header.Length >= 11 && header.Slice(3, 8).SequenceEqual("EXFAT   "u8) ? ImageFormatKind.ExFat : null;
    }

    /// <summary>
    /// Determines whether the header is an ELF executable, which is never an image because its segment
    /// data can coincidentally match a filesystem magic.
    /// <para>判定头部是否为 ELF 可执行文件；其段内数据可能偶然命中文件系统魔数，因此从不作为镜像。</para>
    /// </summary>
    /// <param name="header">Header bytes already read from the image. <para>已从镜像读取的头部字节。</para></param>
    /// <returns><see langword="true"/> when the first four bytes are the ELF magic. <para>头四字节为 ELF 魔数时为 <see langword="true"/>。</para></returns>
    private static bool IsElfExecutable(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[0] == 0x7F && header[1] == (byte)'E'
        && header[2] == (byte)'L' && header[3] == (byte)'F';

    /// <summary>
    /// Determines whether the header carries the Android sparse image magic, little-endian 0xED26FF3A.
    /// <para>判定头部是否为 Android sparse 镜像魔数（小端 0xED26FF3A）。</para>
    /// </summary>
    /// <param name="header">Header bytes already read from the image. <para>已从镜像读取的头部字节。</para></param>
    /// <returns><see langword="true"/> when the sparse magic is present. <para>存在 sparse 魔数时为 <see langword="true"/>。</para></returns>
    private static bool IsSparseHeader(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0, 4)) == SparseFormat.SparseHeaderMagic;

    /// <summary>
    /// Determines whether the header carries the OTA payload magic.
    /// <para>判定头部是否为 OTA payload 魔数。</para>
    /// </summary>
    /// <param name="header">Header bytes already read from the image. <para>已从镜像读取的头部字节。</para></param>
    /// <returns><see langword="true"/> when the payload magic is present. <para>存在 payload 魔数时为 <see langword="true"/>。</para></returns>
    private static bool IsPayloadHeader(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header.Slice(0, 4).SequenceEqual("CrAU"u8);

    /// <summary>EROFS superblock magic at offset 1024.<para>EROFS 超级块魔数，位于偏移 1024。</para></summary>
    private const uint ErofsMagic = 0xE0F5E1E2;

    /// <summary>F2FS superblock magic at offset 1024.<para>F2FS 超级块魔数，位于偏移 1024。</para></summary>
    private const uint F2fsMagic = 0xF2F52010;

    /// <summary>
    /// Validates an ext4 superblock structurally, requiring the 0xEF53 magic together with nonzero inode
    /// and block counts and a block size within 64 KiB.
    /// <para>对 ext4 超级块做结构校验：除魔数 0xEF53 外还要求 inode 数与块数非零，且推导块大小不超过 64 KiB。</para>
    /// </summary>
    /// <param name="header">Header bytes already read from the image. <para>已从镜像读取的头部字节。</para></param>
    /// <returns><see langword="true"/> when every superblock field check passes. <para>全部超级块字段校验通过时为 <see langword="true"/>。</para></returns>
    private static bool IsExt4Superblock(ReadOnlySpan<byte> header) =>
        header.Length >= 1082
        && BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(1080, 2)) == 0xEF53
        && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1024, 4)) != 0
        && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1028, 4)) != 0
        && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1048, 4)) <= 6;
}