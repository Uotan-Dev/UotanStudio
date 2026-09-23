namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

/// <summary>Explicit EMI payload conventions; selection belongs to the chosen agent profile.</summary>
public enum MtkEmiPayloadLayout
{
    /// <summary>Retain the complete MTK_BLOADER_INFO block.</summary>
    BootloaderInfo,
    /// <summary>Use bytes after the unique MTK_BIN marker and its 12-byte header.</summary>
    BinaryParameters
}

/// <summary>A bounded copy of explicitly extracted EMI bytes, without hardware compatibility or signature claims.</summary>
public sealed class MtkEmiParameters
{
    private readonly byte[] _payload;

    internal MtkEmiParameters(int version, long sourceOffset, int trailerPaddingBytes,
        MtkEmiPayloadLayout layout, byte[] payload)
    {
        Version = version;
        SourceOffset = sourceOffset;
        TrailerPaddingBytes = trailerPaddingBytes;
        Layout = layout;
        _payload = payload;
    }

    /// <summary>Gets the numeric bootloader-info version from its two-byte version field.</summary>
    public int Version { get; }
    /// <summary>Gets the selected payload's absolute byte offset in the original source.</summary>
    public long SourceOffset { get; }
    /// <summary>Gets whether the recognized 0x800-byte trailer variant was excluded.</summary>
    public int TrailerPaddingBytes { get; }
    /// <summary>Gets the explicitly selected payload convention.</summary>
    public MtkEmiPayloadLayout Layout { get; }
    /// <summary>Gets the extracted payload size, at most 1 MiB.</summary>
    public int Length => _payload.Length;

    /// <summary>Creates an independent read-only view of the retained bytes.</summary>
    /// <returns>A caller-owned stream which cannot mutate the retained parameters.</returns>
    public Stream OpenRead() => new MemoryStream(_payload, writable: false);
}