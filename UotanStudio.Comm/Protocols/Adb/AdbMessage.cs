using System.Buffers.Binary;
using UotanStudio.Hash.Sums;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Represents a single ADB wire message: a fixed 24-byte header followed by an optional payload.
/// <para>表示单条 ADB 线上消息：固定的 24 字节头部加可选负载。</para>
/// </summary>
/// <param name="command">The ADB command identifier. <para>ADB 命令标识。</para></param>
/// <param name="arg0">The first argument. <para>第一个参数。</para></param>
/// <param name="arg1">The second argument. <para>第二个参数。</para></param>
/// <param name="payload">The optional payload bytes. <para>可选的负载字节。</para></param>
internal sealed class AdbMessage(AdbCommand command, uint arg0, uint arg1, byte[]? payload = null)
{
    /// <summary>
    /// Gets the command identifier.
    /// <para>获取命令标识。</para>
    /// </summary>
    /// <value>The command identifier carried in the header. <para>头部携带的命令标识。</para></value>
    public AdbCommand Command { get; } = command;

    /// <summary>
    /// Gets the first argument (local stream id / auth type / protocol version).
    /// <para>获取第一个参数（本地流 id / 认证类型 / 协议版本）。</para>
    /// </summary>
    /// <value>The first argument word. <para>第一个参数值。</para></value>
    public uint Arg0 { get; } = arg0;

    /// <summary>
    /// Gets the second argument (remote stream id / maximum payload length).
    /// <para>获取第二个参数（远端流 id / 最大负载长度）。</para>
    /// </summary>
    /// <value>The second argument word. <para>第二个参数值。</para></value>
    public uint Arg1 { get; } = arg1;

    /// <summary>
    /// Gets the payload bytes, or null when the message has no payload.
    /// <para>获取负载字节，无负载时为 null。</para>
    /// </summary>
    /// <value>The payload bytes, or <c>null</c> when absent. <para>负载字节，缺失时为 <c>null</c>。</para></value>
    public byte[]? Payload { get; } = payload;

    /// <summary>
    /// Decodes the payload as UTF-8 text.
    /// <para>将负载解码为 UTF-8 文本。</para>
    /// </summary>
    /// <returns>The payload decoded as UTF-8, or an empty string. <para>以 UTF-8 解码的负载，缺失时为空字符串。</para></returns>
    public string PayloadAsString() => Payload is { Length: > 0 } ? System.Text.Encoding.UTF8.GetString(Payload) : string.Empty;
}

/// <summary>
/// Serializes and deserializes ADB wire messages (24-byte header + payload).
/// <para>序列化与反序列化 ADB 线上消息（24 字节头部 + 负载）。</para>
/// </summary>
internal static class AdbMessaging
{
    /// <summary>
    /// Computes the ADB CRC (32-bit additive checksum of payload bytes).
    /// <para>计算 ADB CRC（32 位累加校验和）。</para>
    /// </summary>
    /// <param name="payload">The payload bytes to sum. <para>待累加的负载字节。</para></param>
    /// <returns>The 32-bit additive checksum. <para>32 位累加校验和。</para></returns>
    public static uint ComputeCrc(byte[]? payload)
    {
        if (payload is null || payload.Length == 0)
        {
            return 0;
        }

        return ByteSum32.Compute(payload);
    }

    /// <summary>
    /// Serializes header + payload into one buffer.
    /// <para>将头部与负载拼接为单个缓冲区。</para>
    /// </summary>
    /// <param name="message">The message to serialize. <para>要序列化的消息。</para></param>
    /// <returns>The header followed by the payload bytes. <para>头部及其后的负载字节。</para></returns>
    public static byte[] Serialize(AdbMessage message)
    {
        var command = (uint)message.Command;
        var payloadLength = message.Payload is null ? 0u : (uint)message.Payload.Length;
        var payloadCrc = ComputeCrc(message.Payload);

        var packet = new byte[AdbConstants.HeaderSize + (message.Payload?.Length ?? 0)];
        WriteHeader(packet, command, message.Arg0, message.Arg1, payloadLength, payloadCrc);
        if (message.Payload is { Length: > 0 })
        {
            message.Payload.AsSpan().CopyTo(packet.AsSpan(AdbConstants.HeaderSize));
        }

        return packet;
    }

    /// <summary>
    /// Serializes only the 24-byte header.
    /// <para>仅序列化 24 字节头部。</para>
    /// </summary>
    /// <param name="message">The message whose header is written. <para>要写出头部的消息。</para></param>
    /// <returns>The 24-byte header buffer. <para>24 字节头部缓冲区。</para></returns>
    public static byte[] SerializeHeader(AdbMessage message)
    {
        var header = new byte[AdbConstants.HeaderSize];
        WriteHeader(header, (uint)message.Command, message.Arg0, message.Arg1,
            message.Payload is null ? 0u : (uint)message.Payload.Length, ComputeCrc(message.Payload));
        return header;
    }

