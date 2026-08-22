using UotanStudio.Models;

namespace UotanStudio.Messages;

/// <summary>
/// <para>主题变更消息，当用户切换主题时发送。</para>
/// Theme changed message, sent when the user switches the theme.
/// </summary>
/// <remarks>
/// <para>创建主题变更消息。</para>
/// Creates a theme changed message.
/// </remarks>
/// <param name="themeMode">
/// <para>新的主题模式。</para>
/// The new theme mode.
/// </param>
public class ThemeChangedMessage(ThemeMode themeMode)
{
    /// <summary>
    /// <para>获取新的主题模式。</para>
    /// Gets the new theme mode.
    /// </summary>
    public ThemeMode ThemeMode { get; } = themeMode;
}

/// <summary>
/// <para>语言变更消息，当用户切换语言时发送。</para>
/// Language changed message, sent when the user switches the language.
/// </summary>
/// <remarks>
/// <para>创建语言变更消息。</para>
/// Creates a language changed message.
/// </remarks>
/// <param name="cultureName">
/// <para>新的语言文化名称（如 "en-US"、"zh-CN"）。</para>
/// The new language culture name (e.g., "en-US", "zh-CN").
/// </param>
public class LanguageChangedMessage(string cultureName)
{
    /// <summary>
    /// <para>获取新的语言文化名称。</para>
    /// Gets the new language culture name.
    /// </summary>
    public string CultureName { get; } = cultureName;
}

/// <summary>
/// <para>设置保存失败消息，当设置持久化失败时发送。</para>
/// Settings save failed message, sent when settings persistence fails.
/// </summary>
/// <remarks>
/// <para>创建设置保存失败消息。</para>
/// Creates a settings save failed message.
/// </remarks>
/// <param name="errorMessage">
/// <para>错误信息描述。</para>
/// The error message description.
/// </param>
public class SettingsSaveFailedMessage(string errorMessage)
{
    /// <summary>
    /// <para>获取错误信息。</para>
    /// Gets the error message.
    /// </summary>
    public string ErrorMessage { get; } = errorMessage;
}

/// <summary>
/// <para>语言状态变更消息，当应用程序语言环境发生切换时发送，包含新旧语言信息。</para>
/// Language state changed message, sent when the application language environment switches, containing both old and new language information.
/// </summary>
/// <remarks>
/// <para>初始化 <see cref="LanguageStateChangedMessage"/> 类的新实例。</para>
/// Initializes a new instance of the <see cref="LanguageStateChangedMessage"/> class.
/// </remarks>
/// <param name="cultureName">
/// <para>新的语言文化名称。</para>
/// The new language culture name.
/// </param>
/// <param name="previousCultureName">
/// <para>切换前的语言文化名称。</para>
/// The previous language culture name.
/// </param>
public class LanguageStateChangedMessage(string cultureName, string previousCultureName)
{
    /// <summary>
    /// <para>获取新的语言文化名称。</para>
    /// Gets the new language culture name.
    /// </summary>
    public string CultureName { get; } = cultureName;

    /// <summary>
    /// <para>获取切换前的语言文化名称。</para>
    /// Gets the previous language culture name before the switch.
    /// </summary>
    public string PreviousCultureName { get; } = previousCultureName;
}

/// <summary>
/// <para>语言包更新消息，当已注册的语言包被更高版本替换时发送。</para>
/// Language pack updated message, sent when a registered language pack is replaced by a higher version.
/// </summary>
/// <remarks>
/// <para>初始化 <see cref="LanguagePackUpdatedMessage"/> 类的新实例。</para>
/// Initializes a new instance of the <see cref="LanguagePackUpdatedMessage"/> class.
/// </remarks>
/// <param name="cultureName">
/// <para>被更新的语言包区域性名称。</para>
/// The culture name of the updated language pack.
/// </param>
/// <param name="newVersion">
/// <para>新版本号字符串。</para>
/// The new version string.
/// </param>
public class LanguagePackUpdatedMessage(string cultureName, string newVersion)
{
    /// <summary>
    /// <para>获取被更新的语言包区域性名称。</para>
    /// Gets the culture name of the updated language pack.
    /// </summary>
    public string CultureName { get; } = cultureName;

    /// <summary>
    /// <para>获取新版本号。</para>
    /// Gets the new version number.
    /// </summary>
    public string NewVersion { get; } = newVersion;
}

/// <summary>
/// <para>语言包注册拒绝消息，当语言包因版本不兼容等原因被拒绝注册时发送。</para>
/// Language pack rejected message, sent when a language pack is rejected from registration due to version incompatibility or other reasons.
/// </summary>
/// <remarks>
/// <para>初始化 <see cref="LanguagePackRejectedMessage"/> 类的新实例。</para>
/// Initializes a new instance of the <see cref="LanguagePackRejectedMessage"/> class.
/// </remarks>
/// <param name="cultureName">
/// <para>被拒绝的语言包区域性名称。</para>
/// The culture name of the rejected language pack.
/// </param>
/// <param name="reason">
/// <para>拒绝原因描述。</para>
/// The rejection reason description.
/// </param>
public class LanguagePackRejectedMessage(string cultureName, string reason)
{
    /// <summary>
    /// <para>获取被拒绝的语言包区域性名称。</para>
    /// Gets the culture name of the rejected language pack.
    /// </summary>
    public string CultureName { get; } = cultureName;

    /// <summary>
    /// <para>获取拒绝原因。</para>
    /// Gets the rejection reason.
    /// </summary>
    public string Reason { get; } = reason;
}
