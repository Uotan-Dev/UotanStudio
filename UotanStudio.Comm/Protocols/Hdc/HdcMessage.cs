using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// HDC payload header for TCP/server channels (packed, big-endian).
/// <para>HDC TCP/服务通道包头（紧凑排列，大端序）。</para>
/// </summary>
/// <remarks>
/// Wire layout (11 bytes): flag[2] + option[1] + sessionId[4] + dataSize[4].
/// <para>线格式（11 字节）：flag[2] + option[1] + sessionId[4] + dataSize[4]。</para>
/// </remarks>
public readonly struct HdcPayloadHead
{
    /// <summary>Packet flag byte 0 ('H'). 包标识字节 0（'H'）。</summary>
    public byte Flag0 { get; init; }
    /// <summary>Packet flag byte 1 ('W'). 包标识字节 1（'W'）。</summary>
    public byte Flag1 { get; init; }
    /// <summary>Option byte (1 = header). 选项字节（1 = 头部）。</summary>
    public byte Option { get; init; }
    /// <summary>Session identifier. 会话标识。</summary>
    public uint SessionId { get; init; }
    /// <summary>Data size in bytes following this header. 跟随此头部的数据字节数。</summary>
    public uint DataSize { get; init; }

    /// <summary>
    /// Fixed header size in bytes (11).
    /// <para>头部固定字节大小（11）。</para>
    /// </summary>
    public const int Size = HdcConstants.HeaderSize;

    /// <summary>
    /// Serializes this header into the destination span in big-endian order.
    /// <para>以大端序将此头部序列化到目标 span。</para>
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
    /// Parses a <see cref="HdcPayloadHead"/> from the given span.
    /// <para>从给定 span 解析 <see cref="HdcPayloadHead"/>。</para>
    /// </summary>
    /// <param name="data">Source span of at least 11 bytes. 至少 11 字节的源 span。</param>
    /// <returns>The parsed header. 解析后的头部。</returns>
    /// <exception cref="InvalidDataException">Thrown when the span is too short. span 长度不足时抛出。</exception>
    public static HdcPayloadHead Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new InvalidDataException($"Invalid HDC payload header size: {data.Length}, expected {Size}.");
        }

        return new HdcPayloadHead
        {
            Flag0 = data[0],
            Flag1 = data[1],
            Option = data[2],
            SessionId = BinaryPrimitives.ReadUInt32BigEndian(data[3..]),
            DataSize = BinaryPrimitives.ReadUInt32BigEndian(data[7..]),
        };
    }
}

/// <summary>
/// HDC USB fragment header (packed, big-endian).
/// <para>HDC USB 分片包头（紧凑排列，大端序）。</para>
/// </summary>
/// <remarks>
/// Wire layout (11 bytes): flag[2] + option[1] + sessionId[4] + dataSize[4].
/// <para>线格式（11 字节）：flag[2] + option[1] + sessionId[4] + dataSize[4]。</para>
/// </remarks>
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

