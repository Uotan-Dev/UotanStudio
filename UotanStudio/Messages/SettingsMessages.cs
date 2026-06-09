using UotanStudio.Models;

namespace UotanStudio.Messages;

/// <summary>
/// <para>主题变更消息，当用户切换主题时发送。</para>
/// Theme changed message, sent when the user switches the theme.
/// </summary>
public class ThemeChangedMessage
{
    /// <summary>
    /// <para>获取新的主题模式。</para>
    /// Gets the new theme mode.
    /// </summary>
    public ThemeMode ThemeMode { get; }

    /// <summary>
    /// <para>创建主题变更消息。</para>
    /// Creates a theme changed message.
    /// </summary>
    /// <param name="themeMode">
    /// <para>新的主题模式。</para>
    /// The new theme mode.
    /// </param>
    public ThemeChangedMessage(ThemeMode themeMode)
    {
        ThemeMode = themeMode;
    }
}

/// <summary>
/// <para>语言变更消息，当用户切换语言时发送。</para>
/// Language changed message, sent when the user switches the language.
/// </summary>
public class LanguageChangedMessage
{
    /// <summary>
    /// <para>获取新的语言文化名称。</para>
    /// Gets the new language culture name.
    /// </summary>
    public string CultureName { get; }

    /// <summary>
    /// <para>创建语言变更消息。</para>
    /// Creates a language changed message.
    /// </summary>
    /// <param name="cultureName">
    /// <para>新的语言文化名称（如 "en-US"、"zh-CN"）。</para>
    /// The new language culture name (e.g., "en-US", "zh-CN").
    /// </param>
    public LanguageChangedMessage(string cultureName)
    {
        CultureName = cultureName;
    }
}

/// <summary>
/// <para>设置保存失败消息，当设置持久化失败时发送。</para>
/// Settings save failed message, sent when settings persistence fails.
/// </summary>
public class SettingsSaveFailedMessage
{
    /// <summary>
    /// <para>获取错误信息。</para>
    /// Gets the error message.
    /// </summary>
    public string ErrorMessage { get; }

    /// <summary>
    /// <para>创建设置保存失败消息。</para>
    /// Creates a settings save failed message.
    /// </summary>
    /// <param name="errorMessage">
    /// <para>错误信息描述。</para>
    /// The error message description.
    /// </param>
    public SettingsSaveFailedMessage(string errorMessage)
    {
        ErrorMessage = errorMessage;
    }
}
