using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Apple.Tunnel;

/// <summary>XPC object type codes. <para>XPC 对象类型码。</para></summary>
public static class XpcType
{
    /// <summary>NULL. <para>NULL。</para></summary>
    public const uint Null = 0x00001000;

    /// <summary>BOOL. <para>BOOL。</para></summary>
    public const uint Bool = 0x00002000;

    /// <summary>INT64. <para>INT64。</para></summary>
    public const uint Int64 = 0x00003000;

    /// <summary>UINT64. <para>UINT64。</para></summary>
    public const uint UInt64 = 0x00004000;

    /// <summary>DOUBLE. <para>DOUBLE。</para></summary>
    public const uint Double = 0x00005000;

    /// <summary>DATE (nanoseconds since 1970). <para>DATE（自 1970 起的纳秒）。</para></summary>
    public const uint Date = 0x00007000;

    /// <summary>DATA. <para>DATA。</para></summary>
    public const uint Data = 0x00008000;

    /// <summary>STRING. <para>STRING。</para></summary>
    public const uint String = 0x00009000;

    /// <summary>UUID. <para>UUID。</para></summary>
    public const uint Uuid = 0x0000A000;

    /// <summary>ARRAY. <para>ARRAY。</para></summary>
    public const uint Array = 0x0000E000;

    /// <summary>DICTIONARY. <para>DICTIONARY。</para></summary>
    public const uint Dictionary = 0x0000F000;

    /// <summary>FILE_TRANSFER. <para>FILE_TRANSFER。</para></summary>
    public const uint FileTransfer = 0x0001A000;
}

/// <summary>XPC wrapper flag bits. <para>XPC 包装帧标志位。</para></summary>
[Flags]
public enum XpcFlags : uint
{
    /// <summary>Always set. <para>恒置位。</para></summary>
    AlwaysSet = 0x00000001,

    /// <summary>Ping frame. <para>Ping 帧。</para></summary>
    Ping = 0x00000002,

    /// <summary>Payload present. <para>存在负载。</para></summary>
    DataPresent = 0x00000100,

    /// <summary>Requesting a reply. <para>请求应答。</para></summary>
    WantingReply = 0x00010000,

    /// <summary>Reply frame. <para>应答帧。</para></summary>
    Reply = 0x00020000,

    /// <summary>Initiate handshake. <para>发起握手。</para></summary>
    InitHandshake = 0x00400000,
}

/// <summary>
/// RemoteXPC wire codec: the recursive XPC object encoding plus the wrapper frame
/// (magic 0x29B00B92, flags, size, message id, payload header 0x42133742/5).
/// <para>RemoteXPC 线编解码：递归 XPC 对象编码与包装帧（magic 0x29B00B92、flags、size、message id、
/// 负载头 0x42133742/5）。</para>
/// </summary>
public static class XpcCodec
{
    /// <summary>Gets the wrapper magic. <para>获取包装帧魔数。</para></summary>
    public const uint WrapperMagic = 0x29B00B92;

    /// <summary>Gets the payload magic. <para>获取负载魔数。</para></summary>
    public const uint PayloadMagic = 0x42133742;

    /// <summary>Gets the payload protocol version. <para>获取负载协议版本。</para></summary>
    public const uint PayloadProtocolVersion = 5;

    private const int MaxPayloadBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Builds one complete wrapper frame carrying <paramref name="dictionary"/>. The
    /// <see cref="XpcFlags.DataPresent"/> flag follows the upstream rule and is set only for
    /// non-empty dictionaries.
    /// <para>构建一条携带 <paramref name="dictionary"/> 的完整包装帧。<see cref="XpcFlags.DataPresent"/>
    /// 标志遵循上游规则，仅在字典非空时置位。</para>
    /// </summary>
    /// <param name="dictionary">The message dictionary. <para>消息字典。</para></param>
    /// <param name="messageId">The frame message id. <para>帧消息 ID。</para></param>
    /// <param name="wantingReply">Whether a reply is requested. <para>是否请求应答。</para></param>
    /// <returns>The frame bytes. <para>帧字节。</para></returns>
    public static byte[] BuildWrapper(IReadOnlyDictionary<string, object?> dictionary, ulong messageId = 0,
        bool wantingReply = false)
    {
        using var payload = new MemoryStream();
        WriteU32(payload, PayloadMagic);
        WriteU32(payload, PayloadProtocolVersion);
        WriteObject(payload, dictionary);

        var body = payload.ToArray();
        using var output = new MemoryStream();
        WriteU32(output, WrapperMagic);
        var flags = XpcFlags.AlwaysSet;
        if (dictionary.Count > 0)
        {
            flags |= XpcFlags.DataPresent;
        }

        if (wantingReply)
        {
            flags |= XpcFlags.WantingReply;
        }

        WriteU32(output, (uint)flags);
        WriteU64(output, (ulong)body.Length);
        WriteU64(output, messageId);
        output.Write(body);
        return output.ToArray();
    }

