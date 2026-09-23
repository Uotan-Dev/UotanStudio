using System.Buffers;
using System.Diagnostics;
using System.IO.Ports;
using System.Runtime.ExceptionServices;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Comm.Transports.Hsuart;

public sealed partial class HsuartTransport
{
    /// <summary>
    /// Sends an EndOfTransfer packet to signal the end of a data transfer.
    /// <para>发送 EndOfTransfer 数据包以表示数据传输结束。</para>
    /// </summary>
    public void SendEndOfTransfer()
    {
        ThrowIfDisposed();
        EnsureConnected();
        _writeGate.Wait();
        try
        {
            _readGate.Wait();
            try
            {
                _sessionGate.Wait();
                try
                {
                    EnsureSessionConnected();
                    var encodedBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
                    var encodedLength = HsuartPacket.Encode(HsuartPacketId.EndOfTransferPacket, [], encodedBuffer, out var error);

                    if (error != HsuartPacketError.Success)
                    {
                        throw new IOException($"Failed to encode HSUART EndOfTransfer packet: {error}");
                    }

                    Serial.Write(encodedBuffer, 0, encodedLength, DefaultTimeoutMs);
                }
                finally
                {
                    _sessionGate.Release();
                }
            }
            finally
            {
                _readGate.Release();
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Asynchronously sends an EndOfTransfer packet to signal the end of a data transfer.
    /// <para>异步发送 EndOfTransfer 数据包以表示数据传输结束。</para>
    /// </summary>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task representing the asynchronous send operation. <para>表示异步发送操作的任务。</para></returns>
    public async Task SendEndOfTransferAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await EnsureSessionConnectedAsync(cancellationToken).ConfigureAwait(false);
                    var encodedBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
                    var encodedLength = HsuartPacket.Encode(HsuartPacketId.EndOfTransferPacket, [], encodedBuffer, out var error);

                    if (error != HsuartPacketError.Success)
                    {
                        throw new IOException($"Failed to encode HSUART EndOfTransfer packet: {error}");
                    }

                    await Serial.WriteAsync(encodedBuffer, 0, encodedLength, DefaultTimeoutMs, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _sessionGate.Release();
                }
            }
            finally
            {
                _readGate.Release();
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Returns the raw byte stream of the underlying serial port. COBS framing and ACK handling
    /// are bypassed — use only for diagnostics or raw boot-mode access.
    /// <para>返回底层串口的原始字节流。COBS 封装与 ACK 处理被绕过——仅用于诊断或原始引导模式访问。</para>
    /// </summary>
    /// <returns>A stream over the serial port. <para>串口上的流。</para></returns>
    public Stream GetStream() => Serial.GetStream();

    private void SendProtocolPacket(byte[] payload)
    {
        var encodedBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
        var encodedLength = HsuartPacket.Encode(HsuartPacketId.ProtocolPacket, payload, encodedBuffer, out var error);

        if (error != HsuartPacketError.Success)
        {
            throw new IOException($"Failed to encode HSUART protocol packet: {error}");
        }

        Serial.Write(encodedBuffer, 0, encodedLength, DefaultTimeoutMs);
    }

    private async Task SendProtocolPacketAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var encodedBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
        var encodedLength = HsuartPacket.Encode(HsuartPacketId.ProtocolPacket, payload, encodedBuffer, out var error);

        if (error != HsuartPacketError.Success)
        {
            throw new IOException($"Failed to encode HSUART protocol packet: {error}");
        }

        await Serial.WriteAsync(encodedBuffer, 0, encodedLength, DefaultTimeoutMs, cancellationToken).ConfigureAwait(false);
    }

    private void SendAck()
    {
        var ackBuffer = new byte[1];
        var ackLength = HsuartPacket.Encode(HsuartPacketId.AckPacket, [], ackBuffer, out var error);

        if (error != HsuartPacketError.Success)
        {
            throw new IOException($"Failed to encode HSUART ACK packet: {error}");
        }

        Serial.Write(ackBuffer, 0, ackLength, DefaultTimeoutMs);
    }

    private async Task SendAckAsync(CancellationToken cancellationToken)
    {
        var ackBuffer = new byte[1];
        var ackLength = HsuartPacket.Encode(HsuartPacketId.AckPacket, [], ackBuffer, out var error);

        if (error != HsuartPacketError.Success)
        {
            throw new IOException($"Failed to encode HSUART ACK packet: {error}");
        }

        await Serial.WriteAsync(ackBuffer, 0, ackLength, DefaultTimeoutMs, cancellationToken).ConfigureAwait(false);
    }

    private void WaitForAck(int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        var nonAckFrames = 0;
        var performedPostSendRead = false;
        while (nonAckFrames < MaxRetries)
        {
            try
            {
                var remaining = RemainingTimeout(timeoutMs, stopwatch);
                byte[] response;
                if (performedPostSendRead && _receivedFrames.Count > 0)
                {
                    response = _receivedFrames.Dequeue();
                }
                else
                {
                    response = _rawAcknowledgementMode
                        ? ReadAcknowledgementOrFrame(remaining)
                        : ReadFrame(remaining);
                    performedPostSendRead = true;
                }
                var decoded = DecodePacket(response, response.Length, out var packetId);
                if (packetId == HsuartPacketId.AckPacket)
                {
                    return;
                }
                if (packetId == HsuartPacketId.NakPacket)
                {
                    throw new IOException("Target returned NAK for HSUART packet.");
                }
                nonAckFrames++;
                if (packetId == HsuartPacketId.ProtocolPacket && decoded.Length > 0)
                {
                    _receivedPayloads.Enqueue(decoded);
                }
                else
                {
                    HandleReceivedPacketId(packetId);
                }
            }
            catch (TimeoutException)
            {
                break;
            }
        }

        throw new IOException("No ACK received within the HSUART acknowledgement budget.");
    }

    private async Task WaitForAckAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var nonAckFrames = 0;
        var performedPostSendRead = false;
        while (nonAckFrames < MaxRetries)
        {
            try
            {
                var remaining = RemainingTimeout(timeoutMs, stopwatch);
                byte[] response;
                if (performedPostSendRead && _receivedFrames.Count > 0)
                {
                    response = _receivedFrames.Dequeue();
                }
                else
                {
                    response = _rawAcknowledgementMode
                        ? await ReadAcknowledgementOrFrameAsync(remaining, cancellationToken).ConfigureAwait(false)
                        : await ReadFrameAsync(remaining, cancellationToken).ConfigureAwait(false);
                    performedPostSendRead = true;
                }
                var decoded = DecodePacket(response, response.Length, out var packetId);
                if (packetId == HsuartPacketId.AckPacket)
                {
                    return;
                }
                if (packetId == HsuartPacketId.NakPacket)
                {
                    throw new IOException("Target returned NAK for HSUART packet.");
                }
                nonAckFrames++;
                if (packetId == HsuartPacketId.ProtocolPacket && decoded.Length > 0)
                {
                    _receivedPayloads.Enqueue(decoded);
                }
                else
                {
                    HandleReceivedPacketId(packetId);
                }
            }
            catch (TimeoutException)
            {
                break;
            }
        }

        throw new IOException("No ACK received within the HSUART acknowledgement budget.");
    }

    private static int RemainingTimeout(int timeoutMs, Stopwatch stopwatch)
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

    private static byte[] DecodePacket(byte[] buffer, int length, out HsuartPacketId packetId)
    {
        if (length <= 0)
        {
            packetId = 0;
            return [];
        }

        if (length == 1)
        {
            var singleByte = buffer[0];
            if (singleByte == (byte)HsuartPacketId.AckPacket)
            {
                packetId = HsuartPacketId.AckPacket;
                return [];
            }
            if (singleByte == (byte)HsuartPacketId.NakPacket)
            {
                packetId = HsuartPacketId.NakPacket;
                return [];
            }
        }

        var dstBuffer = new byte[HsuartPacket.MaxPacketSize];
        var decodedLength = HsuartPacket.Decode(buffer.AsSpan(0, length), out packetId, dstBuffer, out var error);

        if (error != HsuartPacketError.Success)
        {
            throw new IOException($"HSUART packet decode error: {error}");
        }

        if (decodedLength == 0)
        {
            return [];
        }

        var result = new byte[decodedLength];
        Array.Copy(dstBuffer, result, decodedLength);
        return result;
    }

    private void HandleReceivedPacketId(HsuartPacketId packetId)
    {
        switch (packetId)
        {
            case HsuartPacketId.VersionPacket:
            case HsuartPacketId.EndOfTransferPacket:
                SendAck();
                break;
            case HsuartPacketId.NakPacket:
            case HsuartPacketId.ReadyToReadPacket:
                break;
            case HsuartPacketId.ProtocolPacket:
                break;
            case HsuartPacketId.AckPacket:
                break;
            default:
                break;
        }
    }

    private void PrepareAcknowledgementBoundary()
    {
        var retained = new Queue<byte[]>();
        while (_receivedFrames.Count > 0)
        {
            var frame = _receivedFrames.Dequeue();
            if (frame.Length != 1 || frame[0] is not ((byte)HsuartPacketId.AckPacket) and not ((byte)HsuartPacketId.NakPacket))
            {
                retained.Enqueue(frame);
            }
        }
        while (retained.Count > 0)
        {
            _receivedFrames.Enqueue(retained.Dequeue());
        }

        _frameParser.TakeIsolatedAcknowledgement();
    }

}