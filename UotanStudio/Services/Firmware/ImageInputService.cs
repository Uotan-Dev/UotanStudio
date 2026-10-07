using System.Buffers.Binary;
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

/// <summary>Filesystem layout probed from a raw image header. 从 raw 镜像头部探测出的文件系统布局。</summary>
internal enum RawImageLayout
{
    /// <summary>ext4 filesystem image (magic 0xEF53 at offset 1080). ext4 文件系统镜像（魔数 0xEF53 位于偏移 1080）。</summary>
    Ext4,

    /// <summary>EROFS filesystem image (magic 0xE0F5E1E2 at offset 1024). EROFS 文件系统镜像（魔数 0xE0F5E1E2 位于偏移 1024）。</summary>
    Erofs,

    /// <summary>F2FS filesystem image (magic 0xF2F52010 at offset 1024). F2FS 文件系统镜像（魔数 0xF2F52010 位于偏移 1024）。</summary>
    F2fs,
}

/// <summary>
/// <para>镜像探测得到的格式标识。持久化与界面文本使用 <see cref="ImageInputService.GetFormatLabel"/>
/// 映射出的既有标签，枚举名本身不作为显示文案。</para>
/// The format identity produced by image probing. Persistence and UI text use the established labels
/// mapped by <see cref="ImageInputService.GetFormatLabel"/>; the enum names are never display text.
/// </summary>
internal enum ImageFormatKind
{
    /// <summary>Unrecognized content. 未能识别的内容。</summary>
    Unknown,

    /// <summary>Raw image without a recognized header. 无可识别头部的 raw 镜像。</summary>
    Raw,

    /// <summary>Directory entry packed into an image (not a probe result). 打包用的目录条目（非探测结果）。</summary>
    Folder,

    /// <summary>OTA block-data transfer list or <c>.dat</c>. OTA 块数据 transfer list 或 <c>.dat</c>。</summary>
    Dat,

    /// <summary>Brotli-compressed block data. Brotli 压缩的块数据。</summary>
    Brotli,

    /// <summary>Android logical partition (super) container. Android 逻辑分区（super）容器。</summary>
    Super,

    /// <summary>Android sparse image. Android 稀疏镜像。</summary>
    Sparse,

    /// <summary>OTA payload ("CrAU"). OTA payload（"CrAU"）。</summary>
    Payload,

    /// <summary>CPIO archive. CPIO 归档。</summary>
    Cpio,

    /// <summary>Oppo OFP/OPS/OZIP package. Oppo OFP/OPS/OZIP 包。</summary>
    Oppo,

    /// <summary>ext4 filesystem image. ext4 文件系统镜像。</summary>
    Ext4,

    /// <summary>EROFS filesystem image. EROFS 文件系统镜像。</summary>
    Erofs,

    /// <summary>F2FS filesystem image. F2FS 文件系统镜像。</summary>
    F2fs,

    /// <summary>FAT12/16/32 volume. FAT12/16/32 卷。</summary>
    Fat,

    /// <summary>exFAT volume. exFAT 卷。</summary>
    ExFat,

    /// <summary>AOSP boot image. AOSP boot 镜像。</summary>
    Boot,

    /// <summary>AOSP init_boot image. AOSP init_boot 镜像。</summary>
    InitBoot,

    /// <summary>AOSP recovery image. AOSP recovery 镜像。</summary>
    Recovery,

    /// <summary>Vendor boot image. vendor_boot 镜像。</summary>
    VendorBoot,

    /// <summary>MediaTek boot image. MediaTek boot 镜像。</summary>
    MtkBoot,

    /// <summary>Samsung PXA boot image. 三星 PXA boot 镜像。</summary>
    SamsungBoot,

    /// <summary>DHTB boot image. DHTB boot 镜像。</summary>
    DhtbBoot,

    /// <summary>ChromeOS kernel image. ChromeOS 内核镜像。</summary>
    ChromeOsBoot,

