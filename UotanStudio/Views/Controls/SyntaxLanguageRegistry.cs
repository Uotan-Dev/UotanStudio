namespace UotanStudio.Views.Controls;

/// <summary>
/// Syntax language registry, resolving TextMate syntax language ids by file extension for the document
/// editor.
/// <para>语法语言注册表，按文件扩展名解析 TextMate 语法语言标识，供文档编辑器选择语法高亮。</para>
/// </summary>
public static class SyntaxLanguageRegistry
{
    /// <summary>
    /// Placeholder id for the "auto-detect" option (empty string); used by the UI dropdown to indicate
    /// extension-based automatic selection.
    /// <para>「自动检测」选项的占位标识（空字符串），UI 下拉栏用此值表示按扩展名自动选择。</para>
    /// </summary>
    public const string AutoDetectId = "";

    /// <summary>
    /// All registered syntax languages, each carrying a TextMate language id and a friendly display
    /// name, for use by the UI dropdown.
    /// <para>所有已注册的语法语言列表，每项包含 TextMate 语言标识与友好显示名称，供 UI 下拉栏使用。</para>
    /// </summary>
    private static readonly IReadOnlyList<(string Id, string DisplayName)> s_allLanguages =
    [
        ("ini", "INI"),
        ("xml", "XML"),
        ("json", "JSON"),
        ("shell", "Shell"),
        ("makefile", "Makefile"),
        ("c", "C"),
        ("cpp", "C++"),
        ("csharp", "C#"),
        ("markdown", "Markdown"),
        ("yaml", "YAML"),
        ("dts", "DTS"),
        ("lua", "Lua"),
    ];

    /// <summary>
    /// Returns all registered syntax languages (id + display name) for the properties-panel dropdown.
    /// <para>返回全部已注册语法语言（含标识与显示名称），供属性面板下拉栏枚举。</para>
    /// </summary>
    public static IReadOnlyList<(string Id, string DisplayName)> GetAllLanguages() => s_allLanguages;

    /// <summary>Returns the TextMate syntax language id by file extension (case-insensitive); returns <c>null</c> for unsupported extensions so no syntax highlighting is installed.
    /// <para>按扩展名（不区分大小写）返回 TextMate 语法语言标识；不支持的扩展名返回 <c>null</c>，不安装语法高亮。</para></summary>
    /// <param name="fileNameOrPath">A file name or a full path.<para>文件名或完整路径。</para></param>
    /// <returns>The TextMate syntax language id (e.g. <c>ini</c>, <c>xml</c>); <c>null</c> for unknown extensions.
    /// <para>TextMate 语法语言标识（如 <c>ini</c>、<c>xml</c>）；未知扩展名为 <c>null</c>。</para></returns>
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
            ".dts" or ".dtsi" => "dts",
            ".lua" => "lua",
            _ => null
        };
    }
}