/// <summary>
/// Per-packet channel metadata carried after each payload header (packed, big-endian).
/// <para>每个包的通道元数据，紧跟在负载头部之后（紧凑排列，大端序）。</para>
/// </summary>
/// <remarks>
/// Wire layout (14 bytes): channelId[4] + commandFlag[2] + checkSum[4] + vcode[4].
/// <para>线格式（14 字节）：channelId[4] + commandFlag[2] + checkSum[4] + vcode[4]。</para>
/// </remarks>
public readonly struct HdcPayloadProtect
{
    /// <summary>Channel identifier. 通道标识。</summary>
    public uint ChannelId { get; init; }
    /// <summary>Command flag (HdcCommand value). 命令标志（HdcCommand 值）。</summary>
    public ushort CommandFlag { get; init; }
    /// <summary>CRC-32 checksum of the payload. 负载的 CRC-32 校验和。</summary>
    public uint CheckSum { get; init; }
    /// <summary>Version / verification code. 版本/验证码。</summary>
    public uint VCode { get; init; }

    /// <summary>
    /// Fixed protect-structure size in bytes (14).
    /// <para>保护结构固定字节大小（14）。</para>
    /// </summary>
    public const int Size = 14;

    /// <summary>
    /// Serializes this protect structure into the destination span in big-endian order.
    /// <para>以大端序将此保护结构序列化到目标 span。</para>
    /// </summary>
    /// <param name="destination">Target span of at least 14 bytes. 至少 14 字节的目标 span。</param>
    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination[0..], ChannelId);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], CommandFlag);
        BinaryPrimitives.WriteUInt32BigEndian(destination[6..], CheckSum);
        BinaryPrimitives.WriteUInt32BigEndian(destination[10..], VCode);
    }

    /// <summary>
    /// Parses a <see cref="HdcPayloadProtect"/> from the given span.
    /// <para>从给定 span 解析 <see cref="HdcPayloadProtect"/>。</para>
    /// </summary>
    /// <param name="data">Source span of at least 14 bytes. 至少 14 字节的源 span。</param>
    /// <returns>The parsed protect structure. 解析后的保护结构。</returns>
    /// <exception cref="InvalidDataException">Thrown when the span is too short. span 长度不足时抛出。</exception>
    public static HdcPayloadProtect Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new InvalidDataException($"Invalid HDC payload protect size: {data.Length}, expected {Size}.");
        }

        return new HdcPayloadProtect
        {
            ChannelId = BinaryPrimitives.ReadUInt32BigEndian(data[0..]),
            CommandFlag = BinaryPrimitives.ReadUInt16BigEndian(data[4..]),
            CheckSum = BinaryPrimitives.ReadUInt32BigEndian(data[6..]),
            VCode = BinaryPrimitives.ReadUInt32BigEndian(data[10..]),
        };
    }
}

/// <summary>
/// HDC session-level handshake message.
/// <para>HDC 会话级握手消息。</para>
/// </summary>
/// <remarks>
/// Contains authentication type, session identity, connect key and optional crypto material.
/// <para>包含认证类型、会话标识、连接密钥和可选的加密材料。</para>
/// </remarks>
public struct HdcSessionHandShake
{
    /// <summary>Protocol banner ("OHOS HDC"). 协议横幅（"OHOS HDC"）。</summary>
    public string Banner { get; set; }

    /// <summary>Authentication verification type. 认证验证类型。</summary>
    public HdcAuthVerifyType AuthType { get; set; }

    /// <summary>Session identifier. 会话标识。</summary>
    public uint SessionId { get; set; }

    /// <summary>Connect key (≤ 32 bytes). 连接密钥（≤ 32 字节）。</summary>
    public string ConnectKey { get; set; }

    /// <summary>
    /// Authentication buffer (RSA public key or signature), may be null.
    /// <para>认证缓冲区（RSA 公钥或签名），可为 null。</para>
    /// </summary>
    public byte[]? Buf { get; set; }

    /// <summary>Protocol version. 协议版本。</summary>
    public uint Version { get; set; }

