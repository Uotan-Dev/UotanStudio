using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.Archive.Browsing;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Magic;
using UotanStudio.Services.Logging;

namespace UotanStudio.Views.Controls;

/// <summary>
/// <para>文档打开方式。</para>
/// The mode in which a document is opened.
/// </summary>
public enum EditorOpenMode
{
    /// <summary>
    /// <para>自动：先按扩展名白名单定向，不确定时回落到内容探测。</para>
    /// Automatic: resolve via the extension whitelist, falling back to content sniffing when uncertain.
    /// </summary>
    Auto,

    /// <summary>
    /// <para>强制以文本编辑器打开（带语法高亮）。</para>
    /// Force open in the text editor (with syntax highlighting).
    /// </summary>
    Text,

    /// <summary>
    /// <para>强制以 16 进制编辑器打开。</para>
    /// Force open in the hex editor.
    /// </summary>
    Hex,

    /// <summary>
    /// <para>以设备树双栏编辑器打开（DTB/DTBO）。</para>
    /// Open in the device-tree dual-pane editor (DTB/DTBO).
    /// </summary>
    Dtb,

    /// <summary>
    /// <para>以 DEX 双栏编辑器打开（结构树 + 反汇编）。</para>
    /// Open in the DEX dual-pane editor (structure tree + disassembly).
    /// </summary>
    Dex,

    /// <summary>
    /// <para>以 Manifest 编辑器打开（AXML：AndroidManifest.xml 等二进制 XML）。</para>
    /// Open in the Manifest editor (AXML: AndroidManifest.xml and other binary XML files).
    /// </summary>
    Axml,

    /// <summary>
    /// <para>以 ARSC 资源表编辑器打开（resources.arsc）。</para>
    /// Open in the ARSC resource-table editor (resources.arsc).
    /// </summary>
    Arsc,

    /// <summary>
    /// <para>以 APK 元数据编辑器打开（Android Package，展示图标/包名/版本/权限/ABI）。</para>
    /// Open in the APK metadata editor (Android Package; shows icon/package/version/permissions/ABIs).
    /// </summary>
    Apk,

    /// <summary>
    /// <para>以固件元数据面板打开（格式名/摘要/元数据/条目，基于格式处理器注册表）。</para>
    /// Open in the firmware metadata panel (format name/summary/metadata/entries, driven by the
    /// format-handler registry).
    /// </summary>
    Firmware,

    /// <summary>
    /// <para>以文件管理器文档编辑器打开（展示 ext4/EROFS/F2FS 等文件系统镜像的内部目录树）。</para>
    /// Open in the file-manager document editor (shows the internal directory tree of filesystem
    /// images such as ext4/EROFS/F2FS).
    /// </summary>
    FileSystem,

    /// <summary>
    /// <para>以分区表编辑器打开（Android LP super 元数据或 Rockchip parameter 分区表，
    /// 展示分区名/起始/大小/类型并支持编辑保存）。</para>
    /// Open in the partition-table editor (Android LP super metadata or Rockchip parameter partition
    /// tables, showing name/start/size/type with edit-and-save support).
    /// </summary>
    Partition,

    /// <summary>Open as a read-only log document. 以只读日志文档打开。</summary>
    Log
}

/// <summary>
/// <para>文件打开方式判定引擎：按扩展名白名单优先、内容探测兜底，综合判断文件应以何种编辑器打开。</para>
/// File open-mode resolution engine: extension-whitelist first, content sniffing as fallback, deciding
/// which editor a file should open in.
/// </summary>
public static class FileOpenMode
{
    /// <summary>
    /// <para>自动打开时用于格式与二进制内容判定的有界文件头长度。</para>
    /// Bounded file-header length used for format and binary-content detection during automatic opening.
    /// </summary>
    public const int ContentProbeLength = 65536;

    /// <summary>
    /// <para>解析指定文件应使用的打开方式。规则：<c>.dts→Text</c>、<c>.dtb/.dtbo→Dtb</c>、
    /// 已知固件扩展名（格式处理器注册表聚合，如 <c>.img/.pac/.ops/.nb0</c>）→<c>Firmware</c>、
    /// 已知二进制/固件扩展名→<c>Hex</c>、已知文本/代码扩展名→<c>Text</c>、其余→<c>Auto</c>。</para>
    /// Resolves the open mode for the given path: <c>.dts→Text</c>, <c>.dtb/.dtbo→Dtb</c>,
    /// known firmware extensions (aggregated from the format-handler registry, e.g.
    /// <c>.img/.pac/.ops/.nb0</c>)→<c>Firmware</c>, known binary/firmware extensions→<c>Hex</c>,
    /// known text/code extensions→<c>Text</c>, else <c>Auto</c>.
    /// </summary>
    /// <param name="path">
    /// <para>文件名或完整路径。</para>
    /// A file name or full path.
    /// </param>
    public static EditorOpenMode ResolveMode(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return EditorOpenMode.Auto;
        }

