using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Text;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Views.Controls;

/// <summary>
/// The mode in which a document is opened.
/// <para>文档打开方式。</para>
/// </summary>
public enum EditorOpenMode
{
    /// <summary>
    /// Automatic: resolve via the extension whitelist, falling back to content sniffing when uncertain.
    /// <para>自动：先按扩展名白名单定向，不确定时回落到内容探测。</para>
    /// </summary>
    Auto,

    /// <summary>
    /// Force open in the text editor (with syntax highlighting).
    /// <para>强制以文本编辑器打开（带语法高亮）。</para>
    /// </summary>
    Text,

    /// <summary>
    /// Force open in the hex editor.
    /// <para>强制以 16 进制编辑器打开。</para>
    /// </summary>
    Hex,

    /// <summary>
    /// Open in the device-tree dual-pane editor (DTB/DTBO).
    /// <para>以设备树双栏编辑器打开（DTB/DTBO）。</para>
    /// </summary>
    Dtb,

    /// <summary>
    /// Open in the DEX dual-pane editor (structure tree + disassembly).
    /// <para>以 DEX 双栏编辑器打开（结构树 + 反汇编）。</para>
    /// </summary>
    Dex,

    /// <summary>
    /// Open in the Manifest editor (AXML: AndroidManifest.xml and other binary XML files).
    /// <para>以 Manifest 编辑器打开（AXML：AndroidManifest.xml 等二进制 XML）。</para>
    /// </summary>
    Axml,

    /// <summary>
    /// Open in the ARSC resource-table editor (resources.arsc).
    /// <para>以 ARSC 资源表编辑器打开（resources.arsc）。</para>
    /// </summary>
    Arsc,

    /// <summary>
    /// Open in the APK metadata editor (Android Package; shows icon/package/version/permissions/ABIs).
    /// <para>以 APK 元数据编辑器打开（Android Package，展示图标/包名/版本/权限/ABI）。</para>
    /// </summary>
    Apk,

    /// <summary>
    /// Open in the firmware metadata panel (format name/summary/metadata/entries, driven by the
    /// format-handler registry).
    /// <para>以固件元数据面板打开（格式名/摘要/元数据/条目，基于格式处理器注册表）。</para>
    /// </summary>
    Firmware,

    /// <summary>
    /// Open in the file-manager document editor (shows the internal directory tree of filesystem
    /// images such as ext4/EROFS/F2FS).
    /// <para>以文件管理器文档编辑器打开（展示 ext4/EROFS/F2FS 等文件系统镜像的内部目录树）。</para>
    /// </summary>
    FileSystem,

    /// <summary>
    /// Open in the partition-table editor (Android LP super metadata or Rockchip parameter partition
    /// tables, showing name/start/size/type with edit-and-save support).
    /// <para>以分区表编辑器打开（Android LP super 元数据或 Rockchip parameter 分区表，
    /// 展示分区名/起始/大小/类型并支持编辑保存）。</para>
    /// </summary>
    Partition,

    /// <summary>
    /// Open as a read-only log document.
    /// <para>以只读日志文档打开。</para>
    /// </summary>
    Log,

    /// <summary>Open validated ELF metadata in a read-only information panel.</summary>
    Elf,

    /// <summary>Open the Qualcomm image analysis workspace explicitly.
    /// <para>显式打开 Qualcomm 镜像分析工作区。</para></summary>
    Qualcomm,

    /// <summary>Open the Qualcomm QCN NV backup in the structured QCN editor.
    /// <para>以结构化 QCN 编辑器打开 Qualcomm QCN NV 备份。</para></summary>
    Qcn
}

/// <summary>
/// File open-mode resolution engine: extension-whitelist first, content sniffing as fallback, deciding
/// which editor a file should open in.
/// <para>文件打开方式判定引擎：按扩展名白名单优先、内容探测兜底，综合判断文件应以何种编辑器打开。</para>
/// </summary>
public static class FileOpenMode
{
    /// <summary>
    /// Bounded file-header length used for format and binary-content detection during automatic opening.
    /// <para>自动打开时用于格式与二进制内容判定的有界文件头长度。</para>
    /// </summary>
    public const int ContentProbeLength = 65536;