    /// <summary>
    /// Serializes the session handshake to a byte array (big-endian, length-prefixed strings).
    /// <para>将会话握手序列化为字节数组（大端序，长度前缀字符串）。</para>
    /// </summary>
    /// <param name="authType">Auth type. 认证类型。</param>
    /// <param name="sessionId">Session id. 会话标识。</param>
    /// <param name="connectKey">Connect key. 连接密钥。</param>
    /// <param name="authBuf">Auth buffer (nullable). 认证缓冲区（可为 null）。</param>
    /// <param name="version">Protocol version. 协议版本。</param>
    /// <returns>Serialized bytes. 序列化后的字节。</returns>
    public static byte[] Serialize(
        HdcAuthVerifyType authType,
        uint sessionId,
        string connectKey,
        byte[]? authBuf,
        uint version)
    {
        var bannerBytes = Encoding.UTF8.GetBytes(HdcConstants.HandshakeMessage);
        var keyBytes = Encoding.UTF8.GetBytes(connectKey);
        var bufLen = authBuf?.Length ?? 0;

        // banner-len(4) + banner + authType(1) + sessionId(4)
        // + key-len(4) + key + buf-len(4) + buf + version(4)
        var totalSize = 4 + bannerBytes.Length + 1 + 4
                      + 4 + keyBytes.Length
                      + 4 + bufLen
                      + 4;
        var result = new byte[totalSize];
        var span = result.AsSpan();
        int offset = 0;

        // Banner (length-prefixed)
        BinaryPrimitives.WriteUInt32BigEndian(span[offset..], (uint)bannerBytes.Length);
        offset += 4;
        bannerBytes.CopyTo(result, offset);
        offset += bannerBytes.Length;

        // AuthType
        result[offset] = (byte)authType;
        offset += 1;

        // SessionId
        BinaryPrimitives.WriteUInt32BigEndian(span[offset..], sessionId);
        offset += 4;

        // ConnectKey (length-prefixed)
        BinaryPrimitives.WriteUInt32BigEndian(span[offset..], (uint)keyBytes.Length);
        offset += 4;
        keyBytes.CopyTo(result, offset);
        offset += keyBytes.Length;

        // Buf (length-prefixed)
        BinaryPrimitives.WriteUInt32BigEndian(span[offset..], (uint)bufLen);
        offset += 4;
        if (authBuf is { Length: > 0 })
        {
            authBuf.CopyTo(result, offset);
            offset += authBuf.Length;
        }

        // Version
        BinaryPrimitives.WriteUInt32BigEndian(span[offset..], version);

        return result;
    }

    /// <summary>
    /// Deserializes a session handshake from the given byte span.
    /// <para>从给定字节 span 反序列化会话握手。</para>
    /// </summary>
    /// <param name="data">Source data. 源数据。</param>
    /// <returns>The parsed handshake. 解析后的握手。</returns>
    /// <exception cref="InvalidDataException">Thrown on malformed data. 数据格式错误时抛出。</exception>
    public static HdcSessionHandShake Parse(ReadOnlySpan<byte> data)
    {
        int offset = 0;

        // Banner
        if (data.Length < offset + 4)
            throw new InvalidDataException("HDC handshake data too short for banner length.");
        uint bannerLen = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        offset += 4;
        if (data.Length < offset + (int)bannerLen)
            throw new InvalidDataException("HDC handshake data too short for banner.");
        string banner = Encoding.UTF8.GetString(data.Slice(offset, (int)bannerLen));
        offset += (int)bannerLen;

        // AuthType
        if (data.Length < offset + 1)
            throw new InvalidDataException("HDC handshake data too short for auth type.");
        var authType = (HdcAuthVerifyType)data[offset];
        offset += 1;

        // SessionId
        if (data.Length < offset + 4)
            throw new InvalidDataException("HDC handshake data too short for session id.");
        uint sessionId = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        offset += 4;

        // ConnectKey
        if (data.Length < offset + 4)
            throw new InvalidDataException("HDC handshake data too short for connect-key length.");
        uint keyLen = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        offset += 4;
        if (data.Length < offset + (int)keyLen)
            throw new InvalidDataException("HDC handshake data too short for connect-key.");
        string connectKey = Encoding.UTF8.GetString(data.Slice(offset, (int)keyLen));
        offset += (int)keyLen;

        // Buf
        if (data.Length < offset + 4)
            throw new InvalidDataException("HDC handshake data too short for buf length.");
        uint bufLen = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
        offset += 4;
        if (data.Length < offset + (int)bufLen)
            throw new InvalidDataException("HDC handshake data too short for buf.");
        byte[]? buf = bufLen > 0 ? data.Slice(offset, (int)bufLen).ToArray() : null;
        offset += (int)bufLen;

        // Version
        if (data.Length < offset + 4)
            throw new InvalidDataException("HDC handshake data too short for version.");
        uint version = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);

        return new HdcSessionHandShake
        {
            Banner = banner,
            AuthType = authType,
            SessionId = sessionId,
            ConnectKey = connectKey,
            Buf = buf,
            Version = version,
        };
    }
}

