namespace UotanStudio.Firmware.VendorFirmware.MediaTek;

/// <summary>Explicit EMI payload conventions; selection belongs to the chosen agent profile.
/// <para>显式的 EMI 负载约定；选择哪种约定由所选 agent 配置决定。</para></summary>
public enum MtkEmiPayloadLayout
{
    /// <summary>Retain the complete MTK_BLOADER_INFO block.
    /// <para>保留完整的 MTK_BLOADER_INFO 块。</para></summary>
    BootloaderInfo,
    /// <summary>Use bytes after the unique MTK_BIN marker and its 12-byte header.
    /// <para>取唯一 MTK_BIN 标记及其 12 字节头之后的字节。</para></summary>
    BinaryParameters
}

/// <summary>A bounded copy of explicitly extracted EMI bytes, without hardware compatibility or signature claims.
/// <para>显式提取的 EMI 字节的有界副本，不声明硬件兼容性或签名有效性。</para></summary>
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

    /// <summary>Gets the numeric bootloader-info version from its two-byte version field.
    /// <para>由两字节版本字段得到的 bootloader-info 数值版本。</para></summary>
    /// <value>A one- or two-digit decimal version parsed from the bytes following the <c>MTK_BLOADER_INFO_v</c> marker.
    /// <para>从 <c>MTK_BLOADER_INFO_v</c> 标记之后的字节解析出的一位或两位十进制版本。</para></value>
    public int Version { get; }
    /// <summary>Gets the selected payload's absolute byte offset in the original source.
    /// <para>所选负载在原始源中的绝对字节偏移。</para></summary>
    /// <value>
    /// For the <see cref="MtkEmiPayloadLayout.BinaryParameters"/> layout the offset of the first
    /// byte after the MTK_BIN header; otherwise the offset of the retained block itself.
    /// <para>为 <see cref="MtkEmiPayloadLayout.BinaryParameters"/> 布局时指向 MTK_BIN 头之后的第一个字节；否则指向保留块本身。</para>
    /// </value>
    public long SourceOffset { get; }
    /// <summary>Gets whether the recognized 0x800-byte trailer variant was excluded.
    /// <para>指示是否剔除了可识别的 0x800 字节尾部变体。</para></summary>
    /// <value><c>0x800</c> when a padded preloader trailer was recognized and stepped over, <c>0</c> otherwise.
    /// <para>识别到填充的 preloader 尾部并跳过时为 <c>0x800</c>，否则为 <c>0</c>。</para></value>
    public int TrailerPaddingBytes { get; }
    /// <summary>Gets the explicitly selected payload convention.
    /// <para>获取显式选择的负载约定。</para></summary>
    /// <value>The layout that was requested when the bytes were extracted.
    /// <para>提取字节时所请求的布局。</para></value>
    public MtkEmiPayloadLayout Layout { get; }
    /// <summary>Gets the extracted payload size, at most 1 MiB.
    /// <para>提取到的负载大小，最大 1 MiB。</para></summary>
    /// <value>The positive byte count of the retained payload.
    /// <para>保留负载的正字节数。</para></value>
    public int Length => _payload.Length;

    /// <summary>Creates an independent read-only view of the retained bytes.
    /// <para>为保留的字节创建一个独立的只读视图。</para></summary>
    /// <returns>A caller-owned stream which cannot mutate the retained parameters.
    /// <para>归调用方所有的流，无法修改所保留的参数。</para></returns>
    public Stream OpenRead() => new MemoryStream(_payload, writable: false);
}