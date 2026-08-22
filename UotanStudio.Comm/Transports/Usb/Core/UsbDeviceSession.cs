using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Comm.Transports.Usb.Backend;

namespace UotanStudio.Comm.Transports.Usb.Core;

/// <summary>
/// Represents an active session over a USB device, implementing both synchronous and asynchronous I/O.
/// <para>表示 USB 设备上的活动会话，实现同步和异步 I/O。</para>
/// Reads, writes and control transfers are serialized through separate direction gates so
/// concurrent protocol threads (e.g. a status thread plus a transfer thread in adb-style
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

    public void SetInterfaceAltSetting(byte interfaceNumber, byte altSetting)
    {
        _controlGate.Wait();
        try
        {
            _device.SetInterfaceAltSetting(interfaceNumber, altSetting);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task SetInterfaceAltSettingAsync(byte interfaceNumber, byte altSetting, CancellationToken cancellationToken = default)
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

    public void SetConfiguration(byte configuration)
    {
        _controlGate.Wait();
        try
        {
            _device.SetConfiguration(configuration);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task SetConfigurationAsync(byte configuration, CancellationToken cancellationToken = default)
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
        // A transport reset must not overlap any direction; take all gates in a fixed
        // order (read → write → control) so Reset/Dispose never deadlock with each other.
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
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _device.ResetAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _controlGate.Release();
            _writeGate.Release();
            _readGate.Release();
        }
    }

    public void Dispose()
    {
        // Idempotent: a second Dispose (e.g. via UsbSessionCollection after the caller
        // already disposed the session) must not touch the released semaphores again.
        // <para>幂等：二次 Dispose 不得再次触碰已释放的信号量。</para>
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Waiting here also drains any in-flight async operation before the device is released.
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
            _readGate.Dispose();
            _writeGate.Dispose();
            _controlGate.Dispose();
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
            throw new ArgumentOutOfRangeException(nameof(length), "USB control transfers are limited to 65535 bytes (16-bit wLength).");
        }
    }
}
