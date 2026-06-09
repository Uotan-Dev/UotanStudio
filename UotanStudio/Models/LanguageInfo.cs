using System;

namespace UotanStudio.Models;

/// <summary>
/// <para>表示一种可用语言的动态信息，支持运行时动态扩展。</para>
/// Represents dynamic information about an available language, supporting runtime extension.
/// </summary>
public class LanguageInfo : IEquatable<LanguageInfo>
{
    /// <summary>
    /// <para>获取或设置语言的区域性名称（如 "en-US"、"zh-CN"）。</para>
    /// Gets or sets the culture name of the language (e.g., "en-US", "zh-CN").
    /// </summary>
    public string CultureName { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置语言的原生名称（如 "English"、"简体中文"）。</para>
    /// Gets or sets the native name of the language (e.g., "English", "简体中文").
    /// </summary>
    public string NativeName { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置语言的英文名称（如 "English"、"Chinese (Simplified)"）。</para>
    /// Gets or sets the English name of the language (e.g., "English", "Chinese (Simplified)").
    /// </summary>
    public string EnglishName { get; set; } = string.Empty;

    /// <summary>
    /// <para>获取或设置一个值，指示该语言是否为应用程序内置语言。</para>
    /// Gets or sets a value indicating whether the language is built-in to the application.
    /// </summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// <para>获取或设置一个值，指示该选项是否表示"跟随系统语言"。</para>
    /// Gets or sets a value indicating whether this option represents "follow system language".
    /// </summary>
    public bool IsSystemLanguage { get; set; }

    /// <summary>
    /// <para>获取用于在界面中显示的名称。优先显示原生名称，若原生名称为空则显示区域性名称。</para>
    /// Gets the display name for UI. Prefers the native name; falls back to the culture name if empty.
    /// </summary>
    public string DisplayName => string.IsNullOrEmpty(NativeName) ? CultureName : NativeName;

    /// <summary>
    /// <para>初始化 <see cref="LanguageInfo"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="LanguageInfo"/> class.
    /// </summary>
    public LanguageInfo() { }

    /// <summary>
    /// <para>使用指定的区域性名称、原生名称、英文名称和内置标志初始化 <see cref="LanguageInfo"/> 类的新实例。</para>
    /// Initializes a new instance of the <see cref="LanguageInfo"/> class with the specified culture name, native name, English name, and built-in flag.
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
    /// <param name="isBuiltIn">
    /// <para>是否为内置语言。</para>
    /// Whether the language is built-in.
    /// </param>
    public LanguageInfo(string cultureName, string nativeName, string englishName, bool isBuiltIn = false)
    {
        CultureName = cultureName;
        NativeName = nativeName;
        EnglishName = englishName;
        IsBuiltIn = isBuiltIn;
    }

    /// <summary>
    /// <para>创建表示"跟随系统语言"的 <see cref="LanguageInfo"/> 实例。</para>
    /// Creates a <see cref="LanguageInfo"/> instance representing "follow system language".
    /// </summary>
    /// <returns>
    /// <para>表示跟随系统语言的 <see cref="LanguageInfo"/> 实例。</para>
    /// A <see cref="LanguageInfo"/> instance representing follow system language.
    /// </returns>
    public static LanguageInfo SystemLanguage { get; } = new()
    {
        CultureName = string.Empty,
        NativeName = "Auto",
        EnglishName = "Auto Detect",
        IsBuiltIn = true,
        IsSystemLanguage = true
    };

    /// <summary>
    /// <para>判断当前实例是否与另一个 <see cref="LanguageInfo"/> 相等。</para>
    /// Determines whether the current instance is equal to another <see cref="LanguageInfo"/>.
    /// </summary>
    /// <param name="other">
    /// <para>要比较的另一个实例。</para>
    /// The other instance to compare.
    /// </param>
    /// <returns>
    /// <para>如果相等则为 true；否则为 false。</para>
    /// True if equal; otherwise, false.
    /// </returns>
    public bool Equals(LanguageInfo? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return IsSystemLanguage == other.IsSystemLanguage && CultureName == other.CultureName;
    }

    /// <summary>
    /// <para>判断当前实例是否与指定对象相等。</para>
    /// Determines whether the current instance is equal to the specified object.
    /// </summary>
    public override bool Equals(object? obj) => Equals(obj as LanguageInfo);

    /// <summary>
    /// <para>获取哈希代码。</para>
    /// Gets the hash code.
    /// </summary>
    public override int GetHashCode() => HashCode.Combine(IsSystemLanguage, CultureName);

    /// <summary>
    /// <para>返回表示当前语言的字符串。</para>
    /// Returns a string representing the current language.
    /// </summary>
    public override string ToString() => IsSystemLanguage ? "System" : CultureName;

    /// <summary>
    /// <para>判断两个 <see cref="LanguageInfo"/> 实例是否相等。</para>
    /// Determines whether two <see cref="LanguageInfo"/> instances are equal.
    /// </summary>
    public static bool operator ==(LanguageInfo? left, LanguageInfo? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }

    /// <summary>
    /// <para>判断两个 <see cref="LanguageInfo"/> 实例是否不相等。</para>
    /// Determines whether two <see cref="LanguageInfo"/> instances are not equal.
    /// </summary>
    public static bool operator !=(LanguageInfo? left, LanguageInfo? right) => !(left == right);
}
