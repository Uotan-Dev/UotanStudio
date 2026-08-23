namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Represents a single ADB wire message: a fixed 24-byte header followed by an optional payload.
/// <para>表示单条 ADB 线上消息：固定的 24 字节头部加可选负载。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="AdbMessage"/> class.
/// <para>初始化 <see cref="AdbMessage"/> 类的新实例。</para>
/// </remarks>
/// <param name="command">The ADB command identifier. ADB 命令标识。</param>
/// <param name="arg0">The first argument. 第一个参数。</param>
/// <param name="arg1">The second argument. 第二个参数。</param>
/// <param name="payload">The optional payload bytes. 可选的负载字节。</param>
public sealed class AdbMessage(AdbCommand command, uint arg0, uint arg1, byte[]? payload = null)
{
    /// <summary>
    /// Gets the command identifier.
    /// <para>获取命令标识。</para>
    /// </summary>
    public AdbCommand Command { get; } = command;

    /// <summary>
    /// Gets the first argument (local stream id / auth type / protocol version).
    /// <para>获取第一个参数（本地流 id / 认证类型 / 协议版本）。</para>
    /// </summary>
    public uint Arg0 { get; } = arg0;

    /// <summary>
    /// Gets the second argument (remote stream id / maximum payload length).
    /// <para>获取第二个参数（远端流 id / 最大负载长度）。</para>
    /// </summary>
    public uint Arg1 { get; } = arg1;

    /// <summary>
    /// Gets the payload bytes, or null when the message has no payload.
    /// <para>获取负载字节，无负载时为 null。</para>
    /// </summary>
    public byte[]? Payload { get; } = payload;

    /// <summary>
    /// Decodes the payload as UTF-8 text.
    /// <para>将负载解码为 UTF-8 文本。</para>
    /// </summary>
    /// <returns>The payload as a string. 负载的字符串形式。</returns>
    public string PayloadAsString() => Payload is { Length: > 0 } ? System.Text.Encoding.UTF8.GetString(Payload) : string.Empty;
}

/// <summary>
/// Serializes and deserializes ADB wire messages (24-byte header + payload).
/// <para>序列化与反序列化 ADB 线上消息（24 字节头部 + 负载）。</para>
/// </summary>
public static class AdbMessaging
{
    /// <summary>
    /// Computes the ADB CRC (32-bit additive checksum of payload bytes).
    /// <para>计算 ADB CRC（32 位累加校验和）。</para>
    /// </summary>
    /// <param name="payload">The payload bytes. 负载字节。</param>
    /// <returns>The computed CRC value. 计算得到的 CRC 值。</returns>
    public static uint ComputeCrc(byte[]? payload)
    {
        if (payload is null || payload.Length == 0)
        {
            return 0;
        }

        uint crc = 0;
        foreach (var b in payload)
        {
            crc = (crc + b) & 0xFFFFFFFF;
        }

        return crc;
    }

    /// <summary>
    /// Serializes header + payload into one buffer.
    /// <para>将头部与负载拼接为单个缓冲区。</para>
    /// </summary>
    /// <param name="message">The message to serialize. 要序列化的消息。</param>
    /// <returns>The serialized packet bytes. 序列化后的数据包字节。</returns>
    public static byte[] Serialize(AdbMessage message)
    {
        var command = (uint)message.Command;
        var payloadLength = message.Payload is null ? 0u : (uint)message.Payload.Length;
        var payloadCrc = ComputeCrc(message.Payload);

        var packet = new byte[AdbConstants.HeaderSize + (message.Payload?.Length ?? 0)];
        using var ms = new MemoryStream(packet);
        using var bw = new BinaryWriter(ms);
        bw.Write(command);
        bw.Write(message.Arg0);
        bw.Write(message.Arg1);
        bw.Write(payloadLength);
        bw.Write(payloadCrc);
        bw.Write(command ^ 0xFFFFFFFF); // Magic
        if (message.Payload is { Length: > 0 })
        {
            bw.Write(message.Payload);
        }

        return packet;
    }

    /// <summary>
    /// Serializes only the 24-byte header.
    /// <para>仅序列化 24 字节头部。ADB-over-USB 要求头部与负载分两次 bulk-OUT 传输。</para>
    /// </summary>
    /// <param name="message">The message to serialize. 要序列化的消息。</param>
    /// <returns>The 24-byte header. 24 字节头部。</returns>
    public static byte[] SerializeHeader(AdbMessage message)
    {
        var command = (uint)message.Command;
        var payloadLength = message.Payload is null ? 0u : (uint)message.Payload.Length;
        var payloadCrc = ComputeCrc(message.Payload);

        var header = new byte[AdbConstants.HeaderSize];
        using var ms = new MemoryStream(header);
        using var bw = new BinaryWriter(ms);
        bw.Write(command);
        bw.Write(message.Arg0);
        bw.Write(message.Arg1);
        bw.Write(payloadLength);
        bw.Write(payloadCrc);
        bw.Write(command ^ 0xFFFFFFFF); // Magic
        return header;
    }

