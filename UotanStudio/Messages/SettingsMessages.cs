using UotanStudio.Models;

namespace UotanStudio.Messages;

/// <summary>
/// Theme changed message, sent when the user switches the theme.
/// <para>主题变更消息，当用户切换主题时发送。</para>
/// </summary>
public class ThemeChangedMessage
{
    /// <summary>
    /// Gets the new theme mode.
    /// <para>获取新的主题模式。</para>
    /// </summary>
    /// <value>The theme mode carried by this message.<para>本消息携带的主题模式。</para></value>
    public ThemeMode ThemeMode { get; }

    /// <summary>
    /// Creates a theme changed message.
    /// <para>创建主题变更消息。</para>
    /// </summary>
    /// <param name="themeMode">
    /// The new theme mode.
    /// <para>新的主题模式。</para>
    /// </param>
    public ThemeChangedMessage(ThemeMode themeMode)
    {
        ThemeMode = themeMode;
    }
}

/// <summary>
/// Settings save failed message, sent when settings persistence fails.
/// <para>设置保存失败消息，当设置持久化失败时发送。</para>
/// </summary>
public class SettingsSaveFailedMessage
{
    /// <summary>
    /// Gets the error message.
    /// <para>获取错误信息。</para>
    /// </summary>
    /// <value>The description of the persistence failure.<para>持久化失败的描述信息。</para></value>
    public string ErrorMessage { get; }

    /// <summary>
    /// Creates a settings save failed message.
    /// <para>创建设置保存失败消息。</para>
    /// </summary>
    /// <param name="errorMessage">
    /// The error message description.
    /// <para>错误信息描述。</para>
    /// </param>
    public SettingsSaveFailedMessage(string errorMessage)
    {
        ErrorMessage = errorMessage;
    }
}

/// <summary>
/// Language state changed message, sent when the application language environment switches, containing both old and new language information.
/// <para>语言状态变更消息，当应用程序语言环境发生切换时发送，包含新旧语言信息。</para>
/// </summary>
public class LanguageStateChangedMessage
{
    /// <summary>
    /// Gets the new language culture name.
    /// <para>获取新的语言文化名称。</para>
    /// </summary>
    /// <value>The culture name after the switch.<para>切换完成后的文化名称。</para></value>
    public string CultureName { get; }

    /// <summary>
    /// Gets the previous language culture name before the switch.
    /// <para>获取切换前的语言文化名称。</para>
    /// </summary>
    /// <value>The culture name held before the switch.<para>切换之前持有的文化名称。</para></value>
    public string PreviousCultureName { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguageStateChangedMessage"/> class.
    /// <para>初始化 <see cref="LanguageStateChangedMessage"/> 类的新实例。</para>
    /// </summary>
    /// <param name="cultureName">
    /// The new language culture name.
    /// <para>新的语言文化名称。</para>
    /// </param>
    /// <param name="previousCultureName">
    /// The previous language culture name.
    /// <para>切换前的语言文化名称。</para>
    /// </param>
    public LanguageStateChangedMessage(string cultureName, string previousCultureName)
    {
        CultureName = cultureName;
        PreviousCultureName = previousCultureName;
    }
}