    /// <summary>Resolves the open mode for the given path: <c>.dts→Text</c>, <c>.dtb/.dtbo→Dtb</c>, known firmware extensions (aggregated from the format-handler registry, e.g. <c>.img/.pac/.ops/.nb0</c>)→<c>Firmware</c>, known binary/firmware extensions→<c>Hex</c>, known text/code extensions→<c>Text</c>, else <c>Auto</c>.
    /// <para>解析指定文件应使用的打开方式。规则：<c>.dts→Text</c>、<c>.dtb/.dtbo→Dtb</c>、 已知固件扩展名（格式处理器注册表聚合，如 <c>.img/.pac/.ops/.nb0</c>）→<c>Firmware</c>、 已知二进制/固件扩展名→<c>Hex</c>、已知文本/代码扩展名→<c>Text</c>、其余→<c>Auto</c>。</para></summary>
    /// <param name="path">A file name or full path.<para>文件名或完整路径。</para></param>
    public static EditorOpenMode ResolveMode(string path) =>
        Services.Workspace.Documents.DocumentOpenRegistry.ResolveMode(path);

    /// <summary>Resolves the open mode; for unknown/no-extension files, uses the loaded header bytes as a magic fallback. Valid ELF headers open the information panel; other strong binary types use <c>Hex</c>.
    /// <para>解析应使用的打开方式；对未知/无扩展名文件，以已加载的头部字节做 magic 兜底， 有效 ELF 头进入信息面板，其余强二进制类型返回 <c>Hex</c>。</para></summary>
    /// <param name="path">A file name or full path.<para>文件名或完整路径。</para></param>
    /// <param name="header">Optional loaded file header bytes; magic fallback is skipped when <c>null</c>.
    /// <para>可选的已加载文件头部字节；为 <c>null</c> 时不计 magic 兜底。</para></param>
    public static EditorOpenMode ResolveMode(string path, byte[]? header, FormatDetectionResult? detection = null) =>
        Services.Workspace.Documents.DocumentOpenRegistry.ResolveMode(path, header, detection);