    /// <summary>
    /// Builds a header-only wrapper (for example the handshake frames).
    /// <para>构建只有头的包装帧（例如握手帧）。</para>
    /// </summary>
    /// <param name="flags">The frame flags. <para>帧标志。</para></param>
    /// <param name="messageId">The frame message id. <para>帧消息 ID。</para></param>
    /// <returns>The frame bytes. <para>帧字节。</para></returns>
    public static byte[] BuildHeaderWrapper(XpcFlags flags, ulong messageId = 0)
    {
        using var output = new MemoryStream();
        WriteU32(output, WrapperMagic);
        WriteU32(output, (uint)flags);
        WriteU64(output, 0);
        WriteU64(output, messageId);
        return output.ToArray();
    }

    /// <summary>
    /// Parses one wrapper frame from the front of the buffer, reporting the consumed length.
    /// <para>从缓冲区头部解析一个包装帧并报告消耗长度。</para>
    /// </summary>
    /// <param name="buffer">The buffered bytes; may contain more than one frame. <para>缓冲字节；可含多帧。</para></param>
    /// <param name="consumed">Receives the frame size in bytes. <para>接收帧大小。</para></param>
    /// <returns>The decoded dictionary, or <see langword="null"/> for header-only frames. <para>解码字典；仅有头帧为 <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidDataException">The frame is malformed, oversized or incomplete. <para>帧畸形、超大或不完整时抛出。</para></exception>
    public static IReadOnlyDictionary<string, object?>? ParseWrapper(ReadOnlySpan<byte> buffer, out int consumed)
    {
        consumed = 0;
        if (buffer.Length < 24)
        {
            return null;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (magic != WrapperMagic)
        {
            throw new InvalidDataException("The RemoteXPC frame carries an invalid magic.");
        }

        BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(4));
        var size = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(8));
        if (size > MaxPayloadBytes)
        {
            throw new InvalidDataException($"The RemoteXPC payload {size} exceeds the size limit.");
        }

        var total = 24 + (int)size;
        if (buffer.Length < total)
        {
            return null;
        }

        consumed = total;
        if (size < 8)
        {
            return null;
        }

