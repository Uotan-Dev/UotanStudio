namespace UotanStudio.Firmware.BoundedContexts.Compression;

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

    private static readonly Dictionary<CompressionFormat, CompressionFormatInfo> ByFormat =
        AllFormats.ToDictionary(f => f.Format);

    private static readonly Dictionary<string, CompressionFormat> ByName =
        BuildNameIndex();



    private static Dictionary<string, CompressionFormat> BuildNameIndex()
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
        return index;
    }

    public static IReadOnlyList<CompressionFormatInfo> All => AllFormats;

    /// <summary>
    /// Gets the metadata of the specified format; returns null for unknown formats.
    /// <para>获取指定格式的元数据；未知格式返回 null。</para>
    /// </summary>
    public static CompressionFormatInfo? GetInfo(CompressionFormat format) => ByFormat.TryGetValue(format, out var info) ? info : null;

    /// <summary>
    /// Determines whether the format is supported.
    /// <para>判断格式是否受支持。</para>
    /// </summary>
    public static bool IsSupported(CompressionFormat format) => ByFormat.ContainsKey(format);

    /// <summary>
    /// Gets the compression format from the file name extension.
    /// <para>根据文件名扩展名获取压缩格式。</para>
    /// </summary>
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
    public static string ToExtension(CompressionFormat format)
    {
        var info = GetInfo(format);
        return info is { Extensions.Length: > 0 } ? info.Extensions[0] : "";
    }

    /// <summary>
    /// Gets the display name of the compression format.
    /// <para>获取压缩格式的显示名称。</para>
    /// </summary>
    public static string GetDisplayName(CompressionFormat format) => GetInfo(format)?.Name ?? "none";

    /// <summary>
    /// Parses a compression format by name or alias (case-insensitive).
    /// <para>按名称或别名解析压缩格式（不区分大小写）。</para>
    /// </summary>
    public static CompressionFormat Parse(string name) => !TryParse(name, out var format) ? throw new ArgumentException($"Unknown compression format: {name}", nameof(name)) : format;

    /// <summary>
    /// Tries to parse a compression format by name or alias (case-insensitive).
    /// <para>尝试按名称或别名解析压缩格式（不区分大小写）。</para>
    /// </summary>
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