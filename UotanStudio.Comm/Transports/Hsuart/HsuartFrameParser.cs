using System.Buffers;
using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.ExceptionServices;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Comm.Transports.Hsuart;

/// <summary>Incrementally separates raw ACK/NAK bytes and zero-delimited COBS frames.</summary>
public sealed class HsuartFrameParser
{
    private const int MaxAcknowledgementPrefixLength = 16;
    private const int MaxEncodedPacketSize = HsuartPacket.MaxPacketSize + 16;

    private readonly byte[] _buffer = new byte[MaxEncodedPacketSize];
    private int _start;
    private int _end;

    /// <summary>Gets whether an incomplete COBS frame is buffered.</summary>
    public bool HasPending => _end != _start;

    /// <summary>Consumes an arbitrary serial chunk and returns all complete frames.</summary>
    /// <param name="data">Arbitrary bytes read from the serial stream.</param>
    /// <param name="acceptRawAcknowledgements">Whether raw ACK/NAK bytes are expected in the current protocol state.</param>
    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> data, bool acceptRawAcknowledgements = false)
    {
        var frames = new List<byte[]>();
        var offset = 0;
        while (offset < data.Length)
        {
            var take = Math.Min(MaxEncodedPacketSize - (_end - _start), data.Length - offset);
            if (take <= 0)
            {
                ClearBuffer();
                throw new InvalidDataException("HSUART frame exceeds the maximum encoded packet size.");
            }
            EnsureRoom(take);
            data.Slice(offset, take).CopyTo(_buffer.AsSpan(_end));
            _end += take;
            offset += take;

            if (!ConsumeCompleteFrames(frames, acceptRawAcknowledgements) && offset < data.Length)
            {
                ClearBuffer();
                throw new InvalidDataException("HSUART frame exceeds the maximum encoded packet size.");
            }
        }

        if (acceptRawAcknowledgements && frames.Count > 0 &&
            _end - _start == 1 && IsAcknowledgement(_buffer[_start]))
        {
            frames.Add([_buffer[_start]]);
            _start = 0;
            _end = 0;
        }
        else if (acceptRawAcknowledgements && _end - _start > 1)
        {
            var pending = _buffer.AsSpan(_start, _end - _start);
            if (IsAcknowledgement(pending[0]) && !CouldBeFrame(pending) && CouldBeFrame(pending[1..]))
            {
                frames.Add([pending[0]]);
                _start++;
                if (_start == _end)
                {
                    _start = 0;
                    _end = 0;
                }
            }
        }

        return frames;
    }

    /// <summary>Gets whether the buffered data consists solely of an ambiguous raw ACK/NAK candidate.</summary>
    public bool HasIsolatedAcknowledgementCandidate =>
        _end - _start == 1 && IsAcknowledgement(_buffer[_start]);

    /// <summary>
    /// Explicitly consumes a raw ACK/NAK after the caller has established an out-of-band packet
    /// boundary. A serial read boundary or a short inter-byte delay is not such a boundary: 0x06
    /// is also a legal first COBS code, so the stream reader deliberately never calls this method.
    /// </summary>
    public byte[]? TakeIsolatedAcknowledgement()
    {
        if (_end - _start == 1 && IsAcknowledgement(_buffer[_start]))
        {
            var acknowledgement = new[] { _buffer[_start] };
            _start = 0;
            _end = 0;
            return acknowledgement;
        }
        return null;
    }

    /// <summary>Clears a partial frame when starting a new connection.</summary>
    public void Reset()
    {
        _start = 0;
        _end = 0;
    }

    private bool ConsumeCompleteFrames(List<byte[]> frames, bool acceptRawAcknowledgements)
    {
        var consumedAny = false;
        while (_start < _end)
        {
            var zeroIndex = _buffer.AsSpan(_start, _end - _start).IndexOf((byte)0);
            if (zeroIndex < 0)
            {
                break;
            }
            consumedAny = true;
            EmitCompleteCandidate(frames, zeroIndex, acceptRawAcknowledgements);
            if (_start == _end)
            {
                _start = 0;
                _end = 0;
            }
        }
        return consumedAny;
    }

    private void EmitCompleteCandidate(List<byte[]> frames, int length, bool acceptRawAcknowledgements)
    {
        var candidate = _buffer.AsSpan(_start, length + 1);
        _start += length + 1;

        if (acceptRawAcknowledgements && !IsValidFrame(candidate))
        {
            var prefixLength = 0;
            var maxPrefixLength = Math.Min(length, MaxAcknowledgementPrefixLength);
            while (prefixLength < maxPrefixLength && IsAcknowledgement(candidate[prefixLength]))
            {
                prefixLength++;
            }

            var split = prefixLength > 0 && IsValidFrame(candidate[prefixLength..])
                ? prefixLength
                : prefixLength > 1 && IsValidFrame(candidate[(prefixLength - 1)..])
                    ? prefixLength - 1
                    : 0;
            if (split > 0)
            {
                for (var i = 0; i < split; i++)
                {
                    frames.Add([candidate[i]]);
                }
                frames.Add(candidate[split..].ToArray());
                return;
            }
        }
        frames.Add(candidate.ToArray());
    }

    private void EnsureRoom(int needed)
    {
        if (_buffer.Length - _end >= needed)
        {
            return;
        }
        if (_start > 0)
        {
            if (_end > _start)
            {
                _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
            }
            _end -= _start;
            _start = 0;
        }
    }

    private void ClearBuffer()
    {
        _start = 0;
        _end = 0;
    }

    private static bool IsAcknowledgement(byte value) =>
        value is (byte)HsuartPacketId.AckPacket or (byte)HsuartPacketId.NakPacket;

    private static bool CouldBeFrame(ReadOnlySpan<byte> candidate)
    {
        if (candidate.IsEmpty || candidate[0] == 0)
        {
            return false;
        }

        return candidate.Length == 1 || IsPacketId(candidate[1]);
    }

    private static bool IsPacketId(byte value) => value is
        (byte)HsuartPacketId.ProtocolPacket or (byte)HsuartPacketId.EndOfTransferPacket or
        (byte)HsuartPacketId.VersionPacket or (byte)HsuartPacketId.ReadyToReadPacket;

    private static bool IsValidFrame(ReadOnlySpan<byte> candidate)
    {
        var decoded = ArrayPool<byte>.Shared.Rent(HsuartPacket.MaxPacketSize);
        try
        {
            HsuartPacket.Decode(candidate, out _, decoded, out var error);
            return error == HsuartPacketError.Success;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(decoded);
        }
    }
}
