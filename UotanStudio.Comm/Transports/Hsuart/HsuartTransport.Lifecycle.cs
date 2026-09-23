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
    /// Connects to the HSUART device and performs version negotiation.
    /// <para>连接 HSUART 设备并执行版本协商。</para>
    /// </summary>
    /// <exception cref="IOException">Version negotiation was rejected (NAK) or failed to encode. <para>版本协商被拒绝（NAK）或编码失败。</para></exception>
    public void Connect()
    {
        ThrowIfDisposed();
        var disconnectGeneration = Volatile.Read(ref _disconnectGeneration);
        _lifecycleGate.Wait();
        try
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _disconnectRequests) != 0 ||
                disconnectGeneration != Volatile.Read(ref _disconnectGeneration))
            {
                throw new InvalidOperationException("HSUART transport is disconnecting.");
            }
            _writeGate.Wait();
            try
            {
                _readGate.Wait();
                try
                {
                    _sessionGate.Wait();
                    try
                    {
                        ThrowIfDisposed();
                        ClearSessionState();
                        RenewSessionCancellation();
                        Serial.Connect();
                        if (Volatile.Read(ref _disconnectRequests) != 0 ||
                            disconnectGeneration != Volatile.Read(ref _disconnectGeneration))
                        {
                            Serial.Disconnect();
                            throw new InvalidOperationException("HSUART transport is disconnecting.");
                        }
                        try
                        {
                            PerformVersionNegotiation();
                        }
                        catch
                        {
                            ClearSessionState();
                            Serial.Disconnect();
                            throw;
                        }
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
        finally
        {
            _lifecycleGate.Release();
        }
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
        var disconnectGeneration = Volatile.Read(ref _disconnectGeneration);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _disconnectRequests) != 0 ||
                disconnectGeneration != Volatile.Read(ref _disconnectGeneration))
            {
                throw new InvalidOperationException("HSUART transport is disconnecting.");
            }
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        ThrowIfDisposed();
                        ClearSessionState();
                        RenewSessionCancellation();
                        await Serial.ConnectAsync(cancellationToken).ConfigureAwait(false);
                        if (Volatile.Read(ref _disconnectRequests) != 0 ||
                            disconnectGeneration != Volatile.Read(ref _disconnectGeneration))
                        {
                            Serial.Disconnect();
                            throw new InvalidOperationException("HSUART transport is disconnecting.");
                        }
                        try
                        {
                            await PerformVersionNegotiationAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                            ClearSessionState();
                            Serial.Disconnect();
                            throw;
                        }
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
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Disconnects from the HSUART device.
    /// <para>断开与 HSUART 设备的连接。</para>
    /// </summary>
    public void Disconnect()
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _disconnectRequests);
        Interlocked.Increment(ref _disconnectGeneration);
        CancelSession();
        Exception? disconnectError = null;
        try
        {
            try
            {
                Serial.Disconnect();
            }
            catch (Exception ex)
            {
                disconnectError = ex;
            }

            _lifecycleGate.Wait();
            try
            {
                _writeGate.Wait();
                try
                {
                    _readGate.Wait();
                    try
                    {
                        _sessionGate.Wait();
                        try
                        {
                            ClearSessionState();
                            RenewSessionCancellation();
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
            finally
            {
                _lifecycleGate.Release();
            }

            if (disconnectError is not null)
            {
                ExceptionDispatchInfo.Capture(disconnectError).Throw();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _disconnectRequests);
        }
    }

    /// <summary>
    /// Disposes the transport, releasing the underlying serial transport.
    /// <para>释放传输及其底层串口传输。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _lifetimeCts.Cancel();
        CancelSession();
        Exception? disposeError = null;
        try
        {
            Serial.Dispose();
        }
        catch (Exception ex)
        {
            disposeError = ex;
        }
        _lifecycleGate.Wait();
        try
        {
            _writeGate.Wait();
            try
            {
                _readGate.Wait();
                try
                {
                    _sessionGate.Wait();
                    try
                    {
                        _disposed = true;
                        _versionNegotiated = false;
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
        finally
        {
            _lifecycleGate.Release();
        }

        if (disposeError is not null)
        {
            ExceptionDispatchInfo.Capture(disposeError).Throw();
        }
    }

    private void CancelSession()
    {
        lock (_sessionCtsLock)
        {
            _sessionCts.Cancel();
        }
    }

    private void RenewSessionCancellation()
    {
        lock (_sessionCtsLock)
        {
            _sessionCts.Dispose();
            _sessionCts = new CancellationTokenSource();
        }
    }

    private CancellationTokenSource CreateSessionLinkedTokenSource(CancellationToken cancellationToken)
    {
        lock (_sessionCtsLock)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetimeCts.Token, _sessionCts.Token);
        }
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
            var response = ReadNegotiationResponse(VersionNegotiationTimeoutMs);
            DecodePacket(response, response.Length, out var packetId);
            if (packetId == HsuartPacketId.VersionPacket)
            {
                SendAck();
            }
            else if (packetId == HsuartPacketId.NakPacket)
            {
                throw new IOException("HSUART version negotiation rejected by device (NAK).");
            }
        }
        catch (TimeoutException)
        {
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
            var response = await ReadNegotiationResponseAsync(VersionNegotiationTimeoutMs, cancellationToken).ConfigureAwait(false);
            DecodePacket(response, response.Length, out var packetId);
            if (packetId == HsuartPacketId.VersionPacket)
            {
                await SendAckAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (packetId == HsuartPacketId.NakPacket)
            {
                throw new IOException("HSUART version negotiation rejected by device (NAK).");
            }
        }
        catch (TimeoutException)
        {
        }

        _versionNegotiated = true;
    }

    private void ClearSessionState()
    {
        _versionNegotiated = false;
        ClearBufferedData();
    }

    private void ClearBufferedData()
    {
        _frameParser.Reset();
        _receivedFrames.Clear();
        _receivedPayloads.Clear();
        _pendingPayload = null;
        _pendingPayloadOffset = 0;
    }

    private void EnsureSessionConnected()
    {
        EnsureConnected();
        PerformVersionNegotiation();
    }

    private async Task EnsureSessionConnectedAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        await PerformVersionNegotiationAsync(cancellationToken).ConfigureAwait(false);
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
        if (Volatile.Read(ref _disposeStarted) != 0 || Volatile.Read(ref _disposed))
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }
}