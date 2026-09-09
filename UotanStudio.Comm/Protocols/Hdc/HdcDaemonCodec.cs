using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>Device-side framing and SerialStruct protobuf encoding, distinct from host channels.</summary>
public static class HdcDaemonCodec
{
    /// <summary>Maximum accepted serialized metadata length.</summary>
    public const int MaximumMetadataSize = 1024;

    /// <summary>Encodes session.h PayloadHead and serial_struct.h PayloadProtect.</summary>
    public static byte[] Encode(HdcMessage message)
    {
        var payload = message.Payload ?? [];
        if (payload.Length > HdcConstants.MaxPayloadSize) throw new InvalidDataException("HDC payload exceeds limit.");
        using var metadata = new MemoryStream();
        Number(metadata, 1, message.ChannelId);
        Number(metadata, 2, (uint)message.Command);
        Number(metadata, 3, 0); // Upstream disables the optional additive checksum by default.
        Number(metadata, 4, 9);
        var packet = new byte[checked(11 + (int)metadata.Length + payload.Length)];
        packet[0] = (byte)'H'; packet[1] = (byte)'W'; packet[4] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(5), (ushort)metadata.Length);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(7), (uint)payload.Length);
        metadata.GetBuffer().AsSpan(0, (int)metadata.Length).CopyTo(packet.AsSpan(11));
        payload.CopyTo(packet, 11 + (int)metadata.Length);
        return packet;
    }

    /// <summary>Validates a fixed daemon header before allocating its body.</summary>
    public static (int Metadata, int Payload) ReadHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != 11 || header[0] != 'H' || header[1] != 'W' || header[2] != 0 || header[3] != 0 || header[4] != 1)
            throw new InvalidDataException("Invalid or unsupported HDC daemon header.");
        var metadata = BinaryPrimitives.ReadUInt16BigEndian(header[5..]);
        var payload = BinaryPrimitives.ReadUInt32BigEndian(header[7..]);
        if (metadata is 0 or > MaximumMetadataSize || payload > HdcConstants.MaxPayloadSize)
            throw new InvalidDataException("HDC daemon frame exceeds limits.");
        return (metadata, (int)payload);
    }

    /// <summary>Decodes a complete device packet and checks its verification code and optional checksum.</summary>
    public static HdcMessage Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 11) throw new InvalidDataException("Truncated HDC packet.");
        var (metadata, payloadSize) = ReadHeader(packet[..11]);
        if (packet.Length != 11 + metadata + payloadSize) throw new InvalidDataException("HDC packet length mismatch.");
        var fields = ReadFields(packet.Slice(11, metadata));
        var channel = GetNumber(fields, 1); var command = GetNumber(fields, 2);
        var checksum = GetNumber(fields, 3);
        if (GetNumber(fields, 4) != 9 || channel > uint.MaxValue || command > ushort.MaxValue || checksum > 255)
            throw new InvalidDataException("Invalid HDC protection metadata.");
        var payload = packet[(11 + metadata)..].ToArray();
        if (checksum != 0 && checksum != (uint)(payload.Sum(b => (int)b) & 255))
            throw new InvalidDataException("HDC payload checksum mismatch.");
        return new((HdcCommand)command, (uint)channel, 0, payload);
    }

    /// <summary>Serializes a daemon authentication handshake. Auth state is not a signing algorithm.</summary>
    public static byte[] Handshake(byte authState, uint sessionId, string connectKey, byte[] buffer, string version)
    {
        using var output = new MemoryStream();
        Bytes(output, 1, Encoding.UTF8.GetBytes(HdcConstants.HandshakeMessage));
        Number(output, 2, authState); Number(output, 3, sessionId);
        Bytes(output, 4, Encoding.UTF8.GetBytes(connectKey)); Bytes(output, 5, buffer);
        Bytes(output, 6, Encoding.UTF8.GetBytes(version));
        return output.ToArray();
    }

    /// <summary>Parses bounded protobuf handshake fields.</summary>
    public static (byte State, uint SessionId, byte[] Buffer, string Version) ReadHandshake(ReadOnlySpan<byte> payload)
    {
        var fields = ReadFields(payload);
        if (Encoding.UTF8.GetString(GetBytes(fields, 1)) != HdcConstants.HandshakeMessage)
            throw new InvalidDataException("Invalid HDC handshake banner.");
        var state = GetNumber(fields, 2); var session = GetNumber(fields, 3);
        if (state > 255 || session > uint.MaxValue) throw new InvalidDataException("Invalid HDC handshake identity.");
        return ((byte)state, (uint)session, GetBytes(fields, 5), Encoding.UTF8.GetString(GetBytes(fields, 6)));
    }

    private static void Varint(Stream output, ulong value)
    {
        while (value >= 128) { output.WriteByte((byte)(value | 128)); value >>= 7; }
        output.WriteByte((byte)value);
    }
    internal static void Number(Stream output, uint field, ulong value) { Varint(output, field << 3); Varint(output, value); }
    internal static void Bytes(Stream output, uint field, byte[] value)
    {
        Varint(output, (field << 3) | 2); Varint(output, (uint)value.Length); output.Write(value);
    }
    private static ulong Varint(ReadOnlySpan<byte> input, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (offset >= input.Length) throw new InvalidDataException("Truncated HDC varint.");
            var next = input[offset++];
            if (shift == 63 && next > 1) throw new InvalidDataException("Overflowing HDC varint.");
            value |= (ulong)(next & 127) << shift;
            if (next < 128) return value;
        }
        throw new InvalidDataException("Overflowing HDC varint.");
    }
    internal static Dictionary<uint, (ulong Number, byte[]? Bytes)> ReadFields(ReadOnlySpan<byte> input, bool allowPadding = false)
    {
        if (input.Length > HdcConstants.MaxPayloadSize) throw new InvalidDataException("HDC structure exceeds limit.");
        var fields = new Dictionary<uint, (ulong, byte[]?)>();
        var offset = 0;
        while (offset < input.Length)
        {
            var key = Varint(input, ref offset);
            if (allowPadding && key == 0) break;
            if (key >> 3 is 0 or > 536870911 || fields.Count >= 64) throw new InvalidDataException("Invalid HDC field tag.");
            var tag = (uint)(key >> 3);
            switch (key & 7)
            {
                case 0: fields[tag] = (Varint(input, ref offset), null); break;
                case 2:
                    var length = Varint(input, ref offset);
                    if (length > (ulong)(input.Length - offset)) throw new InvalidDataException("Truncated HDC field.");
                    fields[tag] = (0, input.Slice(offset, (int)length).ToArray()); offset += (int)length; break;
                case 1: case 5:
                    var skip = (key & 7) == 1 ? 8 : 4;
                    if (input.Length - offset < skip) throw new InvalidDataException("Truncated HDC fixed field.");
                    offset += skip; break;
                default: throw new InvalidDataException("Unsupported HDC field wire type.");
            }
        }
        return fields;
    }
    internal static ulong GetNumber(Dictionary<uint, (ulong Number, byte[]? Bytes)> fields, uint tag)
        => fields.TryGetValue(tag, out var field) && field.Bytes is null ? field.Number : throw new InvalidDataException("Missing HDC integer field.");
    internal static byte[] GetBytes(Dictionary<uint, (ulong Number, byte[]? Bytes)> fields, uint tag)
        => fields.TryGetValue(tag, out var field) && field.Bytes is { } bytes ? bytes : [];
}