        var payload = buffer.Slice(24, (int)size);
        if (BinaryPrimitives.ReadUInt32LittleEndian(payload) != PayloadMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)) != PayloadProtocolVersion)
        {
            throw new InvalidDataException("The RemoteXPC payload header is invalid.");
        }

        var reader = new PayloadReader(payload[8..]);
        var value = reader.ReadObject(0);
        return value as IReadOnlyDictionary<string, object?>
            ?? throw new InvalidDataException("The RemoteXPC frame payload is not a dictionary.");
    }

    private static void WriteObject(MemoryStream output, object? value)
    {
        switch (value)
        {
            case null:
                WriteU32(output, XpcType.Null);
                break;
            case true:
                WriteU32(output, XpcType.Bool);
                WriteU32(output, 1);
                break;
            case false:
                WriteU32(output, XpcType.Bool);
                WriteU32(output, 0);
                break;
            case XpcUInt64 integer:
                WriteU32(output, XpcType.UInt64);
                WriteU64(output, integer.Value);
                break;
            case XpcInt64 integer:
                WriteU32(output, XpcType.Int64);
                WriteU64(output, unchecked((ulong)integer.Value));
                break;
            case int integer:
                WriteU32(output, XpcType.Int64);
                WriteU64(output, (ulong)integer);
                break;
            case long integer:
                WriteU32(output, XpcType.Int64);
                WriteU64(output, (ulong)integer);
                break;
            case double real:
                WriteU32(output, XpcType.Double);
                WriteU64(output, BitConverter.DoubleToUInt64Bits(real));
                break;
            case string text:
                {
                    var utf8 = Encoding.UTF8.GetBytes(text);
                    WriteU32(output, XpcType.String);
                    WriteU32(output, (uint)(utf8.Length + 1));
                    output.Write(utf8);
                    output.WriteByte(0);
                    WritePadding(output, utf8.Length + 1);
                    break;
                }
            case byte[] data:
                WriteU32(output, XpcType.Data);
                WriteU32(output, (uint)data.Length);
                output.Write(data);
                WritePadding(output, data.Length);
                break;
            case Guid uuid:
                WriteU32(output, XpcType.Uuid);
                output.Write(uuid.ToByteArray());
                break;
            case List<object?> list:
                {
                    using var body = new MemoryStream();
                    WriteU32(body, (uint)list.Count);
                    foreach (var item in list)
                    {
                        WriteObject(body, item);
                    }

                    WriteU32(output, XpcType.Array);
                    WriteContainerBody(output, body);
                    break;
                }
            case IReadOnlyDictionary<string, object?> dictionary:
                {
                    using var body = new MemoryStream();
                    WriteU32(body, (uint)dictionary.Count);
                    foreach (var entry in dictionary)
                    {
                        WriteDictionaryKey(body, entry.Key);
                        WriteObject(body, entry.Value);
                    }

                    WriteU32(output, XpcType.Dictionary);
                    WriteContainerBody(output, body);
                    break;
                }
            default:
                throw new ArgumentException($"Unsupported XPC value type {value!.GetType().Name}.");
        }
    }

    private static void WriteContainerBody(MemoryStream output, MemoryStream body)
    {
        WriteU32(output, (uint)body.Length);
        body.Position = 0;
        body.CopyTo(output);
    }

    private static void WriteDictionaryKey(MemoryStream output, string key)
    {
        var utf8 = Encoding.UTF8.GetBytes(key);
        output.Write(utf8);
        output.WriteByte(0);
        WritePadding(output, utf8.Length + 1);
    }

    private static void WritePadding(MemoryStream output, int bodyLength)
    {
        var padding = (4 - (bodyLength % 4)) % 4;
        for (var i = 0; i < padding; i++)
        {
            output.WriteByte(0);
        }
    }

    private static void WriteU32(MemoryStream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteU64(MemoryStream output, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        output.Write(bytes);
    }

    /// <summary>Boxing helper marking a value as unsigned in XPC. <para>标记 XPC 无符号值的装箱辅助。</para></summary>
    /// <param name="value">The value. <para>值。</para></param>
    /// <returns>The boxed unsigned value. <para>装箱后的无符号值。</para></returns>
    public static XpcUInt64 UInt64(ulong value) => new(value);

    /// <summary>Boxing helper marking a value as signed in XPC. <para>标记 XPC 有符号值的装箱辅助。</para></summary>
    /// <param name="value">The value. <para>值。</para></param>
    /// <returns>The boxed signed value. <para>装箱后的有符号值。</para></returns>
    public static XpcInt64 Int64(long value) => new(value);

    /// <summary>An XPC unsigned 64-bit value. <para>XPC 无符号 64 位值。</para></summary>
    /// <param name="Value">The value. <para>值。</para></param>
    public readonly record struct XpcUInt64(ulong Value);

    /// <summary>An XPC signed 64-bit value. <para>XPC 有符号 64 位值。</para></summary>
    /// <param name="Value">The value. <para>值。</para></param>
    public readonly record struct XpcInt64(long Value);

    private ref struct PayloadReader(ReadOnlySpan<byte> payload)
    {
        private readonly ReadOnlySpan<byte> _payload = payload;
        private int _position;
        private long _elements;

        public object? ReadObject(int depth)
        {
            if (depth > 64 || ++_elements > 1 << 20)
            {
                throw new InvalidDataException("The RemoteXPC payload exceeds its structural limits.");
            }

            var type = ReadU32();
            switch (type)
            {
                case XpcType.Null:
                    return null;
                case XpcType.Bool:
                    return ReadU32() != 0;
                case XpcType.UInt64:
                    return new XpcUInt64(ReadU64());
                case XpcType.Int64:
                    return new XpcInt64(unchecked((long)ReadU64()));
                case XpcType.Double:
                    return BitConverter.UInt64BitsToDouble(ReadU64());
                case XpcType.Date:
                    var nanoseconds = unchecked((long)ReadU64());
                    return DateTime.UnixEpoch.AddTicks(nanoseconds / 100);
                case XpcType.Uuid:
                    var uuid = ReadBounded(16);
                    return new Guid(uuid);
                case XpcType.String:
                    {
                        var length = (int)ReadU32();
                        var bytes = ReadBounded(length);
                        var terminator = Array.IndexOf(bytes, (byte)0);
                        var text = Encoding.UTF8.GetString(bytes, 0, terminator < 0 ? bytes.Length : terminator);
                        SkipAlignment(length);
                        return text;
                    }
                case XpcType.Data:
                    {
                        var length = (int)ReadU32();
                        var bytes = ReadBounded(length);
                        SkipAlignment(length);
                        return bytes;
                    }
                case XpcType.Array:
                    {
                        var end = ReadContainerEnd();
                        var count = ReadU32();
                        var items = new List<object?>((int)count);
                        for (var i = 0; i < count; i++)
                        {
                            items.Add(ReadObject(depth + 1));
                        }

                        SeekContainerEnd(end);
                        return items;
                    }
                case XpcType.Dictionary:
                    {
                        var end = ReadContainerEnd();
                        var count = ReadU32();
                        var dictionary = new Dictionary<string, object?>((int)count);
                        for (var i = 0; i < count; i++)
                        {
                            var key = ReadAlignedString();
                            dictionary[key] = ReadObject(depth + 1);
                        }

                        SeekContainerEnd(end);
                        return dictionary;
                    }
                case XpcType.FileTransfer:
                    {
                        var messageId = ReadU64();
                        _ = ReadObject(depth + 1);
                        return new XpcUInt64(messageId);
                    }
                default:
                    throw new InvalidDataException($"The RemoteXPC object type 0x{type:X} is unsupported.");
            }
        }

        private long ReadContainerEnd()
        {
            var bodySize = ReadU32();
            var end = _position + bodySize;
            if (end > _payload.Length)
            {
                throw new InvalidDataException("The RemoteXPC container exceeds the payload.");
            }

            return end;
        }

        private void SeekContainerEnd(long end)
        {
            if (_position > end)
            {
                throw new InvalidDataException("The RemoteXPC container entries exceed the declared size.");
            }

            _position = (int)end;
        }

        private string ReadAlignedString()
        {
            var start = _position;
            while (_position < _payload.Length && _payload[_position] != 0)
            {
                _position++;
            }

            if (_position >= _payload.Length)
            {
                throw new InvalidDataException("The RemoteXPC dictionary key is not NUL-terminated.");
            }

            var text = Encoding.UTF8.GetString(_payload.Slice(start, _position - start));
            _position++;
            SkipAlignment(_position - start);
            return text;
        }

        private void SkipAlignment(int bodyLength)
        {
            var padding = (4 - (bodyLength % 4)) % 4;
            if (_position + padding > _payload.Length)
            {
                throw new InvalidDataException("The RemoteXPC payload ends inside alignment padding.");
            }

            _position += padding;
        }

        private uint ReadU32() => (uint)ReadLittle(4);

        private ulong ReadU64() => ReadLittle(8);

        private ulong ReadLittle(int size)
        {
            if (_position + size > _payload.Length)
            {
                throw new InvalidDataException("The RemoteXPC payload ends unexpectedly.");
            }

            ulong value = 0;
            for (var i = size - 1; i >= 0; i--)
            {
                value = (value << 8) | _payload[_position + i];
            }

            _position += size;
            return value;
        }

        private byte[] ReadBounded(int size)
        {
            if (size < 0 || _position + size > _payload.Length)
            {
                throw new InvalidDataException("The RemoteXPC payload ends unexpectedly.");
            }

            var bytes = _payload.Slice(_position, size).ToArray();
            _position += size;
            return bytes;
        }
    }
}