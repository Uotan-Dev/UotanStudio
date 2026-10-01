using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.BootSecurity.BootImage.Formats;
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
/// <para>镜像输入侧职责：格式探测与源文件路径解析。只返回纯数据（格式字符串、布局枚举、绝对路径），
/// 依赖经构造参数显式注入；不引用视图、ViewModel 或任何 UI 单例。解包目录规则仍由
/// <see cref="ImageOperationPaths"/> 负责，本服务不生成输出路径。</para>
/// Image input-side responsibilities: format probing and source-path resolution. Only plain data is
/// returned (format strings, layout enums, absolute paths); dependencies are injected explicitly via
/// constructor parameters; no view, ViewModel or UI singleton is referenced. Unpack-directory rules
/// stay with <see cref="ImageOperationPaths"/>; this service never produces output paths.
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
    /// <para>探测镜像格式：扩展名优先（cpio/oppo/sparse），随后读取 1082 字节头部按魔数判定
    /// （Android sparse 0xED26FF3A、payload "CrAU"、boot 系列经 <see cref="BootImageDetector"/>
    /// 签名探测并按家族与分区角色输出 <c>boot</c>/<c>init_boot</c>/<c>recovery</c>/<c>vendor_boot</c>/
    /// <c>mtk_boot</c>/<c>samsung_boot</c>/<c>dhtb_boot</c>/<c>chromeos_boot</c>/<c>tegra_blob</c>/
    /// <c>zimage</c> 标签、RSCE 资源容器 <c>rk_resource</c>、ext4 超级块
    /// 结构校验、erofs 0xE0F5E1E2@1024、f2fs 0xF2F52010@1024、FAT12/16/32/exFAT 卷引导）；
    /// ELF 可执行文件一律返回 <c>unknown</c>，其段内数据可能偶然命中文件系统魔数；头部读取
    /// 失败回退扩展名推断，仍无法识别返回 "unknown"。只读文件头部，不做任何写入或调度。</para>
    /// Probes the image format: extensions first (cpio/oppo/sparse), then a 1082-byte header is read
    /// and matched by magic (Android sparse 0xED26FF3A, payload "CrAU", the boot family via
    /// <see cref="BootImageDetector"/> signature probes labeled by family and partition role as
    /// <c>boot</c>/<c>init_boot</c>/<c>recovery</c>/<c>vendor_boot</c>/<c>mtk_boot</c>/<c>samsung_boot</c>/
    /// <c>dhtb_boot</c>/<c>chromeos_boot</c>/<c>tegra_blob</c>/<c>zimage</c>, the RSCE resource container
    /// as <c>rk_resource</c>,
    /// a structural ext4 superblock check, erofs 0xE0F5E1E2@1024, f2fs 0xF2F52010@1024,
    /// FAT12/16/32/exFAT boot records); ELF executables always return <c>unknown</c> because their
    /// segment data can coincidentally match a filesystem magic; header read failures fall back to
    /// extension inference, and "unknown" is returned when nothing matches. Read-only header access;
    /// no writes or dispatching.
    /// </summary>
    internal static string DetectFormat(string filePath)
    {
        // OTA block data may begin with filesystem magic; the container suffix takes precedence.
        if (filePath.EndsWith(".transfer.list", StringComparison.OrdinalIgnoreCase)) return "dat";
        if (filePath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) return "dat";
        if (filePath.EndsWith(".br", StringComparison.OrdinalIgnoreCase)) return "brotli";
        if (new UotanStudio.Firmware.BlockImage.Lp.LpFormatHandler().CanHandle(filePath)) return "super";
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        switch (extension)
        {
            case ".cpio":
                return "cpio";
            case ".ofp":
            case ".ops":
            case ".ozip":
                return "oppo";
            case ".simg":
            case ".sparse":
                return "sparse";
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
                return "unknown";
            }

            // Android sparse 魔数 0xED26FF3A（小端）。
            // Android sparse magic 0xED26FF3A (little-endian).
            if (IsSparseHeader(header))
            {
                return "sparse";
            }

            if (IsPayloadHeader(header))
            {
                return "payload";
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
                return "ext4";
            }

            // erofs 文件系统镜像：魔数 0xE0F5E1E2 位于偏移 1024。
            // erofs filesystem image: magic 0xE0F5E1E2 at offset 1024.
            if (header.Length >= 1028
                && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == ErofsMagic)
            {
                return "erofs";
            }

            // f2fs 文件系统镜像：魔数 0xF2F52010 位于偏移 1024。
            // f2fs filesystem image: magic 0xF2F52010 at offset 1024.
            if (header.Length >= 1028
                && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1024, 4)) == F2fsMagic)
            {
                return "f2fs";
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
            return "raw";
        }

        return extension == ".bin" && Path.GetFileName(filePath).Contains("payload", StringComparison.OrdinalIgnoreCase) ? "payload" : "unknown";
    }

    /// <summary>
    /// <para>判定格式标签是否属于 boot 系列（解包视图登记、解包分派与刷入选择共用）。</para>
    /// Decides whether a format label belongs to the boot family (shared by unpack-view registration,
    /// extraction dispatch and flash selection).
    /// </summary>
    /// <param name="format"><para><see cref="DetectFormat"/> 返回的格式标签。</para>Format label from <see cref="DetectFormat"/>.</param>
    /// <returns><para>属于 boot 系列时为 <see langword="true"/>。</para><see langword="true"/> for boot-family labels.</returns>
    internal static bool IsBootFamilyFormat(string format) => format is "boot" or "init_boot" or "recovery"
        or "vendor_boot" or "mtk_boot" or "samsung_boot" or "dhtb_boot" or "chromeos_boot" or "tegra_blob"
        or "zimage" or "rk_resource";

    /// <summary>
    /// <para>用 <see cref="BootImageDetector"/> 的签名探测判定 boot 系列标签：AOSP 镜像按文件名的
    /// 分区角色细分 <c>init_boot</c>/<c>recovery</c>/<c>boot</c>，vendor_boot、MTK、Samsung PXA、DHTB、
    /// ChromeOS、Tegra blob 与 zImage 各自成标签；RSCE 资源容器记 <c>rk_resource</c>；payload 与
    /// 未识别家族返回 null 交由后续文件系统判定。</para>
    /// Decides the boot-family label through <see cref="BootImageDetector"/> signature probes: AOSP
    /// images split into <c>init_boot</c>/<c>recovery</c>/<c>boot</c> by the partition role taken from
    /// the file name, while vendor boot, MTK, Samsung PXA, DHTB, ChromeOS, Tegra blob and zImage each
    /// get their own label; RSCE resource containers report <c>rk_resource</c>; payload and unmatched
    /// families return null so the filesystem probes continue.
    /// </summary>
    private static string? DetectBootFamilyLabel(byte[] header, string filePath)
    {
        if (header.Length < 8)
        {
            return null;
        }

        if (RockchipResourceImage.DetectResource(header) == RockchipResourceImage.BootResourceType.RockchipResource)
        {
            return "rk_resource";
        }

        return BootImageDetector.Detect(header).Format switch
        {
            BootImageDetector.ImageFormat.AospBoot => Path.GetFileNameWithoutExtension(filePath) switch
            {
                var stem when stem.Contains("init_boot", StringComparison.OrdinalIgnoreCase) => "init_boot",
                var stem when stem.Contains("recovery", StringComparison.OrdinalIgnoreCase) => "recovery",
                _ => "boot"
            },
            BootImageDetector.ImageFormat.VendorBoot => "vendor_boot",
            BootImageDetector.ImageFormat.MtkBoot => "mtk_boot",
            BootImageDetector.ImageFormat.SamsungPxa => "samsung_boot",
            BootImageDetector.ImageFormat.DhtbBoot => "dhtb_boot",
            BootImageDetector.ImageFormat.ChromeOs => "chromeos_boot",
            BootImageDetector.ImageFormat.TegraBlob => "tegra_blob",
            BootImageDetector.ImageFormat.ZImage => "zimage",
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
            if (new UotanStudio.Firmware.BlockImage.Lp.LpFormatHandler().CanHandle(filePath))
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
    /// 命中返回 <c>"fat"</c>/<c>"exfat"</c>，否则返回 null。</para>
    /// Matches a FAT12/FAT16/FAT32/exFAT volume boot record: it requires the 0x55AA boot-record
    /// signature at offset 510, then compares the FAT filesystem type name at offset 54/82 or the
    /// "EXFAT   " filesystem name at offset 3. Returns <c>"fat"</c>/<c>"exfat"</c> on a match, else null.
    /// </summary>
    private static string? MatchFatFamilyFormat(ReadOnlySpan<byte> header)
    {
        if (header.Length < 512 || header[510] != 0x55 || header[511] != 0xAA)
        {
            return null;
        }

        var oem12 = Encoding.ASCII.GetString(header.Slice(54, 8)).Trim();
        if (oem12 is "FAT12" or "FAT16")
        {
            return "fat";
        }

        if (header.Length >= 90 && Encoding.ASCII.GetString(header.Slice(82, 8)).Trim() == "FAT32")
        {
            return "fat";
        }

        return header.Length >= 11 && Encoding.ASCII.GetString(header.Slice(3, 8)) == "EXFAT   " ? "exfat" : null;
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
        header.Length >= 4 && Encoding.ASCII.GetString(header.Slice(0, 4)) == "CrAU";

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