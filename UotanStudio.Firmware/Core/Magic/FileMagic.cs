namespace UotanStudio.Firmware.Core.Magic;

/// <summary>
/// Public entry point for managed magic identification: the generator compiles the magic(5)
/// corpus into C# rule tables; at runtime nothing parses text or a .mgc file.
/// <para>托管魔数识别的公共入口：基于生成器把 magic(5) 语料编译成 C# 规则表，运行时不再解析文本或 .mgc。</para>
/// </summary>
public static class FileMagic
{
    /// <summary>
    /// Default header probe length; covers the offsets of the vast majority of type magics.
    /// <para>默认头部读取长度（字节）。覆盖绝大多数文件类型魔数所在偏移。</para>
    /// </summary>
    public const int DefaultProbeLength = 65536;

    /// <summary>
    /// Identifies the bytes, returning type/MIME/extension info; <c>null</c> when unknown.
    /// <para>识别文件字节，返回类型/MIME/扩展名等信息；未知返回 <c>null</c>。</para>
    /// </summary>
    /// <param name="data">Leading bytes of the file to match against the compiled rules.
    /// <para>参与编译规则匹配的文件头部字节。</para></param>
    /// <returns>
    /// The matched descriptor, or <see langword="null"/> when no rule matches.
    /// <para>匹配到的描述符；无规则命中时为 <see langword="null"/>。</para>
    /// </returns>
    public static MagicInfo? Identify(ReadOnlySpan<byte> data) => CompiledMagicEngine.Match(data);

    /// <summary>
    /// Identifies a file path, reading only the first <paramref name="probeLength"/> bytes.
    /// <para>识别文件路径，仅读取头部 <paramref name="probeLength"/> 字节。</para>
    /// </summary>
    /// <param name="path">
    /// Path of the file to probe; blank or missing paths yield no result.
    /// <para>要探测的文件路径；空白或不存在的返回空。</para>
    /// </param>
    /// <param name="probeLength">
    /// Maximum header bytes to read, clamped to the actual file length.
    /// <para>读取的最大头部字节数，会被限制到实际文件长度。</para>
    /// </param>
    /// <returns>
    /// The matched descriptor, or <see langword="null"/> when the file is unknown, empty,
    /// unreadable, or inaccessible.
    /// <para>匹配到的描述符；文件未知、为空、不可读或无法访问时返回 <see langword="null"/>。</para>
    /// </returns>
    public static MagicInfo? IdentifyFile(string path, int probeLength = DefaultProbeLength)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0x4000, FileOptions.SequentialScan);
            var len = (int)Math.Min(probeLength, fs.Length);
            if (len <= 0)
            {
                return null;
            }

            var buffer = new byte[len];
            var read = 0;
            while (read < len)
            {
                var n = fs.Read(buffer, read, len - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            return Identify(buffer.AsSpan(0, read));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the human-readable type description (the old <c>ManagedMagic.Detect</c> surface); <c>null</c> when unknown.
    /// <para>返回人类可读类型描述（等价旧 <c>ManagedMagic.Detect</c>），未知返回 <c>null</c>。</para>
    /// </summary>
    /// <param name="data">Leading bytes of the file to identify. <para>待识别的文件头部字节。</para></param>
    /// <returns>
    /// The matched rule's human-readable description, or <see langword="null"/> when nothing matches.
    /// <para>匹配规则的人类可读描述；无命中时为 <see langword="null"/>。</para>
    /// </returns>
    public static string? Describe(ReadOnlySpan<byte> data) => Identify(data)?.Description;

    /// <summary>
    /// Returns the MIME type; <c>null</c> when unknown.
    /// <para>返回 MIME 类型，未知返回 <c>null</c>。</para>
    /// </summary>
    /// <param name="data">Leading bytes of the file to identify. <para>待识别的文件头部字节。</para></param>
    /// <returns>
    /// The matched rule's MIME type, or <see langword="null"/> when nothing matches or the rule carries none.
    /// <para>匹配规则的 MIME 类型；无命中或规则未携带 MIME 时返回 <see langword="null"/>。</para>
    /// </returns>
    public static string? MimeType(ReadOnlySpan<byte> data) => Identify(data)?.MimeType;

    /// <summary>
    /// Fast ELF check (<c>0x7f 'E' 'L' 'F'</c>). Used to scan embedded ELF at byte offsets without
    /// running the full engine per offset.
    /// <para>快速 ELF 判别（<c>0x7f 'E' 'L' 'F'</c> 头部）。用于在字节流中按偏移扫描内嵌 ELF，
    /// 避免对每个偏移调用完整引擎。</para>
    /// </summary>
    /// <param name="data">Bytes starting at the candidate offset. <para>从候选偏移起始的字节。</para></param>
    /// <returns>
    /// <see langword="true"/> when at least four bytes are given and they start with
    /// <c>0x7f 'E' 'L' 'F'</c>; shorter spans return <see langword="false"/>.
    /// <para>至少给出四个字节且以 <c>0x7f 'E' 'L' 'F'</c> 开头时返回 <see langword="true"/>；长度不足返回 <see langword="false"/>。</para>
    /// </returns>
    public static bool IsElf(ReadOnlySpan<byte> data) =>
        data.StartsWith("\u007fELF"u8);
}