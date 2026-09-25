using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace UotanStudio.Comm.Protocols.Fastboot.Transport;

internal sealed class FastbootTcpFrames
{
    private readonly byte[] _readLength = new byte[8];
    private readonly byte[] _writeLength = new byte[8];
    private ulong _remaining;

    internal void Reset() => _remaining = 0;

    internal static void Handshake(NetworkStream stream)
    {
        var handshake = Encoding.ASCII.GetBytes("FB01");
        stream.Write(handshake);
        var response = new byte[4];
        if (ReadFully(stream, response, 0, 4) != 4)
        {
            throw new IOException("Fastboot TCP handshake failed: unexpected response or timeout.");
        }

        var text = Encoding.ASCII.GetString(response);
        if (!text.StartsWith("FB", StringComparison.Ordinal))
        {
            throw new IOException("Fastboot TCP handshake failed: unrecognized initialization Message.");
        }

        var versionText = text.Substring(2, 2);
        if (!int.TryParse(versionText, out var version) || version < 1)
        {
            throw new IOException($"Fastboot TCP handshake failed: unknown TCP protocol version {versionText} (host version 01).");
        }
    }

    internal int Read(NetworkStream stream, byte[] buffer, int offset, int length)
    {
        if (_remaining == 0)
        {
            if (ReadFully(stream, _readLength, 0, 8) != 8)
            {
                throw new IOException("Failed to read Message length from the fastboot TCP stream.");
            }

            AcceptLength();
        }

        var read = ReadFully(stream, buffer, offset, (int)Math.Min((ulong)length, _remaining));
        _remaining -= (ulong)read;
        return read;
    }

    internal async Task<int> ReadAsync(NetworkStream stream, byte[] buffer, int offset, int length, CancellationToken token)
    {
        if (_remaining == 0)
        {
            if (await ReadFullyAsync(stream, _readLength, 0, 8, token).ConfigureAwait(false) != 8)
            {
                throw new IOException("Failed to read Message length from the fastboot TCP stream.");
            }

            AcceptLength();
        }

        var read = await ReadFullyAsync(stream, buffer, offset, (int)Math.Min((ulong)length, _remaining), token).ConfigureAwait(false);
        _remaining -= (ulong)read;
        return read;
    }

    internal long Write(NetworkStream stream, byte[] data, int offset, int length)
    {
        BinaryPrimitives.WriteUInt64BigEndian(_writeLength, (ulong)length);
        stream.Write(_writeLength);
        stream.Write(data, offset, length);
        stream.Flush();
        return length;
    }

    internal async Task<long> WriteAsync(NetworkStream stream, byte[] data, int offset, int length, CancellationToken token)
    {
        BinaryPrimitives.WriteUInt64BigEndian(_writeLength, (ulong)length);
        await stream.WriteAsync(_writeLength, token).ConfigureAwait(false);
        await stream.WriteAsync(data.AsMemory(offset, length), token).ConfigureAwait(false);
        return length;
    }

    private void AcceptLength()
    {
        var length = BinaryPrimitives.ReadUInt64BigEndian(_readLength);
        if (length > uint.MaxValue)
        {
            throw new IOException($"Invalid TCP frame length: {length} (exceeds uint.MaxValue).");
        }

        _remaining = length;
    }

    private static int ReadFully(NetworkStream stream, byte[] buffer, int offset, int length)
    {
        var total = 0;
        while (total < length)
        {
            var read = stream.Read(buffer, offset + total, length - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task<int> ReadFullyAsync(NetworkStream stream, byte[] buffer, int offset, int length, CancellationToken token)
    {
        var total = 0;
        while (total < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset + total, length - total), token).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}