using System.Buffers.Binary;

namespace UotanStudio.Firmware.BootSecurity.Patcher.Kernel;

/// <summary>
/// <para>arm64 Linux Image 头（前 64 字节）的只读解析结果，字段布局遵循内核 booting 规范与 arch/arm64/include/asm/image.h。</para>
/// <para>Read-only parse result for the 64-byte arm64 Linux Image header, laid out per the kernel booting document and arch/arm64/include/asm/image.h.</para>
/// </summary>
internal readonly struct Arm64ImageHeader
{
    private const int HeaderLength = 0x40;

    private Arm64ImageHeader(ulong textOffset, ulong imageSize, ulong flags)
    {
        TextOffset = textOffset;
        ImageSize = imageSize;
        Flags = flags;
    }

    /// <summary>
    /// <para>text_offset：内核 text 段相对加载基址的偏移，位于镜像偏移 0x08。</para>
    /// <para>TextOffset: the kernel text offset from the load base, stored at image offset 0x08.</para>
    /// </summary>
    public ulong TextOffset { get; }

    /// <summary>
    /// <para>image_size：内存中镜像的大小，位于镜像偏移 0x10，可能大于引导镜像实际存在的字节数。</para>
    /// <para>ImageSize: the in-memory image size at image offset 0x10, which may exceed the bytes actually present.</para>
    /// </summary>
    public ulong ImageSize { get; }

    /// <summary>
    /// <para>flags：镜像偏移 0x18 处的标志位原值。</para>
    /// <para>Flags: the raw flag word at image offset 0x18.</para>
    /// </summary>
    public ulong Flags { get; }

    /// <summary>
    /// <para>页大小位移：flags[2:1] 编码 0=未指定、1=4KiB、2=16KiB、3=64KiB；未指定按 4KiB（12 位）处理。</para>
    /// <para>PageSizeShift: decoded from flags[2:1] (0 unspecified, 1 4KiB, 2 16KiB, 3 64KiB); unspecified is treated as 4KiB, i.e. shift 12.</para>
    /// </summary>
    public int PageSizeShift => ((Flags >> 1) & 0x3) switch { 2 => 14, 3 => 16, _ => 12 };

    /// <summary>
    /// <para>按规范解析镜像前 64 字节；要求长度足够且偏移 0x38 处为 ARM\x64 魔数，成功时填充 text_offset、image_size 与 flags。</para>
    /// <para>Parses the first 64 bytes per the spec, requiring sufficient length and the ARM\x64 magic at offset 0x38, filling text_offset, image_size and flags on success.</para>
    /// </summary>
    /// <param name="image"><para>内核镜像字节。</para>The kernel image bytes.</param>
    /// <param name="header"><para>解析成功时的头部字段。</para>The parsed header fields on success.</param>
    /// <returns><para>镜像为合法 arm64 Image 时为 true。</para>True when the image is a valid arm64 Image.</returns>
    public static bool TryParse(ReadOnlySpan<byte> image, out Arm64ImageHeader header)
    {
        header = default;
        if (image.Length < HeaderLength || !image.Slice(0x38, 4).SequenceEqual("ARM\x64"u8)) return false;
        header = new Arm64ImageHeader(
            BinaryPrimitives.ReadUInt64LittleEndian(image[0x08..]),
            BinaryPrimitives.ReadUInt64LittleEndian(image[0x10..]),
            BinaryPrimitives.ReadUInt64LittleEndian(image[0x18..]));
        return true;
    }
}