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
    Firmware
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