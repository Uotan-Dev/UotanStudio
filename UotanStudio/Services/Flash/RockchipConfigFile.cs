using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Services.Flash;

/// <summary>
/// One row of an RKDevTool download-list configuration file (<c>config.cfg</c>).
/// <para>RKDevTool 下载列表配置文件（<c>config.cfg</c>）中的一行。</para>
/// </summary>
/// <remarks>
/// <para>The tool writes a fixed 610-byte record per row: a 2-byte marker, an 80-byte UTF-16 name field, a
/// 512-byte UTF-16 path field and a 16-byte trailing block whose first eight bytes behave like the row
/// address. Unknown trailing bytes are preserved so a parse/serialize round trip is byte-identical.</para>
/// <para>该工具为每行写入固定 610 字节记录：2 字节标记、80 字节 UTF-16 名称、512 字节 UTF-16 路径，以及
/// 16 字节尾块（前 8 字节表现为行地址）。未知尾字节按原样保留，因此解析后再序列化可逐字节还原。</para>
/// </remarks>
public sealed record RockchipConfigEntry
{
    /// <summary>Gets the row name as shown in the download list.<para>获取下载列表中显示的行名称。</para></summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the image path the row points at.<para>获取该行指向的镜像路径。</para></summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the row address taken from the trailing block.<para>获取从尾块中读出的行地址。</para></summary>
    public ulong Address { get; init; }

    /// <summary>Gets the trailing block exactly as stored, including bytes after the address.
    /// <para>获取原样保留的尾块，包括地址之后的字节。</para></summary>
    public byte[] Trailing { get; init; } = new byte[RockchipConfigFile.TrailingLength];
}

/// <summary>
/// Reads and writes the binary download-list configuration RKDevTool stores next to its executable.
/// <para>读写 RKDevTool 存放在可执行文件旁的二进制下载列表配置。</para>
/// </summary>
/// <remarks>
/// <para>The layout is a 29-byte header (<c>"CFG\0"</c>, a 16-bit header length, then a date/time-like block)
/// followed by fixed 610-byte rows. The row count is implied by the file length; the observed samples
/// (<c>config.cfg</c>, <c>rock-5b-emmc.cfg</c>) are 1249 bytes for two rows.</para>
/// <para>布局为 29 字节头部（<c>"CFG\0"</c>、16 位头部长度，随后是日期时间样式的数据块），其后是固定
/// 610 字节的行。行数由文件长度决定；观测到的样本（<c>config.cfg</c>、<c>rock-5b-emmc.cfg</c>）为两行共
/// 1249 字节。</para>
/// </remarks>
public static class RockchipConfigFile
{
    /// <summary>Size of the fixed file header.<para>固定文件头的大小。</para></summary>
    public const int HeaderLength = 29;

    /// <summary>Size of one download-list row.<para>一行下载记录的大小。</para></summary>
    public const int RecordLength = 610;

    /// <summary>Size of the UTF-16 name field inside a row.<para>行内 UTF-16 名称字段的大小。</para></summary>
    public const int NameFieldLength = 80;

    /// <summary>Size of the UTF-16 path field inside a row.<para>行内 UTF-16 路径字段的大小。</para></summary>
    public const int PathFieldLength = 512;

    /// <summary>Size of the trailing block that follows the path field.<para>路径字段之后的尾块大小。</para></summary>
    public const int TrailingLength = 16;

    /// <summary>Marker every row starts with.<para>每行起始处的标记。</para></summary>
    public const ushort RecordMarker = 0x0262;

    private const int NameFieldOffset = 2;
    private const int PathFieldOffset = NameFieldOffset + NameFieldLength;
    private const int TrailingOffset = PathFieldOffset + PathFieldLength;
    private static readonly byte[] Magic = "CFG"u8.ToArray();