/// <summary>
/// HDC channel-level handshake (sent when opening a new channel).
/// <para>HDC 通道级握手（打开新通道时发送）。</para>
/// </summary>
/// <remarks>
/// Wire layout: banner[12] + connect-key (length-prefixed UTF-8).
/// <para>线格式：banner[12] + 连接密钥（长度前缀 UTF-8）。</para>
/// </remarks>
public struct HdcChannelHandShake
{
    /// <summary>
    /// Fixed 12-byte banner ("OHOS HDC" + 4 null bytes).
    /// <para>固定 12 字节横幅（"OHOS HDC" + 4 个空字节）。</para>
    /// </summary>
    public byte[] Banner { get; set; }

    /// <summary>Connect key. 连接密钥。</summary>
    public string ConnectKey { get; set; }

    /// <summary>Fixed banner size in bytes. 固定横幅字节大小。</summary>
    public const int BannerSize = 12;

    /// <summary>
    /// Creates the default 12-byte banner ("OHOS HDC\0\0\0\0").
    /// <para>创建默认的 12 字节横幅（"OHOS HDC\0\0\0\0"）。</para>
    /// </summary>
    public static byte[] CreateDefaultBanner()
    {
        var banner = new byte[BannerSize];
        var msg = Encoding.UTF8.GetBytes(HdcConstants.HandshakeMessage);
        msg.CopyTo(banner, 0);
        // Remaining bytes are already zero-initialized.
        return banner;
    }

    /// <summary>
    /// Serializes the channel handshake to a byte array (big-endian, length-prefixed connect key).
    /// <para>将会话通道握手序列化为字节数组（大端序，长度前缀连接密钥）。</para>
    /// </summary>
    /// <param name="connectKey">Connect key. 连接密钥。</param>
    /// <returns>Serialized bytes. 序列化后的字节。</returns>
    public static byte[] Serialize(string connectKey)
    {
        var banner = CreateDefaultBanner();
        var keyBytes = Encoding.UTF8.GetBytes(connectKey);

        // banner(12) + key-len(4) + key
        var result = new byte[BannerSize + 4 + keyBytes.Length];
        var span = result.AsSpan();

        banner.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32BigEndian(span[BannerSize..], (uint)keyBytes.Length);
        keyBytes.CopyTo(result, BannerSize + 4);

        return result;
    }

    /// <summary>
    /// Deserializes a channel handshake from the given byte span.
    /// <para>从给定字节 span 反序列化通道握手。</para>
    /// </summary>
    /// <param name="data">Source data. 源数据。</param>
    /// <returns>The parsed channel handshake. 解析后的通道握手。</returns>
    /// <exception cref="InvalidDataException">Thrown on malformed data. 数据格式错误时抛出。</exception>
    public static HdcChannelHandShake Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < BannerSize + 4)
            throw new InvalidDataException("HDC channel handshake data too short.");

        var banner = data[..BannerSize].ToArray();

        uint keyLen = BinaryPrimitives.ReadUInt32BigEndian(data[BannerSize..]);
        if (data.Length < BannerSize + 4 + (int)keyLen)
            throw new InvalidDataException("HDC channel handshake data too short for connect-key.");

        string connectKey = Encoding.UTF8.GetString(data.Slice(BannerSize + 4, (int)keyLen));

        return new HdcChannelHandShake
        {
            Banner = banner,
            ConnectKey = connectKey,
        };
    }
}

/// <summary>
/// Represents a single HDC wire message: command, channel/session IDs, and optional payload.
/// <para>表示单条 HDC 线上消息：命令、通道/会话标识及可选负载。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="HdcMessage"/> record.
/// <para>初始化 <see cref="HdcMessage"/> 记录的新实例。</para>
/// </remarks>
/// <param name="Command">The HDC command identifier. HDC 命令标识。</param>
/// <param name="ChannelId">The channel identifier. 通道标识。</param>
/// <param name="SessionId">The session identifier. 会话标识。</param>
/// <param name="Payload">The optional payload bytes. 可选的负载字节。</param>
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