        var ext = Path.GetExtension(path);

        if (ext.Equals(".log", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".logcat", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".adblog", StringComparison.OrdinalIgnoreCase)) return EditorOpenMode.Log;

        // 设备树双栏编辑器优先于纯二进制。
        // Device tree dual-pane editor takes precedence over plain binary.
        if (IsDtbExtension(ext))
        {
            return EditorOpenMode.Dtb;
        }

        // DEX 二进制：走 DEX 双栏编辑器（结构树 + 反汇编）。
        // DEX blobs open in the DEX dual-pane editor (structure tree + disassembly).
        if (IsDexExtension(ext))
        {
            return EditorOpenMode.Dex;
        }

        // JAR 容器：与 APK 同为 ZIP+DEX 容器，走 DEX 编辑器（多 DEX 条目 + 资源浏览）。
        // JAR containers: like APK, a ZIP+DEX container, opening in the DEX editor
        // (multi-DEX entries + resource browsing).
        if (IsJarExtension(ext))
        {
            return EditorOpenMode.Dex;
        }

        // ARSC 资源表：走 ARSC 编辑器（resources.arsc 等）。
        // ARSC resource tables open in the ARSC editor (resources.arsc etc.).
        if (IsArscExtension(ext))
        {
            return EditorOpenMode.Arsc;
        }

        // APK 包：走 APK 元数据编辑器（优先于已知二进制列表中的 .apk→Hex）。
        // APK packages open in the APK metadata editor (before the known-binary .apk→Hex entry).
        if (IsApkExtension(ext))
        {
            return EditorOpenMode.Apk;
        }

        // 设备树源文件按文本（语法高亮）打开。
        // Device tree sources open as text (with syntax highlighting).
        if (IsDtsExtension(ext))
        {
            return EditorOpenMode.Text;
        }

        // 文件系统镜像（ext4/EROFS/F2FS）：扩展名命中专用集合时以内容魔数确认后路由到文件管理器
        // 编辑器。注意 .img 与固件格式重叠，必须用魔数判定（ext4 0xEF53@1080、erofs 0xE0F5E1E2@1024、
        // f2fs 0xF2F52010@1024），未命中则继续走固件/十六进制分支。
        // Filesystem images (ext4/EROFS/F2FS): when the extension matches the dedicated set, confirm by
        // content magic before routing to the file-manager editor. .img overlaps with firmware formats,
        // so the magic must decide (ext4 0xEF53@1080, erofs 0xE0F5E1E2@1024, f2fs 0xF2F52010@1024);
        // otherwise the Firmware/Hex branches below take over.
        if (IsFilesystemImageExtension(ext) && IsFilesystemImage(path))
        {
            return EditorOpenMode.FileSystem;
        }

        // 归档及压缩文件在文件管理器中浏览；压缩归档展开内部目录，单文件压缩显示一个条目。
        // Browse archives and compressed files; single-file compression exposes one member.
        if (ArchiveBrowser.SupportsPath(path))
        {
            return EditorOpenMode.FileSystem;
        }

        // 分区表内容探测（LP super 元数据）：仅当文件名/扩展名可能为 super 镜像时才读取内容，
        // 避免对普通文件做多余 I/O；LP 几何魔数（0x616c4467 @4096）优先于固件面板接管 .img/.super。
        // Partition-table content sniffing (LP super metadata): only reads content when the name or
        // extension could be a super image, avoiding extra I/O for ordinary files; the LP geometry
        // magic (0x616c4467 @4096) wins over the firmware panel for .img/.super files.
        if (IsLpCandidatePath(path) && IsLpSuperImage(path))
        {
            return EditorOpenMode.Partition;
        }

        // GPT / MBR 分区表内容探测：候选路径（.img/.gpt/.mbr/.bin 或无扩展名）读取头部，
        // GPT 魔数优先于 MBR（GPT 镜像含保护性 MBR，两者都会命中 0x55AA）。
        // GPT / MBR partition-table content sniffing: candidate paths (.img/.gpt/.mbr/.bin or none)
        // get a header read; the GPT magic wins over MBR (a GPT image carries a protective MBR so both
        // would match 0x55AA).
        if (IsPartitionTableCandidatePath(path) && (IsGptImage(path) || IsMbrImage(path)))
        {
            return EditorOpenMode.Partition;
        }

        // 固件元数据面板：扩展名命中格式处理器注册表聚合出的集合（.img/.pac/.ops/.nb0 等），
        // 其余专用编辑器已在上方优先拦截（如 .dtb/.dex/.arsc/.apk）。
        // Firmware metadata panel: the extension matches the set aggregated from the registered
        // format handlers (.img/.pac/.ops/.nb0 etc.); dedicated editors above already claim their own.
        if (IsFirmwareExtension(ext))
        {
            return EditorOpenMode.Firmware;
        }

        if (IsKnownBinaryExtension(ext))
        {
            return EditorOpenMode.Hex;
        }

