using System;
using System.IO;
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
    Dex
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
    /// 已知二进制/固件扩展名→<c>Hex</c>、已知文本/代码扩展名→<c>Text</c>、其余→<c>Auto</c>。</para>
    /// Resolves the open mode for the given path: <c>.dts→Text</c>, <c>.dtb/.dtbo→Dtb</c>,
    /// known binary/firmware extensions→<c>Hex</c>, known text/code extensions→<c>Text</c>, else <c>Auto</c>.
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

        // 设备树源文件按文本（语法高亮）打开。
        // Device tree sources open as text (with syntax highlighting).
        if (IsDtsExtension(ext))
        {
            return EditorOpenMode.Text;
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
        var mode = ResolveMode(path);
        if (mode != EditorOpenMode.Auto)
        {
            return mode;
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
    /// <para>是否为 DTS 设备树源文件扩展名。</para>
    /// Whether the extension is a DTS device-tree source extension.
    /// </summary>
    public static bool IsDtsExtension(string? ext) =>
        ext is not null && ext.Equals(".dts", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <para>是否为已知二进制 / 固件扩展名（应走十六进制编辑器）。
    /// 该集合与 <c>UotanStudio.Firmware</c> 各格式处理器的 <c>FileExtensions</c> 保持一致。</para>
    /// Whether the extension is a known binary/firmware one (should open in the hex editor).
    /// The set stays in sync with the <see cref="E:UotanStudio.Firmware.Common.IFormatHandler.FileExtensions"/>
    /// of the firmware format handlers.
    /// </summary>
    public static bool IsKnownBinaryExtension(string? ext)
    {
        if (ext is null)
        {
            return false;
        }

        return ext.ToLowerInvariant() switch
        {
            ".img" or ".bin" or ".br" or ".payload" or ".dat" or ".tar" or ".gz" or ".tgz" or ".zip" or
            ".apk" or ".boot" or ".vbmeta" or ".nb0" or ".ntpi" or ".dz" or ".kdz" or ".pac" or ".sparse" or
            ".simg" or ".super" or ".ozip" or ".ofp" or ".ops" or ".el" or ".elf" or ".so" => true,
            _ => false
        };
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