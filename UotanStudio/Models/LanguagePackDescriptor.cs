using UotanStudio.Services;

namespace UotanStudio.Models;

/// <summary>
/// Language pack descriptor, providing complete metadata for a language pack, implementing the <see cref="ILanguagePack"/> interface.
/// <para>语言包描述符，提供语言包的完整元数据，实现 <see cref="ILanguagePack"/> 接口。</para>
/// </summary>
public class LanguagePackDescriptor : ILanguagePack
{
    /// <summary>
    /// Gets the culture name of the language pack.
    /// <para>获取语言包的区域性名称。</para>
    /// </summary>
    /// <value>The culture name, such as en-US.<para>区域性名称，例如 en-US。</para></value>
    public string CultureName { get; }

    /// <summary>
    /// Gets the native name of the language.
    /// <para>获取语言的原生名称。</para>
    /// </summary>
    /// <value>The name written in the language itself.<para>以该语言自身书写的名称。</para></value>
    public string NativeName { get; }

    /// <summary>
    /// Gets the English name of the language.
    /// <para>获取语言的英文名称。</para>
    /// </summary>
    /// <value>The name written in English.<para>英文书写的名称。</para></value>
    public string EnglishName { get; }

    /// <summary>
    /// Gets the version of the language pack.
    /// <para>获取语言包的版本号。</para>
    /// </summary>
    /// <value>The language-pack version.<para>语言包的版本。</para></value>
    public LanguagePackVersion Version { get; }

    /// <summary>
    /// Gets the list of module names included in the language pack.
    /// <para>获取语言包包含的模块名称列表。</para>
    /// </summary>
    /// <value>The module names carried by the language pack.<para>语言包包含的模块名称。</para></value>
    public IReadOnlyList<string> ModuleNames { get; }

    /// <summary>
    /// Gets a value indicating whether the language pack is built-in to the application.
    /// <para>获取一个值，指示该语言包是否为应用程序内置语言包。</para>
    /// </summary>
    /// <value>True when the language pack is built-in.<para>语言包为内置语言包时为 true。</para></value>
    public bool IsBuiltIn { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguagePackDescriptor"/> class.
    /// <para>初始化 <see cref="LanguagePackDescriptor"/> 类的新实例。</para>
    /// </summary>
    /// <param name="cultureName">
    /// The culture name.
    /// <para>区域性名称。</para>
    /// </param>
    /// <param name="nativeName">
    /// The native name.
    /// <para>原生名称。</para>
    /// </param>
    /// <param name="englishName">
    /// The English name.
    /// <para>英文名称。</para>
    /// </param>
    /// <param name="version">
    /// The version.
    /// <para>版本号。</para>
    /// </param>
    /// <param name="moduleNames">
    /// The module names list.
    /// <para>模块名称列表。</para>
    /// </param>
    /// <param name="isBuiltIn">
    /// Whether the language pack is built-in.
    /// <para>是否为内置语言包。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when cultureName, nativeName, or englishName is null.
    /// <para>当 cultureName、nativeName 或 englishName 为 null 时抛出。</para>
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
        ModuleNames = moduleNames ?? [];
        IsBuiltIn = isBuiltIn;
    }

    /// <summary>
    /// Creates a <see cref="LanguageInfo"/> instance from this descriptor.
    /// <para>从该描述符创建 <see cref="LanguageInfo"/> 实例。</para>
    /// </summary>
    /// <returns>
    /// The corresponding <see cref="LanguageInfo"/> instance.
    /// <para>对应的 <see cref="LanguageInfo"/> 实例。</para>
    /// </returns>
    public LanguageInfo ToLanguageInfo() => new(CultureName, NativeName, EnglishName, IsBuiltIn);
}