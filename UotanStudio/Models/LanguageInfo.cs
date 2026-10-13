namespace UotanStudio.Models;

/// <summary>
/// Represents dynamic information about an available language, supporting runtime extension.
/// <para>表示一种可用语言的动态信息，支持运行时动态扩展。</para>
/// </summary>
public class LanguageInfo : IEquatable<LanguageInfo>
{
    /// <summary>
    /// Gets or sets the culture name of the language.
    /// <para>获取或设置语言的区域性名称。</para>
    /// </summary>
    /// <value>The culture name, such as en-US.<para>区域性名称，例如 en-US。</para></value>
    public string CultureName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the native name of the language.
    /// <para>获取或设置语言的原生名称。</para>
    /// </summary>
    /// <value>The name written in the language itself.<para>以该语言自身书写的名称。</para></value>
    public string NativeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the English name of the language.
    /// <para>获取或设置语言的英文名称。</para>
    /// </summary>
    /// <value>The name written in English.<para>英文书写的名称。</para></value>
    public string EnglishName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the language is built-in to the application.
    /// <para>获取或设置一个值，指示该语言是否为应用程序内置语言。</para>
    /// </summary>
    /// <value>True when the language is built-in.<para>语言为内置语言时为 true。</para></value>
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this option represents "follow system language".
    /// <para>获取或设置一个值，指示该选项是否表示"跟随系统语言"。</para>
    /// </summary>
    /// <value>True when the option follows the system language.<para>选项跟随系统语言时为 true。</para></value>
    public bool IsSystemLanguage { get; set; }

    /// <summary>
    /// Gets the display name. Prefers the native name and falls back to the culture name when empty.
    /// <para>获取用于显示的名称。优先显示原生名称，原生名称为空时显示区域性名称。</para>
    /// </summary>
    /// <value>The name to display.<para>用于显示的名称。</para></value>
    public string DisplayName => string.IsNullOrEmpty(NativeName) ? CultureName : NativeName;

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguageInfo"/> class.
    /// <para>初始化 <see cref="LanguageInfo"/> 类的新实例。</para>
    /// </summary>
    public LanguageInfo() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguageInfo"/> class with the specified culture name, native name, English name, and built-in flag.
    /// <para>使用指定的区域性名称、原生名称、英文名称和内置标志初始化 <see cref="LanguageInfo"/> 类的新实例。</para>
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
    /// <param name="isBuiltIn">
    /// Whether the language is built-in.
    /// <para>是否为内置语言。</para>
    /// </param>
    public LanguageInfo(string cultureName, string nativeName, string englishName, bool isBuiltIn = false)
    {
        CultureName = cultureName;
        NativeName = nativeName;
        EnglishName = englishName;
        IsBuiltIn = isBuiltIn;
    }

    /// <summary>
    /// Creates a <see cref="LanguageInfo"/> instance representing "follow system language".
    /// <para>创建表示"跟随系统语言"的 <see cref="LanguageInfo"/> 实例。</para>
    /// </summary>
    /// <value>A <see cref="LanguageInfo"/> instance for the system-language option.<para>表示跟随系统语言选项的 <see cref="LanguageInfo"/> 实例。</para></value>
    public static LanguageInfo SystemLanguage { get; } = new()
    {
        CultureName = string.Empty,
        NativeName = "Auto",
        EnglishName = "Auto Detect",
        IsBuiltIn = true,
        IsSystemLanguage = true
    };

    /// <summary>
    /// Determines whether the current instance is equal to another <see cref="LanguageInfo"/>.
    /// <para>判断当前实例是否与另一个 <see cref="LanguageInfo"/> 相等。</para>
    /// </summary>
    /// <param name="other">
    /// The other instance to compare.
    /// <para>要比较的另一个实例。</para>
    /// </param>
    /// <returns>
    /// True if equal; otherwise, false.
    /// <para>如果相等则为 true；否则为 false。</para>
    /// </returns>
    public bool Equals(LanguageInfo? other)
    {
        if (other is null) return false;
        return ReferenceEquals(this, other) ? true : IsSystemLanguage == other.IsSystemLanguage && CultureName == other.CultureName;
    }

    /// <summary>
    /// Determines whether the current instance is equal to the specified object.
    /// <para>判断当前实例是否与指定对象相等。</para>
    /// </summary>
    /// <param name="obj">The object to compare. <para>要比较的对象。</para></param>
    /// <returns>True if equal; otherwise, false.<para>如果相等则为 true；否则为 false。</para></returns>
    public override bool Equals(object? obj) => Equals(obj as LanguageInfo);

    /// <summary>
    /// Gets the hash code.
    /// <para>获取哈希代码。</para>
    /// </summary>
    /// <returns>The hash code for this instance.<para>当前实例的哈希代码。</para></returns>
    public override int GetHashCode() => HashCode.Combine(IsSystemLanguage, CultureName);

    /// <summary>
    /// Returns a string representing the current language.
    /// <para>返回表示当前语言的字符串。</para>
    /// </summary>
    /// <returns>The culture name, or System for the system-language option.<para>区域性名称；跟随系统语言时返回 System。</para></returns>
    public override string ToString() => IsSystemLanguage ? "System" : CultureName;

    /// <summary>
    /// Determines whether two <see cref="LanguageInfo"/> instances are equal.
    /// <para>判断两个 <see cref="LanguageInfo"/> 实例是否相等。</para>
    /// </summary>
    public static bool operator ==(LanguageInfo? left, LanguageInfo? right)
    {
        return left is null ? right is null : left.Equals(right);
    }

    /// <summary>
    /// Determines whether two <see cref="LanguageInfo"/> instances are not equal.
    /// <para>判断两个 <see cref="LanguageInfo"/> 实例是否不相等。</para>
    /// </summary>
    public static bool operator !=(LanguageInfo? left, LanguageInfo? right) => !(left == right);
}