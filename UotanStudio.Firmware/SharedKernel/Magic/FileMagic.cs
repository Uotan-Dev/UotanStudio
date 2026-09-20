namespace UotanStudio.Firmware.SharedKernel.Magic;

/// <summary>
/// <para>托管魔数识别的公共入口：基于生成器把上游 magic(5) 语料编译成 C# 规则表，运行时不再解析文本或 .mgc。</para>
/// Public entry point for managed magic identification: the generator compiles the upstream magic(5)
/// corpus into C# rule tables; at runtime nothing parses text or a .mgc file.
/// </summary>
public static class FileMagic
{
    /// <summary>
    /// <para>默认头部读取长度（字节）。覆盖绝大多数文件类型魔数所在偏移。</para>
    /// Default header probe length; covers the offsets of the vast majority of type magics.
    /// </summary>
    public const int DefaultProbeLength = 65536;

    /// <summary>
    /// <para>识别文件字节，返回类型/MIME/扩展名等信息；未知返回 <c>null</c>。</para>
    /// Identifies the bytes, returning type/MIME/extension info; <c>null</c> when unknown.
    /// </summary>
    public static MagicInfo? Identify(ReadOnlySpan<byte> data) => CompiledMagicEngine.Match(data);

    /// <summary>
    /// <para>识别文件路径，仅读取头部 <paramref name="probeLength"/> 字节。</para>
    /// Identifies a file path, reading only the first <paramref name="probeLength"/> bytes.
    /// </summary>
    public static MagicInfo? IdentifyFile(string path, int probeLength = DefaultProbeLength)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0x4000, FileOptions.SequentialScan);
            int len = (int)Math.Min(probeLength, fs.Length);
            if (len <= 0)
            {
                return null;
            }

            byte[] buffer = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = fs.Read(buffer, read, len - read);
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
    /// <para>返回人类可读类型描述（等价旧 <c>ManagedMagic.Detect</c>），未知返回 <c>null</c>。</para>
    /// Returns the human-readable type description (the old <c>ManagedMagic.Detect</c> surface); <c>null</c> when unknown.
    /// </summary>
    public static string? Describe(ReadOnlySpan<byte> data) => Identify(data)?.Description;

    /// <summary>
    /// <para>返回 MIME 类型，未知返回 <c>null</c>。</para>
    /// Returns the MIME type; <c>null</c> when unknown.
    /// </summary>
    public static string? MimeType(ReadOnlySpan<byte> data) => Identify(data)?.MimeType;

    /// <summary>
    /// <para>快速 ELF 判别（<c>0x7f 'E' 'L' 'F'</c> 头部）。用于在字节流中按偏移扫描内嵌 ELF，
    /// 避免对每个偏移调用完整引擎。</para>
    /// Fast ELF check (<c>0x7f 'E' 'L' 'F'</c>). Used to scan embedded ELF at byte offsets without
    /// running the full engine per offset.
    /// </summary>
    public static bool IsElf(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == 0x7f && data[1] == (byte)'E' && data[2] == (byte)'L' && data[3] == (byte)'F';
}