    /// <summary>
    /// Determines whether a bounded content sample is clearly binary. Samples with a UTF text BOM remain
    /// text; otherwise a NUL byte or more than 30% control characters classifies the sample as binary.
    /// <para>通过有界内容样本判断文件是否明显为二进制。带 UTF 文本 BOM 的样本保持文本；
    /// 否则出现 NUL，或控制字符占比超过 30%，即判为二进制。</para>
    /// </summary>
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
    public static bool IsDtbExtension(string? ext) =>
        ext is not null && ext.Equals(".dtb", StringComparison.OrdinalIgnoreCase)
        || ext is not null && ext.Equals(".dtbo", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the extension is a DEX one.
    /// <para>是否为 DEX 扩展名。</para>
    /// </summary>
    public static bool IsDexExtension(string? ext) =>
        ext is not null && ext.Equals(".dex", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the extension is a JAR container one (opens in the DEX editor to browse
    /// classes*.dex entries and resources).
    /// <para>是否为 JAR 容器扩展名（走 DEX 编辑器浏览内部 classes*.dex 与资源）。</para>
    /// </summary>
    public static bool IsJarExtension(string? ext) =>
        ext is not null && ext.Equals(".jar", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the extension is an ARSC resource-table one.
    /// <para>是否为 ARSC 资源表扩展名。</para>
    /// </summary>
    public static bool IsArscExtension(string? ext) =>
        ext is not null && ext.Equals(".arsc", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the extension is an APK package one.
    /// <para>是否为 APK 包扩展名。</para>
    /// </summary>
    public static bool IsApkExtension(string? ext) =>
        ext is not null && ext.Equals(".apk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the extension is a QCN one. The OLE2/CFB magic is shared with .doc/.msi files, so
    /// routing is extension-only; non-QCN content surfaces a localized error at load time.
    /// <para>是否为 QCN 扩展名。注意 OLE2/CFB 魔数与 .doc/.msi 共用，因此仅按扩展名路由，
    /// 非 QCN 内容在编辑器加载时给出本地化错误。</para>
    /// </summary>
    public static bool IsQcnExtension(string? ext) =>
        ext is not null && ext.Equals(".qcn", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the extension is a DTS device-tree source extension.
    /// <para>是否为 DTS 设备树源文件扩展名。</para>
    /// </summary>
    public static bool IsDtsExtension(string? ext) =>
        ext is not null && ext.Equals(".dts", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the extension is a filesystem-image one (.img/.ext4/.erofs/.f2fs).
    /// <para>是否为文件系统镜像扩展名（.img/.ext4/.erofs/.f2fs）。</para>
    /// </summary>
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
    /// Determines whether the file content matches an ext4, EROFS or F2FS magic.
    /// <para>判断文件内容是否命中 ext4、EROFS 或 F2FS 魔数。</para>
    /// </summary>
    /// <param name="path">
    /// A file name or full path.
    /// <para>文件名或完整路径。</para>
    /// </param>
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
    /// <param name="path">
    /// A file name or full path.
    /// <para>文件名或完整路径。</para>
    /// </param>
    public static bool IsSparseImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var magic = FormatDetector.ReadHeader(path, 4);
            return magic.Length == 4
                && BinaryPrimitives.ReadUInt32LittleEndian(magic) == SparseFormat.SparseHeaderMagic;
        }
        catch
        {

            return false;
        }
    }

    /// <summary>
    /// Whether the header holds the FDT device-tree magic (big-endian 0xD00DFEED at offset 0) or the
    /// DTBO table magic (big-endian 0xD7B7AB1E).
    /// <para>是否为 FDT 设备树魔数（大端 0xD00DFEED 位于偏移 0）或 DTBO 表魔数（大端 0xD7B7AB1E）。</para>
    /// </summary>
    public static bool IsDtbMagic(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
        {
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        return magic == Firmware.DeviceTree.Dt.FdtConstants.FdtMagic
            || magic == Firmware.DeviceTree.Dt.FdtConstants.DtboMagic;
    }

    /// <summary>
    /// Whether the header holds the DEX magic (ASCII "dex\n" at offset 0).
    /// <para>是否为 DEX 魔数（ASCII "dex\n" 位于偏移 0）。</para>
    /// </summary>
    public static bool IsDexMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[0] == (byte)'d' && header[1] == (byte)'e'
            && header[2] == (byte)'x' && header[3] == (byte)'\n';

    /// <summary>Checks strong Android/vendor firmware signatures independent of the file extension.</summary>
    public static bool IsFirmwareMagic(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 && (header[..8].SequenceEqual("ANDROID!"u8)
            || header[..8].SequenceEqual("VNDRBOOT"u8)))
        {
            return true;
        }

        if (header.Length >= 4 && (header[..4].SequenceEqual("CrAU"u8)
            || header[..4].SequenceEqual("AVBf"u8)
            || header[..4].SequenceEqual("RKFW"u8)
            || header[..4].SequenceEqual("RKAF"u8)
            || header is [0x55, 0xAA, 0x5A, 0xA5, ..]))
        {
            return true;
        }

        return header.Length >= 0x28
            && (header.Slice(0x24, 4) is [0x18, 0x28, 0x6F, 0x01]
                || header.Slice(0x24, 4) is [0xED, 0xFE, 0x0F, 0x00]
                || header.Slice(0x24, 4) is [0x00, 0x0F, 0xFE, 0xED]
                || header.Slice(0x24, 4) is [0x01, 0x6F, 0x28, 0x18]);
    }

    /// <summary>
    /// Whether the header holds a ZIP container magic ("PK\x03\x04" / "PK\x05\x06" / "PK\x07\x08",
    /// covering empty archives and spanned entries).
    /// <para>是否为 ZIP 容器魔数（"PK\x03\x04" / "PK\x05\x06" / "PK\x07\x08"，覆盖空归档与跨卷条目）。</para>
    /// </summary>
    public static bool IsZipMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[0] == (byte)'P' && header[1] == (byte)'K'
            && (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07)
            && (header[3] == 0x04 || header[3] == 0x06 || header[3] == 0x08);

    /// <summary>
    /// Determines whether the header bytes match any filesystem magic (ext4/EROFS/F2FS/FAT12/16/32/exFAT).
    /// <para>判断头部字节是否命中任意一种文件系统魔数（ext4/EROFS/F2FS/FAT12/16/32/exFAT）。</para>
    /// </summary>
    public static bool IsFilesystemMagic(ReadOnlySpan<byte> header) =>
        IsExt4Magic(header) || IsErofsMagic(header) || IsF2fsMagic(header) || IsFatFamilyMagic(header);

    /// <summary>
    /// Whether the header carries a FAT family (FAT12/FAT16/FAT32) or exFAT volume boot record, used only as
    /// an open-mode hint; structural validation stays with the <c>UotanStudio.Firmware</c> parsers.
    /// <para>判断头部是否命中 FAT 家族（FAT12/FAT16/FAT32）或 exFAT 卷引导记录，用作文件打开模式判据；
    /// 结构校验仍由 <c>UotanStudio.Firmware</c> 的解析器完成。</para>
    /// </summary>
    public static bool IsFatFamilyMagic(ReadOnlySpan<byte> header) => IsFatMagic(header) || IsExFatMagic(header);

    /// <summary>
    /// Whether the header is a FAT12/FAT16/FAT32 volume boot record: it requires the 0x55AA signature at
    /// offset 510 and matches the filesystem name at offset 54 or 82.
    /// <para>是否为 FAT12/FAT16/FAT32 卷引导记录：要求偏移 510 的 0x55AA 签名，并比对偏移 54 或 82 的文件系统名。</para>
    /// </summary>
    public static bool IsFatMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 512 && header[510] == 0x55 && header[511] == 0xAA
        && (Encoding.ASCII.GetString(header.Slice(54, 8)).Trim() is "FAT12" or "FAT16"
            || header.Length >= 90 && Encoding.ASCII.GetString(header.Slice(82, 8)).Trim() == "FAT32");

    /// <summary>
    /// Whether the header is an exFAT volume boot record: it requires the 0x55AA signature at offset 510 and
    /// the "EXFAT   " filesystem name at offset 3.
    /// <para>是否为 exFAT 卷引导记录：要求偏移 510 的 0x55AA 签名与偏移 3 的 "EXFAT   " 文件系统名。</para>
    /// </summary>
    public static bool IsExFatMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 512 && header[510] == 0x55 && header[511] == 0xAA
        && header.Slice(3, 8).SequenceEqual("EXFAT   "u8);

    /// <summary>
    /// Whether the header holds the ext4 magic (little-endian 0xEF53 at offset 1080).
    /// <para>是否为 ext4 魔数（小端 0xEF53 位于偏移 1080）。</para>
    /// </summary>
    public static bool IsExt4Magic(ReadOnlySpan<byte> header) =>
        header.Length >= 1082 && BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(1080, 2)) == 0xEF53;

    /// <summary>
    /// Whether the header holds the EROFS magic (little-endian 0xE0F5E1E2 at offset 1024).
    /// <para>是否为 EROFS 魔数（小端 0xE0F5E1E2 位于偏移 1024）。</para>
    /// </summary>
    public static bool IsErofsMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 1028 && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1024, 4)) == 0xE0F5E1E2;

    /// <summary>
    /// Whether the header holds the F2FS magic (little-endian 0xF2F52010 at offset 1024).
    /// <para>是否为 F2FS 魔数（小端 0xF2F52010 位于偏移 1024）。</para>
    /// </summary>
    public static bool IsF2fsMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 1028 && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1024, 4)) == 0xF2F52010;

    /// <summary>
    /// Header length read for filesystem magic detection (covers past the ext4 magic tail).
    /// <para>文件系统魔数判定所需的头部读取长度（覆盖至 ext4 魔数尾部）。</para>
    /// </summary>
    private const int FilesystemHeaderLength = 1082;

    /// <summary>
    /// Header length read for LP super metadata magic detection (4-byte geometry magic at offset 4096).
    /// <para>LP super 元数据魔数判定所需的头部读取长度（偏移 4096 处的 4 字节几何魔数）。</para>
    /// </summary>
    private const int LpMetadataHeaderLength = 4100;

    /// <summary>
    /// Header length read for Rockchip parameter text detection (the CMDLINE line usually sits within
    /// the first 512 bytes).
    /// <para>Rockchip parameter 文本探测读取的头部长度（CMDLINE 行通常位于前 512 字节内）。</para>
    /// </summary>
    private const int RockchipParameterHeaderLength = 512;

    /// <summary>
    /// Whether the header bytes hold LP super metadata (little-endian geometry magic 0x616c4467 at
    /// offset 4096).
    /// <para>判断头部字节是否为 LP super 元数据（小端几何魔数 0x616c4467 位于偏移 4096）。</para>
    /// </summary>
    public static bool IsLpMetadataMagic(ReadOnlySpan<byte> header) =>
        header.Length >= LpMetadataHeaderLength
        && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4096, 4))
            == Firmware.BlockImage.Lp.MetadataFormat.LP_METADATA_GEOMETRY_MAGIC;

    /// <summary>
    /// Whether the header bytes hold Rockchip parameter text: the content must be printable
    /// ASCII/text controls (a UTF-8 BOM may be skipped) and contain both <c>CMDLINE:</c> and
    /// <c>mtdparts=</c> markers.
    /// <para>判断头部字节是否为 Rockchip parameter 文本：内容须为可打印 ASCII/文本控制字符
    /// （可跳过 UTF-8 BOM），且同时包含 <c>CMDLINE:</c> 与 <c>mtdparts=</c> 特征。</para>
    /// </summary>
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
    /// Determines whether the file content holds LP super metadata (reads the geometry magic at
    /// offset 4096). Returns <c>false</c> on a read failure or mismatch.
    /// <para>判断文件内容是否为 LP super 元数据（读取偏移 4096 处的几何魔数）。
    /// 读取失败或内容不匹配返回 <c>false</c>。</para>
    /// </summary>
    /// <param name="path">
    /// A file name or full path.
    /// <para>文件名或完整路径。</para>
    /// </param>
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
    /// Determines whether the file content is a Rockchip parameter text (reads the file start and
    /// checks the CMDLINE:/mtdparts= markers). Returns <c>false</c> on a read failure or mismatch.
    /// <para>判断文件内容是否为 Rockchip parameter 文本（读取文件起始处并检查 CMDLINE:/mtdparts= 特征）。
    /// 读取失败或内容不匹配返回 <c>false</c>。</para>
    /// </summary>
    /// <param name="path">
    /// A file name or full path.
    /// <para>文件名或完整路径。</para>
    /// </param>
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
    /// Header length read for partition-table content detection (covers the GPT headers that EDL combined
    /// images place away from LBA 1, and the MBR signature @510).
    /// <para>分区表内容探测所需的头部读取长度（覆盖 EDL 合并 GPT 镜像里不在 LBA 1 的头部，
    /// 以及 MBR 签名 @510）。</para>
    /// </summary>
    private const int PartitionHeaderLength = 64 * 1024;

    /// <summary>
    /// Whether the header bytes carry a sector-aligned GPT signature (<c>EFI PART</c>).
    /// <para>判断头部字节是否携带扇区对齐的 GPT 签名（<c>EFI PART</c>）。</para>
    /// </summary>
    public static bool IsGptMagic(ReadOnlySpan<byte> header) =>
        Models.GptTable.HasSignature(header);

    /// <summary>
    /// Whether the header contains a populated MBR partition table rather than a FAT boot record.
    /// <para>判断头部是否包含有效的非空 MBR 分区表，排除 FAT 卷引导扇区。</para>
    /// </summary>
    public static bool IsMbrMagic(ReadOnlySpan<byte> header) =>
        Models.MbrTable.HasPartitionTable(header);

    /// <summary>
    /// Determines whether the file content is a GPT partition table (reads the header and checks the
    /// "EFI PART" signature). Returns <c>false</c> on a read failure or mismatch.
    /// <para>判断文件内容是否为 GPT 分区表（读取头部并检查 "EFI PART" 签名）。
    /// 读取失败或内容不匹配返回 <c>false</c>。</para>
    /// </summary>
    public static bool IsGptImage(string path) => IsPartitionImage(path, IsGptMagic);

    /// <summary>
    /// Determines whether the file content is an MBR partition table (reads the header and checks the
    /// 0x55AA signature). Returns <c>false</c> on a read failure or mismatch.
    /// <para>判断文件内容是否为 MBR 分区表（读取头部并检查 0x55AA 签名）。
    /// 读取失败或内容不匹配返回 <c>false</c>。</para>
    /// </summary>
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

    private static HashSet<string> LoadFirmwareExtensions()
    {

        FirmwareFormatRegistry.RegisterAll();

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