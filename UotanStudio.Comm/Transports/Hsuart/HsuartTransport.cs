using System.IO.Ports;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Comm.Transports.Hsuart;

/// <summary>
/// An <see cref="ICommTransport"/> implementation for the HSUART protocol: a serial link
/// wrapped in COBS packet framing with CRC-16, version negotiation, and ACK/NAK semantics.
/// <para>HSUART 协议的 <see cref="ICommTransport"/> 实现：以 COBS 数据包封装（含 CRC-16、
/// 版本协商、ACK/NAK 语义）包裹串行链路。</para>
/// Every payload write is framed as a protocol packet and acknowledged by the device before
/// returning; every read returns the payload of the next received protocol packet.
/// <para>每次负载写入都会被封装为协议数据包并在返回前等待设备确认；每次读取返回下一个
/// 收到的协议数据包的负载。</para>
/// </summary>
/// <remarks>
/// Initializes a new <see cref="HsuartTransport"/> with the specified serial port parameters.
/// <para>使用指定的串口参数初始化 <see cref="HsuartTransport"/> 的新实例。</para>
/// </remarks>
/// <param name="portName">The serial port name (e.g. "COM3" or "/dev/ttyUSB0"). <para>串口名称（如 "COM3" 或 "/dev/ttyUSB0"）。</para></param>
/// <param name="baudRate">The baud rate. Default is 3000000. <para>波特率。默认 3000000。</para></param>
/// <param name="parity">The parity setting. Default is <see cref="Parity.None"/>. <para>奇偶校验设置。默认 <see cref="Parity.None"/>。</para></param>
/// <param name="dataBits">The number of data bits. Default is 8. <para>数据位数。默认 8。</para></param>
/// <param name="stopBits">The stop bits setting. Default is <see cref="StopBits.One"/>. <para>停止位设置。默认 <see cref="StopBits.One"/>。</para></param>
/// <param name="handshake">The handshake setting. Default is <see cref="Handshake.None"/>. <para>握手设置。默认 <see cref="Handshake.None"/>。</para></param>
public sealed class HsuartTransport(
    string portName,
    int baudRate = 3000000,
    Parity parity = Parity.None,
    int dataBits = 8,
    StopBits stopBits = StopBits.One,
    Handshake handshake = Handshake.None) : ICommTransport
{
    private bool _disposed;
    private bool _versionNegotiated;

    private const int MaxRetries = 3;
    private const int VersionNegotiationTimeoutMs = 5000;

    /// <summary>
    /// Gets a value indicating whether the underlying serial transport is connected.
    /// <para>获取底层串口传输是否已连接。</para>
    /// </summary>
    public bool IsConnected => Serial.IsConnected;

    /// <summary>
    /// Gets or sets the default timeout in milliseconds for read and write operations.
    /// <para>获取或设置读写操作的默认超时时间（毫秒）。</para>
    /// </summary>
    public int DefaultTimeoutMs
    {
        get => Serial.DefaultTimeoutMs;
        set => Serial.DefaultTimeoutMs = value;
    }

    /// <summary>
    /// Gets the underlying serial transport, exposed for callers that need raw serial access.
    /// <para>获取底层串口传输，供需要原始串口访问的调用方使用。</para>
    /// </summary>
    public SerialTransport Serial { get; } = new SerialTransport(portName, baudRate, parity, dataBits, stopBits, handshake);

    /// <summary>
    /// Connects to the HSUART device and performs version negotiation.
    /// <para>连接 HSUART 设备并执行版本协商。</para>
    /// </summary>
    /// <exception cref="IOException">Version negotiation was rejected (NAK) or failed to encode. <para>版本协商被拒绝（NAK）或编码失败。</para></exception>
    public void Connect()
    {
        ThrowIfDisposed();
        Serial.Connect();
        PerformVersionNegotiation();
    }

    /// <summary>
    /// Asynchronously connects to the HSUART device and performs version negotiation.
    /// <para>异步连接 HSUART 设备并执行版本协商。</para>
    /// </summary>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task representing the asynchronous connect operation. <para>表示异步连接操作的任务。</para></returns>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await Serial.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await PerformVersionNegotiationAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Disconnects from the HSUART device.
    /// <para>断开与 HSUART 设备的连接。</para>
    /// </summary>
    public void Disconnect()
    {
        _versionNegotiated = false;
        Serial.Disconnect();
    }

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

        var frameBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
        var rawBytes = Serial.Read(frameBuffer, 0, frameBuffer.Length, timeoutMs);

        if (rawBytes == 0)
        {
            return 0;
        }

        var decoded = DecodePacket(frameBuffer, rawBytes, out var packetId);
        HandleReceivedPacketId(packetId);

        if (packetId == HsuartPacketId.ProtocolPacket && decoded.Length > 0)
        {
            var copyLength = Math.Min(decoded.Length, length);
            Array.Copy(decoded, 0, buffer, offset, copyLength);
            return copyLength;
        }

        return 0;
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

        var frameBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
        var rawBytes = await Serial.ReadAsync(frameBuffer, 0, frameBuffer.Length, timeoutMs, cancellationToken).ConfigureAwait(false);

        if (rawBytes == 0)
        {
            return 0;
        }

        var decoded = DecodePacket(frameBuffer, rawBytes, out var packetId);
        HandleReceivedPacketId(packetId);

        if (packetId == HsuartPacketId.ProtocolPacket && decoded.Length > 0)
        {
            var copyLength = Math.Min(decoded.Length, length);
            Array.Copy(decoded, 0, buffer, offset, copyLength);
            return copyLength;
        }

        return 0;
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

        SendProtocolPacket(payload);
        WaitForAck(timeoutMs);
        return length;
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

        await SendProtocolPacketAsync(payload, cancellationToken).ConfigureAwait(false);
        await WaitForAckAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        return length;
    }

    /// <summary>
    /// Resets the HSUART transport by clearing the underlying serial buffers.
    /// <para>通过清除底层串口缓冲区来重置 HSUART 传输。</para>
    /// </summary>
    public void Reset()
    {
        EnsureConnected();
        Serial.Reset();
    }

    /// <summary>
    /// Sends an EndOfTransfer packet to signal the end of a data transfer.
    /// <para>发送 EndOfTransfer 数据包以表示数据传输结束。</para>
    /// </summary>
    public void SendEndOfTransfer()
    {
        ThrowIfDisposed();
        EnsureConnected();

        var encodedBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
        var encodedLength = HsuartPacket.Encode(HsuartPacketId.EndOfTransferPacket, [], encodedBuffer, out var error);

        if (error != HsuartPacketError.Success)
        {
            throw new IOException($"Failed to encode HSUART EndOfTransfer packet: {error}");
        }

        Serial.Write(encodedBuffer, 0, encodedLength, DefaultTimeoutMs);
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

        var encodedBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
        var encodedLength = HsuartPacket.Encode(HsuartPacketId.EndOfTransferPacket, [], encodedBuffer, out var error);

        if (error != HsuartPacketError.Success)
        {
            throw new IOException($"Failed to encode HSUART EndOfTransfer packet: {error}");
        }

        await Serial.WriteAsync(encodedBuffer, 0, encodedLength, DefaultTimeoutMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the raw byte stream of the underlying serial port. COBS framing and ACK handling
    /// are bypassed — use only for diagnostics or raw boot-mode access.
    /// <para>返回底层串口的原始字节流。COBS 封装与 ACK 处理被绕过——仅用于诊断或原始引导模式访问。</para>
    /// </summary>
    /// <returns>A stream over the serial port. <para>串口上的流。</para></returns>
    public Stream GetStream() => Serial.GetStream();

    /// <summary>
    /// Disposes the transport, releasing the underlying serial transport.
    /// <para>释放传输及其底层串口传输。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _versionNegotiated = false;
        Serial.Dispose();
    }

    private void PerformVersionNegotiation()
    {
        if (_versionNegotiated)
        {
            return;
        }

        var encodedBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
        var encodedLength = HsuartPacket.Encode(HsuartPacketId.VersionPacket, [], encodedBuffer, out var error);

        if (error != HsuartPacketError.Success)
        {
            throw new IOException($"Failed to encode HSUART version packet: {error}");
        }

        Serial.Write(encodedBuffer, 0, encodedLength, DefaultTimeoutMs);

        try
        {
            var responseBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
            var responseBytes = Serial.Read(responseBuffer, 0, responseBuffer.Length, VersionNegotiationTimeoutMs);

            if (responseBytes > 0)
            {
                DecodePacket(responseBuffer, responseBytes, out var packetId);
                if (packetId == HsuartPacketId.VersionPacket)
                {
                    SendAck();
                }
                else if (packetId == HsuartPacketId.NakPacket)
                {
                    throw new IOException("HSUART version negotiation rejected by device (NAK).");
                }
            }
        }
        catch (TimeoutException)
        {
            // Some devices do not answer the version packet; proceed regardless.
            // 部分设备不回应版本包；无论是否回应都继续。
        }

        _versionNegotiated = true;
    }

    private async Task PerformVersionNegotiationAsync(CancellationToken cancellationToken)
    {
        if (_versionNegotiated)
        {
            return;
        }

        var encodedBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
        var encodedLength = HsuartPacket.Encode(HsuartPacketId.VersionPacket, [], encodedBuffer, out var error);

        if (error != HsuartPacketError.Success)
        {
            throw new IOException($"Failed to encode HSUART version packet: {error}");
        }

        await Serial.WriteAsync(encodedBuffer, 0, encodedLength, DefaultTimeoutMs, cancellationToken).ConfigureAwait(false);

        try
        {
            var responseBuffer = new byte[HsuartPacket.MaxPacketSize + 16];
            var responseBytes = await Serial.ReadAsync(responseBuffer, 0, responseBuffer.Length, VersionNegotiationTimeoutMs, cancellationToken).ConfigureAwait(false);

            if (responseBytes > 0)
            {
                DecodePacket(responseBuffer, responseBytes, out var packetId);
                if (packetId == HsuartPacketId.VersionPacket)
                {
                    await SendAckAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (packetId == HsuartPacketId.NakPacket)
                {
                    throw new IOException("HSUART version negotiation rejected by device (NAK).");
                }
            }
        }
        catch (TimeoutException)
        {
            // Some devices do not answer the version packet; proceed regardless.
            // 部分设备不回应版本包；无论是否回应都继续。
        }

        _versionNegotiated = true;
    }

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
        var responseBuffer = new byte[HsuartPacket.MaxPacketSize + 16];

        for (var retry = 0; retry < MaxRetries; retry++)
        {
            try
            {
                var responseBytes = Serial.Read(responseBuffer, 0, responseBuffer.Length, timeoutMs);
                if (responseBytes == 0)
                {
                    continue;
                }

                DecodePacket(responseBuffer, responseBytes, out var packetId);

                if (packetId == HsuartPacketId.AckPacket)
                {
                    return;
                }

                if (packetId == HsuartPacketId.NakPacket)
                {
                    throw new IOException("Target returned NAK for HSUART packet.");
                }

                // Control packets other than ACK/NAK are ignored while waiting for the ACK.
                // 等待 ACK 期间忽略除 ACK/NAK 之外的控制包。
            }
            catch (TimeoutException)
            {
                // Retry on timeout.
                // 超时后重试。
            }
        }

        throw new IOException($"No ACK received after {MaxRetries} retries.");
    }

    private async Task WaitForAckAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        var responseBuffer = new byte[HsuartPacket.MaxPacketSize + 16];

        for (var retry = 0; retry < MaxRetries; retry++)
        {
            try
            {
                var responseBytes = await Serial.ReadAsync(responseBuffer, 0, responseBuffer.Length, timeoutMs, cancellationToken).ConfigureAwait(false);
                if (responseBytes == 0)
                {
                    continue;
                }

                DecodePacket(responseBuffer, responseBytes, out var packetId);

                if (packetId == HsuartPacketId.AckPacket)
                {
                    return;
                }

                if (packetId == HsuartPacketId.NakPacket)
                {
                    throw new IOException("Target returned NAK for HSUART packet.");
                }

                // Control packets other than ACK/NAK are ignored while waiting for the ACK.
                // 等待 ACK 期间忽略除 ACK/NAK 之外的控制包。
            }
            catch (TimeoutException)
            {
                // Retry on timeout.
                // 超时后重试。
            }
        }

        throw new IOException($"No ACK received after {MaxRetries} retries.");
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

    private void EnsureConnected()
    {
        ThrowIfDisposed();
        if (!Serial.IsConnected)
        {
            throw new InvalidOperationException("HSUART transport is not connected.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }
}