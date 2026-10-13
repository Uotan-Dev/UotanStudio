namespace UotanStudio.Firmware.Compression;

/// <summary>
/// Immutable metadata describing one registered compression format.
/// <para>描述一个已注册压缩格式的不可变元数据。</para>
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
    /// The enum value this entry describes.
    /// <para>本条目所对应的枚举值。</para>
    /// </summary>
    /// <value>The registered <see cref="CompressionFormat"/>. <para>已注册的 <see cref="CompressionFormat"/>。</para></value>
    public CompressionFormat Format { get; }

    /// <summary>
    /// The canonical lowercase name of the format.
    /// <para>格式规范的小写名称。</para>
    /// </summary>
    /// <value>
    /// Name used for display and as the primary key of case-insensitive name lookup.
    /// <para>用于显示的名称，也是不区分大小写名称查找的主键。</para>
    /// </value>
    public string Name { get; }

    /// <summary>
    /// Alternative names accepted by name parsing.
    /// <para>名称解析时接受的替代名称。</para>
    /// </summary>
    /// <value>
    /// Alias strings matched case-insensitively alongside <see cref="Name"/>.
    /// <para>与 <see cref="Name"/> 一起参与不区分大小写匹配的别名列表。</para>
    /// </value>
    public string[] Aliases { get; }

    /// <summary>
    /// File extensions that map to this format.
    /// <para>可映射到该格式的文件扩展名。</para>
    /// </summary>
    /// <value>
    /// Extensions with leading dots, matched case-insensitively; the first entry is the preferred
    /// extension used when writing output.
    /// <para>含点号前缀的扩展名，匹配时不区分大小写；第一项为写出输出时首选的扩展名。</para>
    /// </value>
    public string[] Extensions { get; }

    /// <summary>
    /// Leading bytes that identify the format in raw data.
    /// <para>在原始数据中标识该格式的前导字节。</para>
    /// </summary>
    /// <value>
    /// The magic byte sequence, or <see langword="null"/> when the format has no detectable magic.
    /// <para>魔数字节序列；格式没有可用于检测的魔数时为 <see langword="null"/>。</para>
    /// </value>
    public byte[]? Magic { get; }

    /// <summary>
    /// Whether the compression services can produce this format.
    /// <para>压缩服务能否生成该格式。</para>
    /// </summary>
    /// <value><see langword="true"/> when compression is supported. <para>支持压缩时返回 <see langword="true"/>。</para></value>
    public bool CanCompress { get; }

    /// <summary>
    /// Whether the compression services can decode this format.
    /// <para>压缩服务能否解压该格式。</para>
    /// </summary>
    /// <value><see langword="true"/> when decompression is supported. <para>支持解压时返回 <see langword="true"/>。</para></value>
    public bool CanDecompress { get; }

    /// <summary>
    /// Whether the codec processes data incrementally instead of buffering the whole result.
    /// <para>编解码器是否增量处理数据而非完整缓冲结果。</para>
    /// </summary>
    /// <value><see langword="true"/> for streaming codecs, <see langword="false"/> for whole-buffer codecs.
    /// <para>流式编解码器为 <see langword="true"/>，整块缓冲的编解码器为 <see langword="false"/>。</para></value>
    public bool IsStreaming { get; }

    /// <summary>
    /// Whether a magic byte sequence is registered for the format.
    /// <para>该格式是否注册了魔数字节序列。</para>
    /// </summary>
    /// <value><see langword="true"/> when <see cref="Magic"/> is non-null and non-empty.
    /// <para><see cref="Magic"/> 非空且长度大于零时为 <see langword="true"/>。</para></value>
    public bool HasMagic => Magic is { Length: > 0 };
}