    /// <summary>
    /// Parses a raw 24-byte header.
    /// <para>解析原始 24 字节头部。</para>
    /// </summary>
    /// <param name="header">The 24-byte header buffer. 24 字节头部缓冲区。</param>
    /// <returns>The parsed header fields. 解析后的头部字段。</returns>
    public static (AdbCommand Command, uint Arg0, uint Arg1, uint PayloadLength, uint PayloadCrc) ParseHeader(byte[] header)
    {
        if (header is null || header.Length != AdbConstants.HeaderSize)
        {
            throw new InvalidDataException($"Invalid ADB header size: {header?.Length}");
        }

        using var ms = new MemoryStream(header);
        using var br = new BinaryReader(ms);
        var command = br.ReadUInt32();
        var arg0 = br.ReadUInt32();
        var arg1 = br.ReadUInt32();
        var payloadLength = br.ReadUInt32();
        var payloadCrc = br.ReadUInt32();
        var magic = br.ReadUInt32();

        return (command ^ 0xFFFFFFFF) != magic
            ? throw new InvalidDataException("Invalid ADB message magic!")
            : ((AdbCommand Command, uint Arg0, uint Arg1, uint PayloadLength, uint PayloadCrc))((AdbCommand)command, arg0, arg1, payloadLength, payloadCrc);
    }

    /// <summary>
    /// Verifies the payload CRC against the header value.
    /// <para>校验负载 CRC 是否与头部值匹配。</para>
    /// </summary>
    /// <param name="payload">The payload bytes. 负载字节。</param>
    /// <param name="expectedCrc">The expected CRC value. 期望的 CRC 值。</param>
    public static void VerifyCrc(byte[]? payload, uint expectedCrc)
    {
        var actual = ComputeCrc(payload);
        if (actual != expectedCrc)
        {
            throw new InvalidDataException($"ADB payload CRC mismatch: expected 0x{expectedCrc:X8}, got 0x{actual:X8}.");
        }
    }

    /// <summary>
    /// Builds the CNXN connect message.
    /// <para>构建 CNXN 连接消息。</para>
    /// </summary>
    public static AdbMessage BuildConnect() =>
        new(AdbCommand.Cnxn, AdbConstants.ProtocolVersion, AdbConstants.MaxPayload,
            System.Text.Encoding.UTF8.GetBytes(AdbConstants.BuildConnectPayload()));

    /// <summary>
    /// Builds an AUTH message.
    /// <para>构建 AUTH 消息。</para>
    /// </summary>
    /// <param name="type">The authentication subtype. 认证子类型。</param>
    /// <param name="data">The authentication data. 认证数据。</param>
    public static AdbMessage BuildAuth(AdbAuthType type, byte[] data) =>
        new(AdbCommand.Auth, (uint)type, 0, data);

    /// <summary>
    /// Builds an OPEN message.
    /// <para>构建 OPEN 消息。</para>
    /// </summary>
    /// <param name="localId">The local stream identifier. 本地流标识。</param>
    /// <param name="destination">The service destination. 服务目标。</param>
    public static AdbMessage BuildOpen(uint localId, string destination) =>
        new(AdbCommand.Open, localId, 0, System.Text.Encoding.UTF8.GetBytes($"{destination}\0"));

    /// <summary>
    /// Builds a WRTE message.
    /// <para>构建 WRTE 消息。</para>
    /// </summary>
    /// <param name="localId">The local stream identifier. 本地流标识。</param>
    /// <param name="remoteId">The remote stream identifier. 远端流标识。</param>
    /// <param name="data">The data to write. 要写入的数据。</param>
    public static AdbMessage BuildWrite(uint localId, uint remoteId, byte[] data) =>
        new(AdbCommand.Wrte, localId, remoteId, data);

    /// <summary>
    /// Builds a CLSE message.
    /// <para>构建 CLSE 消息。</para>
    /// </summary>
    /// <param name="localId">The local stream identifier. 本地流标识。</param>
    /// <param name="remoteId">The remote stream identifier. 远端流标识。</param>
    public static AdbMessage BuildClose(uint localId, uint remoteId) =>
        new(AdbCommand.Clse, localId, remoteId);

    /// <summary>
    /// Builds an OKAY message.
    /// <para>构建 OKAY 消息。</para>
    /// </summary>
    /// <param name="localId">The local stream identifier. 本地流标识。</param>
    /// <param name="remoteId">The remote stream identifier. 远端流标识。</param>
    public static AdbMessage BuildOkay(uint localId, uint remoteId) =>
        new(AdbCommand.Okay, localId, remoteId);
}