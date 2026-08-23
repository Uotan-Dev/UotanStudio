using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Parsers;

/// <summary>
/// <para>管理各固件格式的解析器注册表。</para>
/// Manages the parser registry for each firmware format.
/// </summary>
public static class ParserRegistry
{
    private static readonly Dictionary<OppFormat, IOppFormatParser> Parsers = [];

    /// <summary>
    /// <para>注册指定格式的解析器。</para>
    /// Registers a parser for the specified format.
    /// </summary>
    /// <param name="parser"><para>要注册的解析器实例。</para>The parser instance to register.</param>
    public static void Register(IOppFormatParser parser) => Parsers[parser.SupportedFormat] = parser;

    /// <summary>
    /// <para>获取指定格式的解析器。</para>
    /// Gets the parser for the specified format.
    /// </summary>
    /// <param name="format"><para>固件格式。</para>The firmware format.</param>
    /// <returns><para>注册的解析器实例。</para>The registered parser instance.</returns>
    /// <exception cref="UnsupportedOppVersionException">
    /// <para>指定格式没有注册的解析器。</para>
    /// No parser is registered for the specified format.
    /// </exception>
    public static IOppFormatParser GetParser(OppFormat format)
    {
        return Parsers.TryGetValue(format, out var parser)
            ? parser
            : throw new UnsupportedOppVersionException($"No parser registered for format '{format}'.");
    }

    /// <summary>
    /// <para>查询指定格式是否有注册的解析器。</para>
    /// Queries whether a parser is registered for the specified format.
    /// </summary>
    /// <param name="format"><para>固件格式。</para>The firmware format.</param>
    /// <returns><para>若已注册返回 true；否则返回 false。</para>True if registered; otherwise, false.</returns>
    public static bool IsRegistered(OppFormat format) => Parsers.ContainsKey(format);
}