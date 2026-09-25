using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace UotanStudio.Comm.Protocols.Fastboot.Transport;

internal sealed class FastbootUdpPackets(IPEndPoint endpoint, int maxAttempts)
{
    private const int HeaderSize = 4;
    private const ushort HostMaxPacketSize = 8192;
    private const ushort MinPacketSize = 512;

    private int _sequence;
    private int _maxDataLength;

    private enum PacketId : byte
    {
        Error = 0,
        DeviceQuery = 1,
        Initialization = 2,
        Fastboot = 3
    }

    private enum PacketFlag : byte
    {
        None = 0,
        Continuation = 1
    }

    internal void Reset() => _sequence = 0;

    internal void Handshake(UdpClient client)
    {
        var attempts = Math.Max(maxAttempts, 5);
        var response = Exchange(client, PacketId.DeviceQuery, PacketFlag.None, 0, [], 0, 0, attempts, false, null, 0, 0, out _, out _);
        if (response.Length < 2)
        {
            throw new IOException("Invalid query response from fastboot target.");
        }

        _sequence = 1;
        var init = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(2, 2), HostMaxPacketSize);
        response = Exchange(client, PacketId.Initialization, PacketFlag.None, (ushort)_sequence, init, 0, 4, attempts, false, null, 0, 0, out _, out _);
        if (response.Length < 4)
        {
            throw new IOException("Invalid initialization response from fastboot target.");
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(0, 2));
        var size = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2));
        if (version < 1)
        {
            throw new IOException($"Fastboot target reported invalid protocol version {version}.");
        }

        if (size < MinPacketSize)
        {
            throw new IOException($"Fastboot target reported invalid packet size {size}.");
        }

        _maxDataLength = Math.Min(HostMaxPacketSize, size) - HeaderSize;
        _sequence = (_sequence + 1) & 0xFFFF;
    }

    internal int Read(UdpClient client, byte[] output, int offset, int length)
    {
        Exchange(client, PacketId.Fastboot, PacketFlag.None, (ushort)_sequence, [], 0, 0, maxAttempts, false,
            output, offset, length, out var next, out var written);
        _sequence = next;
        return written;
    }

    internal void Write(UdpClient client, byte[] data, int offset, int length)
    {
        var sent = 0;
        do
        {
            var chunk = Math.Min(length - sent, _maxDataLength);
            var flag = sent + chunk < length ? PacketFlag.Continuation : PacketFlag.None;
            Exchange(client, PacketId.Fastboot, flag, (ushort)_sequence, data, offset + sent, chunk, maxAttempts, true,
                null, 0, 0, out var next, out _);
            _sequence = next;
            sent += chunk;
        }
        while (sent < length);
    }

    private byte[] Exchange(
        UdpClient client, PacketId id, PacketFlag flag, ushort sequence, byte[] data, int offset, int length,
        int attempts, bool requireEmpty, byte[]? output, int outputOffset, int outputLength,
        out ushort nextSequence, out int written)
    {
        List<byte>? collected = output is null && !requireEmpty ? [] : null;
        written = 0;
        while (true)
        {
            var packet = new byte[HeaderSize + length];
            packet[0] = (byte)id;
            packet[1] = (byte)flag;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), sequence);
            if (length > 0)
            {
                Array.Copy(data, offset, packet, HeaderSize, length);
            }

            var valid = false;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                client.Send(packet, packet.Length, endpoint);
                try
                {
                    while (true)
                    {
                        IPEndPoint from = new(IPAddress.Any, 0);
                        var response = client.Receive(ref from);
                        if (!from.Equals(endpoint) || response.Length < HeaderSize ||
                            BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2)) != sequence ||
                            (response[0] != (byte)id && response[0] != (byte)PacketId.Error))
                        {
                            continue;
                        }

                        if (response[0] == (byte)PacketId.Error)
                        {
                            throw new IOException("Fastboot target returned an error response.");
                        }

                        var payloadLength = response.Length - HeaderSize;
                        if (payloadLength > 0)
                        {
                            if (requireEmpty)
                            {
                                throw new IOException("UDP protocol error: fastboot target sent data out-of-turn.");
                            }

                            if (output is not null)
                            {
                                if (written + payloadLength > outputLength)
                                {
                                    throw new IOException("UDP protocol error: receive overflow, fastboot target sent too much data.");
                                }

                                Buffer.BlockCopy(response, HeaderSize, output, outputOffset + written, payloadLength);
                                written += payloadLength;
                            }
                            else if (collected is not null)
                            {
                                if (collected.Count > HostMaxPacketSize - payloadLength)
                                {
                                    throw new IOException("UDP protocol error: handshake response exceeds the packet limit.");
                                }

                                for (var i = HeaderSize; i < response.Length; i++)
                                {
                                    collected.Add(response[i]);
                                }
                            }
                        }

                        valid = true;
                        sequence = (ushort)((sequence + 1) & 0xFFFF);
                        if ((response[1] & (byte)PacketFlag.Continuation) == 0)
                        {
                            nextSequence = sequence;
                            return collected is null ? [] : [.. collected];
                        }

                        id = (PacketId)response[0];
                        flag = PacketFlag.None;
                        offset = 0;
                        length = 0;
                        break;
                    }
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                }

                if (valid)
                {
                    break;
                }
            }

            if (!valid)
            {
                throw new IOException($"Failed to receive a response after {attempts} transmission attempts.");
            }
        }
    }
}