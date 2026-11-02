using System.Buffers.Binary;
using UotanStudio.Hash.RockchipCrc32;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Builds and unwraps the length-prefixed Rockchip <c>PARM</c> container that the vendor tools persist at the
/// fixed parameter flash address: four ASCII magic bytes, a little-endian payload length, the parameter text and
/// a trailing little-endian Rockchip CRC-32 of the payload.
/// <para>构建并解包厂商工具持久化在固定 parameter 闪存地址上的带长度前缀 Rockchip <c>PARM</c> 容器：四个 ASCII
/// 魔数字节、小端负载长度、parameter 文本，以及尾部对负载计算的小端 Rockchip CRC-32。</para>
/// </summary>
/// <remarks>
/// The layout mirrors the vendor <c>MakeParamBuffer</c>/<c>GetParameter_Loader</c> pair: magic
/// <c>0x4D524150</c> ("PARM"), a <see langword="uint"/> payload length at offset 4, the payload at offset 8 and the
/// CRC-32 of the payload immediately after it. Writes are bounded by <see cref="MaxParameterSectors"/> 512-byte
/// sectors, the cap the vendor writer enforces, so the wrapped image always fits the reserved parameter window.
/// <para>布局与厂商 <c>MakeParamBuffer</c>/<c>GetParameter_Loader</c> 对一致：魔数 <c>0x4D524150</c>（"PARM"）、
/// 偏移 4 的小端 <see langword="uint"/> 负载长度、偏移 8 起的负载，以及紧随其后的负载 CRC-32。写入受
/// <see cref="MaxParameterSectors"/> 个 512 字节扇区约束（厂商写入器的上限），使封装后的镜像始终落在保留的
/// parameter 窗口内。</para>
/// </remarks>
public static class RockchipParmContainer
{
    /// <summary>The little-endian container magic "PARM" (<c>0x4D524150</c>). <para>小端容器魔数 "PARM"（<c>0x4D524150</c>）。</para></summary>
    public const uint Magic = 0x4D524150;

    /// <summary>Header length: magic plus the payload-length field. <para>头部长度：魔数加负载长度字段。</para></summary>
    public const int HeaderLength = 8;

    /// <summary>Trailer length: the payload CRC-32. <para>尾部长度：负载 CRC-32。</para></summary>
    public const int TrailerLength = sizeof(uint);

    /// <summary>
    /// The flash LBA the vendor tools persist the wrapped parameter at.
    /// <para>厂商工具持久化封装 parameter 的闪存 LBA。</para>
    /// </summary>
    public const long FirmwareParameterStartLba = 0x2000;

    /// <summary>
    /// The sector count the vendor tools read back when loading the wrapped parameter.
    /// <para>厂商工具加载封装 parameter 时回读的扇区数。</para>
    /// </summary>
    public const int FirmwareParameterReadSectors = 512;

    /// <summary>
    /// The legacy raw-parameter LBA older loaders used before the wrapped parameter existed.
    /// <para>封装 parameter 出现之前旧 loader 使用的裸 parameter LBA。</para>
    /// </summary>
    public const long LegacyParameterStartLba = 0x20;

    /// <summary>
    /// The legacy sector count read back from the legacy raw-parameter LBA.
    /// <para>从旧裸 parameter LBA 回读的扇区数。</para>
    /// </summary>
    public const int LegacyParameterReadSectors = 128;

    /// <summary>
    /// The maximum number of 512-byte sectors a wrapped parameter may occupy; the vendor writer rejects
    /// anything larger before any byte reaches flash.
    /// <para>封装 parameter 最多占用的 512 字节扇区数；厂商写入器在任何字节落盘前拒绝更大的值。</para>
    /// </summary>
    public const int MaxParameterSectors = 1024;

    /// <summary>
    /// Wraps parameter text into the vendor <c>PARM</c> container.
    /// <para>把 parameter 文本封装进厂商 <c>PARM</c> 容器。</para>
    /// </summary>
    /// <param name="payload">The parameter text bytes; non-empty and bounded by
    /// <see cref="MaxParameterSectors"/> sectors once wrapped.<para>parameter 文本字节；非空且封装后不超过
    /// <see cref="MaxParameterSectors"/> 个扇区。</para></param>
    /// <returns>The wrapped container bytes.<para>封装后的容器字节。</para></returns>
    /// <exception cref="ArgumentException">The payload is empty or the wrapped image would exceed
    /// <see cref="MaxParameterSectors"/> sectors.<para>负载为空，或封装后的镜像超过
    /// <see cref="MaxParameterSectors"/> 个扇区。</para></exception>
    public static byte[] Wrap(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new ArgumentException("The parameter payload must not be empty.", nameof(payload));
        }

        var totalBytes = (long)HeaderLength + payload.Length + TrailerLength;
        var sectors = (totalBytes + 511) / 512;
        if (sectors > MaxParameterSectors)
        {
            throw new ArgumentException(
                $"The wrapped parameter needs {sectors} sectors, more than the {MaxParameterSectors}-sector cap.",
                nameof(payload));
        }

        var wrapped = new byte[totalBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(wrapped.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(wrapped.AsSpan(HeaderLength));
        BinaryPrimitives.WriteUInt32LittleEndian(
            wrapped.AsSpan(HeaderLength + payload.Length), RockchipCrc32.Compute(payload));
        return wrapped;
    }

    /// <summary>
    /// Unwraps a vendor <c>PARM</c> container: the magic must match, the declared payload length must fit the
    /// buffer and the trailing CRC-32 must match the payload.
    /// <para>解包厂商 <c>PARM</c> 容器：魔数必须匹配，声明的负载长度必须落在缓冲区内，且尾部 CRC-32 必须与负载相符。</para>
    /// </summary>
    /// <param name="data">The bytes read from the parameter window; trailing zero padding is ignored.
    /// <para>从 parameter 窗口读出的字节；尾部零填充会被忽略。</para></param>
    /// <returns>The parameter payload bytes.<para>parameter 负载字节。</para></returns>
    /// <exception cref="InvalidDataException">The magic, declared length or CRC-32 does not match the container
    /// layout.<para>魔数、声明长度或 CRC-32 与容器布局不符。</para></exception>
    public static byte[] Unwrap(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        {
            throw new InvalidDataException("The data does not start with the Rockchip PARM magic.");
        }

        var payloadLength = (long)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (payloadLength <= 0 || HeaderLength + payloadLength + TrailerLength > data.Length)
        {
            throw new InvalidDataException(
                $"The PARM payload length {payloadLength} does not fit the {data.Length} bytes read.");
        }

        var payload = data.Slice(HeaderLength, (int)payloadLength);
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(HeaderLength + payloadLength)..]);
        if (RockchipCrc32.Compute(payload) != storedCrc)
        {
            throw new InvalidDataException("The PARM payload CRC-32 does not match the stored value.");
        }

        return payload.ToArray();
    }

    /// <summary>
    /// Whether the buffer starts with the vendor <c>PARM</c> magic.
    /// <para>缓冲区是否以厂商 <c>PARM</c> 魔数开头。</para>
    /// </summary>
    /// <param name="data">The leading bytes of a candidate container.<para>候选容器的起始字节。</para></param>
    /// <returns><see langword="true"/> when the magic matches.<para>魔数匹配时为 <see langword="true"/>。</para></returns>
    public static bool HasMagic(ReadOnlySpan<byte> data)
        => data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;
}