    /// <summary>NVIDIA Tegra blob. NVIDIA Tegra blob。</summary>
    TegraBlob,

    /// <summary>zImage kernel. zImage 内核。</summary>
    ZImage,

    /// <summary>Rockchip RSCE resource container. 瑞芯微 RSCE 资源容器。</summary>
    RkResource,
}

/// <summary>
/// <para>镜像输入侧职责：格式探测与源文件路径解析。只返回纯数据（<see cref="ImageFormatKind"/> 格式标识、
/// 既有标签字符串、布局枚举、绝对路径），依赖经构造参数显式注入；不引用视图、ViewModel 或任何 UI 单例。
/// 解包目录规则仍由 <see cref="ImageOperationPaths"/> 负责，本服务不生成输出路径。</para>
/// Image input-side responsibilities: format probing and source-path resolution. Only plain data is
/// returned (an <see cref="ImageFormatKind"/> identity, its established label, layout enums and absolute
/// paths); dependencies are injected explicitly via constructor parameters; no view, ViewModel or UI
/// singleton is referenced. Unpack-directory rules stay with <see cref="ImageOperationPaths"/>; this
/// service never produces output paths.
/// </summary>
internal sealed class ImageInputService
{
    private readonly Func<string, string?> _resolvePath;

    /// <summary>Warning sink: (category, message). 警告输出槽：（类别，消息）。</summary>
    private readonly Action<string, string> _logWarning;

    /// <summary>
    /// <para>创建输入服务。<paramref name="resolvePath"/> 把工程内相对路径解析为绝对路径
    /// （生产环境来自 <c>ProjectContextService.ResolvePath</c>）；<paramref name="logWarning"/>
    /// 接收（类别，消息）警告（生产环境来自 <c>LogService.Write(LogLevel.Warn, …)</c>）。</para>
    /// Creates the input service. <paramref name="resolvePath"/> maps a project-relative path to an
    /// absolute one (production: <c>ProjectContextService.ResolvePath</c>); <paramref name="logWarning"/>
    /// receives (category, message) warnings (production: <c>LogService.Write(LogLevel.Warn, …)</c>).
    /// </summary>
    public ImageInputService(Func<string, string?> resolvePath, Action<string, string> logWarning)
    {
        _resolvePath = resolvePath ?? throw new ArgumentNullException(nameof(resolvePath));
        _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
    }

    /// <summary>
    /// <para>解析并校验源文件/目录条目：相对路径不可解析或目标不存在时记警告并返回 null，
    /// 目录条目按 <c>Directory.Exists</c> 校验，文件条目按 <c>File.Exists</c> 校验。</para>
    /// Resolves and validates a source file/directory entry: logs a warning and returns null when the
    /// relative path cannot be resolved or the target is missing; directory entries check
    /// <c>Directory.Exists</c> while file entries check <c>File.Exists</c>.
    /// </summary>
    /// <param name="relativePath"><para>工程内相对路径。</para>Project-relative path.</param>
    /// <param name="isDirectory"><para>是否为目录条目（打包模式）。</para>Whether the entry is a directory (pack mode).</param>
    /// <param name="operation"><para>操作名，仅用于警告文案。</para>Operation name, used in warning text only.</param>
    /// <returns><para>绝对源路径；不可用为 null。</para>Absolute source path; null when unavailable.</returns>
    internal string? ResolveSourcePath(string relativePath, bool isDirectory, string operation)
    {
        var source = _resolvePath(relativePath);
        if (source is null)
        {
            _logWarning("Images", $"{operation} aborted: path not found: {relativePath}  {operation}中止：找不到路径：{relativePath}");
            return null;
        }

        // 目录条目（打包模式）检查 Directory.Exists，文件条目检查 File.Exists。
        // Directory entries (pack mode) check Directory.Exists; file entries check File.Exists.
        if (isDirectory ? !Directory.Exists(source) : !File.Exists(source))
        {
            _logWarning("Images", $"{operation} aborted: not found: {relativePath}  {operation}中止：找不到：{relativePath}");
            return null;
        }

        return source;
    }

