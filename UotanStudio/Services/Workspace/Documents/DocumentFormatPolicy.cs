using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Text;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.BlockImage;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.BlockImage.Mbr;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.DeviceTree.Dt;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.F2fs;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.VendorFirmware.Rockchip;
using UotanStudio.Services.Ui.Workspace.Documents;
using UotanStudio.UiAbstractions;

namespace UotanStudio.Services.Workspace.Documents;

/// <summary>
/// Document format policy: the authority for open-mode routing and the format evidence behind it.
/// Content checks delegate to the firmware format registry, its format handlers, the magic engine and the
/// partition-table probe, so this policy never restates a format signature; extension checks remain
/// display/open hints only.
/// <para>文档格式策略：打开方式路由与判定证据的权威来源。内容判定委托给固件格式注册表、各格式处理器、
/// 魔数引擎与分区表探测器，因此本策略不再复述任何格式签名；扩展名判定仅作为展示与打开方式提示。</para>
/// </summary>
public static class DocumentFormatPolicy
{
    /// <summary>
    /// Bounded file-header length used for format and binary-content detection during automatic opening.
    /// <para>自动打开时用于格式与二进制内容判定的有界文件头长度。</para>
    /// </summary>
    public const int ContentProbeLength = 65536;

    /// <summary>Resolves the open mode for the given path: <c>.dts→Text</c>, <c>.dtb/.dtbo→Dtb</c>, known firmware extensions (aggregated from the format-handler registry, e.g. <c>.img/.pac/.ops/.nb0</c>)→<c>Firmware</c>, known binary/firmware extensions→<c>Hex</c>, known text/code extensions→<c>Text</c>, else <c>Auto</c>.
    /// <para>解析指定文件应使用的打开方式。规则：<c>.dts→Text</c>、<c>.dtb/.dtbo→Dtb</c>、 已知固件扩展名（格式处理器注册表聚合，如 <c>.img/.pac/.ops/.nb0</c>）→<c>Firmware</c>、 已知二进制/固件扩展名→<c>Hex</c>、已知文本/代码扩展名→<c>Text</c>、其余→<c>Auto</c>。</para></summary>
    /// <param name="path">A file name or full path.<para>文件名或完整路径。</para></param>
    /// <returns>The resolved open mode.<para>解析出的打开方式。</para></returns>
    public static EditorOpenMode ResolveMode(string path) =>
        DocumentOpenRegistry.ResolveMode(path);

    /// <summary>Resolves the open mode; for unknown/no-extension files, uses the loaded header bytes as a magic fallback. Valid ELF headers open the information panel; other strong binary types use <c>Hex</c>.
    /// <para>解析应使用的打开方式；对未知/无扩展名文件，以已加载的头部字节做 magic 兜底， 有效 ELF 头进入信息面板，其余强二进制类型返回 <c>Hex</c>。</para></summary>
    /// <param name="path">A file name or full path.<para>文件名或完整路径。</para></param>
    /// <param name="header">Optional loaded file header bytes; magic fallback is skipped when <c>null</c>.
    /// <para>可选的已加载文件头部字节；为 <c>null</c> 时不计数 magic 兜底。</para></param>
    /// <param name="detection">Optional format-detection result used by the registry rules.
    /// <para>可选的格式检测结果，供注册表规则使用。</para></param>
    /// <returns>The resolved open mode.<para>解析出的打开方式。</para></returns>
    public static EditorOpenMode ResolveMode(string path, byte[]? header, FormatDetectionResult? detection = null) =>
        DocumentOpenRegistry.ResolveMode(path, header, detection);

    /// <summary>
    /// Determines whether a bounded content sample is clearly binary. Samples with a UTF text BOM remain
    /// text; otherwise a NUL byte or more than 30% control characters classifies the sample as binary.
    /// <para>通过有界内容样本判断文件是否明显为二进制。带 UTF 文本 BOM 的样本保持文本；
    /// 否则出现 NUL，或控制字符占比超过 30%，即判为二进制。</para>
    /// </summary>
    /// <param name="data">Bounded content sample.<para>有界内容样本。</para></param>
    /// <returns><see langword="true"/> when the sample is binary.<para>样本为二进制时为 <see langword="true"/>。</para></returns>
    public static bool IsBinaryContent(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || HasTextBom(data))
        {
            return false;
        }

