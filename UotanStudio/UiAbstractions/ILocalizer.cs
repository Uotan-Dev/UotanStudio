namespace UotanStudio.UiAbstractions;

/// <summary>
/// Localized string lookup consumed by view models and services that must not depend on the
/// concrete language implementation.
/// <para>供视图模型与服务消费的本地化字符串查询契约，使其不依赖具体语言实现。</para>
/// </summary>
public interface ILocalizer
{
    /// <summary>
    /// Gets the localized value for a resource key.
    /// <para>获取资源键对应的本地化值。</para>
    /// </summary>
    /// <param name="key">The resource key to look up. <para>要查找的资源键。</para></param>
    /// <returns>The localized value, or <paramref name="key"/> itself when the key is unknown.
    /// <para>本地化值；键未知时返回 <paramref name="key"/> 本身。</para></returns>
    string GetString(string key);

    /// <summary>
    /// Gets the localized value for a resource key and formats it with the supplied arguments.
    /// <para>获取资源键对应的本地化值，并用给定参数格式化。</para>
    /// </summary>
    /// <param name="key">The resource key to look up. <para>要查找的资源键。</para></param>
    /// <param name="args">The arguments substituted into the localized format string.
    /// <para>代入本地化格式字符串的参数。</para></param>
    /// <returns>The formatted localized value, or the unformatted value when formatting fails.
    /// <para>格式化后的本地化值；格式化失败时返回未格式化的值。</para></returns>
    string GetString(string key, params object[] args);
}