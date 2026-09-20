namespace UotanStudio.Firmware.BoundedContexts.Compression;

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

    public CompressionFormat Format { get; }

    public string Name { get; }

    public string[] Aliases { get; }

    public string[] Extensions { get; }

    public byte[]? Magic { get; }

    public bool CanCompress { get; }

    public bool CanDecompress { get; }

    public bool IsStreaming { get; }

    public bool HasMagic => Magic is { Length: > 0 };
}