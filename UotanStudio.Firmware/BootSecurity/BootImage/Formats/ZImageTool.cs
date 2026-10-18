using UotanStudio.Firmware.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
namespace UotanStudio.Firmware.BootSecurity.BootImage.Formats;

/// <summary>
/// Reads the ARM zImage header and locates its compressed payload.
/// <para>读取 ARM zImage 头部并定位其压缩载荷。</para>
/// </summary>
internal static class ZImageTool
{
    private const int MagicOffset = 0x24;
    private const int HeaderSize = 0x40;

    /// <summary>
    /// Outcome of a zImage header parse: validity, byte order, header addresses and the located payload.
    /// <para>zImage 头部解析结果：有效性、字节序、头部地址以及定位到的载荷。</para>
    /// </summary>
    public sealed class ParseResult
    {
        /// <summary>Whether the parse produced a usable header and payload.
        /// <para>解析是否得到了可用的头部与载荷。</para></summary>
        /// <value><see langword="true"/> when the magic and payload were accepted; <see langword="false"/> when
        /// <see cref="ErrorMessage"/> explains the rejection.
        /// <para>魔数与载荷被接受时为 <see langword="true"/>；由 <see cref="ErrorMessage"/> 说明拒绝原因时为 <see langword="false"/>。</para></value>
        public bool IsValid { get; init; }

        /// <summary>Byte order indicated by the matched zImage magic.
        /// <para>由匹配到的 zImage 魔数指示的字节序。</para></summary>
        /// <value><see langword="true"/> for big-endian header fields; only meaningful when <see cref="IsValid"/>.
        /// <para>头部字段为大端序时为 <see langword="true"/>；仅在 <see cref="IsValid"/> 时有意义。</para></value>
        public bool IsBigEndian { get; init; }

        /// <summary>Compression format detected for the located payload.
        /// <para>对定位到的载荷检测出的压缩格式。</para></summary>
        /// <value>A <see cref="CompressionType"/> value; <see cref="CompressionType.None"/> when the payload
        /// is not a recognized compressed stream.
        /// <para>取值为 <see cref="CompressionType"/> 枚举成员；载荷不是可识别的压缩流时为 <see cref="CompressionType.None"/>。</para></value>
        public CompressionType CompressionType { get; init; }

        /// <summary>Kernel entry address read from the header at offset 0x28.
        /// <para>从头部偏移 0x28 读取的内核入口地址。</para></summary>
        /// <value>32-bit value decoded with the detected byte order; <c>0</c> when <see cref="IsValid"/> is <see langword="false"/>.
        /// <para>按检测到的字节序解码的 32 位值；<see cref="IsValid"/> 为 <see langword="false"/> 时为 <c>0</c>。</para></value>
        public ulong EntryAddress { get; init; }

        /// <summary>Kernel start address read from the header at offset 0x30.
        /// <para>从头部偏移 0x30 读取的内核起始地址。</para></summary>
        /// <value>32-bit value decoded with the detected byte order; <c>0</c> when <see cref="IsValid"/> is <see langword="false"/>.
        /// <para>按检测到的字节序解码的 32 位值；<see cref="IsValid"/> 为 <see langword="false"/> 时为 <c>0</c>。</para></value>
        public ulong KernelStart { get; init; }

        /// <summary>Kernel end address read from the header at offset 0x34.
        /// <para>从头部偏移 0x34 读取的内核结束地址。</para></summary>
        /// <value>32-bit value decoded with the detected byte order; <c>0</c> when <see cref="IsValid"/> is <see langword="false"/>.
        /// <para>按检测到的字节序解码的 32 位值；<see cref="IsValid"/> 为 <see langword="false"/> 时为 <c>0</c>。</para></value>
        public ulong KernelEnd { get; init; }