/// <summary>
/// Serializes and deserializes HDC wire messages using big-endian (network) byte order.
/// <para>使用大端序（网络序）序列化与反序列化 HDC 线上消息。</para>
/// </summary>
/// <remarks>
/// All multi-byte integers are written in big-endian order (AOT compatible, no reflection).
/// <para>所有多字节整数以大端序写入（AOT 兼容，无反射）。</para>
/// </remarks>
public static class HdcMessaging
{
    /// <summary>
    /// Computes CRC-32 (ISO-HDLC / IEEE 802.3, polynomial 0xEDB88320) over the payload.
    /// <para>计算负载的 CRC-32（ISO-HDLC / IEEE 802.3，多项式 0xEDB88320）。</para>
    /// </summary>
    /// <param name="payload">The payload bytes (may be null). 负载字节（可为 null）。</param>
    /// <returns>The CRC-32 value, or 0 for empty input. CRC-32 值，空输入时返回 0。</returns>
    public static uint ComputeCrc(byte[]? payload)
    {
        if (payload is null || payload.Length == 0)
        {
            return 0;
        }

        uint crc = 0xFFFFFFFF;
        foreach (var b in payload)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0
                    ? (crc >> 1) ^ 0xEDB88320u
                    : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// Serializes a full HDC message (PayloadHead + PayloadProtect + payload) in big-endian order.
    /// <para>以大端序序列化完整的 HDC 消息（PayloadHead + PayloadProtect + 负载）。</para>
    /// </summary>
    /// <param name="msg">The message to serialize. 要序列化的消息。</param>
    /// <param name="sessionId">The session identifier for the header. 头部使用的会话标识。</param>
    /// <returns>The serialized packet bytes. 序列化后的数据包字节。</returns>
    public static byte[] Serialize(HdcMessage msg, uint sessionId)
    {
        var payloadLength = msg.Payload is null ? 0u : (uint)msg.Payload.Length;
        var dataSize = (uint)HdcPayloadProtect.Size + payloadLength;
        var totalSize = HdcPayloadHead.Size + (int)dataSize;
        var packet = new byte[totalSize];
        var span = packet.AsSpan();

        // PayloadHead
        new HdcPayloadHead
        {
            Flag0 = (byte)HdcConstants.PacketFlag[0],
            Flag1 = (byte)HdcConstants.PacketFlag[1],
            Option = (byte)HdcUsbOption.Header,
            SessionId = sessionId,
            DataSize = dataSize,
        }.WriteTo(span[..HdcPayloadHead.Size]);

        // PayloadProtect
        new HdcPayloadProtect
        {
            ChannelId = msg.ChannelId,
            CommandFlag = (ushort)msg.Command,
            CheckSum = ComputeCrc(msg.Payload),
            VCode = 0,
        }.WriteTo(span.Slice(HdcPayloadHead.Size, HdcPayloadProtect.Size));

        // Payload data
        if (msg.Payload is { Length: > 0 })
        {
            msg.Payload.CopyTo(packet, HdcPayloadHead.Size + HdcPayloadProtect.Size);
        }

        return packet;
    }

    /// <summary>
    /// Serializes only the header portion (PayloadHead + PayloadProtect, no payload data).
    /// <para>仅序列化头部（PayloadHead + PayloadProtect，不含负载数据）。
    /// HDC USB 要求头部与负载分两次传输。</para>
    /// </summary>
    /// <param name="msg">The message to serialize the header of. 要序列化头部的消息。</param>
    /// <param name="sessionId">The session identifier for the header. 头部使用的会话标识。</param>
    /// <returns>The 25-byte header (11 + 14). 25 字节头部（11 + 14）。</returns>
    public static byte[] SerializeHeader(HdcMessage msg, uint sessionId)
    {
        var payloadLength = msg.Payload is null ? 0u : (uint)msg.Payload.Length;
        var dataSize = (uint)HdcPayloadProtect.Size + payloadLength;

        var header = new byte[HdcPayloadHead.Size + HdcPayloadProtect.Size];
        var span = header.AsSpan();

        // PayloadHead
        new HdcPayloadHead
        {
            Flag0 = (byte)HdcConstants.PacketFlag[0],
            Flag1 = (byte)HdcConstants.PacketFlag[1],
            Option = (byte)HdcUsbOption.Header,
            SessionId = sessionId,
            DataSize = dataSize,
        }.WriteTo(span[..HdcPayloadHead.Size]);

        // PayloadProtect
        new HdcPayloadProtect
        {
            ChannelId = msg.ChannelId,
            CommandFlag = (ushort)msg.Command,
            CheckSum = ComputeCrc(msg.Payload),
            VCode = 0,
        }.WriteTo(span.Slice(HdcPayloadHead.Size, HdcPayloadProtect.Size));

        return header;
    }

    /// <summary>
    /// Parses a raw 11-byte <see cref="HdcPayloadHead"/>.
    /// <para>解析原始 11 字节 <see cref="HdcPayloadHead"/>。</para>
    /// </summary>
    /// <param name="header">The 11-byte header buffer. 11 字节头部缓冲区。</param>
    /// <returns>The parsed header. 解析后的头部。</returns>
    public static HdcPayloadHead ParseHeader(byte[] header) =>
        HdcPayloadHead.Parse(header);

    /// <summary>
    /// Parses a raw 14-byte <see cref="HdcPayloadProtect"/>.
    /// <para>解析原始 14 字节 <see cref="HdcPayloadProtect"/>。</para>
    /// </summary>
    /// <param name="data">The 14-byte protect buffer. 14 字节保护结构缓冲区。</param>
    /// <returns>The parsed protect structure. 解析后的保护结构。</returns>
    public static HdcPayloadProtect ParsePayloadProtect(byte[] data) =>
        HdcPayloadProtect.Parse(data);

    /// <summary>
    /// Builds a session-level connect handshake <see cref="HdcMessage"/>.
    /// <para>构建会话级连接握手 <see cref="HdcMessage"/>。</para>
    /// </summary>
    /// <param name="authType">Authentication type. 认证类型。</param>
    /// <param name="sessionId">Session identifier. 会话标识。</param>
    /// <param name="connectKey">Connect key (≤ 32 bytes). 连接密钥（≤ 32 字节）。</param>
    /// <param name="authBuf">Authentication buffer (public key / signature, nullable). 认证缓冲区（公钥/签名，可为 null）。</param>
    /// <param name="version">Protocol version. 协议版本。</param>
    /// <returns>The handshake message. 握手消息。</returns>
    public static HdcMessage BuildConnectHandshake(
        HdcAuthVerifyType authType,
        uint sessionId,
        string connectKey,
        byte[]? authBuf,
        uint version)
    {
        var payload = HdcSessionHandShake.Serialize(authType, sessionId, connectKey, authBuf, version);
        return new HdcMessage(HdcCommand.Handshake, 0, sessionId, payload);
    }

    /// <summary>
    /// Builds a channel-level handshake <see cref="HdcMessage"/>.
    /// <para>构建通道级握手 <see cref="HdcMessage"/>。</para>
    /// </summary>
    /// <param name="connectKey">Connect key. 连接密钥。</param>
    /// <returns>The channel handshake message. 通道握手消息。</returns>
    public static HdcMessage BuildChannelHandshake(string connectKey)
    {
        var payload = HdcChannelHandShake.Serialize(connectKey);
        return new HdcMessage(HdcCommand.Handshake, 0, 0, payload);
    }

    /// <summary>
    /// Builds a generic command <see cref="HdcMessage"/>.
    /// <para>构建通用命令 <see cref="HdcMessage"/>。</para>
    /// </summary>
    /// <param name="cmd">The command identifier. 命令标识。</param>
    /// <param name="channelId">The target channel. 目标通道。</param>
    /// <param name="payload">Optional payload (nullable). 可选负载（可为 null）。</param>
    /// <returns>The command message. 命令消息。</returns>
    public static HdcMessage BuildCommand(HdcCommand cmd, uint channelId, byte[]? payload) =>
        new(cmd, channelId, 0, payload);

    /// <summary>
    /// Builds a heartbeat keep-alive <see cref="HdcMessage"/>.
    /// <para>构建心跳保活 <see cref="HdcMessage"/>。</para>
    /// </summary>
    /// <param name="channelId">The channel to send the heartbeat on. 发送心跳的通道。</param>
    /// <returns>The heartbeat message. 心跳消息。</returns>
    public static HdcMessage BuildHeartbeat(uint channelId) =>
        new(HdcCommand.HeartbeatMsg, channelId, 0);
}
