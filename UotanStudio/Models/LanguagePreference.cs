namespace UotanStudio.Models;

/// <summary>
/// <para>已过时：请使用 <see cref="LanguageInfo"/> 替代。此枚举仅为向后兼容迁移而保留。</para>
/// Obsolete: Use <see cref="LanguageInfo"/> instead. This enum is retained only for backward-compatible migration.
/// </summary>
[System.Obsolete("Use LanguageInfo instead. This enum is retained only for backward-compatible migration.")]
public enum LanguagePreference
{
    /// <summary>
    /// <para>跟随系统语言。</para>
    /// Follow the system language.
    /// </summary>
    System = 0,

    /// <summary>
    /// <para>英语。</para>
    /// English.
    /// </summary>
    English = 1,

    /// <summary>
    /// <para>简体中文。</para>
    /// Simplified Chinese.
    /// </summary>
    Chinese = 2
}