namespace UotanStudio.Firmware.Compress;

/// <summary>
/// 压缩格式的元数据信息。
/// <para>Metadata information of a compression format.</para>
/// </summary>
public sealed class CompressionFormatInfo
{
    internal CompressionFormatInfo(
        CompressionFormat format,
        string name,
        string[] aliases,
        string[] extensions,
        byte[]? magic,
        bool canCompress,
        bool canDecompress,
        bool isStreaming)
    {
        Format = format;
        Name = name;
        Aliases = aliases;
        Extensions = extensions;
        Magic = magic;
        CanCompress = canCompress;
        CanDecompress = canDecompress;
        IsStreaming = isStreaming;
    }

    /// <summary>
    /// 格式类型。
    /// <para>The format type.</para>
    /// </summary>
    public CompressionFormat Format { get; }

    /// <summary>
    /// 格式显示名称（如 "gzip"）。
    /// <para>The display name of the format (e.g. "gzip").</para>
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// 可用于解析格式名称的别名（如 ["gz", "gzip"]）。
    /// <para>Aliases usable for parsing the format name (e.g. ["gz", "gzip"]).</para>
    /// </summary>
    public string[] Aliases { get; }

    /// <summary>
    /// 关联的文件扩展名（含点号，如 [".gz", ".gzip"]）。
    /// <para>Associated file extensions (with leading dot, e.g. [".gz", ".gzip"]).</para>
    /// </summary>
    public string[] Extensions { get; }

    /// <summary>
    /// 用于魔数检测的头部字节；为 null 表示无法通过魔数可靠检测（如 brotli / 原始 deflate）。
    /// <para>Magic bytes used for detection; null means the format cannot be reliably detected by magic (e.g. brotli / raw deflate).</para>
    /// </summary>
    public byte[]? Magic { get; }

    /// <summary>
    /// 是否支持压缩。
    /// <para>Whether compression is supported.</para>
    /// </summary>
    public bool CanCompress { get; }

    /// <summary>
    /// 是否支持解压。
    /// <para>Whether decompression is supported.</para>
    /// </summary>
    public bool CanDecompress { get; }

    /// <summary>
    /// 是否支持流式处理（不将整个数据缓冲进内存）。
    /// <para>Whether streaming is supported (without buffering the whole data in memory).</para>
    /// </summary>
    public bool IsStreaming { get; }

    /// <summary>
    /// 是否具有可用于检测的魔数。
    /// <para>Whether the format has magic bytes usable for detection.</para>
    /// </summary>
    public bool HasMagic => Magic is { Length: > 0 };
}