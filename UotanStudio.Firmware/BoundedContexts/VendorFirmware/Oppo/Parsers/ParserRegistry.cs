using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Parsers;

public static class ParserRegistry
{
    private static readonly Dictionary<OppFormat, IOppFormatParser> Parsers = [];

    /// <summary>
    /// Registers a parser for the specified format.
    /// <para>注册指定格式的解析器。</para>
    /// </summary>
    public static void Register(IOppFormatParser parser) => Parsers[parser.SupportedFormat] = parser;

    /// <summary>
    /// Gets the parser for the specified format.
    /// <para>获取指定格式的解析器。</para>
    /// </summary>
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
    public static bool IsRegistered(OppFormat format) => Parsers.ContainsKey(format);
}