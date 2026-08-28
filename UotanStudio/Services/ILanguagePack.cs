using UotanStudio.Models;

namespace UotanStudio.Services;

/// <summary>
/// <para>定义语言包的注册契约，包含语言包的元数据信息。</para>
/// Defines the registration contract for a language pack, containing its metadata.
/// </summary>
public interface ILanguagePack
{
    /// <summary>
    /// <para>获取语言包的区域性名称（如 "en-US"、"zh-CN"）。</para>
    /// Gets the culture name of the language pack (e.g., "en-US", "zh-CN").
    /// </summary>
    string CultureName { get; }

    /// <summary>
    /// <para>获取语言的原生名称（如 "English"、"简体中文"）。</para>
    /// Gets the native name of the language (e.g., "English", "简体中文").
    /// </summary>
    string NativeName { get; }

    /// <summary>
    /// <para>获取语言的英文名称（如 "English"、"Chinese (Simplified)"）。</para>
    /// Gets the English name of the language (e.g., "English", "Chinese (Simplified)").
    /// </summary>
    string EnglishName { get; }

    /// <summary>
    /// <para>获取语言包的版本号。</para>
    /// Gets the version of the language pack.
    /// </summary>
    LanguagePackVersion Version { get; }

    /// <summary>
    /// <para>获取语言包包含的模块名称列表（如 "Menu"、"Dock"、"Node"、"Settings"）。</para>
    /// Gets the list of module names included in the language pack (e.g., "Menu", "Dock", "Node", "Settings").
    /// </summary>
    IReadOnlyList<string> ModuleNames { get; }
}