using System.Collections.Frozen;

namespace UotanStudio.Firmware.Compression;

/// <summary>
/// Registry of the compression formats supported by this assembly, with name, alias and extension lookup.
/// <para>受本程序集支持的压缩格式注册表，提供名称、别名与扩展名查找。</para>
/// </summary>
public static class CompressionFormats
{
    private static readonly CompressionFormatInfo[] AllFormats =
    [
        new(CompressionFormat.Gzip, "gzip", ["gz", "gzip"], [".gz", ".gzip"],
            [0x1F, 0x8B], canCompress: true, canDecompress: true, isStreaming: true),
        new(CompressionFormat.Zlib, "zlib", ["zlib"], [".zlib", ".zz"],
            null, canCompress: true, canDecompress: true, isStreaming: true),
        new(CompressionFormat.Deflate, "deflate", ["deflate", "def"], [".deflate", ".defl"],
            null, canCompress: true, canDecompress: true, isStreaming: true),
        new(CompressionFormat.Brotli, "brotli", ["brotli", "br"], [".br", ".brotli"],
            null, canCompress: true, canDecompress: true, isStreaming: true),
        new(CompressionFormat.Lz4, "lz4", ["lz4"], [".lz4"],
            [0x04, 0x22, 0x4D, 0x18], canCompress: true, canDecompress: true, isStreaming: true),
        new(CompressionFormat.Lz4Legacy, "lz4_legacy", ["lz4_legacy", "lz4-legacy"], [".lz4_legacy"],
            [0x02, 0x21, 0x4C, 0x18], canCompress: true, canDecompress: true, isStreaming: false),
        new(CompressionFormat.Lzma, "lzma", ["lzma"], [".lzma"],
            null, canCompress: true, canDecompress: true, isStreaming: false),
        new(CompressionFormat.Xz, "xz", ["xz"], [".xz"],
            [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00], canCompress: true, canDecompress: true, isStreaming: false),
        new(CompressionFormat.Bzip2, "bzip2", ["bzip2", "bz2"], [".bz2", ".bzip2"],
            [0x42, 0x5A, 0x68], canCompress: true, canDecompress: true, isStreaming: true),
        new(CompressionFormat.Zstd, "zstd", ["zstd", "zst"], [".zst", ".zstd"],
            [0x28, 0xB5, 0x2F, 0xFD], canCompress: true, canDecompress: true, isStreaming: false),
        new(CompressionFormat.Lzop, "lzop", ["lzop", "lzo"], [".lzop", ".lzo"],
            [0x89, 0x4C, 0x5A, 0x4F, 0x00, 0x0D, 0x0A, 0x1A, 0x0A], canCompress: true, canDecompress: true, isStreaming: false),
        new(CompressionFormat.Zopfli, "zopfli", ["zopfli"], [".gz"],
            null, canCompress: false, canDecompress: true, isStreaming: false),
    ];

    private static readonly FrozenDictionary<CompressionFormat, CompressionFormatInfo> ByFormat =
        AllFormats.ToFrozenDictionary(f => f.Format);

    private static readonly FrozenDictionary<string, CompressionFormat> ByName =
        BuildNameIndex();



    private static FrozenDictionary<string, CompressionFormat> BuildNameIndex()
    {
        var index = new Dictionary<string, CompressionFormat>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in AllFormats)
        {
            index[info.Name] = info.Format;
            foreach (var alias in info.Aliases)
            {
                index[alias] = info.Format;
            }
        }
        return index.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every registered format metadata entry, in registration order.
    /// <para>按注册顺序排列的全部格式元数据条目。</para>
    /// </summary>
    /// <value>
    /// A read-only list covering each supported <see cref="CompressionFormat"/> except <see cref="CompressionFormat.None"/>.
    /// <para>只读列表，覆盖除 <see cref="CompressionFormat.None"/> 之外的每个受支持格式。</para>
    /// </value>
    public static IReadOnlyList<CompressionFormatInfo> All => AllFormats;

    /// <summary>
    /// Gets the metadata of the specified format; returns null for unknown formats.
    /// <para>获取指定格式的元数据；未知格式返回 null。</para>
    /// </summary>
    /// <param name="format">The format to look up. <para>要查找的格式。</para></param>
    /// <returns>The registered metadata, or <see langword="null"/> when the format has no entry.
    /// <para>已注册的元数据；格式没有注册条目时返回 <see langword="null"/>。</para></returns>
    public static CompressionFormatInfo? GetInfo(CompressionFormat format) => ByFormat.TryGetValue(format, out var info) ? info : null;

