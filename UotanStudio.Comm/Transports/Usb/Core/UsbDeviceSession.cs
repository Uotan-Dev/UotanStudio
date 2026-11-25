using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;

namespace UotanStudio.Comm.Transports.Usb.Core;

/// <summary>
/// Represents an active session over a USB device, implementing both synchronous and asynchronous I/O.
/// <para>表示 USB 设备上的活动会话，实现同步和异步 I/O。</para>
/// Reads, writes and control transfers are serialized through separate direction gates so
/// concurrent protocol threads (e.g. a status thread plus a transfer thread in adb-Style
/// clients) can run full-duplex: a blocked read on the IN endpoint never stalls a write on
/// the OUT endpoint. Same-direction operations remain strictly serialized to keep transfers
/// on the same endpoints from interleaving.
/// <para>读、写与控制传输通过各自方向的门闩串行化，使并发协议线程（例如 adb 风格客户端的
/// 状态线程与传输线程）可以全双工运行：IN 端点上的阻塞读不会阻塞 OUT 端点上的写。
/// 同向操作仍然严格串行，避免同一端点上的传输交错。</para>
/// </summary>
internal sealed class UsbDeviceSession(string apiName, UsbApiKind kind, UsbDevice device) : IUsbDeviceSession
{
    private readonly UsbDevice _device = device;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private int _disposed;

    public int DefaultTimeoutMs => _device.DefaultTimeoutMs;

    /// <inheritdoc/>
    public bool ResetReenumeratesDevice => _device.ResetReenumeratesDevice;

    public UsbDeviceInfo DeviceInfo { get; } = UsbDeviceInfoFactory.FromBackendDevice(apiName, kind, device);

    public byte EndpointIn => _device.EndpointIn;

    public byte EndpointOut => _device.EndpointOut;

