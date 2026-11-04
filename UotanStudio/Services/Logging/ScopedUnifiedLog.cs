namespace UotanStudio.Services.Logging;

/// <summary>
/// Stamps every entry written through it with one correlation scope while forwarding the rest of the contract
/// unchanged.
/// <para>给通过它写入的每条条目附上同一个关联作用域，其余契约原样转发。</para>
/// </summary>
/// <remarks>
/// The interpolated and format overloads are not re-implemented: the interface defaults format the text and call
/// <see cref="Write(LogLevel, string, string)"/>, which is the only member that adds the scope.
/// <para>插值与格式化重载不重新实现：接口默认实现负责格式化文本并调用
/// <see cref="Write(LogLevel, string, string)"/>，作用域只在这一个成员上补充。</para>
/// </remarks>
internal sealed class ScopedUnifiedLog(IUnifiedLog inner, string scope) : IUnifiedLog
{
    /// <summary>
    /// Forwards the level check to the wrapped target.
    /// <para>把级别判断转发给被包装的目标。</para>
    /// </summary>
    /// <param name="level">The level the entry would be written at. <para>条目将要写入的级别。</para></param>
    /// <returns><see langword="true"/> when the wrapped target records the level.
    /// <para>被包装的目标会记录该级别时为 <see langword="true"/>。</para></returns>
    public bool IsEnabled(LogLevel level) => inner.IsEnabled(level);

    /// <summary>
    /// Writes one entry with the bound scope attached.
    /// <para>写入一条附带绑定作用域的条目。</para>
    /// </summary>
    /// <param name="level">The entry level. <para>条目级别。</para></param>
    /// <param name="source">The log source name. <para>日志来源名称。</para></param>
    /// <param name="message">The log text. <para>日志文本。</para></param>
    public void Write(LogLevel level, string source, string message)
        => inner.Write(level, source, message, scope, null);

    /// <summary>
    /// Writes one formatted entry with the bound scope attached.
    /// <para>写入一条附带绑定作用域的格式化条目。</para>
    /// </summary>
    /// <param name="level">The entry level. <para>条目级别。</para></param>
    /// <param name="source">The log source name. <para>日志来源名称。</para></param>
    /// <param name="format">The composite format string. <para>复合格式字符串。</para></param>
    /// <param name="args">The format arguments. <para>格式化使用的参数数组。</para></param>
    public void Write(LogLevel level, string source, string format, params object[] args)
        => inner.Write(level, source, LogService.FormatMessage(format, args), scope, null);

    /// <summary>
    /// Forwards the minimum level to the wrapped target, so a scope never hides the shared switch.
    /// <para>把最低级别转发给被包装的目标，使作用域不会屏蔽共享开关。</para>
    /// </summary>
    /// <param name="level">The new minimum level. <para>新的最低级别。</para></param>
    public void SetMinimumLevel(LogLevel level) => inner.SetMinimumLevel(level);
}