    /// <summary>
    /// Parses a configuration file and returns its rows plus the verbatim header.
    /// <para>解析配置文件，返回其行与原样保留的头部。</para>
    /// </summary>
    /// <param name="bytes">Complete file contents.<para>完整文件内容。</para></param>
    /// <returns>The parsed rows and header.<para>解析出的行与头部。</para></returns>
    /// <exception cref="InvalidDataException">The magic, header length or row length does not match the format.
    /// <para>魔数、头部长度或行长与格式不符。</para></exception>
    public static (byte[] Header, IReadOnlyList<RockchipConfigEntry> Entries) Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException(
                $"A Rockchip configuration needs at least {HeaderLength} bytes but carries {bytes.Length}.");
        }

        if (!bytes[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("The Rockchip configuration has no CFG magic. 该瑞芯微配置缺少 CFG 魔数。");
        }

        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        if (headerLength != HeaderLength)
        {
            throw new InvalidDataException(
                $"The Rockchip configuration declares a {headerLength}-byte header instead of {HeaderLength}.");
        }

        var payload = bytes.Length - HeaderLength;
        if (payload % RecordLength != 0)
        {
            throw new InvalidDataException(
                $"The Rockchip configuration payload of {payload} bytes is not a multiple of {RecordLength}.");
        }

        var header = bytes[..HeaderLength].ToArray();
        var count = payload / RecordLength;
        var entries = new List<RockchipConfigEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var record = bytes.Slice(HeaderLength + (index * RecordLength), RecordLength);
            var marker = BinaryPrimitives.ReadUInt16LittleEndian(record);
            if (marker != RecordMarker)
            {
                throw new InvalidDataException(
                    $"Row {index} starts with marker 0x{marker:X4} instead of 0x{RecordMarker:X4}.");
            }

            var trailing = record.Slice(TrailingOffset, TrailingLength).ToArray();
            entries.Add(new RockchipConfigEntry
            {
                Name = DecodeField(record.Slice(NameFieldOffset, NameFieldLength)),
                Path = DecodeField(record.Slice(PathFieldOffset, PathFieldLength)),
                Address = BinaryPrimitives.ReadUInt64LittleEndian(trailing),
                Trailing = trailing
            });
        }

        return (header, entries);
    }

    /// <summary>
    /// Parses a configuration file from disk.
    /// <para>从磁盘解析配置文件。</para>
    /// </summary>
    /// <param name="path">Existing configuration file.<para>既有的配置文件。</para></param>
    /// <returns>The parsed header and rows.<para>解析出的头部与行。</para></returns>
    /// <exception cref="InvalidDataException">The file does not match the format.<para>文件与格式不符。</para></exception>
    public static (byte[] Header, IReadOnlyList<RockchipConfigEntry> Entries) Load(string path)
        => Parse(File.ReadAllBytes(path));

    /// <summary>
    /// Serializes one configuration file image with the given header and rows.
    /// <para>用给定头部与行序列化一份配置文件镜像。</para>
    /// </summary>
    /// <param name="header">Header bytes, normally taken from <see cref="Parse"/> or <see cref="CreateHeader"/>.
    /// <para>头部字节，通常来自 <see cref="Parse"/> 或 <see cref="CreateHeader"/>。</para></param>
    /// <param name="entries">Rows to store in the given order.<para>按给定顺序写入的行。</para></param>
    /// <returns>The complete file image.<para>完整的文件镜像。</para></returns>
    /// <exception cref="ArgumentException">The header is not <see cref="HeaderLength"/> bytes long.
    /// <para>头部长度不是 <see cref="HeaderLength"/> 字节。</para></exception>
    public static byte[] Serialize(ReadOnlySpan<byte> header, IReadOnlyList<RockchipConfigEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (header.Length != HeaderLength)
        {
            throw new ArgumentException(
                $"The header must be {HeaderLength} bytes but is {header.Length}.", nameof(header));
        }

        var image = new byte[HeaderLength + (entries.Count * RecordLength)];
        header.CopyTo(image);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            ArgumentNullException.ThrowIfNull(entry);
            var record = image.AsSpan(HeaderLength + (index * RecordLength), RecordLength);
            BinaryPrimitives.WriteUInt16LittleEndian(record, RecordMarker);
            EncodeField(record.Slice(NameFieldOffset, NameFieldLength), entry.Name);
            EncodeField(record.Slice(PathFieldOffset, PathFieldLength), entry.Path);
            var trailing = record.Slice(TrailingOffset, TrailingLength);
            entry.Trailing.AsSpan(0, TrailingLength).CopyTo(trailing);
            BinaryPrimitives.WriteUInt64LittleEndian(trailing, entry.Address);
        }

        return image;
    }

    /// <summary>
    /// Writes a configuration file image to disk through a same-directory temporary file.
    /// <para>经同目录临时文件把配置镜像写入磁盘。</para>
    /// </summary>
    /// <param name="path">Destination file; an existing file is replaced.<para>目标文件；既有文件会被替换。</para></param>
    /// <param name="header">Header bytes to write.<para>要写入的头部字节。</para></param>
    /// <param name="entries">Rows to write.<para>要写入的行。</para></param>
    public static void Save(string path, ReadOnlySpan<byte> header, IReadOnlyList<RockchipConfigEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var image = Serialize(header, entries);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("The configuration path has no parent directory. 配置路径没有父目录。");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, image);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Checks a bounded header sample for the configuration signature: the <c>CFG</c> magic plus the
    /// 16-bit header length the format declares.
    /// <para>检查有界头部样本是否符合配置签名：<c>CFG</c> 魔数加格式声明的 16 位头部长度。</para>
    /// </summary>
    /// <param name="header">Bounded leading bytes of a candidate file.<para>候选文件的有界起始字节。</para></param>
    /// <returns><see langword="true"/> when the sample carries a complete configuration header.
    /// <para>样本携带完整配置头部时为 <see langword="true"/>。</para></returns>
    public static bool IsConfigHeader(ReadOnlySpan<byte> header) =>
        header.Length >= HeaderLength
        && header[..Magic.Length].SequenceEqual(Magic)
        && BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) == HeaderLength;

    /// <summary>
    /// Checks a file on disk for the configuration signature without reading the whole file.
    /// <para>在不读取整个文件的前提下检查磁盘文件的配置签名。</para>
    /// </summary>
    /// <param name="path">Candidate file path; a missing file is not a configuration.
    /// <para>候选文件路径；文件不存在时不算配置。</para></param>
    /// <returns><see langword="true"/> when the leading bytes carry a complete configuration header.
    /// <para>起始字节携带完整配置头部时为 <see langword="true"/>。</para></returns>
    public static bool LooksLike(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[HeaderLength];
            return stream.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false) == HeaderLength
                && IsConfigHeader(header);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a header for a configuration that has no source file. The date/time-like block stays zero
    /// because its exact meaning is not part of the observed samples.
    /// <para>为没有来源文件的配置创建头部。日期时间样式的数据块保持为零，因为其确切含义不在观测样本内。</para>
    /// </summary>
    /// <returns>A <see cref="HeaderLength"/>-byte header carrying only the magic and size fields.
    /// <para>仅携带魔数与长度字段的 <see cref="HeaderLength"/> 字节头部。</para></returns>
    public static byte[] CreateHeader()
    {
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), HeaderLength);
        return header;
    }

    private static string DecodeField(ReadOnlySpan<byte> field)
    {
        var text = Encoding.Unicode.GetString(field);
        var end = text.IndexOf('\0');
        return (end < 0 ? text : text[..end]).TrimEnd();
    }

    private static void EncodeField(Span<byte> field, string value)
    {
        field.Clear();
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var maximum = (field.Length / 2) - 1;
        var text = value.Length > maximum ? value[..maximum] : value;
        Encoding.Unicode.GetBytes(text, field);
    }
}