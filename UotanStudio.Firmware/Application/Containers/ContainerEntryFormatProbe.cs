using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Magic;

namespace UotanStudio.Firmware.Application.Containers;

/// <summary>
/// Bounded content probe for a single archive or vendor-container entry: it reads only a short leading
/// prefix of the entry bytes and names the format, so the new-project list can show what each inner
/// file actually is (for example an <c>ext4</c> image inside a <c>tar.gz</c>) without extracting it.
/// <para>针对单个归档或厂商容器条目的有界内容探测：只读取条目字节的短前缀并给出格式名，
/// 使新建项目列表无需解包即可显示每个内部文件的实际类型（例如 <c>tar.gz</c> 内的 <c>ext4</c> 镜像）。</para>
/// </summary>
/// <remarks>
/// Every probe is bounded: at most <see cref="ProbeLength"/> bytes are read from the entry, the entry
/// itself is opened through the caller's owning stream, and no decompression is performed beyond what the
/// enclosing container already applies. Failures return <see langword="null"/> rather than throwing, because
/// a listing must not fail just because one entry cannot be identified.
/// <para>每次探测都有界：最多从条目读取 <see cref="ProbeLength"/> 字节，条目经调用方持有的流打开，
/// 除外围容器本身的解压外不做额外解压。失败返回 <see langword="null"/> 而不抛出，
/// 因为不能因为一个条目无法识别就让整个列表失败。</para>
/// </remarks>
public static class ContainerEntryFormatProbe
{
    /// <summary>
    /// Maximum leading bytes read from one entry; large enough for every fixed-offset magic the registry probes.
    /// <para>单个条目最多读取的起始字节数；足以覆盖注册表探测的全部固定偏移魔数。</para>
    /// </summary>
    public const int ProbeLength = 64 * 1024;

    /// <summary>
    /// Identifies the content format of bytes already read from an entry.
    /// <para>识别从条目读取到的字节的内容格式。</para>
    /// </summary>
    /// <param name="header">Leading entry bytes; may be shorter than the probe length. <para>条目起始字节；可以短于探测长度。</para></param>
    /// <param name="entryName">Entry name used for cheap extension-based disambiguation. <para>用于按扩展名廉价消歧的条目名。</para></param>
    /// <returns>A short lower-case type label, or <see langword="null"/> when nothing matched.
    /// <para>简短的小写类型标签；无命中时为 <see langword="null"/>。</para></returns>
    public static string? Identify(ReadOnlySpan<byte> header, string? entryName) => Describe(header, entryName);

    /// <summary>
    /// Names the content type of a byte prefix as a short lower-case label (for example <c>ext4</c> or
    /// <c>erofs</c>), falling back to a lower-case magic description and then to the file extension.
    /// <para>把字节前缀的内容类型写成简短的小写标签（如 <c>ext4</c> 或 <c>erofs</c>），
    /// 依次回退到小写化的魔数描述与文件扩展名。</para>
    /// </summary>
    /// <param name="header">Leading bytes of the entry. <para>条目的起始字节。</para></param>
    /// <param name="entryName">Entry name used for the extension fallback. <para>用于扩展名回退的条目名。</para></param>
    /// <returns>The label, or <see langword="null"/> when the bytes are empty and no extension exists.
    /// <para>标签；字节为空且没有扩展名时为 <see langword="null"/>。</para></returns>
    public static string? Describe(ReadOnlySpan<byte> header, string? entryName)
    {
        if (header.Length > 0 && IdentifyStructure(header) is { } structure)
        {
            return structure;
        }

        if (header.Length > 0 && FileMagic.Identify(header)?.Description is { Length: > 0 } description)
        {
            var comma = description.IndexOf(',', StringComparison.Ordinal);
            var firstLine = comma > 0 ? description[..comma] : description;
            return firstLine.Trim().ToLowerInvariant();
        }

        var extension = Path.GetExtension(entryName ?? string.Empty);
        return extension.Length > 1 ? extension[1..].ToLowerInvariant() : null;
    }

