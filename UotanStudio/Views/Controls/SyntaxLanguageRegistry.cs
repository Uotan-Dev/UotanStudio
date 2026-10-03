namespace UotanStudio.Views.Controls;

/// <summary>
/// <para>语法语言注册表，按文件扩展名解析 TextMate 语法语言标识，供文档编辑器选择语法高亮。</para>
/// Syntax language registry, resolving TextMate syntax language ids by file extension for the document
/// editor.
/// </summary>
public static class SyntaxLanguageRegistry
{
    /// <summary>
    /// <para>「自动检测」选项的占位标识（空字符串），UI 下拉栏用此值表示按扩展名自动选择。</para>
    /// Placeholder id for the "auto-detect" option (empty string); used by the UI dropdown to indicate
    /// extension-based automatic selection.
    /// </summary>
    public const string AutoDetectId = "";

    /// <summary>
    /// <para>所有已注册的语法语言列表，每项包含 TextMate 语言标识与友好显示名称，供 UI 下拉栏使用。</para>
    /// All registered syntax languages, each carrying a TextMate language id and a friendly display
    /// name, for use by the UI dropdown.
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
    /// <para>返回全部已注册语法语言（含标识与显示名称），供属性面板下拉栏枚举。</para>
    /// Returns all registered syntax languages (id + display name) for the properties-panel dropdown.
    /// </summary>
    public static IReadOnlyList<(string Id, string DisplayName)> GetAllLanguages() => s_allLanguages;

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
            ".dts" or ".dtsi" => "dts",
            ".lua" => "lua",
            _ => null
        };
    }
}