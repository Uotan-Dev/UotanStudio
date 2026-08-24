namespace UotanStudio.Firmware.Compress;

/// <summary>
/// 压缩格式注册表与工具类：格式枚举、魔数检测、扩展名映射、名称解析。
/// <para>Compression format registry and utilities: format enumeration, magic-byte detection,
/// extension mapping and name parsing.</para>
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
            [0x89, 0x4C, 0x5A, 0x4F, 0x00, 0x0D, 0x0A, 0x1A, 0x0A], canCompress: false, canDecompress: true, isStreaming: false),
        new(CompressionFormat.Zopfli, "zopfli", ["zopfli"], [".gz"],
            null, canCompress: false, canDecompress: true, isStreaming: false),
    ];

    private static readonly Dictionary<CompressionFormat, CompressionFormatInfo> ByFormat =
        AllFormats.ToDictionary(f => f.Format);

    private static readonly Dictionary<string, CompressionFormat> ByName =
        BuildNameIndex();

    // Cached magic byte arrays to avoid per-call allocations on the detection hot path.
    private static readonly byte[] MagicGzip = [0x1F, 0x8B];
    private static readonly byte[] MagicZstd = [0x28, 0xB5, 0x2F, 0xFD];
    private static readonly byte[] MagicBzip2 = [0x42, 0x5A, 0x68];
    private static readonly byte[] MagicXz = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];
    private static readonly byte[] MagicLzop = [0x89, 0x4C, 0x5A, 0x4F, 0x00, 0x0D, 0x0A, 0x1A, 0x0A];

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

    /// <summary>
    /// 所有受支持格式的元数据列表（不含 <see cref="CompressionFormat.None"/>）。
    /// <para>Metadata list of all supported formats (excluding <see cref="CompressionFormat.None"/>).</para>
    /// </summary>
    public static IReadOnlyList<CompressionFormatInfo> All => AllFormats;

    /// <summary>
    /// 获取指定格式的元数据；未知格式返回 null。
    /// <para>Gets the metadata of the specified format; returns null for unknown formats.</para>
    /// </summary>
    public static CompressionFormatInfo? GetInfo(CompressionFormat format) => ByFormat.TryGetValue(format, out var info) ? info : null;

    /// <summary>
    /// 判断格式是否受支持。
    /// <para>Determines whether the format is supported.</para>
    /// </summary>
    public static bool IsSupported(CompressionFormat format) => ByFormat.ContainsKey(format);

    /// <summary>
    /// 根据文件名扩展名获取压缩格式。
    /// <para>Gets the compression format from the file name extension.</para>
    /// </summary>
    /// <param name="filename">文件名（含扩展名）。<para>The file name including extension.</para></param>
    /// <returns>对应的压缩格式；无法识别时返回 <see cref="CompressionFormat.None"/>。<para>The matching format; <see cref="CompressionFormat.None"/> when unrecognized.</para></returns>
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
    /// 获取压缩格式的首选扩展名（含点号）；无对应扩展名时返回空字符串。
    /// <para>Gets the preferred extension (with leading dot) of the format; empty string when none.</para>
    /// </summary>
    public static string ToExtension(CompressionFormat format)
    {
        var info = GetInfo(format);
        return info is { Extensions.Length: > 0 } ? info.Extensions[0] : "";
    }

    /// <summary>
    /// 获取压缩格式的显示名称。
    /// <para>Gets the display name of the compression format.</para>
    /// </summary>
    public static string GetDisplayName(CompressionFormat format) => GetInfo(format)?.Name ?? "none";

    /// <summary>
    /// 按名称或别名解析压缩格式（不区分大小写）。
    /// <para>Parses a compression format by name or alias (case-insensitive).</para>
    /// </summary>
    /// <param name="name">格式名称或别名（如 "gz"、"lz4_legacy"）。<para>Format name or alias (e.g. "gz", "lz4_legacy").</para></param>
    /// <returns>解析结果；失败时抛出 <see cref="ArgumentException"/>。<para>The parsed format; throws <see cref="ArgumentException"/> on failure.</para></returns>
    public static CompressionFormat Parse(string name) => !TryParse(name, out var format) ? throw new ArgumentException($"Unknown compression format: {name}", nameof(name)) : format;

    /// <summary>
    /// 尝试按名称或别名解析压缩格式（不区分大小写）。
    /// <para>Tries to parse a compression format by name or alias (case-insensitive).</para>
    /// </summary>
    /// <param name="name">格式名称或别名。<para>Format name or alias.</para></param>
    /// <param name="format">解析结果；失败时为 <see cref="CompressionFormat.None"/>。<para>The parsed format; <see cref="CompressionFormat.None"/> on failure.</para></param>
    /// <returns>解析成功返回 true；否则返回 false。<para>true when parsed successfully; otherwise false.</para></returns>
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

    public static CompressionFormat Detect(byte[] data) => data == null ? throw new ArgumentNullException(nameof(data)) : Detect(data.AsSpan());

    public static CompressionFormat Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return CompressionFormat.None;
        }

        // 固定魔数优先（最具体、最可靠）。
        // Fixed magics first (most specific and reliable).
        if (StartsWith(data, MagicGzip))
        {
            return CompressionFormat.Gzip;
        }

        if (StartsWith(data, MagicZstd))
        {
            return CompressionFormat.Zstd;
        }

        if (StartsWith(data, MagicBzip2))
        {
            return CompressionFormat.Bzip2;
        }

        if (StartsWith(data, MagicXz))
        {
            return CompressionFormat.Xz;
        }

        if (StartsWith(data, MagicLzop))
        {
            return CompressionFormat.Lzop;
        }

        // LZ4 家族：标准帧 04 22 4D 18；传统 02 21 4C 18。
        // LZ4 family: standard frame 04 22 4D 18; legacy 02 21 4C 18.
        if (data.Length >= 4)
        {
            if (data[0] == 0x04 && data[1] == 0x22 && data[2] == 0x4D && data[3] == 0x18)
            {
                return CompressionFormat.Lz4;
            }

            if (data[0] == 0x02 && data[1] == 0x21 && data[2] == 0x4C && data[3] == 0x18)
            {
                return CompressionFormat.Lz4Legacy;
            }
        }

        // 启发式检测（可能误判，置于固定魔数之后）。
        // Heuristic detection (may produce false positives; placed after fixed magics).
        return IsLzmaFormat(data) ? CompressionFormat.Lzma : IsZlibFormat(data) ? CompressionFormat.Zlib : CompressionFormat.None;
    }

    /// <summary>
    /// 读取流头部检测压缩格式；若流可定位则恢复原位置。
    /// <para>Reads the stream head to detect the compression format; restores the position when the stream is seekable.</para>
    /// </summary>
    /// <param name="input">待检测的流。<para>The stream to detect.</para></param>
    /// <returns>检测到的格式；无法识别时返回 <see cref="CompressionFormat.None"/>。<para>The detected format; <see cref="CompressionFormat.None"/> when unrecognized.</para></returns>
    public static CompressionFormat Detect(Stream input)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        var buffer = new byte[16];
        var read = 0;
        long? position = input.CanSeek ? input.Position : null;

        while (read < buffer.Length)
        {
            var n = input.Read(buffer, read, buffer.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        if (position.HasValue)
        {
            input.Position = position.Value;
        }

        return Detect(buffer.AsSpan(0, read));
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, byte[] magic)
    {
        if (data.Length < magic.Length)
        {
            return false;
        }

        for (var i = 0; i < magic.Length; i++)
        {
            if (data[i] != magic[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 检测数据是否符合 LZMA 格式（启发式）。
    /// <para>Detects whether the data conforms to the LZMA format (heuristic).</para>
    /// <para>LZMA 头结构：1 字节属性字节 + 4 字节字典大小（小端）+ 8 字节未压缩大小（小端）。</para>
    /// <para>LZMA header: 1 property byte + 4-byte dictionary size (little-endian) + 8-byte uncompressed size (little-endian).</para>
    /// </summary>
    private static bool IsLzmaFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length < 13)
        {
            return false;
        }

        if (!IsValidLzmaPropertyByte(data[0]))
        {
            return false;
        }

        var dictSize = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));
        return dictSize != 0 && (dictSize & (dictSize - 1)) == 0 && dictSize is >= 4096 and <= 0x8000000;
    }

    /// <summary>
    /// 验证 LZMA 属性字节是否有效（lc + 9 * (lp + 5 * pb)，lc≤8，lp≤4，pb≤4）。
    /// <para>Validates the LZMA property byte (lc + 9 * (lp + 5 * pb), lc≤8, lp≤4, pb≤4).</para>
    /// </summary>
    private static bool IsValidLzmaPropertyByte(byte propertyByte)
    {
        if (propertyByte > 0xE0)
        {
            return false;
        }

        var pb = propertyByte / (9 * 5);
        var rem = propertyByte % (9 * 5);
        var lp = rem / 9;
        var lc = rem % 9;

        return pb <= 4 && lp <= 4 && lc <= 8;
    }

    /// <summary>
    /// 检测数据是否符合 ZLIB 格式（启发式：CM=8 且 CMF/FLG 可被 31 整除）。
    /// <para>Detects whether the data conforms to the ZLIB format (heuristic: CM=8 and (CMF*256+FLG) % 31 == 0).</para>
    /// </summary>
    private static bool IsZlibFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return false;
        }

        var cmf = data[0];
        var flg = data[1];

        return (cmf & 0x0F) == 8 && (cmf >> 4) <= 7 && ((cmf << 8) | flg) % 31 == 0;
    }
}