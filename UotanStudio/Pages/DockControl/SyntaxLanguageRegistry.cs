using System.IO;

namespace UotanStudio.Pages.DockControl;

/// <summary>
/// <para>语法语言注册表，按文件扩展名解析 TextMate 语法语言标识，供文档编辑器选择语法高亮。</para>
/// Syntax language registry, resolving TextMate syntax language ids by file extension for the document
/// editor.
/// </summary>
public static class SyntaxLanguageRegistry
{
    /// <summary>
    /// <para>按扩展名（不区分大小写）返回 TextMate 语法语言标识；不支持的扩展名返回 <c>null</c>，不安装语法高亮。</para>
    /// Returns the TextMate syntax language id by file extension (case-insensitive); returns <c>null</c>
    /// for unsupported extensions so no syntax highlighting is installed.
    /// </summary>
    /// <param name="fileNameOrPath">
    /// <para>文件名或完整路径。</para>
    /// A file name or a full path.
    /// </param>
    /// <returns>
    /// <para>TextMate 语法语言标识（如 <c>ini</c>、<c>xml</c>）；未知扩展名为 <c>null</c>。</para>
    /// The TextMate syntax language id (e.g. <c>ini</c>, <c>xml</c>); <c>null</c> for unknown extensions.
    /// </returns>
    public static string? GetLanguageId(string fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return null;
        }

        return Path.GetExtension(fileNameOrPath).ToLowerInvariant() switch
        {
            ".prop" or ".properties" or ".rc" or ".ini" => "ini",
            ".xml" => "xml",
            ".json" => "json",
            ".sh" => "shell",
            ".mk" => "makefile",
            ".c" or ".h" => "c",
            ".cpp" or ".cc" => "cpp",
            ".cs" => "csharp",
            ".md" => "markdown",
            ".yaml" or ".yml" => "yaml",
            _ => null
        };
    }
}