    public byte[] Read(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (length > UsbTransferPolicies.MaxReadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), $"length exceeds the safety cap of {UsbTransferPolicies.MaxReadLength} bytes; clamp device-provided frame lengths.");
        }

        _readGate.Wait();
        try
        {
            return _device.Read(length);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public byte[] Read(int length, int timeoutMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (length > UsbTransferPolicies.MaxReadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), $"length exceeds the safety cap of {UsbTransferPolicies.MaxReadLength} bytes; clamp device-provided frame lengths.");
        }

        _readGate.Wait();
        try
        {
            return _device.Read(length, timeoutMs);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public int ReadInto(byte[] buffer, int offset, int length)
    {
        UsbDevice.ValidateBufferRange(buffer, offset, length);
        _readGate.Wait();
        try
        {
            return _device.ReadInto(buffer, offset, length);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public int ReadInto(byte[] buffer, int offset, int length, int timeoutMs)
    {
        UsbDevice.ValidateBufferRange(buffer, offset, length);
        _readGate.Wait();
        try
        {
            return _device.ReadInto(buffer, offset, length, timeoutMs);
        }
        finally
        {
            _readGate.Release();
        }
    }

#if NET8_0_OR_GREATER
    public int ReadInto(Span<byte> buffer, int timeoutMs)
    {
        _readGate.Wait();
        try
        {
            return _device.ReadInto(buffer, timeoutMs);
        }
        finally
        {
            _readGate.Release();
        }
    }
#endif

    public UsbReadResult ReadPacket(byte[] buffer, int offset, int length, int timeoutMs)
    {
        UsbDevice.ValidateBufferRange(buffer, offset, length);
        _readGate.Wait();
        try
        {
            return _device.ReadPacket(buffer, offset, length, timeoutMs);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async Task<UsbReadResult> ReadPacketAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        UsbDevice.ValidateBufferRange(buffer, offset, length);
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _device.ReadPacketAsync(buffer, offset, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async Task<UsbReadResult> ReadPacketAsync(byte[] buffer, int offset, int length, int timeoutMs, IProgress<long>? progress, CancellationToken cancellationToken = default)
    {
        UsbDevice.ValidateBufferRange(buffer, offset, length);
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _device.ReadPacketAsync(buffer, offset, length, timeoutMs, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async Task<int> ReadIntoAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        UsbDevice.ValidateBufferRange(buffer, offset, length);
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _device.ReadIntoAsync(buffer, offset, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public long Write(byte[] data, int length)
    {
        UsbDevice.ValidateWriteData(data, length);
        _writeGate.Wait();
        try
        {
            return _device.Write(data, length);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public long Write(byte[] data, int length, int timeoutMs)
    {
        UsbDevice.ValidateWriteData(data, length);
        _writeGate.Wait();
        try
        {
            return _device.Write(data, length, timeoutMs);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public long Write(byte[] data, int offset, int length, int timeoutMs)
    {
        UsbDevice.ValidateWriteData(data, offset, length);
        _writeGate.Wait();
        try
        {
            return _device.Write(data, offset, length, timeoutMs);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        UsbDevice.ValidateWriteData(data, offset, length);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _device.WriteAsync(data, offset, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, IProgress<long>? progress, CancellationToken cancellationToken = default)
    {
        UsbDevice.ValidateWriteData(data, offset, length);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _device.WriteAsync(data, offset, length, timeoutMs, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<long> WriteAsync(byte[] data, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        UsbDevice.ValidateWriteData(data, length);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _device.WriteAsync(data, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void WriteZlp(int timeoutMs)
    {
        _writeGate.Wait();
        try
        {
            _device.WriteZlp(timeoutMs);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task WriteZlpAsync(int timeoutMs, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _device.WriteZlpAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
    {
        ValidateControlTransfer(buffer, offset, length);
        _controlGate.Wait();
        try
        {
            return _device.ControlTransfer(setupPacket, buffer, offset, length, timeoutMs);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task<byte[]> ReadAsync(int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (length > UsbTransferPolicies.MaxReadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), $"length exceeds the safety cap of {UsbTransferPolicies.MaxReadLength} bytes; clamp device-provided frame lengths.");
        }

        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _device.ReadAsync(length, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async Task<int> ControlTransferAsync(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ValidateControlTransfer(buffer, offset, length);
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _device.ControlTransferAsync(setupPacket, buffer, offset, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    /// <summary>
    /// Switches the interface alternate setting after every direction gate is quiet, because the switch may
    /// re-bind or replace the pipes a transfer would still be using.
    /// <para>在所有方向门闩都空闲后切换接口备用设置，因为该切换可能重新绑定或替换传输仍在使用的管道。</para>
    /// </summary>
    public void SetInterfaceAltSetting(byte interfaceNumber, byte altSetting)
    {
        _readGate.Wait();
        _writeGate.Wait();
        _controlGate.Wait();
        try
        {
            _device.SetInterfaceAltSetting(interfaceNumber, altSetting);
        }
        finally
        {
            _controlGate.Release();
            _writeGate.Release();
            _readGate.Release();
        }
    }

    /// <summary>Asynchronous <see cref="SetInterfaceAltSetting"/> with the same gate ordering.
    /// <para>与 <see cref="SetInterfaceAltSetting"/> 具有相同门闩顺序的异步版本。</para></summary>
    public async Task SetInterfaceAltSettingAsync(byte interfaceNumber, byte altSetting, CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _device.SetInterfaceAltSetting(interfaceNumber, altSetting);
                }
                finally
                {
                    _controlGate.Release();
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <summary>
    /// Switches the device configuration after every direction gate is quiet, because a new configuration
    /// invalidates the interfaces and pipes the session was bound to.
    /// <para>在所有方向门闩都空闲后切换设备配置，因为新配置会使会话绑定的接口与管道失效。</para>
    /// </summary>
    public void SetConfiguration(byte configuration)
    {
        _readGate.Wait();
        _writeGate.Wait();
        _controlGate.Wait();
        try
        {
            _device.SetConfiguration(configuration);
        }
        finally
        {
            _controlGate.Release();
            _writeGate.Release();
            _readGate.Release();
        }
    }

    /// <summary>Asynchronous <see cref="SetConfiguration"/> with the same gate ordering.
    /// <para>与 <see cref="SetConfiguration"/> 具有相同门闩顺序的异步版本。</para></summary>
    public async Task SetConfigurationAsync(byte configuration, CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _device.SetConfiguration(configuration);
                }
                finally
                {
                    _controlGate.Release();
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }
        finally
        {
            _readGate.Release();
        }
    }

    public UsbReadResult ReadInterrupt(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs)
    {
        UsbDevice.ValidateBufferRange(buffer, offset, length);
        _readGate.Wait();
        try
        {
            return _device.ReadInterrupt(endpointAddress, buffer, offset, length, timeoutMs);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async Task<UsbReadResult> ReadInterruptAsync(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        UsbDevice.ValidateBufferRange(buffer, offset, length);
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _device.ReadInterruptAsync(endpointAddress, buffer, offset, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public long WriteInterrupt(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs)
    {
        UsbDevice.ValidateWriteData(data, offset, length);
        _writeGate.Wait();
        try
        {
            return _device.WriteInterrupt(endpointAddress, data, offset, length, timeoutMs);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<long> WriteInterruptAsync(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        UsbDevice.ValidateWriteData(data, offset, length);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _device.WriteInterruptAsync(endpointAddress, data, offset, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Reset()
    {
        _readGate.Wait();
        _writeGate.Wait();
        _controlGate.Wait();
        try
        {
            _device.Reset();
        }
        finally
        {
            _controlGate.Release();
            _writeGate.Release();
            _readGate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await _device.ResetAsync(cancellationToken).ConfigureAwait(false);
                }
                finally { _controlGate.Release(); }
            }
            finally { _writeGate.Release(); }
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <summary>
    /// Releases the underlying device handle while leaving the direction gates alive.
    /// <para>释放底层设备句柄，但保持各方向门闩存活。</para>
    /// </summary>
    /// <remarks>
    /// In-flight reads, writes and control transfers — including protocol message loops that
    /// outlive a failed handshake — acquire and release these gates concurrently with disposal.
    /// The gates never expose <see cref="SemaphoreSlim.AvailableWaitHandle"/>, so disposing them
    /// only turns a normal transport failure into an <see cref="ObjectDisposedException"/> on a
    /// concurrent <c>Release()</c> and mislabels the real I/O error. They are managed objects with
    /// no unmanaged handle until that property is observed, so the garbage collector reclaims them
    /// with the session.
    /// <para>在途的读、写与控制传输（包括比失败握手更晚结束的协议消息循环）会与释放并发地获取和
    /// 归还这些门闩。它们从不暴露 <see cref="SemaphoreSlim.AvailableWaitHandle"/>，因此释放它们
    /// 只会把正常的传输故障在并发 <c>Release()</c> 处变成 <see cref="ObjectDisposedException"/>，
    /// 掩盖真正的 I/O 错误。在未观察该属性前它们没有非托管句柄，属于可随会话一同被垃圾回收的
    /// 托管对象。</para>
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _readGate.Wait();
        _writeGate.Wait();
        _controlGate.Wait();
        try
        {
            _device.Dispose();
        }
        finally
        {
            _controlGate.Release();
            _writeGate.Release();
            _readGate.Release();
        }
    }

    private static void ValidateControlTransfer(byte[]? buffer, int offset, int length)
    {
        if (buffer == null)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(length, 0);
        }
        else
        {
            UsbDevice.ValidateBufferRange(buffer, offset, length);
        }

        if (length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "USB control transfers are limited to 65535 bytes (16-bit WLength).");
        }
    }
}