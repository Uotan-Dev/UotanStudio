using System;
using System.Collections.Generic;
using UotanStudio.Services;

namespace UotanStudio.Models;

/// <summary>
/// <para>语言包描述符，提供语言包的完整元数据，实现 <see cref="ILanguagePack"/> 接口。</para>
/// Language pack descriptor, providing complete metadata for a language pack, implementing the <see cref="ILanguagePack"/> interface.
/// </summary>
public class LanguagePackDescriptor : ILanguagePack
{
    /// <summary>
    /// <para>获取语言包的区域性名称（如 "en-US"、"zh-CN"）。</para>
    /// Gets the culture name of the language pack (e.g., "en-US", "zh-CN").
    /// </summary>
    public string CultureName { get; }

    /// <summary>
    /// <para>获取语言的原生名称。</para>
    /// Gets the native name of the language.
    /// </summary>
    public string NativeName { get; }

    /// <summary>
    /// <para>获取语言的英文名称。</para>
    /// Gets the English name of the language.
    /// </summary>
    public string EnglishName { get; }

    /// <summary>
    /// <para>获取语言包的版本号。</para>
    /// Gets the version of the language pack.
    /// </summary>
    public LanguagePackVersion Version { get; }

    /// <summary>
    /// <para>获取语言包包含的模块名称列表。</para>
    /// Gets the list of module names included in the language pack.
    /// </summary>
    public IReadOnlyList<string> ModuleNames { get; }

    /// <summary>
    /// <para>获取一个值，指示该语言包是否为应用程序内置语言包。</para>
    /// Gets a value indicating whether the language pack is built-in to the application.
    /// </summary>
    public bool IsBuiltIn { get; }

    /// <summary>
    /// <para>初始化 <see cref="LanguagePackDescriptor"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="LanguagePackDescriptor"/> class.
    /// </summary>
    /// <param name="cultureName">
    /// <para>区域性名称。</para>
    /// The culture name.
    /// </param>
    /// <param name="nativeName">
    /// <para>原生名称。</para>
    /// The native name.
    /// </param>
    /// <param name="englishName">
    /// <para>英文名称。</para>
    /// The English name.
    /// </param>
    /// <param name="version">
    /// <para>版本号。</para>
    /// The version.
    /// </param>
    /// <param name="moduleNames">
    /// <para>模块名称列表。</para>
    /// The module names list.
    /// </param>
    /// <param name="isBuiltIn">
    /// <para>是否为内置语言包。</para>
    /// Whether the language pack is built-in.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <para>当 cultureName、nativeName 或 englishName 为 null 时抛出。</para>
    /// Thrown when cultureName, nativeName, or englishName is null.
    /// </exception>
    public LanguagePackDescriptor(
        string cultureName,
        string nativeName,
        string englishName,
        LanguagePackVersion version,
        IReadOnlyList<string> moduleNames,
        bool isBuiltIn = false)
    {
        ArgumentNullException.ThrowIfNull(cultureName);
        ArgumentNullException.ThrowIfNull(nativeName);
        ArgumentNullException.ThrowIfNull(englishName);

        CultureName = cultureName;
        NativeName = nativeName;
        EnglishName = englishName;
        Version = version ?? LanguagePackVersion.Default;
        ModuleNames = moduleNames ?? Array.Empty<string>();
        IsBuiltIn = isBuiltIn;
    }

    /// <summary>
    /// <para>从该描述符创建 <see cref="LanguageInfo"/> 实例。</para>
    /// Creates a <see cref="LanguageInfo"/> instance from this descriptor.
    /// </summary>
    /// <returns>
    /// <para>对应的 <see cref="LanguageInfo"/> 实例。</para>
    /// The corresponding <see cref="LanguageInfo"/> instance.
    /// </returns>
    public LanguageInfo ToLanguageInfo() => new(CultureName, NativeName, EnglishName, IsBuiltIn);
}