        var checkLength = Math.Min(data.Length, ContentProbeLength);
        var nonPrintable = 0;
        for (var i = 0; i < checkLength; i++)
        {
            var value = data[i];
            if (value == 0)
            {
                return true;
            }

            if (value < 0x20 && value != (byte)'\r' && value != (byte)'\n' && value != (byte)'\t')
            {
                nonPrintable++;
            }
        }

        return (double)nonPrintable / checkLength > 0.3d;
    }

    private static bool HasTextBom(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return true;
        }

        return data.Length >= 2
               && (data[0] == 0xFF && data[1] == 0xFE
                   || data[0] == 0xFE && data[1] == 0xFF);
    }

    /// <summary>
    /// Whether the extension is a DTB/DTBO one.
    /// <para>是否为 DTB/DTBO 扩展名。</para>
    /// </summary>
    /// <param name="ext">Extension with or without the leading dot.<para>带或不带前导点的扩展名。</para></param>
    /// <returns><see langword="true"/> for <c>.dtb</c> or <c>.dtbo</c>.<para>为 <c>.dtb</c> 或 <c>.dtbo</c> 时返回 <see langword="true"/>。</para></returns>
    public static bool IsDtbExtension(string? ext) =>
        ext is not null && ext.Equals(".dtb", StringComparison.OrdinalIgnoreCase)
        || ext is not null && ext.Equals(".dtbo", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the extension is a DEX one.
    /// <para>是否为 DEX 扩展名。</para>
    /// </summary>
    /// <param name="ext">Extension with or without the leading dot.<para>带或不带前导点的扩展名。</para></param>
    /// <returns><see langword="true"/> for <c>.dex</c>.<para>为 <c>.dex</c> 时返回 <see langword="true"/>。</para></returns>
    public static bool IsDexExtension(string? ext) =>
        ext is not null && ext.Equals(".dex", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the extension is a JAR container one (opens in the DEX editor to browse
    /// classes*.dex entries and resources).
    /// <para>是否为 JAR 容器扩展名（走 DEX 编辑器浏览内部 classes*.dex 与资源）。</para>
    /// </summary>
    /// <param name="ext">Extension with or without the leading dot.<para>带或不带前导点的扩展名。</para></param>
    /// <returns><see langword="true"/> for <c>.jar</c>.<para>为 <c>.jar</c> 时返回 <see langword="true"/>。</para></returns>
    public static bool IsJarExtension(string? ext) =>
        ext is not null && ext.Equals(".jar", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the extension is an ARSC resource-table one.
    /// <para>是否为 ARSC 资源表扩展名。</para>
    /// </summary>
    /// <param name="ext">Extension with or without the leading dot.<para>带或不带前导点的扩展名。</para></param>
    /// <returns><see langword="true"/> for <c>.arsc</c>.<para>为 <c>.arsc</c> 时返回 <see langword="true"/>。</para></returns>
    public static bool IsArscExtension(string? ext) =>
        ext is not null && ext.Equals(".arsc", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the extension is an APK package one.
    /// <para>是否为 APK 包扩展名。</para>
    /// </summary>
    /// <param name="ext">Extension with or without the leading dot.<para>带或不带前导点的扩展名。</para></param>
    /// <returns><see langword="true"/> for <c>.apk</c>.<para>为 <c>.apk</c> 时返回 <see langword="true"/>。</para></returns>
    public static bool IsApkExtension(string? ext) =>
        ext is not null && ext.Equals(".apk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the extension is a QCN one. The OLE2/CFB magic is shared with .doc/.msi files, so
    /// routing is extension-only; non-QCN content surfaces a localized error at load time.
    /// <para>是否为 QCN 扩展名。注意 OLE2/CFB 魔数与 .doc/.msi 共用，因此仅按扩展名路由，
    /// 非 QCN 内容在编辑器加载时给出本地化错误。</para>
    /// </summary>
    /// <param name="ext">Extension with or without the leading dot.<para>带或不带前导点的扩展名。</para></param>
    /// <returns><see langword="true"/> for <c>.qcn</c>.<para>为 <c>.qcn</c> 时返回 <see langword="true"/>。</para></returns>
    public static bool IsQcnExtension(string? ext) =>
        ext is not null && ext.Equals(".qcn", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the extension is a DTS device-tree source extension.
    /// <para>是否为 DTS 设备树源文件扩展名。</para>
    /// </summary>
    /// <param name="ext">Extension with or without the leading dot.<para>带或不带前导点的扩展名。</para></param>
    /// <returns><see langword="true"/> for <c>.dts</c>.<para>为 <c>.dts</c> 时返回 <see langword="true"/>。</para></returns>
    public static bool IsDtsExtension(string? ext) =>
        ext is not null && ext.Equals(".dts", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the extension is a filesystem-image one (.img/.ext4/.erofs/.f2fs).
    /// <para>是否为文件系统镜像扩展名（.img/.ext4/.erofs/.f2fs）。</para>
    /// </summary>
    /// <param name="ext">Extension with or without the leading dot.<para>带或不带前导点的扩展名。</para></param>
    /// <returns><see langword="true"/> for the filesystem-image extensions.<para>命中文件系统镜像扩展名时为 <see langword="true"/>。</para></returns>
    public static bool IsFilesystemImageExtension(string? ext)
    {
        if (ext is null)
        {
            return false;
        }

        return ext.Equals(".img", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ext4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".erofs", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".f2fs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the extension is a CPIO/TAR archive one (.cpio/.tar).
    /// <para>是否为 CPIO/TAR 归档扩展名（.cpio/.tar）。</para>
    /// </summary>
    /// <param name="ext">Extension with or without the leading dot.<para>带或不带前导点的扩展名。</para></param>
    /// <returns><see langword="true"/> for the archive extensions.<para>命中归档扩展名时为 <see langword="true"/>。</para></returns>
    public static bool IsCpioArchiveExtension(string? ext)
    {
        if (ext is null)
        {
            return false;
        }

        return ext.Equals(".cpio", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tar", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether the file content carries an ext4, EROFS or F2FS filesystem, unwrapping Android
    /// sparse images through the firmware sparse reader.
    /// <para>判断文件内容是否携带 ext4、EROFS 或 F2FS 文件系统；Android sparse 镜像经固件 sparse 读取器解包后判定。</para>
    /// </summary>
    /// <param name="path">A file name or full path.<para>文件名或完整路径。</para></param>
    /// <returns><see langword="true"/> when a filesystem superblock matches.<para>命中文件系统超级块时为 <see langword="true"/>。</para></returns>
    public static bool IsFilesystemImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var header = FormatDetector.ReadHeader(path, FilesystemHeaderLength);
            if (IsFilesystemMagic(header))
            {
                return true;
            }

            if (IsSparseImage(path))
            {
                var logical = SparseReader.PeekLogicalRange(path, 0, FilesystemHeaderLength);
                return IsFilesystemMagic(logical);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Determines whether the file is an Android sparse image.
    /// <para>判断文件是否为 Android sparse 格式镜像。</para>
    /// </summary>
    /// <param name="path">A file name or full path.<para>文件名或完整路径。</para></param>
    /// <returns><see langword="true"/> when the sparse magic is present.<para>存在 sparse 魔数时为 <see langword="true"/>。</para></returns>
    public static bool IsSparseImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var magic = FormatDetector.ReadHeader(path, sizeof(uint));
            return magic.Length >= sizeof(uint)
                && BinaryPrimitives.ReadUInt32LittleEndian(magic) == SparseFormat.SparseHeaderMagic;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the header holds the FDT device-tree magic or the DTBO table magic.
    /// <para>是否为 FDT 设备树魔数或 DTBO 表魔数。</para>
    /// </summary>
    /// <param name="header">Leading file bytes.<para>文件起始字节。</para></param>
    /// <returns><see langword="true"/> when the big-endian magic matches a firmware device tree.<para>大端魔数命中固件设备树时为 <see langword="true"/>。</para></returns>
    public static bool IsDtbMagic(ReadOnlySpan<byte> header) =>
        header.Length >= sizeof(uint)
        && BinaryPrimitives.ReadUInt32BigEndian(header) is FdtConstants.FdtMagic or FdtConstants.DtboMagic;

    /// <summary>
    /// Whether the header holds the DEX magic (ASCII "dex\n" at offset 0).
    /// <para>是否为 DEX 魔数（ASCII "dex\n" 位于偏移 0）。</para>
    /// </summary>
    /// <param name="header">Leading file bytes.<para>文件起始字节。</para></param>
    /// <returns><see langword="true"/> when the DEX marker is present.<para>存在 DEX 标记时为 <see langword="true"/>。</para></returns>
    public static bool IsDexMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[0] == (byte)'d' && header[1] == (byte)'e'
            && header[2] == (byte)'x' && header[3] == (byte)'\n';

    /// <summary>
    /// Checks strong Android/vendor firmware signatures independent of the file extension, through the
    /// firmware boot-image detector, payload probe and the vendor container constants.
    /// <para>与扩展名无关地检查 Android/厂商固件强特征：经固件 boot 镜像探测器、payload 探测与厂商容器常量判定。</para>
    /// </summary>
    /// <param name="header">Leading file bytes.<para>文件起始字节。</para></param>
    /// <returns><see langword="true"/> when a strong firmware signature matches.<para>命中强固件签名时为 <see langword="true"/>。</para></returns>
    public static bool IsFirmwareMagic(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
        {
            return false;
        }

        var data = header.ToArray();
        if (PayloadTool.IsPayload(data))
        {
            return true;
        }

        if (BootImageDetector.Detect(data).Format is BootImageDetector.ImageFormat.AospBoot
            or BootImageDetector.ImageFormat.VendorBoot
            or BootImageDetector.ImageFormat.SamsungPxa
            or BootImageDetector.ImageFormat.ZImage
            or BootImageDetector.ImageFormat.Payload)
        {
            return true;
        }

        if (header.Length < sizeof(uint))
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) is RkFwHeader.MagicRkfw or RkFwHeader.MagicRkaf)
        {
            return true;
        }

        if (header[..4].SequenceEqual(UpdateAppMagic))
        {
            return true;
        }

        return FormatDetector.HasMagic(data, AvbFooter.ExpectedMagic);
    }

    /// <summary>
    /// Huawei update.app container marker, kept as a host-side fallback because the firmware processor is internal.
    /// <para>华为 update.app 容器标记；因固件处理器为 internal，此处保留宿主侧回退标记。</para>
    /// </summary>
    private static ReadOnlySpan<byte> UpdateAppMagic => [0x55, 0xAA, 0x5A, 0xA5];

    /// <summary>
    /// Whether the header holds a ZIP container marker, covering regular, empty and spanned archives.
    /// <para>是否为 ZIP 容器标记，覆盖常规、空归档与跨卷条目。</para>
    /// </summary>
    /// <param name="header">Leading file bytes.<para>文件起始字节。</para></param>
    /// <returns><see langword="true"/> when a ZIP local/either central-directory marker is present.<para>存在 ZIP 本地头或中央目录标记时为 <see langword="true"/>。</para></returns>
    public static bool IsZipMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[0] == (byte)'P' && header[1] == (byte)'K'
            && (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07)
            && (header[3] == 0x04 || header[3] == 0x06 || header[3] == 0x08);

    /// <summary>
    /// Determines whether the header bytes match any filesystem handler (ext4/EROFS/F2FS/FAT12/16/32/exFAT).
    /// <para>判断头部字节是否命中任意文件系统处理器（ext4/EROFS/F2FS/FAT12/16/32/exFAT）。</para>
    /// </summary>
    /// <param name="header">Leading file bytes.<para>文件起始字节。</para></param>
    /// <returns><see langword="true"/> when a filesystem signature matches.<para>命中文件系统签名时为 <see langword="true"/>。</para></returns>
    public static bool IsFilesystemMagic(ReadOnlySpan<byte> header) =>
        IsExt4Magic(header) || IsErofsMagic(header) || IsF2fsMagic(header) || IsFatFamilyMagic(header);

    /// <summary>
    /// Whether the header carries a FAT family (FAT12/FAT16/FAT32) or exFAT volume boot record, used only as
    /// an open-mode hint; structural validation stays with the <c>UotanStudio.Firmware</c> parsers.
    /// <para>判断头部是否命中 FAT 家族（FAT12/FAT16/FAT32）或 exFAT 卷引导记录，用作文件打开模式判据；
    /// 结构校验仍由 <c>UotanStudio.Firmware</c> 的解析器完成。</para>
    /// </summary>
    /// <param name="header">Leading file bytes.<para>文件起始字节。</para></param>
    /// <returns><see langword="true"/> when a FAT-family or exFAT volume name is present.<para>存在 FAT 家族或 exFAT 卷名时为 <see langword="true"/>。</para></returns>
    public static bool IsFatFamilyMagic(ReadOnlySpan<byte> header) => IsFatMagic(header) || IsExFatMagic(header);

    /// <summary>
    /// Whether the header is a FAT12/FAT16/FAT32 volume boot record: the firmware MBR signature plus the
    /// filesystem name at offset 54 or 82.
    /// <para>是否为 FAT12/FAT16/FAT32 卷引导记录：固件 MBR 签名加上偏移 54 或 82 的文件系统名。</para>
    /// </summary>
    /// <param name="header">Leading file bytes.<para>文件起始字节。</para></param>
    /// <returns><see langword="true"/> when the FAT volume name matches.<para>FAT 卷名匹配时为 <see langword="true"/>。</para></returns>
    public static bool IsFatMagic(ReadOnlySpan<byte> header) =>
        MbrTable.HasSignature(header)
        && (Encoding.ASCII.GetString(header.Slice(54, 8)).Trim() is "FAT12" or "FAT16"
            || header.Length >= 90 && Encoding.ASCII.GetString(header.Slice(82, 8)).Trim() == "FAT32");

    /// <summary>
    /// Whether the header is an exFAT volume boot record: the firmware MBR signature plus the
    /// "EXFAT   " filesystem name at offset 3.
    /// <para>是否为 exFAT 卷引导记录：固件 MBR 签名加上偏移 3 的 "EXFAT   " 文件系统名。</para>
    /// </summary>
    /// <param name="header">Leading file bytes.<para>文件起始字节。</para></param>
    /// <returns><see langword="true"/> when the exFAT volume name matches.<para>exFAT 卷名匹配时为 <see langword="true"/>。</para></returns>
    public static bool IsExFatMagic(ReadOnlySpan<byte> header) =>
        MbrTable.HasSignature(header) && header.Slice(3, 8).SequenceEqual("EXFAT   "u8);

    /// <summary>
    /// Whether the header holds the ext4 superblock magic, probed by the ext4 format handler.
    /// <para>是否为 ext4 超级块魔数，由 ext4 格式处理器探测。</para>
    /// </summary>
    /// <param name="header">Leading file bytes.<para>文件起始字节。</para></param>
    /// <returns><see langword="true"/> when the ext4 handler accepts the header.<para>ext4 处理器接受该头部时为 <see langword="true"/>。</para></returns>
    public static bool IsExt4Magic(ReadOnlySpan<byte> header) =>
        MatchesLogicalHandler(new Ext4FormatHandler(), header);

    /// <summary>
    /// Whether the header holds the EROFS superblock magic, probed by the EROFS format handler.
    /// <para>是否为 EROFS 超级块魔数，由 EROFS 格式处理器探测。</para>
    /// </summary>
    /// <param name="header">Leading file bytes.<para>文件起始字节。</para></param>
    /// <returns><see langword="true"/> when the EROFS handler accepts the header.<para>EROFS 处理器接受该头部时为 <see langword="true"/>。</para></returns>
    public static bool IsErofsMagic(ReadOnlySpan<byte> header) =>
        MatchesLogicalHandler(new ErofsFormatHandler(), header);

    /// <summary>
    /// Whether the header holds the F2FS superblock magic, probed by the F2FS format handler.
    /// <para>是否为 F2FS 超级块魔数，由 F2FS 格式处理器探测。</para>
    /// </summary>
    /// <param name="header">Leading file bytes.<para>文件起始字节。</para></param>
    /// <returns><see langword="true"/> when the F2FS handler accepts the header.<para>F2FS 处理器接受该头部时为 <see langword="true"/>。</para></returns>
    public static bool IsF2fsMagic(ReadOnlySpan<byte> header) =>
        MatchesLogicalHandler(new F2fsFormatHandler(), header);

    private static bool MatchesLogicalHandler(IReadonlyFormatHandler handler, ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(header.ToArray(), writable: false);
            return handler switch
            {
                Ext4FormatHandler ext4 => ext4.CanHandleLogicalStream(stream),
                ErofsFormatHandler erofs => erofs.CanHandleLogicalStream(stream),
                F2fsFormatHandler f2fs => f2fs.CanHandleLogicalStream(stream),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Header length read for filesystem magic detection (covers past the ext4 magic tail).
    /// <para>文件系统魔数判定所需的头部读取长度（覆盖至 ext4 魔数尾部）。</para>
    /// </summary>
    private const int FilesystemHeaderLength = 1082;

    /// <summary>
    /// Header length read for LP super metadata magic detection (geometry magic after the reserved bytes).
    /// <para>LP super 元数据魔数判定所需的头部读取长度（保留区之后的几何魔数）。</para>
    /// </summary>
    private const int LpMetadataHeaderLength = MetadataFormat.LP_PARTITION_RESERVED_BYTES + sizeof(uint);

    /// <summary>
    /// Header length read for Rockchip parameter text detection (the CMDLINE line usually sits within
    /// the first 512 bytes).
    /// <para>Rockchip parameter 文本探测读取的头部长度（CMDLINE 行通常位于前 512 字节内）。</para>
    /// </summary>
    private const int RockchipParameterHeaderLength = 512;

    /// <summary>
    /// Whether the header bytes hold LP super metadata, recognised by the registered LP handler.
    /// <para>判断头部字节是否为 LP super 元数据，由已注册的 LP 处理器识别。</para>
    /// </summary>
    /// <param name="header">Leading file bytes.<para>文件起始字节。</para></param>
    /// <returns><see langword="true"/> when the LP probe matches.<para>LP 探测命中时为 <see langword="true"/>。</para></returns>
    public static bool IsLpMetadataMagic(ReadOnlySpan<byte> header)
    {
        EnsureFormatRegistry();
        return FormatDetector.TryGetProbeMatch(string.Empty, header, out var handler) && handler is LpFormatHandler;
    }

    /// <summary>
    /// Whether the header bytes hold Rockchip parameter text: the content must be printable
    /// ASCII/text controls (a UTF-8 BOM may be skipped) and contain both <c>CMDLINE:</c> and
    /// <c>mtdparts=</c> markers.
    /// <para>判断头部字节是否为 Rockchip parameter 文本：内容须为可打印 ASCII/文本控制字符
    /// （可跳过 UTF-8 BOM），且同时包含 <c>CMDLINE:</c> 与 <c>mtdparts=</c> 特征。</para>
    /// </summary>
    /// <param name="header">Leading file bytes.<para>文件起始字节。</para></param>
    /// <returns><see langword="true"/> when the parameter text markers are present.<para>存在 parameter 文本特征时为 <see langword="true"/>。</para></returns>
    public static bool IsRockchipParameterText(ReadOnlySpan<byte> header)
    {
        var offset = 0;
        if (header.Length >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
        {
            offset = 3;
        }

        var length = Math.Min(header.Length, RockchipParameterHeaderLength) - offset;
        if (length < 32)
        {
            return false;
        }

        var text = new char[length];
        for (var i = 0; i < length; i++)
        {
            var b = header[offset + i];
            var isAscii = b is >= 0x20 and <= 0x7E;
            var isControl = b is (byte)'\r' or (byte)'\n' or (byte)'\t';
            if (!isAscii && !isControl)
            {
                return false;
            }

            text[i] = (char)b;
        }

        var content = new string(text);
        return content.Contains("CMDLINE:", StringComparison.OrdinalIgnoreCase)
            && content.Contains("mtdparts=", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether the file content holds LP super metadata (reads the geometry magic).
    /// Returns <c>false</c> on a read failure or mismatch.
    /// <para>判断文件内容是否为 LP super 元数据（读取几何魔数）。读取失败或内容不匹配返回 <c>false</c>。</para>
    /// </summary>
    /// <param name="path">A file name or full path.<para>文件名或完整路径。</para></param>
    /// <returns><see langword="true"/> when the LP probe matches.<para>LP 探测命中时为 <see langword="true"/>。</para></returns>
    public static bool IsLpSuperImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var header = FormatDetector.ReadHeader(path, LpMetadataHeaderLength);
            return IsLpMetadataMagic(header);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Determines whether the file content is Rockchip parameter text. Returns <c>false</c> on a read
    /// failure or mismatch.
    /// <para>判断文件内容是否为 Rockchip parameter 文本。读取失败或内容不匹配返回 <c>false</c>。</para>
    /// </summary>
    /// <param name="path">A file name or full path.<para>文件名或完整路径。</para></param>
    /// <returns><see langword="true"/> when the parameter text markers are present.<para>存在 parameter 文本特征时为 <see langword="true"/>。</para></returns>
    public static bool IsRockchipParameter(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var header = FormatDetector.ReadHeader(path, RockchipParameterHeaderLength);
            return IsRockchipParameterText(header);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Header length read for partition-table content detection (covers GPT headers that EDL combined
    /// images place away from LBA 1, and the MBR signature).
    /// <para>分区表内容探测所需的头部读取长度（覆盖 EDL 合并 GPT 镜像里不在 LBA 1 的头部，以及 MBR 签名）。</para>
    /// </summary>
    private const int PartitionHeaderLength = 64 * 1024;

    /// <summary>
    /// Whether the header bytes carry a sector-aligned GPT signature, as detected by the firmware
    /// partition-table probe.
    /// <para>判断头部字节是否携带扇区对齐的 GPT 签名，由固件分区表探测器判定。</para>
    /// </summary>
    /// <param name="header">Leading file bytes.<para>文件起始字节。</para></param>
    /// <returns><see langword="true"/> when GPT is the detected layout.<para>识别出的布局为 GPT 时为 <see langword="true"/>。</para></returns>
    public static bool IsGptMagic(ReadOnlySpan<byte> header) =>
        PartitionTableProbe.TryDetect(header, out var kind) && kind == PartitionTableKind.Gpt;

    /// <summary>
    /// Whether the header contains a populated MBR partition table rather than a FAT boot record.
    /// Amlogic vendor tables also carry MBR slots, so they keep the historical MBR routing.
    /// <para>判断头部是否包含有效的非空 MBR 分区表，排除 FAT 卷引导扇区。Amlogic 厂商分区表同样携带 MBR
    /// 槽位，因此沿用既有的 MBR 路由。</para>
    /// </summary>
    /// <param name="header">Leading file bytes.<para>文件起始字节。</para></param>
    /// <returns><see langword="true"/> when the MBR or Amlogic layout is detected.<para>识别出 MBR 或 Amlogic 布局时为 <see langword="true"/>。</para></returns>
    public static bool IsMbrMagic(ReadOnlySpan<byte> header) =>
        PartitionTableProbe.TryDetect(header, out var kind)
        && kind is PartitionTableKind.Mbr or PartitionTableKind.Amlogic;

    /// <summary>
    /// Determines whether the file content is a GPT partition table (reads the header and runs the
    /// partition-table probe). Returns <c>false</c> on a read failure or mismatch.
    /// <para>判断文件内容是否为 GPT 分区表（读取头部并运行分区表探测）。读取失败或内容不匹配返回 <c>false</c>。</para>
    /// </summary>
    /// <param name="path">A file name or full path.<para>文件名或完整路径。</para></param>
    /// <returns><see langword="true"/> when GPT is detected.<para>识别出 GPT 时为 <see langword="true"/>。</para></returns>
    public static bool IsGptImage(string path) => IsPartitionImage(path, IsGptMagic);

    /// <summary>
    /// Determines whether the file content is an MBR partition table (reads the header and runs the
    /// partition-table probe). Returns <c>false</c> on a read failure or mismatch.
    /// <para>判断文件内容是否为 MBR 分区表（读取头部并运行分区表探测）。读取失败或内容不匹配返回 <c>false</c>。</para>
    /// </summary>
    /// <param name="path">A file name or full path.<para>文件名或完整路径。</para></param>
    /// <returns><see langword="true"/> when MBR or Amlogic is detected.<para>识别出 MBR 或 Amlogic 时为 <see langword="true"/>。</para></returns>
    public static bool IsMbrImage(string path) => IsPartitionImage(path, IsMbrMagic);

    private static bool IsPartitionImage(string path, Func<ReadOnlySpan<byte>, bool> magic)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var header = FormatDetector.ReadHeader(path, PartitionHeaderLength);
            return magic(header);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the extension is a known firmware one, which opens in the firmware metadata panel.
    /// <para>是否为已知固件扩展名，此类文件走固件元数据面板。</para>
    /// </summary>
    /// <param name="ext">Extension with or without the leading dot.<para>带或不带前导点的扩展名。</para></param>
    /// <returns><see langword="true"/> when a registered format handler claims the extension.<para>已注册处理器声明该扩展名时为 <see langword="true"/>。</para></returns>
    public static bool IsFirmwareExtension(string? ext)
    {
        if (ext is null || ext.Length == 0)
        {
            return false;
        }

        var normalized = ext[0] == '.' ? ext[1..] : ext;
        return _firmwareExtensions.Value.Contains(normalized);
    }

    /// <summary>
    /// Whether the extension is a known binary or firmware one, which opens in the hex editor.
    /// <para>是否为已知二进制或固件扩展名，此类文件走十六进制编辑器。</para>
    /// </summary>
    /// <param name="ext">Extension with or without the leading dot.<para>带或不带前导点的扩展名。</para></param>
    /// <returns><see langword="true"/> when the extension is strongly binary.<para>该扩展名属于强二进制时为 <see langword="true"/>。</para></returns>
    public static bool IsKnownBinaryExtension(string? ext)
    {
        if (ext is null)
        {
            return false;
        }

        if (IsFirmwareExtension(ext))
        {
            return true;
        }

        return _residualBinaryExtensions.Contains(ext.ToLowerInvariant());
    }

    /// <summary>
    /// Residual extensions not covered by the format-handler registry yet still strongly binary (kept on Hex).
    /// <para>格式处理器注册表未覆盖但仍属强二进制的残留扩展名（保持 Hex 打开）。</para>
    /// </summary>
    private static readonly FrozenSet<string> _residualBinaryExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dat", ".tgz", ".zip", ".el", ".elf", ".so" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Static cache of firmware extensions, built on first access by idempotently registering every
    /// format handler and aggregating their extensions.
    /// <para>固件扩展名静态缓存：首次访问时幂等注册全部格式处理器并聚合其扩展名。</para>
    /// </summary>
    private static readonly Lazy<HashSet<string>> _firmwareExtensions = new(LoadFirmwareExtensions);

    /// <summary>
    /// One-time idempotent registration of the firmware format handlers used by probe-hint lookups.
    /// <para>为探测提示查找执行一次幂等的固件格式处理器注册。</para>
    /// </summary>
    private static readonly Lazy<bool> _formatRegistry = new(RegisterFormatHandlers);

    private static bool RegisterFormatHandlers()
    {
        FirmwareFormatRegistry.RegisterAll();
        return true;
    }

    private static void EnsureFormatRegistry() => _ = _formatRegistry.Value;

    private static HashSet<string> LoadFirmwareExtensions()
    {
        EnsureFormatRegistry();

        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in FormatDetector.RegisteredHandlers)
        {
            foreach (var extension in handler.FileExtensions)
            {
                set.Add(extension);
            }
        }

        set.Remove("xml");

        return set;
    }

    /// <summary>
    /// Whether the extension is a known text/code one (should open in the text editor).
    /// Reuses <see cref="SyntaxLanguageRegistry"/> to stay consistent with syntax-language resolution.
    /// <para>是否为已知文本 / 代码扩展名（应走文本编辑器）。
    /// 复用 <see cref="SyntaxLanguageRegistry"/>，与语法高亮语言解析保持一致。</para>
    /// </summary>
    /// <param name="ext">Extension with or without the leading dot.<para>带或不带前导点的扩展名。</para></param>
    /// <returns><see langword="true"/> when the extension is textual.<para>该扩展名属于文本时为 <see langword="true"/>。</para></returns>
    public static bool IsTextExtension(string? ext)
    {
        if (ext is null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(SyntaxLanguageRegistry.GetLanguageId(ext)))
        {
            return true;
        }

        return ext.ToLowerInvariant() switch
        {
            ".txt" or ".log" or ".dtsi" or ".dts" or ".cfg" or ".conf" => true,
            _ => false
        };
    }
}