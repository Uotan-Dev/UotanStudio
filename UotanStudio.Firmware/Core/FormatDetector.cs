namespace UotanStudio.Firmware.Core;

/// <summary>
/// How strongly a detection result is supported by the inspected evidence.
/// <para>检测结果被所检查证据支撑的强度。</para>
/// </summary>
public enum DetectionConfidence
{
    /// <summary>No evidence was evaluated, or the evidence is inconclusive.
    /// <para>未评估任何证据，或证据不足以判断。</para></summary>
    Unknown,
    /// <summary>Only the file extension matched the handler's claimed extensions.
    /// <para>仅文件扩展名与处理器声明的扩展名相符。</para></summary>
    ExtensionOnly,
    /// <summary>The handler accepted the file's own structure; no magic description and no matching
    /// extension backed the claim. <para>处理器认可了文件自身结构；没有魔数描述或相符扩展名作为支撑。</para></summary>
    ValidatedStructure,
    /// <summary>The probe header matched a known magic description.
    /// <para>探测头部匹配到已知的魔数描述。</para></summary>
    StrongMagic
}

/// <summary>
/// Outcome of a detailed format detection, pairing the chosen handler with the evidence that selected it.
/// <para>详细格式识别的结果，把选中的处理器与支撑该选择的证据成对返回。</para>
/// </summary>
/// <param name="Handler">
/// Handler that accepted the file.
/// <para>接受了该文件的处理器。</para>
/// </param>
/// <param name="Confidence">
/// Strength of the evidence behind the choice.
/// <para>支撑该选择的证据强度。</para>
/// </param>
/// <param name="Evidence">
/// Magic description, an <c>extension:</c> claim, or <c>handler-validation</c>.
/// <para>魔数描述、<c>extension:</c> 声明或 <c>handler-validation</c>。</para>
/// </param>
/// <param name="ProbeLength">
/// Number of header bytes actually read, which can be shorter than requested for small files.
/// <para>实际读取的头部字节数；小文件可能少于请求值。</para>
/// </param>
public sealed record FormatDetectionResult(
    IReadonlyFormatHandler Handler,
    DetectionConfidence Confidence,
    string Evidence,
    int ProbeLength);

/// <summary>
/// Registry of format handlers plus file-name and header sniffing used to pick one for a file.
/// <para>格式处理器注册表，并通过文件名与头部嗅探为文件挑选合适的处理器。</para>
/// </summary>
public static class FormatDetector
{
    /// <summary>
    /// Header prefix read once per detection pass; covers every fixed-offset probe in the built-in registry,
    /// including the LP metadata geometry at 8196 bytes and the splash window ending at 16396 bytes.
    /// <para>每次检测过程只读取一次的头部前缀；覆盖内置注册表中全部固定偏移探测，包括 8196 字节处的 LP 元数据几何
    /// 与结束于 16396 字节的 splash 窗口。</para>
    /// </summary>
    private const int RegistryProbeLength = 32 * 1024;

    private static IReadonlyFormatHandler[] Handlers = [];
    private static readonly Lock Gate = new();

