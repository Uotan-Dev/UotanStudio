namespace UotanStudio.Firmware.SharedKernel;

public enum DetectionConfidence
{
    Unknown,
    ExtensionOnly,
    ValidatedStructure,
    StrongMagic
}

public sealed record FormatDetectionResult(
    IReadonlyFormatHandler Handler,
    DetectionConfidence Confidence,
    string Evidence,
    int ProbeLength);

public static class FormatDetector
{
    private static readonly List<IReadonlyFormatHandler> Handlers = [];

    /// <summary>
    /// Registers a format handler.
    /// <para>注册一个格式处理器。</para>
    /// </summary>
    public static void Register(IReadonlyFormatHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (Handlers)
        {
            if (Handlers.All(h => h.FormatName != handler.FormatName))
            {
                Handlers.Add(handler);
            }
        }
    }

    public static IReadOnlyList<IReadonlyFormatHandler> RegisteredHandlers
    {
        get { lock (Handlers) { return [.. Handlers]; } }
    }

    /// <summary>
    /// Detects the format handler for a file by extension + header sniffing.
    /// <para>通过扩展名与头部嗅探识别文件对应的格式处理器。</para>
    /// </summary>
    public static IReadonlyFormatHandler? Detect(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }


        var fileExt = System.IO.Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (!string.IsNullOrEmpty(fileExt))
        {
            List<IReadonlyFormatHandler> snapshot;
            lock (Handlers) { snapshot = [.. Handlers]; }

            foreach (var handler in snapshot)
            {
                if (handler.FileExtensions.Contains(fileExt))
                {
                    try
                    {
                        if (handler.CanHandle(filePath))
                        {
                            return handler;
                        }
                    }
                    catch
                    {

                    }
                }
            }
        }


        List<IReadonlyFormatHandler> sniffSnapshot;
        lock (Handlers) { sniffSnapshot = [.. Handlers]; }

        foreach (var handler in sniffSnapshot)
        {
            try
            {
                if (handler.CanHandle(filePath))
                {
                    return handler;
                }
            }
            catch
            {

            }
        }
        return null;
    }

    /// <summary>
    /// Reads the leading bytes of a file.
    /// <para>读取文件头部字节。</para>
    /// </summary>
    public static FormatDetectionResult? DetectDetailed(string filePath, int probeLength = 65536)
    {
        var handler = Detect(filePath);
        if (handler is null) return null;
        var header = ReadHeader(filePath, Math.Clamp(probeLength, 4, 1024 * 1024));
        var magic = Magic.FileMagic.Describe(header);
        var extension = Path.GetExtension(filePath).TrimStart('.');
        var extensionClaim = !string.IsNullOrEmpty(extension)
            && handler.FileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        return new FormatDetectionResult(handler,
            magic is not null ? DetectionConfidence.StrongMagic
                : extensionClaim ? DetectionConfidence.ExtensionOnly : DetectionConfidence.ValidatedStructure,
            magic ?? (extensionClaim ? $"extension:{extension}" : "handler-validation"), header.Length);
    }

    public static byte[] ReadHeader(string filePath, int count)
    {
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

    /// <summary>
    /// Compares an ASCII magic string with the leading bytes of a header.
    /// <para>将 ASCII 魔数字符串与头部起始字节比较。</para>
    /// </summary>
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

    /// <summary>
    /// Compares a binary magic sequence with the leading bytes of a header.
    /// <para>将二进制魔数序列与头部起始字节比较。</para>
    /// </summary>
    public static bool HasMagic(byte[] header, ReadOnlySpan<byte> magic) => header.AsSpan(0, Math.Min(header.Length, magic.Length)).SequenceEqual(magic);
}