    /// <summary>
    /// Identifies the content format of one entry by reading a bounded prefix from its stream.
    /// <para>从条目流读取有界前缀，识别该条目的内容格式。</para>
    /// </summary>
    /// <param name="openEntry">Opens the entry payload; the returned stream is disposed by this method.
    /// <para>打开条目负载；返回的流由本方法释放。</para></param>
    /// <param name="entryName">Entry name used for cheap extension-based disambiguation. <para>用于按扩展名廉价消歧的条目名。</para></param>
    /// <returns>A short lower-case type label, or <see langword="null"/> when the entry could not be read or identified.
    /// <para>简短的小写类型标签；条目无法读取或识别时为 <see langword="null"/>。</para></returns>
    public static string? Identify(Func<Stream> openEntry, string? entryName)
    {
        ArgumentNullException.ThrowIfNull(openEntry);
        try
        {
            using var stream = openEntry();
            if (stream is null || !stream.CanRead)
            {
                return null;
            }

            var buffer = new byte[(int)Math.Min(ProbeLength, Math.Max(0, TryGetRemainingLength(stream)))];
            if (buffer.Length == 0)
            {
                return null;
            }

            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count <= 0)
                {
                    break;
                }

                read += count;
            }

            return Identify(buffer.AsSpan(0, read), entryName);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
            or UnauthorizedAccessException or ObjectDisposedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Names the structural formats the firmware registry understands that magic(5) alone renders poorly.
    /// <para>给出固件注册表理解、而单靠 magic(5) 难以表达的结构化格式名。</para>
    /// </summary>
    /// <param name="header">Leading entry bytes. <para>条目起始字节。</para></param>
    /// <returns>The structural label, or <see langword="null"/> when no structure matched.
    /// <para>结构化标签；无结构命中时为 <see langword="null"/>。</para></returns>
    private static string? IdentifyStructure(ReadOnlySpan<byte> header)
    {
        if (MatchesAscii(header, "ANDROID!"))
        {
            return "boot";
        }

        if (MatchesAscii(header, "VNDRBOOT"))
        {
            return "vendor_boot";
        }

        if (MatchesAscii(header, "CrAU"))
        {
            return "payload";
        }

        if (MatchesAscii(header, "AVB0"))
        {
            return "avb";
        }

        if (MatchesAscii(header, "LP"))
        {
            return "super";
        }

        if (MatchesAscii(header, "EROFS"))
        {
            return "erofs";
        }

        if (MatchesAscii(header, "hsqs") || MatchesAscii(header, "sqsh"))
        {
            return "squashfs";
        }

        if (header.Length >= 0x438 + 2
            && header[0x438] == 0x53 && header[0x439] == 0xEF)
        {
            return "ext4";
        }

        if (header.Length >= 0x404
            && header[0x400] == 0x10 && header[0x401] == 0x20
            && header[0x402] == 0xF5 && header[0x403] == 0xF2)
        {
            return "f2fs";
        }

        if (header.Length >= 4)
        {
            var magic32 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (magic32 == BlockImage.Sparse.SparseFormat.SparseHeaderMagic)
            {
                return "sparse";
            }
        }

        if (header.Length >= 8 && MatchesAscii(header, "EFI PART"))
        {
            return "gpt";
        }

        if (header.Length >= 0x40 && MatchesAscii(header[0x36..], "FAT"))
        {
            return "fat";
        }

        return null;
    }

    /// <summary>Compares an ASCII literal with the start of a byte span. <para>将 ASCII 字面量与字节跨度起始处比较。</para></summary>
    private static bool MatchesAscii(ReadOnlySpan<byte> data, string literal)
    {
        if (data.Length < literal.Length)
        {
            return false;
        }

        for (var i = 0; i < literal.Length; i++)
        {
            if (data[i] != (byte)literal[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reports how many bytes remain in a stream without requiring seekability.
    /// <para>在不要求可定位的前提下报告流中剩余字节数。</para>
    /// </summary>
    /// <param name="stream">Stream to measure. <para>待测量的流。</para></param>
    /// <returns>The remaining length, or <see cref="ProbeLength"/> when it cannot be determined.
    /// <para>剩余长度；无法确定时为 <see cref="ProbeLength"/>。</para></returns>
    private static long TryGetRemainingLength(Stream stream)
    {
        try
        {
            if (stream.CanSeek)
            {
                return Math.Max(0, stream.Length - stream.Position);
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or ObjectDisposedException or IOException)
        {

        }

        return ProbeLength;
    }
}