    /// <summary>
    /// Registers a format handler.
    /// <para>注册一个格式处理器。</para>
    /// </summary>
    /// <param name="handler">
    /// Handler to add; ignored when a handler with the same <see cref="IReadonlyFormatHandler.FormatName"/>
    /// is already registered, so the first registration of a name wins.
    /// <para>待添加的处理器；当同名 <see cref="IReadonlyFormatHandler.FormatName"/> 已注册时会被忽略，因此同名者以先注册为准。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.
    /// <para><paramref name="handler"/> 为 <see langword="null"/>。</para></exception>
    public static void Register(IReadonlyFormatHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (Gate)
        {
            var current = Handlers;
            foreach (var existing in current)
            {
                if (existing.FormatName == handler.FormatName)
                {
                    return;
                }
            }

            var next = new IReadonlyFormatHandler[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[^1] = handler;
            Volatile.Write(ref Handlers, next);
        }
    }

    /// <summary>
    /// Immutable snapshot of the registered handlers, published when a registration completes.
    /// <para>已注册处理器的不可变快照，在注册完成时发布。</para>
    /// </summary>
    /// <value>
    /// The array published at call time; later registrations replace it and never mutate the returned value.
    /// <para>调用时刻发布的数组；之后的注册会替换它，但绝不会修改已返回的值。</para>
    /// </value>
    public static IReadOnlyList<IReadonlyFormatHandler> RegisteredHandlers => Volatile.Read(ref Handlers);

    /// <summary>Detects the format handler for a file by extension + header sniffing.
    /// <para>通过扩展名与头部嗅探识别文件对应的格式处理器。</para></summary>
    /// <param name="filePath">Path to the file; blank or non-existent paths are rejected without consulting any handler.
    /// <para>文件路径；空白或不存在的路径会直接拒绝，不咨询任何处理器。</para></param>
    /// <returns>The first handler whose extension matches and whose <see cref="IReadonlyFormatHandler.CanHandle"/> returns <see langword="true"/>, otherwise the first handler that accepts the file by content, otherwise <see langword="null"/>. Exceptions thrown by a handler during probing are swallowed.
    /// <para>优先返回扩展名匹配且 <see cref="IReadonlyFormatHandler.CanHandle"/> 为 <see langword="true"/> 的首个处理器； 否则返回按内容接受该文件的首个处理器；都没有时返回 <see langword="null"/>。处理器在探测中抛出的异常会被忽略。</para></returns>
    /// <example>
    /// <code>var handler = FormatDetector.Detect(Path.Combine(input, "boot.img"));</code>
    /// </example>
    public static IReadonlyFormatHandler? Detect(string filePath) => DetectCore(filePath, 0, out _, out _);

    private static IReadonlyFormatHandler? DetectCore(string filePath, int minimumHeaderLength, out byte[] header, out bool headerAvailable)
    {
        header = [];
        headerAvailable = false;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        var probeLength = Math.Max(RegistryProbeLength, minimumHeaderLength);
        long totalLength = 0;
        try
        {
            using var stream = File.OpenRead(filePath);
            totalLength = stream.Length;
            headerAvailable = true;
            var capacity = (int)Math.Min(probeLength, totalLength);
            if (capacity > 0)
            {
                header = new byte[capacity];
                var read = 0;
                while (read < capacity)
                {
                    var n = stream.Read(header, read, capacity - read);
                    if (n <= 0)
                    {
                        break;
                    }

                    read += n;
                }

                if (read != capacity)
                {
                    Array.Resize(ref header, read);
                }
            }
        }
        catch
        {
            header = [];
            headerAvailable = false;
        }

        using var session = headerAvailable ? HeaderProbeSession.Enter(filePath, header, totalLength) : null;
        var fileExt = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (!string.IsNullOrEmpty(fileExt))
        {
            foreach (var handler in RegisteredHandlers)
            {
                if (handler.FileExtensions.Contains(fileExt) && TryCanHandle(handler, header, filePath))
                {
                    return handler;
                }
            }
        }

        foreach (var handler in RegisteredHandlers)
        {
            if (TryCanHandle(handler, header, filePath))
            {
                return handler;
            }
        }

        return null;
    }

    private static bool TryCanHandle(IReadonlyFormatHandler handler, ReadOnlySpan<byte> header, string filePath)
    {
        try
        {
            return handler is IHeaderFormatHandler headerHandler
                ? headerHandler.CanHandle(header, filePath)
                : handler.CanHandle(filePath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads the leading bytes of a file.<para>读取文件头部字节。</para></summary>
    /// <param name="filePath">Path to the file whose leading bytes are read.
    /// <para>读取其起始字节的文件路径。</para></param>
    /// <param name="probeLength">Requested number of bytes; clamped to the range <c>4</c> to <c>1048576</c> before reading.
    /// <para>请求的字节数；读取前会被裁剪到 <c>4</c> 至 <c>1048576</c> 范围。</para></param>
    /// <returns>A detection result carrying the handler, the magic description when the probe header matches one, the plain <c>extension:</c> claim when only the extension matches, and a <c>handler-validation</c> evidence string otherwise; <see langword="null"/> when no handler accepts the file. The reported <see cref="FormatDetectionResult.ProbeLength"/> is the number of bytes actually read.
    /// <para>返回检测结果：包含处理器、探测头部匹配到的魔数描述、仅有扩展名相符时的 <c>extension:</c> 声明， 其余情况为 <c>handler-validation</c> 证据字符串；没有处理器接受该文件时返回 <see langword="null"/>。 所报告的 <see cref="FormatDetectionResult.ProbeLength"/> 是实际读取的字节数。</para></returns>
    /// <exception cref="IOException">The file cannot be read while probing.
    /// <para>探测期间文件无法读取。</para></exception>
    public static FormatDetectionResult? DetectDetailed(string filePath, int probeLength = 65536)
    {
        var requested = Math.Clamp(probeLength, 4, 1024 * 1024);
        var handler = DetectCore(filePath, requested, out var header, out var headerAvailable);
        if (handler is null) return null;
        if (!headerAvailable)
        {
            header = ReadHeader(filePath, requested);
        }

        var reported = Math.Min(header.Length, requested);
        var magic = Magic.FileMagic.Describe(header.AsSpan(0, reported));
        var extension = Path.GetExtension(filePath).TrimStart('.');
        var extensionClaim = !string.IsNullOrEmpty(extension)
            && handler.FileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        return new FormatDetectionResult(handler,
            magic is not null ? DetectionConfidence.StrongMagic
                : extensionClaim ? DetectionConfidence.ExtensionOnly : DetectionConfidence.ValidatedStructure,
            magic ?? (extensionClaim ? $"extension:{extension}" : "handler-validation"), reported);
    }

    /// <summary>Reads up to <paramref name="count"/> bytes from the start of a file in one buffered pass, serving them from the header snapshot of the active detection pass when that snapshot covers the request.
    /// <para>从文件起始处一次性缓冲读取最多 <paramref name="count"/> 字节；活动检测过程的头部快照覆盖请求时直接由该快照提供。</para></summary>
    /// <param name="filePath">Path to the file; the handle is opened for reading and closed before returning, unless the active detection snapshot already holds the requested prefix.
    /// <para>文件路径；句柄以只读方式打开并在返回前关闭，但活动检测快照已持有请求的前缀时不再打开。</para></param>
    /// <param name="count">Maximum number of bytes to read; the caller is responsible for keeping this bounded, and no upper limit is enforced here. A value of <c>0</c> yields an empty array.
    /// <para>读取字节数上限；上限由调用方负责约束，此处不做检查。传入 <c>0</c> 时返回空数组。</para></param>
    /// <returns>A fresh array holding the bytes read, shortened to the actual length when the file is smaller than <paramref name="count"/>; never longer than requested.
    /// <para>持有已读取字节的新数组；文件小于 <paramref name="count"/> 时会缩短为实际长度，绝不会长于请求值。</para></returns>
    /// <exception cref="FileNotFoundException"><paramref name="filePath"/> does not exist.
    /// <para><paramref name="filePath"/> 不存在。</para></exception>
    /// <exception cref="UnauthorizedAccessException">The caller lacks permission to read <paramref name="filePath"/>.
    /// <para>调用方没有读取 <paramref name="filePath"/> 的权限。</para></exception>
    public static byte[] ReadHeader(string filePath, int count)
    {
        if (HeaderProbeSession.TryReadHeader(filePath, count, out var cached))
        {
            return cached;
        }

        using var fs = File.OpenRead(filePath);
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = fs.Read(buffer, read, count - read);
            if (n <= 0)
            {
                break;
            }
            read += n;
        }
        if (read != count)
        {
            Array.Resize(ref buffer, read);
        }
        return buffer;
    }

    /// <summary>Compares an ASCII magic string with the leading bytes of a header.
    /// <para>将 ASCII 魔数字符串与头部起始字节比较。</para></summary>
    /// <param name="header">Header bytes as read from the file; may be shorter than the magic.
    /// <para>从文件读出的头部字节；可以短于魔数。</para></param>
    /// <param name="magic">Magic text whose characters are compared by their low eight bits, so non-ASCII characters are not matched faithfully.
    /// <para>魔数字符串；逐字符只比较低八位，因此非 ASCII 字符不会被准确匹配。</para></param>
    /// <returns><see langword="true"/> only when <paramref name="header"/> starts with every byte of <paramref name="magic"/>; <see langword="false"/> when the header is too short.
    /// <para>仅当 <paramref name="header"/> 以 <paramref name="magic"/> 的全部字节开头时为 <see langword="true"/>；头部过短时为 <see langword="false"/>。</para></returns>
    public static bool HasMagic(byte[] header, string magic)
    {
        if (header.Length < magic.Length)
        {
            return false;
        }
        for (var i = 0; i < magic.Length; i++)
        {
            if (header[i] != (byte)magic[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Compares an ASCII magic string with the header bytes starting at <paramref name="offset"/>.
    /// <para>将 ASCII 魔数字符串与从 <paramref name="offset"/> 开始的头部字节比较。</para></summary>
    /// <param name="header">Header bytes as read from the file; may end before the magic does.
    /// <para>从文件读出的头部字节；可以在魔数结束前截断。</para></param>
    /// <param name="offset">Byte index inside <paramref name="header"/> where the magic is expected.
    /// <para><paramref name="header"/> 内期望出现魔数的字节索引。</para></param>
    /// <param name="magic">Magic text whose characters are compared by their low eight bits, so non-ASCII characters are not matched faithfully.
    /// <para>魔数字符串；逐字符只比较低八位，因此非 ASCII 字符不会被准确匹配。</para></param>
    /// <returns><see langword="true"/> only when the bytes at <paramref name="offset"/> match every byte of <paramref name="magic"/>; <see langword="false"/> when the header is too short.
    /// <para>仅当 <paramref name="offset"/> 起的字节与 <paramref name="magic"/> 的全部字节一致时为 <see langword="true"/>；头部过短时为 <see langword="false"/>。</para></returns>
    public static bool HasMagic(byte[] header, int offset, string magic)
    {
        if (offset < 0 || header.Length - offset < magic.Length)
        {
            return false;
        }
        for (var i = 0; i < magic.Length; i++)
        {
            if (header[offset + i] != (byte)magic[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Compares a binary magic sequence with the leading bytes of a header.
    /// <para>将二进制魔数序列与头部起始字节比较。</para></summary>
    /// <param name="header">Header bytes as read from the file; may be shorter than the magic.
    /// <para>从文件读出的头部字节；可以短于魔数。</para></param>
    /// <param name="magic">Exact byte sequence to look for, compared byte by byte with no case folding or masking.
    /// <para>需要查找的精确字节序列，逐字节比较，不做大小写折叠或掩码。</para></param>
    /// <returns><see langword="true"/> only when <paramref name="header"/> is at least as long as <paramref name="magic"/> and starts with it.
    /// <para>仅当 <paramref name="header"/> 长度不小于 <paramref name="magic"/> 且以其开头时为 <see langword="true"/>。</para></returns>
    public static bool HasMagic(byte[] header, ReadOnlySpan<byte> magic) => header.AsSpan(0, Math.Min(header.Length, magic.Length)).SequenceEqual(magic);

    /// <summary>Confirms whether a pre-read header carries a known structural magic.
    /// <para>确认预先读取的头部是否携带已知的结构魔数。</para></summary>
    /// <param name="path">Path the header was read from, forwarded to each handler for cheap name-based disambiguation; never opened.
    /// <para>读取该头部所用的路径，转发给各处理器用于廉价的名字消歧；绝不打开。</para></param>
    /// <param name="header">Header bytes to confirm against, typically from <see cref="ReadHeader(string, int)"/>; may be shorter than any magic offset, and an empty span simply matches nothing.
    /// <para>用于确认的头部字节，通常来自 <see cref="ReadHeader(string, int)"/>；可能短于任意魔数偏移，空 span 则一律不匹配。</para></param>
    /// <returns><see langword="true"/> when any handler confirms the header; <see langword="false"/> otherwise, including when a handler throws, whose exception is swallowed.
    /// <para>任一处理器确认头部时为 <see langword="true"/>；否则为 <see langword="false"/>，处理器抛出异常时同样被吞掉并返回该值。</para></returns>
    public static bool MatchesProbeHints(string path, ReadOnlySpan<byte> header)
        => TryGetProbeMatch(path, header, out _);

    /// <summary>Confirms a probe match and reports which registered handler matched.
    /// <para>确认探测命中，并报告命中的已注册处理器。</para></summary>
    /// <param name="path">Path the header was read from, forwarded to each handler; never opened.
    /// <para>读取该头部所用的路径，转发给各处理器；绝不打开。</para></param>
    /// <param name="header">Header bytes to confirm against; may be shorter than any magic offset.
    /// <para>用于确认的头部字节；可能短于任意魔数偏移。</para></param>
    /// <param name="handler">The confirming handler when the result is <see langword="true"/>, otherwise <see langword="null"/>.
    /// <para>结果为 <see langword="true"/> 时为确认的处理器，否则为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when a handler confirms the header; <see langword="false"/> otherwise, swallowing any handler exception.
    /// <para>有处理器确认头部时为 <see langword="true"/>；否则为 <see langword="false"/>，并吞掉处理器的任何异常。</para></returns>
    public static bool TryGetProbeMatch(string path, ReadOnlySpan<byte> header, out IReadonlyFormatHandler? handler)
    {
        foreach (var candidate in RegisteredHandlers)
        {
            if (candidate is IFormatProbeHints probe)
            {
                try
                {
                    if (probe.MatchesMagic(header, path))
                    {
                        handler = candidate;
                        return true;
                    }
                }
                catch
                {

                }
            }
        }

        handler = null;
        return false;
    }
}