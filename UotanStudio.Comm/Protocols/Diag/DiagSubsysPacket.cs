using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Comm.Protocols.Diag;

/// <summary>
/// Encodes and decodes the DIAG subsystem dispatch envelope wrapping every EFS2 operation.
/// <para>承载全部 EFS2 操作的 DIAG 子系统分发包络的编解码。</para>
/// </summary>
public static class DiagSubsysPacket
{
    /// <summary>
    /// Encodes a subsystem dispatch request: command byte, subsystem id, subsystem command.
    /// <para>编码子系统分发请求：命令字节、子系统号、子系统命令。</para>
    /// </summary>
    /// <param name="subsystemId">The subsystem identifier. <para>子系统标识。</para></param>
    /// <param name="subsystemCommand">The subsystem command (EFS2 opcode).
    /// <para>子系统命令（EFS2 操作码）。</para></param>
    /// <param name="body">The operation body after the dispatch header.
    /// <para>分发头之后的操作体。</para></param>
    /// <returns>The full request payload starting with the 0x4B command byte.
    /// <para>以 0x4B 命令字节开头的完整请求负载。</para></returns>
    public static byte[] EncodeRequest(ushort subsystemId, ushort subsystemCommand, ReadOnlySpan<byte> body)
    {
        var payload = new byte[5 + body.Length];
        payload[0] = DiagConstants.SubsysDispatchRequest;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), subsystemId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(3), subsystemCommand);
        body.CopyTo(payload.AsSpan(5));
        return payload;
    }

    /// <summary>
    /// Decodes a subsystem dispatch response: the payload starts at the 0x4C command byte.
    /// <para>解码子系统分发响应：负载从 0x4C 命令字节开始。</para>
    /// </summary>
    /// <param name="payload">The payload starting at the command byte.
    /// <para>从命令字节开始的负载。</para></param>
    /// <returns>The subsystem id, subsystem command and body.
    /// <para>子系统号、子系统命令与操作体。</para></returns>
    /// <exception cref="InvalidDataException">The payload is shorter than the dispatch header or
    /// carries a foreign command byte. <para>负载短于分发头或命令字节不符。</para></exception>
    public static (ushort SubsystemId, ushort SubsystemCommand, byte[] Body) DecodeResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5 || payload[0] != DiagConstants.SubsysDispatchResponse)
        {
            throw new InvalidDataException("The response is not a subsystem dispatch reply.");
        }

        return (BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(1)),
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(3)),
            payload.Slice(5).ToArray());
    }

    /// <summary>
    /// Encodes an EFS2 string argument: length-prefixed and NUL-terminated.
    /// <para>编码 EFS2 字符串参数：长度前缀并以 NUL 结尾。</para>
    /// </summary>
    /// <param name="value">The path or name to encode. <para>待编码的路径或名称。</para></param>
    /// <returns>The encoded argument. <para>编码后的参数。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The encoded form exceeds the path limit.
    /// <para>编码结果超出路径上限。</para></exception>
    public static byte[] EncodeEfsString(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes.AsSpan(..^1));
        bytes[^1] = 0;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes.Length, DiagConstants.MaxEfsPathBytes);
        var payload = new byte[2 + bytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)bytes.Length);
        bytes.CopyTo(payload.AsSpan(2));
        return payload;
    }
}

/// <summary>
/// The EFS2 result header every operation reply carries: reply opcode, error and reserved field.
/// <para>每个 EFS2 操作应答携带的结果头：应答操作码、错误码与保留字段。</para>
/// </summary>
public readonly record struct Efs2ResultHeader(ushort Error)
{
    /// <summary>
    /// Decodes the 4-byte result header (error, reserved).
    /// <para>解码 4 字节结果头（错误码、保留字段）。</para>
    /// </summary>
    /// <param name="body">The reply body. <para>应答体。</para></param>
    /// <returns>The decoded header. <para>解码后的头。</para></returns>
    /// <exception cref="InvalidDataException">The body is shorter than 4 bytes.
    /// <para>应答体短于 4 字节。</para></exception>
    public static Efs2ResultHeader Decode(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
        {
            throw new InvalidDataException("The EFS2 reply is shorter than its result header.");
        }

        return new Efs2ResultHeader(BinaryPrimitives.ReadUInt16LittleEndian(body));
    }

    /// <summary>
    /// Whether the operation reported success.
    /// <para>操作是否报告成功。</para>
    /// </summary>
    public bool Succeeded => Error == 0;
}