        if (IsTextExtension(ext))
        {
            return EditorOpenMode.Text;
        }

        // 分区表内容探测（Rockchip parameter 文本）：扩展名不被任何专用编辑器接管
        // （如无扩展名的 "parameter" 文件）时按内容判定。
        // Partition-table content sniffing (Rockchip parameter text): files whose extension is not
        // claimed by a dedicated editor (e.g. the extension-less "parameter" file) are decided by content.
        if (IsRockchipParameter(path))
        {
            return EditorOpenMode.Partition;
        }

        return EditorOpenMode.Auto;
    }

    /// <summary>
    /// <para>解析应使用的打开方式；对未知/无扩展名文件，以已加载的头部字节做 magic 兜底，
    /// 识别出强二进制类型（如 ELF）时返回 <c>Hex</c>。</para>
    /// Resolves the open mode; for unknown/no-extension files, uses the loaded header bytes as a magic
    /// fallback, returning <c>Hex</c> when a strong binary type (e.g. ELF) is recognized.
    /// </summary>
    /// <param name="path">
    /// <para>文件名或完整路径。</para>
    /// A file name or full path.
    /// </param>
    /// <param name="header">
    /// <para>可选的已加载文件头部字节；为 <c>null</c> 时不计 magic 兜底。</para>
    /// Optional loaded file header bytes; magic fallback is skipped when <c>null</c>.
    /// </param>
    public static EditorOpenMode ResolveMode(string path, byte[]? header, FormatDetectionResult? detection = null)
    {
        // AXML 内容探测优先：AndroidManifest.xml 等二进制 XML 即使扩展名为 .xml（会被文本路由
        // 拦截）也应走 Manifest 编辑器，避免按 UTF-8 文本打开乱码。
        // AXML content sniffing first: binary XML files such as AndroidManifest.xml carry a .xml
        // extension (which the text route would claim), so the AXML magic must win to avoid opening
        // them as mangled UTF-8 text.
        if (header is not null && UotanStudio.Firmware.BoundedContexts.AndroidMeta.AXml.Parsing.AXmlParser.IsAxml(header))
        {
            return EditorOpenMode.Axml;
        }

        // DTB/DTBO 魔数优先：FDT（0xD00DFEED）与 DTBO（0xD7B7AB1E，均大端）即使扩展名缺失或
        // 被改为 .img/.bin（会被固件/Hex 路由拦截）也走设备树双栏编辑器。
        // DTB/DTBO magic first: FDT (0xD00DFEED) and DTBO (0xD7B7AB1E, both big-endian) open in the
        // device-tree dual-pane editor even when the extension is absent or renamed to .img/.bin
        // (which the firmware/Hex routes would claim).
        if (header is not null && IsDtbMagic(header))
        {
            return EditorOpenMode.Dtb;
        }

        // DEX 魔数优先："dex\n" 起始的字节码即使无扩展名或被改名也走 DEX 双栏编辑器。
        // DEX magic first: bytecode starting with "dex\n" opens in the DEX dual-pane editor even
        // without an extension or after a rename.
        if (header is not null && IsDexMagic(header))
        {
            return EditorOpenMode.Dex;
        }

        // Strong firmware signatures take precedence over any misleading or missing extension.
        if (header is not null && IsFirmwareMagic(header))
        {
            return EditorOpenMode.Firmware;
        }

        // ZIP 容器魔数：PK 头按内容命中文件管理器浏览；.apk/.jar 的专用编辑器仍由扩展名路由
        // 优先（ZIP 魔数无法区分 APK/JAR 与普通 ZIP）。
        // ZIP container magic: a PK header routes to file-manager browsing by content; the dedicated
        // .apk/.jar editors still win via their extension routes (the ZIP magic cannot tell an APK/JAR
        // from a plain ZIP).
        if (header is not null && IsZipMagic(header) && !IsApkExtension(Path.GetExtension(path)) && !IsJarExtension(Path.GetExtension(path)))
        {
            return EditorOpenMode.FileSystem;
        }

        // 文件系统镜像魔数优先：头部已提供且长度足够时直接命中（ext4 0xEF53@1080、
        // erofs 0xE0F5E1E2@1024、f2fs 0xF2F52010@1024），不必再按扩展名回落固件面板。
        // Filesystem image magic takes priority: when the header is provided and long enough, the magic
        // decides directly (ext4 0xEF53@1080, erofs 0xE0F5E1E2@1024, f2fs 0xF2F52010@1024) without
        // falling back to the firmware panel by extension.
        if (header is not null && IsFilesystemMagic(header))
        {
            return EditorOpenMode.FileSystem;
        }

        // LP super 元数据魔数优先：头部已提供且足够长（覆盖偏移 4096 的几何魔数）时直接命中，
        // 即使扩展名为 .img/.super 会被固件面板接管。
        // LP super metadata magic takes priority: when the header is provided and long enough (covers
        // the geometry magic at offset 4096) it decides directly, even though .img/.super extensions
        // are claimed by the firmware panel.
        if (header is not null && IsLpMetadataMagic(header))
        {
            return EditorOpenMode.Partition;
        }

        // GPT / MBR 分区表魔数：GPT 优先（GPT 镜像含保护性 MBR，两者都会命中 0x55AA）。
        // GPT / MBR partition-table magic: GPT first (a GPT image carries a protective MBR so both
        // would match 0x55AA).
        if (header is not null && (IsGptMagic(header) || IsMbrMagic(header)))
        {
            return EditorOpenMode.Partition;
        }

        // Rockchip parameter 文本特征（CMDLINE: 与 mtdparts=）。
        // Rockchip parameter text characteristics (CMDLINE: and mtdparts=).
        if (header is not null && IsRockchipParameterText(header))
        {
            return EditorOpenMode.Partition;
        }

        // A validated handler must remain authoritative even when the file uses a vendor-specific
        // suffix (for example sparse-super fragments such as .0). Extension lists are only hints;
        // otherwise a successfully detected firmware object fell through to the generic hex editor.
        if (detection is { Confidence: not DetectionConfidence.ExtensionOnly })
        {
            var formatName = detection.Handler.FormatName;
            if (formatName.Contains("Resource Table", StringComparison.OrdinalIgnoreCase))
            {
                return EditorOpenMode.Arsc;
            }

            if (formatName.Contains("Compressed", StringComparison.OrdinalIgnoreCase)
                || formatName.Contains("Archive", StringComparison.OrdinalIgnoreCase))
            {
                return EditorOpenMode.FileSystem;
            }

            return EditorOpenMode.Firmware;
        }

        var mode = ResolveMode(path);
        if (mode is EditorOpenMode.Text or EditorOpenMode.Log && header is not null && IsBinaryContent(header))
        {
            return EditorOpenMode.Hex;
        }

        if (mode is EditorOpenMode.Auto or EditorOpenMode.Text && header is not null
            && (string.IsNullOrEmpty(Path.GetExtension(path)) || Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            && LogFileService.LooksLikeLog(header)) return EditorOpenMode.Log;

        if (mode != EditorOpenMode.Auto)
        {
            return mode;
        }

        // ARSC 内容探测：resources.arsc 为 0x0002 表头。
        // ARSC content sniffing: resources.arsc carries the 0x0002 table header.
        if (header is not null && UotanStudio.Firmware.BoundedContexts.AndroidMeta.Arsc.Parsing.ArscParser.IsArsc(header))
        {
            return EditorOpenMode.Arsc;
        }

        // 无扩展名文件的格式注册表嗅探：遍历已注册格式处理器按内容识别（boot/payload/sparse/
        // CPIO/Oppo/RKAF 等），命中时走固件元数据面板；面板解析失败会由 WorkspaceViewModel
        // 回退到 Hex。仅在扩展名为空时执行，避免与既有扩展名路由双源冲突。
        // Format-registry sniffing for extension-less files: iterate registered format handlers for
        // content-based detection (boot/payload/sparse/CPIO/Oppo/RKAF etc.); a hit opens the firmware
        // metadata panel (which WorkspaceViewModel falls back to Hex on parse failure). Only runs for
        // extension-less files to avoid dual-source conflicts with the extension routes above.
        if (header is not null && header.Length > 0 && string.IsNullOrEmpty(Path.GetExtension(path)))
        {
            FirmwareFormatRegistry.RegisterAll();
            if ((detection ?? FormatDetector.DetectDetailed(path)) is not null)
            {
                return EditorOpenMode.Firmware;
            }
        }

        // Auto 分支的 magic 兜底：识别出强二进制类型视为十六进制。
        // Magic fallback for the Auto branch: a recognized strongly-binary type opens as hex.
        if (header is not null && FileMagic.Describe(header) is not null)
        {
            return EditorOpenMode.Hex;
        }

        // 未命中具体魔数的普通二进制仍统一进入专用 Hex 编辑器，避免先创建 FileView 后
        // 再由其内部内容探测切换视图，造成自动打开与“以十六进制打开”的宿主样式不一致。
        // Generic binary data without a known magic still uses the dedicated Hex editor. This avoids
        // creating FileView first and switching its inner viewer later, which produced a different host UI.
        if (header is not null && IsBinaryContent(header))
        {
            return EditorOpenMode.Hex;
        }

        return EditorOpenMode.Auto;
    }

    /// <summary>
    /// <para>通过有界内容样本判断文件是否明显为二进制。带 UTF 文本 BOM 的样本保持文本；
    /// 否则出现 NUL，或控制字符占比超过 30%，即判为二进制。</para>
    /// Determines whether a bounded content sample is clearly binary. Samples with a UTF text BOM remain
    /// text; otherwise a NUL byte or more than 30% control characters classifies the sample as binary.
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
    /// <para>是否为 DTB/DTBO 扩展名。</para>
    /// Whether the extension is a DTB/DTBO one.
    /// </summary>
    public static bool IsDtbExtension(string? ext) =>
        ext is not null && ext.Equals(".dtb", StringComparison.OrdinalIgnoreCase)
        || ext is not null && ext.Equals(".dtbo", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <para>是否为 DEX 扩展名。</para>
    /// Whether the extension is a DEX one.
    /// </summary>
    public static bool IsDexExtension(string? ext) =>
        ext is not null && ext.Equals(".dex", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <para>是否为 JAR 容器扩展名（走 DEX 编辑器浏览内部 classes*.dex 与资源）。</para>
    /// Whether the extension is a JAR container one (opens in the DEX editor to browse
    /// classes*.dex entries and resources).
    /// </summary>
    public static bool IsJarExtension(string? ext) =>
        ext is not null && ext.Equals(".jar", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <para>是否为 ARSC 资源表扩展名。</para>
    /// Whether the extension is an ARSC resource-table one.
    /// </summary>
    public static bool IsArscExtension(string? ext) =>
        ext is not null && ext.Equals(".arsc", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <para>是否为 APK 包扩展名。</para>
    /// Whether the extension is an APK package one.
    /// </summary>
    public static bool IsApkExtension(string? ext) =>
        ext is not null && ext.Equals(".apk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <para>是否为 DTS 设备树源文件扩展名。</para>
    /// Whether the extension is a DTS device-tree source extension.
    /// </summary>
    public static bool IsDtsExtension(string? ext) =>
        ext is not null && ext.Equals(".dts", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <para>是否为文件系统镜像扩展名（.img/.ext4/.erofs/.f2fs）。</para>
    /// Whether the extension is a filesystem-image one (.img/.ext4/.erofs/.f2fs).
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
    /// <para>是否为 CPIO/TAR 归档扩展名（.cpio/.tar）。</para>
    /// Whether the extension is a CPIO/TAR archive one (.cpio/.tar).
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
    /// <para>判断文件内容是否命中 ext4/EROFS/F2FS 魔数。
    /// 读取头部参与判定的最小长度：ext4 魔数（2 字节）位于 1080 → 需 1082 字节。
    /// 物理偏移未命中时，若为 sparse 包装镜像（如 product.img）则按逻辑偏移重新探测。</para>
    /// Determines whether the file content matches an ext4/EROFS/F2FS magic. The minimal header length
    /// needed is 1082 bytes (ext4 magic, 2 bytes, sits at offset 1080). When the physical offset misses,
    /// sparse-wrapped images (e.g. product.img) are re-probed at the logical offset.
    /// </summary>
    /// <param name="path">
    /// <para>文件名或完整路径。</para>
    /// A file name or full path.
    /// </param>
    public static bool IsFilesystemImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            // 先按物理偏移的原始魔数判定（raw 镜像直接命中）。
            // First check the raw magic at the physical offset (raw images match directly).
            var header = FormatDetector.ReadHeader(path, FilesystemHeaderLength);
            if (IsFilesystemMagic(header))
            {
                return true;
            }

            // 物理魔数未命中：sparse 包装的镜像（如 product.img）按逻辑偏移重新探测内部文件系统。
            // Physical magic missed: for sparse-wrapped images (e.g. product.img) re-probe the internal
            // filesystem by logical offset.
            if (IsSparseImage(path))
            {
                var logical = SparseReader.PeekLogicalRange(path, 0, FilesystemHeaderLength);
                return IsFilesystemMagic(logical);
            }

            return false;
        }
        catch
        {
            // 读取失败按非文件系统镜像处理。
            // A read failure is treated as a non-filesystem image.
            return false;
        }
    }

    /// <summary>
    /// <para>判断文件是否为 Android sparse 格式镜像：物理偏移 0 处的小端魔数为 0xED26FF3A
    /// （Android sparse 镜像头魔数）。读取失败或内容不匹配返回 <c>false</c>。</para>
    /// Determines whether the file is an Android sparse image: the little-endian magic at physical offset
    /// 0 is 0xED26FF3A (the Android sparse image header magic). Returns <c>false</c> on a read failure or
    /// mismatch.
    /// </summary>
    /// <param name="path">
    /// <para>文件名或完整路径。</para>
    /// A file name or full path.
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
            // 读取失败按非 sparse 镜像处理。
            // A read failure is treated as a non-sparse image.
            return false;
        }
    }

    /// <summary>
    /// <para>是否为 FDT 设备树魔数（大端 0xD00DFEED 位于偏移 0）或 DTBO 表魔数（大端 0xD7B7AB1E）。</para>
    /// Whether the header holds the FDT device-tree magic (big-endian 0xD00DFEED at offset 0) or the
    /// DTBO table magic (big-endian 0xD7B7AB1E).
    /// </summary>
    public static bool IsDtbMagic(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
        {
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        return magic == UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Models.FdtConstants.FdtMagic
            || magic == UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Models.FdtConstants.DtboMagic;
    }

    /// <summary>
    /// <para>是否为 DEX 魔数（ASCII "dex\n" 位于偏移 0）。</para>
    /// Whether the header holds the DEX magic (ASCII "dex\n" at offset 0).
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
            || header[..4].SequenceEqual(new byte[] { 0x55, 0xAA, 0x5A, 0xA5 })))
        {
            return true;
        }

        return header.Length >= 0x28
            && (header.Slice(0x24, 4).SequenceEqual(new byte[] { 0x18, 0x28, 0x6F, 0x01 })
                || header.Slice(0x24, 4).SequenceEqual(new byte[] { 0xED, 0xFE, 0x0F, 0x00 })
                || header.Slice(0x24, 4).SequenceEqual(new byte[] { 0x00, 0x0F, 0xFE, 0xED })
                || header.Slice(0x24, 4).SequenceEqual(new byte[] { 0x01, 0x6F, 0x28, 0x18 }));
    }

    /// <summary>
    /// <para>是否为 ZIP 容器魔数（"PK\x03\x04" / "PK\x05\x06" / "PK\x07\x08"，覆盖空归档与跨卷条目）。</para>
    /// Whether the header holds a ZIP container magic ("PK\x03\x04" / "PK\x05\x06" / "PK\x07\x08",
    /// covering empty archives and spanned entries).
    /// </summary>
    public static bool IsZipMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[0] == (byte)'P' && header[1] == (byte)'K'
            && (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07)
            && (header[3] == 0x04 || header[3] == 0x06 || header[3] == 0x08);

    /// <summary>
    /// <para>判断头部字节是否命中任意一种文件系统魔数（ext4/EROFS/F2FS）。</para>
    /// Determines whether the header bytes match any filesystem magic (ext4/EROFS/F2FS).
    /// </summary>
    public static bool IsFilesystemMagic(ReadOnlySpan<byte> header) =>
        IsExt4Magic(header) || IsErofsMagic(header) || IsF2fsMagic(header);

    /// <summary>
    /// <para>是否为 ext4 魔数（小端 0xEF53 位于偏移 1080）。</para>
    /// Whether the header holds the ext4 magic (little-endian 0xEF53 at offset 1080).
    /// </summary>
    public static bool IsExt4Magic(ReadOnlySpan<byte> header) =>
        header.Length >= 1082 && BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(1080, 2)) == 0xEF53;

    /// <summary>
    /// <para>是否为 EROFS 魔数（小端 0xE0F5E1E2 位于偏移 1024）。</para>
    /// Whether the header holds the EROFS magic (little-endian 0xE0F5E1E2 at offset 1024).
    /// </summary>
    public static bool IsErofsMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 1028 && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1024, 4)) == 0xE0F5E1E2;

    /// <summary>
    /// <para>是否为 F2FS 魔数（小端 0xF2F52010 位于偏移 1024）。</para>
    /// Whether the header holds the F2FS magic (little-endian 0xF2F52010 at offset 1024).
    /// </summary>
    public static bool IsF2fsMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 1028 && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1024, 4)) == 0xF2F52010;

    /// <summary>
    /// <para>文件系统魔数判定所需的头部读取长度（覆盖至 ext4 魔数尾部）。</para>
    /// Header length read for filesystem magic detection (covers past the ext4 magic tail).
    /// </summary>
    private const int FilesystemHeaderLength = 1082;

    /// <summary>
    /// <para>LP super 元数据魔数判定所需的头部读取长度（偏移 4096 处的 4 字节几何魔数）。</para>
    /// Header length read for LP super metadata magic detection (4-byte geometry magic at offset 4096).
    /// </summary>
    private const int LpMetadataHeaderLength = 4100;

    /// <summary>
    /// <para>Rockchip parameter 文本探测读取的头部长度（CMDLINE 行通常位于前 512 字节内）。</para>
    /// Header length read for Rockchip parameter text detection (the CMDLINE line usually sits within
    /// the first 512 bytes).
    /// </summary>
    private const int RockchipParameterHeaderLength = 512;

    /// <summary>
    /// <para>判断头部字节是否为 LP super 元数据（小端几何魔数 0x616c4467 位于偏移 4096）。</para>
    /// Whether the header bytes hold LP super metadata (little-endian geometry magic 0x616c4467 at
    /// offset 4096).
    /// </summary>
    public static bool IsLpMetadataMagic(ReadOnlySpan<byte> header) =>
        header.Length >= LpMetadataHeaderLength
        && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4096, 4))
            == UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Models.MetadataFormat.LP_METADATA_GEOMETRY_MAGIC;

    /// <summary>
    /// <para>判断头部字节是否为 Rockchip parameter 文本：内容须为可打印 ASCII/文本控制字符
    /// （可跳过 UTF-8 BOM），且同时包含 <c>CMDLINE:</c> 与 <c>mtdparts=</c> 特征。</para>
    /// Whether the header bytes hold Rockchip parameter text: the content must be printable
    /// ASCII/text controls (a UTF-8 BOM may be skipped) and contain both <c>CMDLINE:</c> and
    /// <c>mtdparts=</c> markers.
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
    /// <para>判断文件内容是否为 LP super 元数据（读取偏移 4096 处的几何魔数）。
    /// 读取失败或内容不匹配返回 <c>false</c>。</para>
    /// Determines whether the file content holds LP super metadata (reads the geometry magic at
    /// offset 4096). Returns <c>false</c> on a read failure or mismatch.
    /// </summary>
    /// <param name="path">
    /// <para>文件名或完整路径。</para>
    /// A file name or full path.
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
            // 读取失败按非 LP 镜像处理。
            // A read failure is treated as a non-LP image.
            return false;
        }
    }

    /// <summary>
    /// <para>判断文件内容是否为 Rockchip parameter 文本（读取文件起始处并检查 CMDLINE:/mtdparts= 特征）。
    /// 读取失败或内容不匹配返回 <c>false</c>。</para>
    /// Determines whether the file content is a Rockchip parameter text (reads the file start and
    /// checks the CMDLINE:/mtdparts= markers). Returns <c>false</c> on a read failure or mismatch.
    /// </summary>
    /// <param name="path">
    /// <para>文件名或完整路径。</para>
    /// A file name or full path.
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
            // 读取失败按非 Rockchip parameter 处理。
            // A read failure is treated as a non-Rockchip parameter file.
            return false;
        }
    }

    /// <summary>
    /// <para>文件名/扩展名是否可能为 LP super 镜像（避免对普通文件做内容读取）：
    /// 文件名以 <c>super</c> 开头，或扩展名为 <c>.img</c>/<c>.super</c>。</para>
    /// Whether the file name/extension could be an LP super image (avoids content reads for ordinary
    /// files): the file name starts with <c>super</c>, or the extension is <c>.img</c>/<c>.super</c>.
    /// </summary>
    private static bool IsLpCandidatePath(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith("super", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ext = Path.GetExtension(path);
        return ext.Equals(".img", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".super", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <para>分区表内容探测所需的头部读取长度（覆盖 GPT 头魔数 @512 与 MBR 签名 @510）。</para>
    /// Header length read for partition-table content detection (covers the GPT header magic @512
    /// and the MBR signature @510).
    /// </summary>
    private const int PartitionHeaderLength = 520;

    /// <summary>
    /// <para>判断头部字节是否携带 GPT 签名（"EFI PART" @512）。</para>
    /// Whether the header bytes carry the GPT signature ("EFI PART" @512).
    /// </summary>
    public static bool IsGptMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 520 && header.Slice(512, 8).SequenceEqual("EFI PART"u8);

    /// <summary>
    /// <para>判断头部是否包含有效的非空 MBR 分区表，排除 FAT 卷引导扇区。</para>
    /// Whether the header contains a populated MBR partition table rather than a FAT boot record.
    /// </summary>
    public static bool IsMbrMagic(ReadOnlySpan<byte> header) =>
        UotanStudio.Models.MbrTable.HasPartitionTable(header);

    /// <summary>
    /// <para>判断文件内容是否为 GPT 分区表（读取头部并检查 "EFI PART" 签名）。
    /// 读取失败或内容不匹配返回 <c>false</c>。</para>
    /// Determines whether the file content is a GPT partition table (reads the header and checks the
    /// "EFI PART" signature). Returns <c>false</c> on a read failure or mismatch.
    /// </summary>
    public static bool IsGptImage(string path) => IsPartitionImage(path, IsGptMagic);

    /// <summary>
    /// <para>判断文件内容是否为 MBR 分区表（读取头部并检查 0x55AA 签名）。
    /// 读取失败或内容不匹配返回 <c>false</c>。</para>
    /// Determines whether the file content is an MBR partition table (reads the header and checks the
    /// 0x55AA signature). Returns <c>false</c> on a read failure or mismatch.
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
            // 读取失败按非分区表处理。
            // A read failure is treated as a non-partition-table file.
            return false;
        }
    }

    /// <summary>
    /// <para>文件名/扩展名是否可能为 GPT/MBR 分区表镜像（避免对普通文件做内容读取）：
    /// 扩展名为 <c>.img</c>/<c>.gpt</c>/<c>.mbr</c>/<c>.bin</c> 或无扩展名。</para>
    /// Whether the file name/extension could be a GPT/MBR partition-table image (avoids content reads
    /// for ordinary files): the extension is <c>.img</c>/<c>.gpt</c>/<c>.mbr</c>/<c>.bin</c> or absent.
    /// </summary>
    private static bool IsPartitionTableCandidatePath(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length == 0)
        {
            return true;
        }

        return ext.Equals(".img", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gpt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mbr", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <para>是否为已知固件扩展名（应走固件元数据面板）。
    /// 集合由 <c>FormatDetector.RegisteredHandlers</c> 各格式处理器的 <c>FileExtensions</c>
    /// 聚合而来，避免与注册表双源维护；首次使用时幂等触发 <c>FirmwareFormatRegistry.RegisterAll</c>。
    /// AXML 处理器登记的 <c>xml</c> 除外——该扩展名必须经内容魔数确认（见 <see cref="ResolveMode(string, byte[])"/>
    /// 的 AXML 分支），纯文本 .xml 仍按文本打开。</para>
    /// Whether the extension is a known firmware one (should open in the firmware metadata panel).
    /// The set is aggregated from the <c>FileExtensions</c> of every <c>FormatDetector.RegisteredHandlers</c>
    /// format handler, avoiding dual-source maintenance with the registry; <c>FirmwareFormatRegistry.RegisterAll</c>
    /// is idempotently triggered on first use. The AXml handler's <c>xml</c> is excluded — that extension must be
    /// content-confirmed by magic (see the AXML branch of <see cref="ResolveMode(string, byte[])"/>), and plain-text
    /// .xml stays on the text route.
    /// </summary>
    public static bool IsFirmwareExtension(string? ext)
    {
        if (ext is null || ext.Length == 0)
        {
            return false;
        }

        // FileExtensions 不带点、小写存储；这里去掉前导点后按 OrdinalIgnoreCase 命中。
        // FileExtensions are stored dot-less and lowercase; strip the leading dot and match case-insensitively.
        var normalized = ext[0] == '.' ? ext[1..] : ext;
        return _firmwareExtensions.Value.Contains(normalized);
    }

    /// <summary>
    /// <para>是否为已知二进制 / 固件扩展名（应走十六进制编辑器）。
    /// 主集合由格式处理器注册表聚合驱动（与 <see cref="IsFirmwareExtension"/> 共用单一数据源），
    /// 未命中注册表但仍是强二进制的残留扩展名（如 <c>.dat/.tgz/.zip/.el/.elf/.so</c>，
    /// 以及 ELF 魔数兜底的场景）保持 <c>Hex</c>。</para>
    /// Whether the extension is a known binary/firmware one (should open in the hex editor).
    /// The primary set is driven by the aggregated format-handler registry (single data source shared with
    /// <see cref="IsFirmwareExtension"/>); extensions not in the registry yet still strongly binary
    /// (e.g. <c>.dat/.tgz/.zip/.el/.elf/.so</c>, plus the ELF magic fallback) keep <c>Hex</c>.
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
    /// <para>格式处理器注册表未覆盖但仍属强二进制的残留扩展名（保持 Hex 打开）。</para>
    /// Residual extensions not covered by the format-handler registry yet still strongly binary (kept on Hex).
    /// </summary>
    private static readonly HashSet<string> _residualBinaryExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".dat", ".tgz", ".zip", ".el", ".elf", ".so" };

    /// <summary>
    /// <para>固件扩展名静态缓存：首次访问时幂等注册全部格式处理器并聚合其扩展名。</para>
    /// Static cache of firmware extensions, built on first access by idempotently registering every
    /// format handler and aggregating their extensions.
    /// </summary>
    private static readonly Lazy<HashSet<string>> _firmwareExtensions = new(LoadFirmwareExtensions);

    private static HashSet<string> LoadFirmwareExtensions()
    {
        // 幂等注册全部格式处理器，确保聚合集合完整；应用启动时通常已注册。
        // Idempotently register every format handler so the aggregated set is complete;
        // the app usually registers them at startup already.
        FirmwareFormatRegistry.RegisterAll();

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in FormatDetector.RegisteredHandlers)
        {
            foreach (var extension in handler.FileExtensions)
            {
                set.Add(extension);
            }
        }

        // AXML 处理器的 "xml" 是内容确认型扩展名（须经 AXML 魔数），不能仅凭扩展名路由到
        // 固件面板，否则纯文本 .xml 会被错误接管；此处的排除是安全名单的负向裁切。
        // The AXml handler's "xml" is content-confirmed (AXML magic) and must not be claimed by
        // extension alone, or plain-text .xml would be misrouted; this exclusion is a negative carve-out
        // of the safe positive list.
        set.Remove("xml");

        return set;
    }

    /// <summary>
    /// <para>是否为已知文本 / 代码扩展名（应走文本编辑器）。
    /// 复用 <see cref="SyntaxLanguageRegistry"/>，与语法高亮语言解析保持一致。</para>
    /// Whether the extension is a known text/code one (should open in the text editor).
    /// Reuses <see cref="SyntaxLanguageRegistry"/> to stay consistent with syntax-language resolution.
    /// </summary>
    public static bool IsTextExtension(string? ext)
    {
        if (ext is null)
        {
            return false;
        }

        // 语法高亮能解析的扩展名一律视为文本。
        // Extensions resolvable by syntax highlighting are treated as text.
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
