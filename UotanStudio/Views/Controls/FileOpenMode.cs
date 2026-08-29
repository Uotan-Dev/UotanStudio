using System.Buffers.Binary;
using UotanStudio.Firmware;
using UotanStudio.Firmware.Common;

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
    FileSystem
}

/// <summary>
/// <para>文件打开方式判定引擎：按扩展名白名单优先、内容探测兜底，综合判断文件应以何种编辑器打开。</para>
/// File open-mode resolution engine: extension-whitelist first, content sniffing as fallback, deciding
/// which editor a file should open in.
/// </summary>
public static class FileOpenMode
{
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
    public static EditorOpenMode ResolveMode(string path, byte[]? header)
    {
        // AXML 内容探测优先：AndroidManifest.xml 等二进制 XML 即使扩展名为 .xml（会被文本路由
        // 拦截）也应走 Manifest 编辑器，避免按 UTF-8 文本打开乱码。
        // AXML content sniffing first: binary XML files such as AndroidManifest.xml carry a .xml
        // extension (which the text route would claim), so the AXML magic must win to avoid opening
        // them as mangled UTF-8 text.
        if (header is not null && UotanStudio.Firmware.AXml.AXmlParser.IsAxml(header))
        {
            return EditorOpenMode.Axml;
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

        var mode = ResolveMode(path);
        if (mode != EditorOpenMode.Auto)
        {
            return mode;
        }

        // ARSC 内容探测：resources.arsc 为 0x0002 表头。
        // ARSC content sniffing: resources.arsc carries the 0x0002 table header.
        if (header is not null && UotanStudio.Firmware.Arsc.ArscParser.IsArsc(header))
        {
            return EditorOpenMode.Arsc;
        }

        // Auto 分支的 magic 兜底：识别出强二进制类型视为十六进制。
        // Magic fallback for the Auto branch: a recognized strongly-binary type opens as hex.
        if (header is not null && ManagedMagic.Detect(header) is not null)
        {
            return EditorOpenMode.Hex;
        }

        return EditorOpenMode.Auto;
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
    /// <para>判断文件内容是否命中 ext4/EROFS/F2FS 魔数。
    /// 读取头部参与判定的最小长度：ext4 魔数（2 字节）位于 1080 → 需 1082 字节。</para>
    /// Determines whether the file content matches an ext4/EROFS/F2FS magic. The minimal header length
    /// needed is 1082 bytes (ext4 magic, 2 bytes, sits at offset 1080).
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
            var header = FormatDetector.ReadHeader(path, FilesystemHeaderLength);
            return IsFilesystemMagic(header);
        }
        catch
        {
            // 读取失败按非文件系统镜像处理。
            // A read failure is treated as a non-filesystem image.
            return false;
        }
    }

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