    /// <summary>
    /// <para>探测镜像格式并返回既有标签字符串：标签由 <see cref="GetFormatLabel"/> 从
    /// <see cref="ProbeFormat"/> 的 <see cref="ImageFormatKind"/> 映射得到，用于持久化与仍按字符串
    /// 比较的调用方；显示文案请用现格式名，不要把标签或枚举名直接当作界面文本。</para>
    /// Probes the image format and returns the established label string: labels come from
    /// <see cref="GetFormatLabel"/> over <see cref="ProbeFormat"/>'s <see cref="ImageFormatKind"/> and serve
    /// persistence and callers that still compare strings; display text uses the current format names,
    /// never a label or the enum name.
    /// </summary>
    internal static string DetectFormat(string filePath) => GetFormatLabel(ProbeFormat(filePath));

    /// <summary>
    /// <para>探测镜像格式：扩展名优先（cpio/oppo/sparse），随后读取 1082 字节头部按魔数判定
    /// （Android sparse 0xED26FF3A、payload "CrAU"、boot 系列经 <see cref="BootImageDetector"/>
    /// 签名探测并按家族与分区角色输出 <see cref="ImageFormatKind.Boot"/>/<see cref="ImageFormatKind.InitBoot"/>/
    /// <see cref="ImageFormatKind.Recovery"/>/<see cref="ImageFormatKind.VendorBoot"/>/<see cref="ImageFormatKind.MtkBoot"/>/
    /// <see cref="ImageFormatKind.SamsungBoot"/>/<see cref="ImageFormatKind.DhtbBoot"/>/<see cref="ImageFormatKind.ChromeOsBoot"/>/
    /// <see cref="ImageFormatKind.TegraBlob"/>/<see cref="ImageFormatKind.ZImage"/> 标签、RSCE 资源容器
    /// <see cref="ImageFormatKind.RkResource"/>、ext4 超级块
    /// 结构校验、erofs 0xE0F5E1E2@1024、f2fs 0xF2F52010@1024、FAT12/16/32/exFAT 卷引导）；
    /// ELF 可执行文件一律返回 <see cref="ImageFormatKind.Unknown"/>，其段内数据可能偶然命中文件系统魔数；
    /// 头部读取失败回退扩展名推断，仍无法识别返回 <see cref="ImageFormatKind.Unknown"/>。
    /// super 判定交给格式注册表（见 <see cref="IsLpSuper"/>）。只读文件头部，不做任何写入或调度。</para>
    /// Probes the image format: extensions first (cpio/oppo/sparse), then a 1082-byte header is read
    /// and matched by magic (Android sparse 0xED26FF3A, payload "CrAU", the boot family via
    /// <see cref="BootImageDetector"/> signature probes labeled by family and partition role, the RSCE
    /// resource container, a structural ext4 superblock check, erofs 0xE0F5E1E2@1024, f2fs
    /// 0xF2F52010@1024, FAT12/16/32/exFAT boot records); an ELF executable always reports
    /// <see cref="ImageFormatKind.Unknown"/> because its segment data can coincidentally match a
    /// filesystem magic; header read failures fall back to extension inference and nothing matching
    /// yields <see cref="ImageFormatKind.Unknown"/>. The super decision is delegated to the format
    /// registry (see <see cref="IsLpSuper"/>). Read-only header access; no writes or dispatching.
    /// </summary>
    internal static ImageFormatKind ProbeFormat(string filePath)
    {
        // OTA block data may begin with filesystem magic; the container suffix takes precedence.
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
            // 读取 1082 字节头部：ext4 魔数 0xEF53 位于 1080、erofs/f2fs 魔数位于 1024。
            // Read a 1082-byte header: ext4 magic 0xEF53 at 1080, erofs/f2fs magic at 1024.
            var header = FormatDetector.ReadHeader(filePath, 1082);

            // ELF 可执行文件不是镜像：其偏移 1024/1080 处的段内数据可能偶然命中文件系统魔数。
            // An ELF executable is never an image: its segment data at offsets 1024/1080 can
            // coincidentally match a filesystem magic. Checked before any extension fallback so a
            // renamed .img ELF never reports "raw".
            if (IsElfExecutable(header))
            {
                return ImageFormatKind.Unknown;
            }

            // Android sparse 魔数 0xED26FF3A（小端）。
            // Android sparse magic 0xED26FF3A (little-endian).
            if (IsSparseHeader(header))
            {
                return ImageFormatKind.Sparse;
            }

            if (IsPayloadHeader(header))
            {
                return ImageFormatKind.Payload;
            }

            // boot 系列：复用库侧 BootImageDetector 签名探测，按家族与分区角色输出区分标签；
            // payload 已由上方 "CrAU" 判定，RSCE 资源容器单独记标签。
            // Boot family: reuse the library BootImageDetector signature probes and label by family and
            // partition role; payload was already decided by "CrAU" above, and RSCE containers get their
            // own label.
            if (DetectBootFamilyLabel(header, filePath) is { } bootLabel)
            {
                return bootLabel;
            }

            // ext4 文件系统镜像：超级块结构校验（见 IsExt4Superblock）。
            // ext4 filesystem image: structural superblock validation (see IsExt4Superblock).
            if (IsExt4Superblock(header))
            {
                return ImageFormatKind.Ext4;
            }

            // erofs 文件系统镜像：魔数 0xE0F5E1E2 位于偏移 1024。
            // erofs filesystem image: magic 0xE0F5E1E2 at offset 1024.
            if (header.Length >= 1028
                && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == ErofsMagic)
            {
                return ImageFormatKind.Erofs;
            }

            // f2fs 文件系统镜像：魔数 0xF2F52010 位于偏移 1024。
            // f2fs filesystem image: magic 0xF2F52010 at offset 1024.
            if (header.Length >= 1028
                && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == F2fsMagic)
            {
                return ImageFormatKind.F2fs;
            }

            // FAT12/FAT16/FAT32/exFAT 文件系统镜像：FAT 系列 OID 位于 54/82，exFAT 检查
            // 偏移 3 的 8 字节文件系统名，并同时要求卷引导记录签名 0x55AA，避免把普通
            // 二进制误判为 FAT 卷。ELF 可执行文件已在上方排除，其段内数据可能偶然命中魔数。
            // FAT12/FAT16/FAT32/exFAT filesystem images: the FAT family OEM name sits at 54/82,
            // exFAT checks the 8-byte filesystem name at offset 3, and both require the 0x55AA
            // boot-record signature so ordinary binaries are not misclassified as FAT volumes.
            // ELF executables are excluded above; their segment data can coincidentally match magic.
            if (MatchFatFamilyFormat(header) is { } fatFamily)
            {
                return fatFamily;
            }
        }
        catch
        {
            // 头部读取失败时回退到扩展名推断。
            // Falls back to extension-based inference when the header cannot be read.
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
    /// <para>把 <see cref="ImageFormatKind"/> 映射为既有格式标签字符串。标签同时用于解决方案快照的
    /// 持久化，因此取值必须与历史写入保持一致；界面显示名不由本方法提供。</para>
    /// Maps an <see cref="ImageFormatKind"/> to the established label string. Labels are also persisted in
    /// the solution snapshot, so the values must stay identical to what earlier builds wrote; display
    /// names are not produced here.
    /// </summary>
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
    /// <para>读入已持久化的格式标签并还原为 <see cref="ImageFormatKind"/>（大小写不敏感）。
    /// 空值或历史遗留的未知标签一律按 <see cref="ImageFormatKind.Unknown"/> 处理，
    /// 由调用方回退到内容探测。</para>
    /// Reads a persisted format label back into an <see cref="ImageFormatKind"/> (case-insensitively).
    /// Blank or legacy unknown labels become <see cref="ImageFormatKind.Unknown"/> so callers fall back to
    /// content probing.
    /// </summary>
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
    /// <para>由格式注册表判定 LP super 容器：RegisterAll 幂等注册全部处理器，Detect 返回的处理器
    /// 即注册表单例，因此按类型判定而不是在宿主新建处理器实例。</para>
    /// Decides an LP super container through the format registry: RegisterAll publishes every handler
    /// idempotently and Detect returns the registered singleton, so the decision is a handler-type check
    /// instead of a handler instance the host constructs on its own.
    /// </summary>
    private static bool IsLpSuper(string filePath)
    {
        FirmwareFormatRegistry.RegisterAll();
        return FormatDetector.Detect(filePath) is LpFormatHandler;
    }

    /// <summary>
    /// <para>判定格式标识是否属于 boot 系列（解包视图登记、解包分派与刷入选择共用）。</para>
    /// Decides whether a format identity belongs to the boot family (shared by unpack-view registration,
    /// extraction dispatch and flash selection).
    /// </summary>
    /// <param name="format"><para><see cref="ProbeFormat"/> 得到的格式标识。</para>The format identity from <see cref="ProbeFormat"/>.</param>
    /// <returns><para>属于 boot 系列时为 <see langword="true"/>。</para><see langword="true"/> for boot-family formats.</returns>
    internal static bool IsBootFamilyFormat(ImageFormatKind format) => format is ImageFormatKind.Boot or ImageFormatKind.InitBoot
        or ImageFormatKind.Recovery or ImageFormatKind.VendorBoot or ImageFormatKind.MtkBoot or ImageFormatKind.SamsungBoot
        or ImageFormatKind.DhtbBoot or ImageFormatKind.ChromeOsBoot or ImageFormatKind.TegraBlob or ImageFormatKind.ZImage
        or ImageFormatKind.RkResource;

    /// <summary>
    /// <para>判定既有格式标签字符串是否属于 boot 系列：先经 <see cref="ParseFormatLabel"/> 还原为
    /// <see cref="ImageFormatKind"/>，未识别的标签按 <see cref="ImageFormatKind.Unknown"/> 处理。</para>
    /// Decides whether an established label string names a boot-family format: it is parsed back into an
    /// <see cref="ImageFormatKind"/> first, and unrecognized labels become
    /// <see cref="ImageFormatKind.Unknown"/>.
    /// </summary>
    /// <param name="format"><para>标签字符串（<see cref="DetectFormat"/> 或持久化值）。</para>The label string (<see cref="DetectFormat"/> or a persisted value).</param>
    /// <returns><para>属于 boot 系列时为 <see langword="true"/>。</para><see langword="true"/> for boot-family labels.</returns>
    internal static bool IsBootFamilyFormat(string format) => IsBootFamilyFormat(ParseFormatLabel(format));

    /// <summary>
    /// <para>用 <see cref="BootImageDetector"/> 的签名探测判定 boot 系列标识：AOSP 镜像按文件名的
    /// 分区角色细分 <see cref="ImageFormatKind.InitBoot"/>/<see cref="ImageFormatKind.Recovery"/>/
    /// <see cref="ImageFormatKind.Boot"/>，vendor_boot、MTK、Samsung PXA、DHTB、ChromeOS、Tegra blob 与
    /// zImage 各自成标识；RSCE 资源容器记 <see cref="ImageFormatKind.RkResource"/>；payload 与
    /// 未识别家族返回 null 交由后续文件系统判定。</para>
    /// Decides the boot-family identity through <see cref="BootImageDetector"/> signature probes: AOSP
    /// images split into <see cref="ImageFormatKind.InitBoot"/>/<see cref="ImageFormatKind.Recovery"/>/
    /// <see cref="ImageFormatKind.Boot"/> by the partition role taken from the file name, while vendor
    /// boot, MTK, Samsung PXA, DHTB, ChromeOS, Tegra blob and zImage each get their own identity; RSCE
    /// resource containers report <see cref="ImageFormatKind.RkResource"/>; payload and unmatched families
    /// return null so the filesystem probes continue.
    /// </summary>
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
    /// <para>读取 1082 字节头部并探测 raw 镜像内的文件系统布局：ext4 超级块结构校验
    /// （见 <see cref="IsExt4Superblock"/>）、erofs/f2fs 魔数位于 1024。头部读取失败时异常原样抛出
    /// （与既有解包回退行为一致），未命中返回 null。</para>
    /// Reads a 1082-byte header and probes the filesystem layout inside a raw image: a structural ext4
    /// superblock check (see <see cref="IsExt4Superblock"/>), erofs/f2fs magic at 1024. Header read
    /// failures propagate unchanged (matching the existing unpack fallback behavior); null when no
    /// magic matches.
    /// </summary>
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
    /// <para>按内容判定镜像内部是否包含文件系统，与文件扩展名无关：卷引导记录命中
    /// EXT4/EROFS/F2FS/FAT12/16/32/exFAT 时为真；OTA payload（"CrAU"）与 LP super
    /// （raw 或 sparse，按容器语义持有文件系统分区）为真；Android sparse 按其展开后的
    /// 逻辑流引导记录判定；ELF 可执行文件一律为假，其段内数据可能偶然命中文件系统魔数。
    /// 头部或逻辑流读取失败返回 false，异常不外抛，也不产生任何写入。</para>
    /// Decides by content, independent of the file extension, whether an image carries a filesystem
    /// internally: true when a volume boot record matches EXT4/EROFS/F2FS/FAT12/16/32/exFAT; true for
    /// an OTA payload ("CrAU") and an LP super (raw or sparse, which holds filesystem partitions by
    /// container semantics); an Android sparse image is decided by the boot record of its expanded
    /// logical stream; an ELF executable is always false because its segment data can coincidentally
    /// match a filesystem magic. Header or logical-stream read failures return false without throwing
    /// and without writing anything.
    /// </summary>
    /// <param name="filePath"><para>待探测的文件路径。</para>Path of the file to probe.</param>
    /// <returns><para>内容内部包含文件系统时为 <see langword="true"/>。</para><see langword="true"/>
    /// when the content carries a filesystem.</returns>
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

            // 文件系统引导记录与 OTA payload 均可由头部直接判定；boot/cpio/dat/br/oppo 等
            // 在偏移 0 处不含引导记录，因而在此自然被排除，无需依赖后缀名。
            // Both a filesystem boot record and an OTA payload are decided from the header alone;
            // boot/cpio/dat/br/oppo carry no boot record at offset 0 and are therefore excluded here
            // naturally, with no reliance on the file extension.
            if (IsFilesystemBootRecord(header) || IsPayloadHeader(header))
            {
                return true;
            }

            // LP super 判定必须先于 sparse 分支：sparse 包装的 super 头部是 sparse 魔数而非文件系统
            // 引导记录，若先走 sparse 逻辑流探测会把 super 误判为不含文件系统。
            // The LP super check must precede the sparse branch: a sparse-wrapped super carries the
            // sparse magic rather than a filesystem boot record in its header, so probing the sparse
            // logical stream first would misclassify the super as carrying no filesystem.
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
    /// <para>判定头部是否携带文件系统卷引导记录：ext4 超级块结构校验、erofs/f2fs 魔数
    /// （位于 1024）或 FAT12/16/32/exFAT 卷引导。不读取文件，仅检查已读取的头部字节。</para>
    /// Determines whether the header carries a filesystem volume boot record: a structural ext4
    /// superblock check, the erofs/f2fs magic (at 1024) or a FAT12/16/32/exFAT volume boot record.
    /// No file is read; only the already-read header bytes are inspected.
    /// </summary>
    private static bool IsFilesystemBootRecord(ReadOnlySpan<byte> header) =>
        IsExt4Superblock(header)
        || header.Length >= 1028
        && (BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1024, 4)) == ErofsMagic
            || BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1024, 4)) == F2fsMagic)
        || MatchFatFamilyFormat(header) is not null;

    /// <summary>
    /// <para>匹配 FAT12/FAT16/FAT32/exFAT 卷引导：要求偏移 510 的 0x55AA 引导记录签名，
    /// 再比对偏移 54/82 的 FAT 文件系统类型名或偏移 3 的 "EXFAT   " 文件系统名。
    /// 命中返回 <see cref="ImageFormatKind.Fat"/>/<see cref="ImageFormatKind.ExFat"/>，否则返回 null。</para>
    /// Matches a FAT12/FAT16/FAT32/exFAT volume boot record: it requires the 0x55AA boot-record
    /// signature at offset 510, then compares the FAT filesystem type name at offset 54/82 or the
    /// "EXFAT   " filesystem name at offset 3. Returns <see cref="ImageFormatKind.Fat"/>/
    /// <see cref="ImageFormatKind.ExFat"/> on a match, else null.
    /// </summary>
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
    /// <para>判定是否为 ELF 可执行文件（头 4 字节 <c>0x7F "ELF"</c>）。ELF 不是镜像：
    /// 其段内数据可能偶然命中文件系统魔数，必须先于任何魔数判定排除。</para>
    /// Determines whether the header is an ELF executable (first four bytes <c>0x7F "ELF"</c>).
    /// An ELF is never an image: its segment data can coincidentally match a filesystem magic, so it
    /// must be excluded before any magic check.
    /// </summary>
    private static bool IsElfExecutable(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[0] == 0x7F && header[1] == (byte)'E'
        && header[2] == (byte)'L' && header[3] == (byte)'F';

    /// <summary>
    /// <para>判定头部是否为 Android sparse 镜像魔数（小端 0xED26FF3A）。</para>
    /// Determines whether the header carries the Android sparse image magic (little-endian 0xED26FF3A).
    /// </summary>
    private static bool IsSparseHeader(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0, 4)) == SparseFormat.SparseHeaderMagic;

    /// <summary>
    /// <para>判定头部是否为 OTA payload 魔数（"CrAU"）。</para>
    /// Determines whether the header carries the OTA payload magic ("CrAU").
    /// </summary>
    private static bool IsPayloadHeader(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header.Slice(0, 4).SequenceEqual("CrAU"u8);

    /// <summary>erofs superblock magic at offset 1024. erofs 超级块魔数，位于偏移 1024。</summary>
    private const uint ErofsMagic = 0xE0F5E1E2;

    /// <summary>f2fs superblock magic at offset 1024. f2fs 超级块魔数，位于偏移 1024。</summary>
    private const uint F2fsMagic = 0xF2F52010;

    /// <summary>
    /// <para>ext4 超级块结构校验：仅比对偏移 1080 的 2 字节魔数 0xEF53 会把偶然落上
    /// <c>53 EF</c> 的任意二进制（如 ELF 段数据）误判为镜像，因此同时要求魔数、
    /// <c>s_inodes_count</c>（@1024）与 <c>s_blocks_count_lo</c>（@1028）非零，且
    /// <c>s_log_block_size</c>（@1048）推导的块大小不超过 64 KiB。</para>
    /// Structural ext4 superblock validation: matching only the 2-byte magic 0xEF53 at offset 1080
    /// misclassifies arbitrary binaries (e.g. ELF segment data) whose bytes happen to be
    /// <c>53 EF</c>, so the check additionally requires a nonzero <c>s_inodes_count</c> (@1024) and
    /// <c>s_blocks_count_lo</c> (@1028) and a <c>s_log_block_size</c> (@1048) whose derived block
    /// size stays within 64 KiB.
    /// </summary>
    private static bool IsExt4Superblock(ReadOnlySpan<byte> header) =>
        header.Length >= 1082
        && BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(1080, 2)) == 0xEF53
        && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1024, 4)) != 0
        && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1028, 4)) != 0
        && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1048, 4)) <= 6;
}