        /// <summary>Byte offset in the input where the compressed payload begins.
        /// <para>输入中压缩载荷起始的字节偏移。</para></summary>
        /// <value>Offset of the first payload byte, at least 0x40; <c>0</c> when <see cref="IsValid"/> is <see langword="false"/>.
        /// <para>载荷首字节的偏移，至少为 0x40；<see cref="IsValid"/> 为 <see langword="false"/> 时为 <c>0</c>。</para></value>
        public int CompressedOffset { get; init; }

        /// <summary>Length in bytes of the located payload.
        /// <para>定位到的载荷的字节长度。</para></summary>
        /// <value>Equal to the input length minus <see cref="CompressedOffset"/>; <c>0</c> when <see cref="IsValid"/> is <see langword="false"/>.
        /// <para>等于输入长度减去 <see cref="CompressedOffset"/>；<see cref="IsValid"/> 为 <see langword="false"/> 时为 <c>0</c>。</para></value>
        public int CompressedSize { get; init; }

        /// <summary>Copy of the payload bytes from <see cref="CompressedOffset"/> to the end of the input.
        /// <para>从 <see cref="CompressedOffset"/> 到输入末尾的载荷字节副本。</para></summary>
        /// <value>Detached byte array safe to keep; empty when <see cref="IsValid"/> is <see langword="false"/>.
        /// <para>可安全持有的独立字节数组；<see cref="IsValid"/> 为 <see langword="false"/> 时为空。</para></value>
        public byte[] CompressedPayload { get; init; } = [];

        /// <summary>Reason the parse was rejected.
        /// <para>解析被拒绝的原因。</para></summary>
        /// <value>Failure message when <see cref="IsValid"/> is <see langword="false"/>; <see langword="null"/> on success.
        /// <para><see cref="IsValid"/> 为 <see langword="false"/> 时给出失败消息；成功时为 <see langword="null"/>。</para></value>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Checks whether the buffer carries a zImage magic word (compressed or uncompressed, little- or big-endian)
    /// at offset 0x24.
    /// <para>检查缓冲区在偏移 0x24 处是否带有 zImage 魔数（压缩或未压缩、小端或大端）。</para>
    /// </summary>
    /// <param name="data">
    /// Candidate kernel image bytes; shorter than 0x40 bytes it can never match.
    /// <para>候选内核镜像字节；不足 0x40 字节时不可能匹配。</para></param>
    /// <returns><see langword="true"/> when one of the four accepted magic byte orders is present;
    /// <see langword="false"/> when the buffer is too short or the magic does not match.
    /// <para>出现四种可接受的魔数字节序之一时为 <see langword="true"/>；缓冲区过短或魔数不匹配时为 <see langword="false"/>。</para></returns>
    public static bool IsZImage(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            return false;
        var magic = data.Slice(MagicOffset, 4);
        return magic.SequenceEqual(new byte[] { 0xED, 0xFE, 0x0F, 0x00 }) ||
            magic.SequenceEqual(new byte[] { 0x00, 0x0F, 0xFE, 0xED }) ||
            magic.SequenceEqual(new byte[] { 0x18, 0x28, 0x6F, 0x01 }) ||
            magic.SequenceEqual(new byte[] { 0x01, 0x6F, 0x28, 0x18 });
    }

