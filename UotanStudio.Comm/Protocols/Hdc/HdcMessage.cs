using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>Upstream packed USBHead: UB, option, BE32 session ID and BE32 block length.</summary>
public readonly struct HdcUsbHead
{
    /// <summary>USB flag byte 0 ('U'). USB 标识字节 0（'U'）。</summary>
    public byte Flag0 { get; init; }
    /// <summary>USB flag byte 1 ('B'). USB 标识字节 1（'B'）。</summary>
    public byte Flag1 { get; init; }
    /// <summary>Option byte (1 = header, 2 = reset). 选项字节（1 = 头部，2 = 重置）。</summary>
    public byte Option { get; init; }
    /// <summary>Session identifier. 会话标识。</summary>
    public uint SessionId { get; init; }
    /// <summary>Data size in bytes following this header. 跟随此头部的数据字节数。</summary>
    public uint DataSize { get; init; }

    /// <summary>
    /// Fixed header size in bytes (11).
    /// <para>头部固定字节大小（11）。</para>
    /// </summary>
    public const int Size = HdcConstants.UsbHeaderSize;

    /// <summary>
    /// Serializes this USB header into the destination span in big-endian order.
    /// <para>以大端序将此 USB 头部序列化到目标 span。</para>
    /// </summary>
    /// <param name="destination">Target span of at least 11 bytes. 至少 11 字节的目标 span。</param>
    public void WriteTo(Span<byte> destination)
    {
        destination[0] = Flag0;
        destination[1] = Flag1;
        destination[2] = Option;
        BinaryPrimitives.WriteUInt32BigEndian(destination[3..], SessionId);
        BinaryPrimitives.WriteUInt32BigEndian(destination[7..], DataSize);
    }

    /// <summary>
    /// Parses a <see cref="HdcUsbHead"/> from the given span.
    /// <para>从给定 span 解析 <see cref="HdcUsbHead"/>。</para>
    /// </summary>
    /// <param name="data">Source span of at least 11 bytes. 至少 11 字节的源 span。</param>
    /// <returns>The parsed USB header. 解析后的 USB 头部。</returns>
    /// <exception cref="InvalidDataException">Thrown when the span is too short. span 长度不足时抛出。</exception>
    public static HdcUsbHead Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new InvalidDataException($"Invalid HDC USB header size: {data.Length}, expected {Size}.");
        }

        return new HdcUsbHead
        {
            Flag0 = data[0],
            Flag1 = data[1],
            Option = data[2],
            SessionId = BinaryPrimitives.ReadUInt32BigEndian(data[3..]),
            DataSize = BinaryPrimitives.ReadUInt32BigEndian(data[7..]),
        };
    }
}

/// <summary>A daemon command with shared channel identity and payload.</summary>
public sealed record HdcMessage(
    HdcCommand Command,
    uint ChannelId,
    uint SessionId,
    byte[]? Payload = null)
{
    /// <summary>
    /// Decodes the payload as UTF-8 text.
    /// <para>将负载解码为 UTF-8 文本。</para>
    /// </summary>
    /// <returns>The payload as a string, or empty when absent. 负载的字符串形式，无负载时为空串。</returns>
    public string PayloadAsString() =>
        Payload is { Length: > 0 } ? Encoding.UTF8.GetString(Payload) : string.Empty;
}

/// <summary>Device message helpers. Host-channel framing belongs to HdcServerClient.</summary>
public static class HdcMessaging
{
    /// <summary>Serializes an upstream daemon packet; session identity is carried by USBHead.</summary>
    public static byte[] Serialize(HdcMessage message, uint sessionId) => HdcDaemonCodec.Encode(message);

    /// <summary>Constructs a native command.</summary>
    public static HdcMessage BuildCommand(HdcCommand command, uint channelId, byte[]? payload)
        => new(command, channelId, 0, payload);
}