    /// <summary>
    /// Determines whether the format is supported.
    /// <para>判断格式是否受支持。</para>
    /// </summary>
    /// <param name="format">The format to test. <para>要检测的格式。</para></param>
    /// <returns><see langword="true"/> when the format has a registered entry.
    /// <para>格式存在注册条目时返回 <see langword="true"/>。</para></returns>
    public static bool IsSupported(CompressionFormat format) => ByFormat.ContainsKey(format);

    /// <summary>
    /// Gets the compression format from the file name extension.
    /// <para>根据文件名扩展名获取压缩格式。</para>
    /// </summary>
    /// <param name="filename">File name or path whose extension is inspected. <para>需要检查扩展名的文件名或路径。</para></param>
    /// <returns>The matched format, or <see cref="CompressionFormat.None"/> for an empty name, a missing
    /// extension, or an extension that matches no registered format. Comparison is case-insensitive.
    /// <para>匹配到的格式；名称为空、缺少扩展名或扩展名未匹配任何注册格式时返回 <see cref="CompressionFormat.None"/>。比较不区分大小写。</para></returns>
    public static CompressionFormat FromExtension(string filename)
    {
        if (string.IsNullOrEmpty(filename))
        {
            return CompressionFormat.None;
        }

        var ext = Path.GetExtension(filename);
        if (string.IsNullOrEmpty(ext))
        {
            return CompressionFormat.None;
        }

        foreach (var info in AllFormats)
        {
            foreach (var candidate in info.Extensions)
            {
                if (string.Equals(ext, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return info.Format;
                }
            }
        }

        return CompressionFormat.None;
    }

    /// <summary>
    /// Gets the preferred extension (with leading dot) of the format; empty string when none.
    /// <para>获取压缩格式的首选扩展名（含点号）；无对应扩展名时返回空字符串。</para>
    /// </summary>
    /// <param name="format">The format to convert. <para>要转换的格式。</para></param>
    /// <returns>The first registered extension of the format, or an empty string when the format has
    /// no entry or no extensions.
    /// <para>该格式注册列表中的第一个扩展名；格式无注册条目或无扩展名时返回空字符串。</para></returns>
    public static string ToExtension(CompressionFormat format)
    {
        var info = GetInfo(format);
        return info is { Extensions.Length: > 0 } ? info.Extensions[0] : "";
    }

    /// <summary>
    /// Gets the display name of the compression format.
    /// <para>获取压缩格式的显示名称。</para>
    /// </summary>
    /// <param name="format">The format to name. <para>要获取名称的格式。</para></param>
    /// <returns>The canonical lowercase name, or <c>"none"</c> for unregistered formats.
    /// <para>规范的小写名称；未注册的格式返回 <c>"none"</c>。</para></returns>
    public static string GetDisplayName(CompressionFormat format) => GetInfo(format)?.Name ?? "none";

    /// <summary>
    /// Parses a compression format by name or alias (case-insensitive).
    /// <para>按名称或别名解析压缩格式（不区分大小写）。</para>
    /// </summary>
    /// <param name="name">A registered format name or alias, optionally surrounded by whitespace.
    /// <para>已注册的格式名称或别名，允许首尾包含空白字符。</para></param>
    /// <returns>The parsed format. <para>解析得到的格式。</para></returns>
    /// <exception cref="ArgumentException">The name is empty or matches no registered name or alias.
    /// <para>名称为空或不匹配任何已注册名称或别名。</para></exception>
    public static CompressionFormat Parse(string name) => !TryParse(name, out var format) ? throw new ArgumentException($"Unknown compression format: {name}", nameof(name)) : format;

    /// <summary>
    /// Tries to parse a compression format by name or alias (case-insensitive).
    /// <para>尝试按名称或别名解析压缩格式（不区分大小写）。</para>
    /// </summary>
    /// <param name="name">A registered format name or alias, or <see langword="null"/>.
    /// <para>已注册的格式名称或别名，或为 <see langword="null"/>。</para></param>
    /// <param name="format">The parsed format on success; <see cref="CompressionFormat.None"/> otherwise.
    /// <para>成功时为解析得到的格式；否则为 <see cref="CompressionFormat.None"/>。</para></param>
    /// <returns><see langword="true"/> when the trimmed name matches a registered name or alias.
    /// <para>去除首尾空白后的名称匹配到已注册名称或别名时返回 <see langword="true"/>。</para></returns>
    public static bool TryParse(string? name, out CompressionFormat format)
    {
        if (name is { Length: > 0 })
        {
            var trimmed = name.Trim();
            if (trimmed.Length > 0 && ByName.TryGetValue(trimmed, out format))
            {
                return true;
            }
        }

        format = CompressionFormat.None;
        return false;
    }
}