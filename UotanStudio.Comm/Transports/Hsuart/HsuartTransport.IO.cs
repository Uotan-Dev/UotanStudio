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
    /// Reads the payload of the next received HSUART protocol packet. Returns 0 when a
    /// control packet (ACK/NAK/version/end-of-transfer) was received instead, after handling it.
    /// <para>读取下一个收到的 HSUART 协议数据包的负载。若收到的是控制包
    /// （ACK/NAK/版本/传输结束），则在处理该控制包后返回 0。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of payload bytes copied. <para>复制的负载字节数。</para></returns>
    /// <exception cref="TimeoutException">No packet arrived within the timeout. <para>超时时间内无数据包到达。</para></exception>
    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
    {
        ThrowIfDisposed();
        EnsureConnected();
        if (length <= 0)
        {
            return 0;
        }

        _readGate.Wait();
        try
        {
            EnsureSessionConnected();
            return ReadPayload(buffer, offset, length, timeoutMs);
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <summary>
    /// Reads the payload of the next received HSUART protocol packet asynchronously.
    /// <para>异步读取下一个收到的 HSUART 协议数据包的负载。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the number of payload bytes copied. <para>返回复制的负载字节数的任务。</para></returns>
    public async Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        if (length <= 0)
        {
            return 0;
        }

        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSessionConnectedAsync(cancellationToken).ConfigureAwait(false);
            return await ReadPayloadAsync(buffer, offset, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <summary>
    /// Frames <paramref name="length"/> bytes as a protocol packet, writes it, and waits for the
    /// device ACK before returning.
    /// <para>将 <paramref name="length"/> 个字节封装为协议数据包并写入，在返回前等待设备 ACK。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>待写入数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>写入字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes written. <para>实际写入的字节数。</para></returns>
    /// <exception cref="IOException">No ACK was received, the target returned NAK, or framing failed. <para>未收到 ACK、目标返回 NAK 或封包失败。</para></exception>
    public long Write(byte[] data, int offset, int length, int timeoutMs)
    {
        ThrowIfDisposed();
        EnsureConnected();
        if (length <= 0)
        {
            return 0;
        }

        var payload = new byte[length];
        Array.Copy(data, offset, payload, 0, length);

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
                    PrepareAcknowledgementBoundary();
                    SendProtocolPacket(payload);
                    WaitForAck(timeoutMs);
                    return length;
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
    /// Frames <paramref name="length"/> bytes as a protocol packet, writes it asynchronously,
    /// and waits for the device ACK before completing.
    /// <para>将 <paramref name="length"/> 个字节封装为协议数据包并异步写入，完成后等待设备 ACK。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>待写入数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>写入字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the number of bytes written. <para>返回实际写入字节数的任务。</para></returns>
    public async Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();
        if (length <= 0)
        {
            return 0;
        }

        var payload = new byte[length];
        Array.Copy(data, offset, payload, 0, length);

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
                    PrepareAcknowledgementBoundary();
                    await SendProtocolPacketAsync(payload, cancellationToken).ConfigureAwait(false);
                    await WaitForAckAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
                    return length;
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
    /// Resets the HSUART transport by clearing the underlying serial buffers.
    /// <para>通过清除底层串口缓冲区来重置 HSUART 传输。</para>
    /// </summary>
    public void Reset()
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
                    Serial.Reset();
                    ClearBufferedData();
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

    private int CopyPayload(byte[] buffer, int offset, int length)
    {
        _pendingPayload ??= _receivedPayloads.Count > 0 ? _receivedPayloads.Dequeue() : null;
        if (_pendingPayload is null)
        {
            return 0;
        }
        var count = Math.Min(length, _pendingPayload.Length - _pendingPayloadOffset);
        Array.Copy(_pendingPayload, _pendingPayloadOffset, buffer, offset, count);
        _pendingPayloadOffset += count;
        if (_pendingPayloadOffset == _pendingPayload.Length)
        {
            _pendingPayload = null;
            _pendingPayloadOffset = 0;
        }
        return count;
    }

    private int ReadPayload(byte[] buffer, int offset, int length, int timeoutMs)
    {
        var copied = CopyPayload(buffer, offset, length);
        if (copied > 0)
        {
            return copied;
        }

        var frame = ReadFrame(timeoutMs);
        var decoded = DecodePacket(frame, frame.Length, out var packetId);
        HandleReceivedPacketId(packetId);
        if (packetId == HsuartPacketId.ProtocolPacket && decoded.Length > 0)
        {
            _receivedPayloads.Enqueue(decoded);
            return CopyPayload(buffer, offset, length);
        }
        return 0;
    }

    private async Task<int> ReadPayloadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken)
    {
        var copied = CopyPayload(buffer, offset, length);
        if (copied > 0)
        {
            return copied;
        }

        var frame = await ReadFrameAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        var decoded = DecodePacket(frame, frame.Length, out var packetId);
        if (packetId is HsuartPacketId.VersionPacket or HsuartPacketId.EndOfTransferPacket)
        {
            await SendAckAsync(cancellationToken).ConfigureAwait(false);
        }
        if (packetId == HsuartPacketId.ProtocolPacket && decoded.Length > 0)
        {
            _receivedPayloads.Enqueue(decoded);
            return CopyPayload(buffer, offset, length);
        }
        return 0;
    }

}