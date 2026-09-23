using System.Buffers;
using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.ExceptionServices;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Comm.Transports.Hsuart;

public sealed partial class HsuartTransport
{
    private byte[] ReadNegotiationResponse(int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        var first = new byte[1];
        var count = Serial.Read(first, 0, 1, RemainingFrameTimeout(timeoutMs, stopwatch));
        if (count == 0)
        {
            throw new TimeoutException("No HSUART version response arrived.");
        }
        if (first[0] == (byte)HsuartPacketId.NakPacket)
        {
            return first;
        }
        foreach (var frame in _frameParser.Feed(first))
        {
            _receivedFrames.Enqueue(frame);
        }
        return ReadFrame(RemainingFrameTimeout(timeoutMs, stopwatch));
    }

    private async Task<byte[]> ReadNegotiationResponseAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var first = new byte[1];
        using var linked = CreateSessionLinkedTokenSource(cancellationToken);
        var count = await Serial.ReadAsync(first, 0, 1, RemainingFrameTimeout(timeoutMs, stopwatch), linked.Token).ConfigureAwait(false);
        if (count == 0)
        {
            throw new TimeoutException("No HSUART version response arrived.");
        }
        if (first[0] == (byte)HsuartPacketId.NakPacket)
        {
            return first;
        }
        foreach (var frame in _frameParser.Feed(first))
        {
            _receivedFrames.Enqueue(frame);
        }
        return await ReadFrameAsync(RemainingFrameTimeout(timeoutMs, stopwatch), cancellationToken).ConfigureAwait(false);
    }

    private byte[] ReadAcknowledgementOrFrame(int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        var raw = ArrayPool<byte>.Shared.Rent(HsuartPacket.MaxPacketSize + 16);
        try
        {
            var count = Serial.Read(raw, 0, raw.Length, RemainingFrameTimeout(timeoutMs, stopwatch));
            if (count == 0)
            {
                throw new TimeoutException("No HSUART acknowledgement arrived.");
            }
            if (count == 1 && raw[0] is (byte)HsuartPacketId.AckPacket or (byte)HsuartPacketId.NakPacket)
            {
                return [raw[0]];
            }
            foreach (var frame in _frameParser.Feed(raw.AsSpan(0, count), _rawAcknowledgementMode))
            {
                _receivedFrames.Enqueue(frame);
            }
            return ReadFrame(RemainingFrameTimeout(timeoutMs, stopwatch), acceptRawAcknowledgements: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private async Task<byte[]> ReadAcknowledgementOrFrameAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var raw = ArrayPool<byte>.Shared.Rent(HsuartPacket.MaxPacketSize + 16);
        using var linked = CreateSessionLinkedTokenSource(cancellationToken);
        try
        {
            var count = await Serial.ReadAsync(raw, 0, raw.Length, RemainingFrameTimeout(timeoutMs, stopwatch), linked.Token).ConfigureAwait(false);
            if (count == 0)
            {
                throw new TimeoutException("No HSUART acknowledgement arrived.");
            }
            if (count == 1 && raw[0] is (byte)HsuartPacketId.AckPacket or (byte)HsuartPacketId.NakPacket)
            {
                return [raw[0]];
            }
            foreach (var frame in _frameParser.Feed(raw.AsSpan(0, count), _rawAcknowledgementMode))
            {
                _receivedFrames.Enqueue(frame);
            }
            return await ReadFrameAsync(RemainingFrameTimeout(timeoutMs, stopwatch), cancellationToken, acceptRawAcknowledgements: true).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private byte[] ReadFrame(int timeoutMs, bool acceptRawAcknowledgements = false)
    {
        var stopwatch = Stopwatch.StartNew();
        var raw = ArrayPool<byte>.Shared.Rent(HsuartPacket.MaxPacketSize + 16);
        try
        {
            while (_receivedFrames.Count == 0)
            {
                var count = Serial.Read(raw, 0, raw.Length, RemainingFrameTimeout(timeoutMs, stopwatch));
                if (count == 0)
                {
                    throw new TimeoutException("No complete HSUART frame arrived.");
                }
                foreach (var frame in _frameParser.Feed(raw.AsSpan(0, count), acceptRawAcknowledgements))
                {
                    _receivedFrames.Enqueue(frame);
                }
            }
            return _receivedFrames.Dequeue();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private async Task<byte[]> ReadFrameAsync(int timeoutMs, CancellationToken cancellationToken, bool acceptRawAcknowledgements = false)
    {
        var stopwatch = Stopwatch.StartNew();
        var raw = ArrayPool<byte>.Shared.Rent(HsuartPacket.MaxPacketSize + 16);
        using var linked = CreateSessionLinkedTokenSource(cancellationToken);
        try
        {
            while (_receivedFrames.Count == 0)
            {
                var count = await Serial.ReadAsync(raw, 0, raw.Length, RemainingFrameTimeout(timeoutMs, stopwatch), linked.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new TimeoutException("No complete HSUART frame arrived.");
                }
                foreach (var frame in _frameParser.Feed(raw.AsSpan(0, count), acceptRawAcknowledgements))
                {
                    _receivedFrames.Enqueue(frame);
                }
            }
            return _receivedFrames.Dequeue();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private static int RemainingFrameTimeout(int timeoutMs, Stopwatch stopwatch)
    {
        if (timeoutMs == Timeout.Infinite)
        {
            return Timeout.Infinite;
        }
        if (timeoutMs < Timeout.Infinite)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        }

        var remaining = timeoutMs - (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
        return Math.Max(0, remaining);
    }

}