    private static void WriteHeader(Span<byte> destination, uint command, uint arg0, uint arg1, uint payloadLength, uint payloadCrc)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, command);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], arg0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], arg1);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], payloadCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], command ^ 0xFFFFFFFF);
    }

    /// <summary>
    /// Parses a raw 24-byte header.
    /// <para>解析原始 24 字节头部。</para>
    /// </summary>
    /// <param name="header">The raw 24-byte header buffer. <para>原始 24 字节头部缓冲区。</para></param>
    /// <returns>The decoded header fields. <para>解码后的头部字段。</para></returns>
    public static (AdbCommand Command, uint Arg0, uint Arg1, uint PayloadLength, uint PayloadCrc) ParseHeader(byte[] header)
    {
        if (header is null || header.Length != AdbConstants.HeaderSize)
        {
            throw new InvalidDataException($"Invalid ADB header size: {header?.Length}");
        }

        var fields = header.AsSpan();
        var command = BinaryPrimitives.ReadUInt32LittleEndian(fields);
        var arg0 = BinaryPrimitives.ReadUInt32LittleEndian(fields[4..]);
        var arg1 = BinaryPrimitives.ReadUInt32LittleEndian(fields[8..]);
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(fields[12..]);
        var payloadCrc = BinaryPrimitives.ReadUInt32LittleEndian(fields[16..]);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(fields[20..]);

        if ((command ^ 0xFFFFFFFF) != magic)
        {
            // The decoded words identify the desync class without another round trip: a plausible command with a
            // wrong magic means the frame started mid-stream, while arbitrary bytes mean another reader (an adb
            // server or a second host handle) is splitting the same inbound stream.
            // 解码字段可在不追加往返的情况下区分失步类型：命令合理而 magic 不符说明帧从中途开始读取，完全随机的
            // 字节说明另一个读取方（adb server 或第二个主机句柄）正在分割同一条入站流。
            throw new InvalidDataException(
                $"Invalid ADB message magic! command=0x{command:X8}, magic=0x{magic:X8}, " +
                $"expected=0x{command ^ 0xFFFFFFFF:X8}, first8={Convert.ToHexString(fields[..8])}");
        }

        return ((AdbCommand Command, uint Arg0, uint Arg1, uint PayloadLength, uint PayloadCrc))(
            (AdbCommand)command, arg0, arg1, payloadLength, payloadCrc);
    }

    /// <summary>
    /// Validates the header CRC field of an inbound message. Peers send either the reserved
    /// zero value or the legacy additive byte sum, and only those two values are accepted.
    /// <para>校验入站消息头部的 CRC 字段。对端只会发送保留的零值或旧式字节累加和，仅这两种取值被接受。</para>
    /// </summary>
    /// <param name="payload">The payload bytes to checksum. <para>待校验的负载字节。</para></param>
    /// <param name="expectedCrc">The CRC field as received. <para>收到的 CRC 字段值。</para></param>
    /// <exception cref="InvalidDataException">The header CRC matches neither the reserved zero value nor the legacy additive checksum.
    /// <para>头部 CRC 既不是保留的零值也不是旧式累加校验和。</para></exception>
    public static void VerifyCrc(byte[]? payload, uint expectedCrc)
    {
        if (expectedCrc == 0)
        {
            return;
        }

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
    /// <returns>The constructed CNXN message. <para>构建出的 CNXN 消息。</para></returns>
    public static AdbMessage BuildConnect() =>
        new(AdbCommand.Cnxn, AdbConstants.ProtocolVersion, AdbConstants.MaxPayload,
            System.Text.Encoding.UTF8.GetBytes(AdbConstants.BuildConnectPayload()));

    /// <summary>
    /// Builds an AUTH message.
    /// <para>构建 AUTH 消息。</para>
    /// </summary>
    /// <param name="type">The authentication subtype. <para>认证子类型。</para></param>
    /// <param name="data">The authentication data. <para>认证数据。</para></param>
    /// <returns>The constructed AUTH message. <para>构建出的 AUTH 消息。</para></returns>
    public static AdbMessage BuildAuth(AdbAuthType type, byte[] data) =>
        new(AdbCommand.Auth, (uint)type, 0, data);

    /// <summary>
    /// Builds an OPEN message.
    /// <para>构建 OPEN 消息。</para>
    /// </summary>
    /// <param name="localId">The local stream identifier. <para>本地流标识。</para></param>
    /// <param name="destination">The service destination. <para>服务目标。</para></param>
    /// <returns>The constructed OPEN message. <para>构建出的 OPEN 消息。</para></returns>
    public static AdbMessage BuildOpen(uint localId, string destination) =>
        new(AdbCommand.Open, localId, 0, System.Text.Encoding.UTF8.GetBytes($"{destination}\0"));

    /// <summary>
    /// Builds a WRTE message.
    /// <para>构建 WRTE 消息。</para>
    /// </summary>
    /// <param name="localId">The local stream identifier. <para>本地流标识。</para></param>
    /// <param name="remoteId">The remote stream identifier. <para>远端流标识。</para></param>
    /// <param name="data">The data to write. <para>要写入的数据。</para></param>
    /// <returns>The constructed WRTE message. <para>构建出的 WRTE 消息。</para></returns>
    public static AdbMessage BuildWrite(uint localId, uint remoteId, byte[] data) =>
        new(AdbCommand.Wrte, localId, remoteId, data);

    /// <summary>
    /// Builds a CLSE message.
    /// <para>构建 CLSE 消息。</para>
    /// </summary>
    /// <param name="localId">The local stream identifier. <para>本地流标识。</para></param>
    /// <param name="remoteId">The remote stream identifier. <para>远端流标识。</para></param>
    /// <returns>The constructed CLSE message. <para>构建出的 CLSE 消息。</para></returns>
    public static AdbMessage BuildClose(uint localId, uint remoteId) =>
        new(AdbCommand.Clse, localId, remoteId);

    /// <summary>
    /// Builds an OKAY message.
    /// <para>构建 OKAY 消息。</para>
    /// </summary>
    /// <param name="localId">The local stream identifier. <para>本地流标识。</para></param>
    /// <param name="remoteId">The remote stream identifier. <para>远端流标识。</para></param>
    /// <returns>The constructed OKAY message. <para>构建出的 OKAY 消息。</para></returns>
    public static AdbMessage BuildOkay(uint localId, uint remoteId) =>
        new(AdbCommand.Okay, localId, remoteId);
}