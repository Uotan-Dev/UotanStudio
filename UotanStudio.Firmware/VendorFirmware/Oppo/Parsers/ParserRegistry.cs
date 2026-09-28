using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.VendorFirmware.Oppo.Parsers;

/// <summary>
/// Process-wide registry mapping each <see cref="OppFormat"/> to the parser that reads it.
/// <para>进程级注册表，将每个 <see cref="OppFormat"/> 映射到读取它的解析器。</para>
/// </summary>
public static class ParserRegistry
{
    private static readonly Dictionary<OppFormat, IOppFormatParser> Parsers = [];

    /// <summary>
    /// Registers a parser for the specified format.
    /// <para>注册指定格式的解析器。</para>
    /// </summary>
    /// <param name="parser">
    /// Parser to store under its <see cref="IOppFormatParser.SupportedFormat"/>; a later registration for the same format replaces the previous one.
    /// <para>以其 <see cref="IOppFormatParser.SupportedFormat"/> 为键存储的解析器；对同一格式的后续注册会覆盖之前的注册。</para>
    /// </param>
    public static void Register(IOppFormatParser parser) => Parsers[parser.SupportedFormat] = parser;

    /// <summary>
    /// Gets the parser for the specified format.
    /// <para>获取指定格式的解析器。</para>
    /// </summary>
    /// <param name="format">
    /// Container format whose parser is requested.
    /// <para>需要获取其解析器的容器格式。</para>
    /// </param>
    /// <returns>The registered parser for <paramref name="format"/>.
    /// <para><paramref name="format"/> 已注册的解析器。</para></returns>
    /// <exception cref="UnsupportedOppVersionException">No parser is registered for <paramref name="format"/>.
    /// <para><paramref name="format"/> 没有注册任何解析器。</para></exception>
    public static IOppFormatParser GetParser(OppFormat format)
    {
        return Parsers.TryGetValue(format, out var parser)
            ? parser
            : throw new UnsupportedOppVersionException($"No parser registered for format '{format}'.");
    }

    /// <summary>
    /// Queries whether a parser is registered for the specified format.
    /// <para>查询指定格式是否有注册的解析器。</para>
    /// </summary>
    /// <param name="format">
    /// Container format to test.
    /// <para>要检测的容器格式。</para>
    /// </param>
    /// <returns><see langword="true"/> when a parser is registered for <paramref name="format"/>; otherwise <see langword="false"/>.
    /// <para><paramref name="format"/> 已注册解析器时为 <see langword="true"/>；否则为 <see langword="false"/>。</para></returns>
    public static bool IsRegistered(OppFormat format) => Parsers.ContainsKey(format);
}