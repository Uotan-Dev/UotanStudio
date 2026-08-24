namespace UotanStudio.Firmware.Common;

/// <summary>
/// Detects firmware file formats by magic bytes/headers and registered handlers.
/// <para>通过魔数/头部与已注册处理器识别固件文件格式。</para>
/// </summary>
public static class FormatDetector
{
    private static readonly List<IFormatHandler> Handlers = [];

    /// <summary>
    /// Registers a format handler.
    /// <para>注册一个格式处理器。</para>
    /// </summary>
    public static void Register(IFormatHandler handler)
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

    /// <summary>
    /// Gets all registered handlers.
    /// <para>获取全部已注册处理器。</para>
    /// </summary>
    public static IReadOnlyList<IFormatHandler> RegisteredHandlers
    {
        get { lock (Handlers) { return [.. Handlers]; } }
    }

    /// <summary>
    /// Detects the format handler for a file by extension + header sniffing.
    /// <para>通过扩展名与头部嗅探识别文件对应的格式处理器。</para>
    /// </summary>
    public static IFormatHandler? Detect(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        lock (Handlers)
        {
            foreach (var handler in Handlers)
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
                    // A broken handler must not break detection of others.
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Reads the leading bytes of a file.
    /// <para>读取文件头部字节。</para>
    /// </summary>
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