    /// <summary>Parses the zImage header, decodes its address fields with the detected byte order, and copies the compressed payload found by scanning forward from offset 0x40 for a recognized compressed stream.
    /// <para>解析 zImage 头部，按检测到的字节序解码地址字段，并拷贝从偏移 0x40 向后扫描到的可识别压缩流载荷。</para></summary>
    /// <param name="data">Full zImage bytes; must be at least 0x40 long for the magic to match.
    /// <para>完整的 zImage 字节；至少 0x40 字节长才可能匹配魔数。</para></param>
    /// <returns>A valid result with <see cref="ParseResult.CompressedPayload"/> holding the payload from its start offset to the end of the input, or an invalid result whose <see cref="ParseResult.ErrorMessage"/> reports an invalid magic, an unsupported magic, or a missing payload. The parse never throws on malformed input.
    /// <para>有效结果中 <see cref="ParseResult.CompressedPayload"/> 保存从载荷起始偏移到输入末尾的字节； 无效结果的 <see cref="ParseResult.ErrorMessage"/> 会报告魔数无效、魔数不受支持或未找到载荷。 解析对畸形输入不抛出异常。</para></returns>
    /// <example>
    /// <code>
    /// ZImageTool.ParseResult result = ZImageTool.Parse(imageBytes);
    /// if (result.IsValid)
    /// {
    /// Console.WriteLine($"Payload: {result.CompressionType}, {result.CompressedSize} bytes");
    /// }
    /// </code>
    /// </example>
    public static ParseResult Parse(ReadOnlySpan<byte> data)
    {
        if (!IsZImage(data))
            return new() { ErrorMessage = "Invalid zImage magic." };

        var littleMagic = ReadUInt32(data[MagicOffset..], false);
        var bigEndian = data.Slice(MagicOffset, 4).SequenceEqual(new byte[] { 0x00, 0x0F, 0xFE, 0xED }) ||
            data.Slice(MagicOffset, 4).SequenceEqual(new byte[] { 0x01, 0x6F, 0x28, 0x18 });
        var magic = ReadUInt32(data[MagicOffset..], bigEndian);
        if (littleMagic is not (0x000FFEEDu or 0x016F2818u or 0x18286F01u or 0xEDFE0F00u))
            return new() { ErrorMessage = "Unsupported zImage magic." };

        var payloadOffset = FindPayloadOffset(data);
        if (payloadOffset < HeaderSize || payloadOffset >= data.Length)
            return new() { ErrorMessage = "Compressed zImage payload was not found." };

        var payload = data[payloadOffset..].ToArray();
        return new ParseResult
        {
            IsValid = true,
            IsBigEndian = bigEndian,
            CompressionType = BootCompressor.Detect(payload),
            EntryAddress = ReadUInt32(data[0x28..], bigEndian),
            KernelStart = ReadUInt32(data[0x30..], bigEndian),
            KernelEnd = ReadUInt32(data[0x34..], bigEndian),
            CompressedOffset = payloadOffset,
            CompressedSize = payload.Length,
            CompressedPayload = payload
        };
    }

    /// <summary>
    /// Parses the buffer and returns the kernel payload, optionally decompressing it.
    /// <para>解析缓冲区并返回内核载荷，可选择是否解压。</para>
    /// </summary>
    /// <param name="data">
    /// Full zImage bytes to parse. <para>待解析的完整 zImage 字节。</para></param>
    /// <param name="decompress">
    /// When <see langword="true"/>, a recognized compressed payload is decompressed; a payload whose format
    /// is unrecognized or whose decompression fails is returned unchanged.
    /// <para>为 <see langword="true"/> 时，对可识别的压缩载荷进行解压；格式不可识别或解压失败的载荷原样返回。</para></param>
    /// <returns>The kernel bytes, or an empty array when <see cref="Parse"/> rejects the input.
    /// <para>内核字节；当 <see cref="Parse"/> 拒绝输入时返回空数组。</para></returns>
    public static byte[] ExtractKernel(ReadOnlySpan<byte> data, bool decompress = true)
    {
        var parsed = Parse(data);
        if (!parsed.IsValid)
            return [];
        return decompress ? BootCompressor.DecompressOrReturn(parsed.CompressedPayload) : parsed.CompressedPayload;
    }

    private static int FindPayloadOffset(ReadOnlySpan<byte> data)
    {
        for (var i = HeaderSize; i + 4 <= data.Length; i++)
        {
            var probeLength = Math.Min(8, data.Length - i);
            if (BootCompressor.Detect(data.Slice(i, probeLength)) != CompressionType.None)
                return i;
        }
        return HeaderSize;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, bool bigEndian) =>
        bigEndian ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data) :
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data);
}