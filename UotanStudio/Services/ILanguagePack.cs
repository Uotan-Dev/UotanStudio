using UotanStudio.Models;

namespace UotanStudio.Services;

/// <summary>
/// Defines the registration contract for a language pack, containing its metadata.
/// <para>定义语言包的注册契约，包含语言包的元数据信息。</para>
/// </summary>
public interface ILanguagePack
{
    /// <summary>
    /// Gets the culture name of the language pack, such as "en-US" or "zh-CN".
    /// <para>获取语言包的区域性名称，如 "en-US" 或 "zh-CN"。</para>
    /// </summary>
    /// <value>The culture identifier of the pack. <para>语言包的区域性标识。</para></value>
    string CultureName { get; }

    /// <summary>
    /// Gets the language name written in the language's own script.
    /// <para>获取以语言自身文字书写的语言名称，如 "English" 或 "简体中文"。</para>
    /// </summary>
    /// <value>The language name written in its own script. <para>以语言自身文字书写的名称。</para></value>
    string NativeName { get; }

    /// <summary>
    /// Gets the English name of the language, such as "English" or "Chinese (Simplified)".
    /// <para>获取语言的英文名称，如 "English" 或 "Chinese (Simplified)"。</para>
    /// </summary>
    /// <value>The English-language name of the language. <para>该语言的英文名义。</para></value>
    string EnglishName { get; }

    /// <summary>
    /// Gets the version of the language pack.
    /// <para>获取语言包的版本号。</para>
    /// </summary>
    /// <value>The declared version of the pack. <para>语言包声明的版本。</para></value>
    LanguagePackVersion Version { get; }

    /// <summary>
    /// Gets the list of module names included in the language pack, such as "Menu", "Dock", "Node" and "Settings".
    /// <para>获取语言包包含的模块名称列表，如 "Menu"、"Dock"、"Node" 与 "Settings"。</para>
    /// </summary>
    /// <value>The included module names. <para>包含的模块名列表。</para></value>
    IReadOnlyList<string> ModuleNames { get; }
}