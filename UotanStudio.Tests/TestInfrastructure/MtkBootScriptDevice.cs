using System.Buffers.Binary;

namespace UotanStudio.Tests.TestInfrastructure;

/// <summary>
/// Scripts a MediaTek Boot ROM target over <see cref="ScriptedCommTransport"/>: answers the
/// complemented handshake byte by byte, echoes boot commands, and returns identity payloads.
/// Multi-byte writes are data phases and are recorded without a response.
/// </summary>
internal sealed class MtkBootScriptDevice
{
    private static readonly byte[] Handshake = [0xA0, 0x0A, 0x50, 0x05];

    private readonly List<byte> _handshakeReceived = [];
    private readonly List<byte> _commandsReceived = [];
    private int _handshakeIndex;

    internal MtkBootScriptDevice()
    {
        Transport = new ScriptedCommTransport();
        Transport.OnWrite = Respond;
    }

    internal ScriptedCommTransport Transport { get; }

    /// <summary>Gets the handshake bytes the target actually observed, in order.</summary>
    internal IReadOnlyList<byte> HandshakeReceived => _handshakeReceived;

    /// <summary>Gets the command bytes observed after the handshake completed, in order.</summary>
    internal IReadOnlyList<byte> CommandsReceived => _commandsReceived;

    /// <summary>Gets whether the full four-byte handshake was consumed.</summary>
    internal bool HandshakeComplete { get; private set; }

    /// <summary>
    /// Gets or sets how many whole handshake attempts are rejected with a wrong complement before the
    /// target synchronizes. A rejected attempt resets the internal byte position, matching a device
    /// that restarts from the first byte.
    /// </summary>
    internal int RejectedHandshakeAttempts { get; set; }

    internal ushort HardwareCode { get; set; } = 0x0CA3;

    internal ushort HardwareSubCode { get; set; } = 0x0002;

    internal ushort HardwareVersion { get; set; } = 0xCA00;

    internal ushort SoftwareVersion { get; set; } = 0x0003;

    internal ushort TargetConfigurationStatus { get; set; }

    internal byte BootLoaderVersion { get; set; } = 0xFE;

    internal byte BromVersion { get; set; } = 0x05;

    internal byte[] Meid { get; set; } = [0x01, 0x02, 0x03, 0x04];

    internal byte[] Socid { get; set; } = [0xAA, 0xBB];

    private IEnumerable<byte[]> Respond(byte[] written)
    {
        if (written.Length != 1)
        {
            return [];
        }

        var value = written[0];
        if (!HandshakeComplete)
        {
            return RespondHandshake(value);
        }

        _commandsReceived.Add(value);
        return RespondCommand(value);
    }

    private IEnumerable<byte[]> RespondHandshake(byte value)
    {
        _handshakeReceived.Add(value);
        if (value != Handshake[_handshakeIndex])
        {
            _handshakeIndex = 0;
            return [];
        }

        if (_handshakeIndex == 0 && RejectedHandshakeAttempts > 0)
        {
            RejectedHandshakeAttempts--;
            _handshakeIndex = 0;
            return [[(byte)(value ^ 0xFE)]];
        }

        _handshakeIndex++;
        if (_handshakeIndex == Handshake.Length)
        {
            HandshakeComplete = true;
        }

        return [[(byte)(value ^ 0xFF)]];
    }

    private IEnumerable<byte[]> RespondCommand(byte command) => command switch
    {
        0xFD => EchoAnd(command, HardwarePayload()),
        0xFC => EchoAnd(command, VersionPayload()),
        0xD8 => EchoAnd(command, ConfigurationPayload()),
        0xFE => [[BootLoaderVersion]],
        0xFF => [[BromVersion]],
        0xE1 => EchoAnd(command, IdentityPayload(Meid)),
        0xE7 => EchoAnd(command, IdentityPayload(Socid)),
        _ => []
    };

    private static IEnumerable<byte[]> EchoAnd(byte command, byte[] payload) => [[command], payload];

    private byte[] HardwarePayload()
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(payload, HardwareCode);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), HardwareSubCode);
        return payload;
    }

    private byte[] VersionPayload()
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(payload, HardwareSubCode);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), HardwareVersion);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), SoftwareVersion);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6), 0x0004);
        return payload;
    }

    private byte[] ConfigurationPayload()
    {
        var payload = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(payload, 0x0000_0001);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), TargetConfigurationStatus);
        return payload;
    }

    private static byte[] IdentityPayload(byte[] identity)
    {
        var payload = new byte[4 + identity.Length + 2];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)identity.Length);
        identity.CopyTo(payload.AsSpan(4));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4 + identity.Length), 0);
        return payload;
    }
}