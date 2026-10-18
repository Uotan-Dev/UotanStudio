using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend;

internal abstract partial class UsbDevice
{
    /// <summary>
    /// Performs a USB control transfer.
    /// <para>执行 USB 控制传输。</para>
    /// </summary>
    /// <param name="setupPacket">The setup packet for the control request. <para>控制请求的 Setup 包。</para></param>
    /// <param name="buffer">The data buffer, or <c>null</c> for zero-length transfers. <para>数据缓冲区；零长度传输时为 <c>null</c>。</para></param>
    /// <param name="offset">The byte offset within the buffer. <para>缓冲区内的字节偏移量。</para></param>
    /// <param name="length">The number of bytes to transfer. <para>要传输的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes transferred. <para>已传输的字节数。</para></returns>
    /// <exception cref="NotSupportedException">Thrown when the device does not support control transfers. <para>当设备不支持控制传输时抛出。</para></exception>
    public virtual int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs) => throw new NotSupportedException($"{GetType().Name} does not support control transfers.");

    /// <summary>
    /// Sends a zero-length packet (ZLP) on the bulk OUT endpoint.
    /// <para>在批量 OUT 端点上发送零长度包（ZLP）。</para>
    /// The default implementation reports the capability as unsupported; backends that can
    /// perform an explicit zero-length bulk write override it.
    /// <para>默认实现报告该能力不受支持；可执行显式零长度批量写入的后端覆盖此方法。</para>
    /// </summary>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    public virtual void WriteZlp(int timeoutMs)
        => throw new NotSupportedException("The backend does not support explicit zero-length packet (ZLP) writes.");

    /// <summary>
    /// Asynchronously sends a zero-length packet (ZLP) on the bulk OUT endpoint.
    /// <para>异步在批量 OUT 端点上发送零长度包（ZLP）。</para>
    /// </summary>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that represents the zero-length write operation. <para>表示零长度写入操作的任务。</para></returns>
    public virtual Task WriteZlpAsync(int timeoutMs, CancellationToken cancellationToken = default)
        => UsbAsyncExecution.Run(() => WriteZlp(timeoutMs), cancellationToken);

    /// <summary>
    /// Asynchronously performs a USB control transfer.
    /// <para>异步执行 USB 控制传输。</para>
    /// </summary>
    /// <param name="setupPacket">The setup packet for the control request. <para>控制请求的 Setup 包。</para></param>
    /// <param name="buffer">The data buffer, or <c>null</c> for zero-length transfers. <para>数据缓冲区；零长度传输时为 <c>null</c>。</para></param>
    /// <param name="offset">The byte offset within the buffer. <para>缓冲区内的字节偏移量。</para></param>
    /// <param name="length">The number of bytes to transfer. <para>要传输的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that represents the asynchronous control transfer with the number of bytes transferred. <para>表示异步控制传输并返回已传输字节数的任务。</para></returns>
    public virtual Task<int> ControlTransferAsync(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default) => UsbAsyncExecution.Run(() => ControlTransfer(setupPacket, buffer, offset, length, timeoutMs), cancellationToken);

    /// <summary>
    /// Sets the active alternate setting of an interface via the standard SET_INTERFACE
    /// control request (request 0x0B, WValue = alternate setting, WIndex = interface number).
    /// <para>通过标准 SET_INTERFACE 控制请求（请求 0x0B，WValue = 备用设置，WIndex = 接口编号）
    /// 设置接口的活动备用设置。</para>
    /// </summary>
    /// <param name="interfaceNumber">The interface number. <para>接口编号。</para></param>
    /// <param name="altSetting">The alternate setting to activate. <para>要激活的备用设置。</para></param>
    public virtual void SetInterfaceAltSetting(byte interfaceNumber, byte altSetting)
    {
        var setup = new UsbSetupPacket
        {
            RequestType = 0x01,
            Request = 0x0B,
            Value = altSetting,
            Index = interfaceNumber,
            Length = 0
        };
        _ = ControlTransfer(setup, null, 0, 0, DefaultTimeoutMs);
    }

    /// <summary>
    /// Sets the active device configuration via the standard SET_CONFIGURATION control
    /// request (request 0x09, WValue = configuration value).
    /// <para>通过标准 SET_CONFIGURATION 控制请求（请求 0x09，WValue = 配置值）
    /// 设置设备的活动配置。</para>
    /// </summary>
    /// <param name="configuration">The configuration value to activate. <para>要激活的配置值。</para></param>
    public virtual void SetConfiguration(byte configuration)
    {
        var setup = new UsbSetupPacket
        {
            RequestType = 0x00,
            Request = 0x09,
            Value = configuration,
            Index = 0,
            Length = 0
        };
        _ = ControlTransfer(setup, null, 0, 0, DefaultTimeoutMs);
    }

    /// <summary>Reads bytes from an interrupt IN endpoint into a caller-provided buffer.
    /// <para>从中断 IN 端点将数据读取到调用方提供的缓冲区。</para>The default implementation reports the capability as unsupported; backends that can perform interrupt transfers (usbfs/libusb/WinUSB) override this method.<para>默认实现报告该能力不受支持；可执行中断传输的后端（usbfs/libusb/WinUSB） 覆盖此方法。</para></summary>
    /// <param name="endpointAddress">The interrupt IN endpoint address (bit 7 set).
    /// <para>中断 IN 端点地址（bit 7 置位）。</para></param>
    /// <param name="buffer">The destination buffer.<para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset.<para>目标偏移量。</para></param>
    /// <param name="length">Maximum number of bytes to read.<para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds.<para>超时时间（毫秒）。</para></param>
    /// <returns>The read result with byte count and outcome flags.
    /// <para>包含字节数与结果标志的读取结果。</para></returns>
    public virtual UsbReadResult ReadInterrupt(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs)
        => throw new NotSupportedException("The backend does not support interrupt endpoint transfers.");

    /// <summary>
    /// Writes bytes to an interrupt OUT endpoint.
    /// <para>向中断 OUT 端点写入字节数据。</para>
    /// </summary>
    /// <param name="endpointAddress">The interrupt OUT endpoint address (bit 7 clear). <para>中断 OUT 端点地址（bit 7 清零）。</para></param>
    /// <param name="data">The data to write. <para>要写入的数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes written. <para>实际写入的字节数。</para></returns>
    public virtual long WriteInterrupt(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs)
        => throw new NotSupportedException("The backend does not support interrupt endpoint transfers.");

    /// <summary>
    /// Reads bytes from an interrupt IN endpoint into a caller-provided buffer asynchronously.
    /// <para>异步从中断 IN 端点将数据读取到调用方提供的缓冲区。</para>
    /// </summary>
    /// <param name="endpointAddress">The interrupt IN endpoint address (bit 7 set). <para>中断 IN 端点地址（bit 7 置位）。</para></param>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">Maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that resolves to the read result. <para>返回读取结果的任务。</para></returns>
    public virtual Task<UsbReadResult> ReadInterruptAsync(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        => UsbAsyncExecution.Run(() => ReadInterrupt(endpointAddress, buffer, offset, length, timeoutMs), cancellationToken);

    /// <summary>
    /// Writes bytes to an interrupt OUT endpoint asynchronously.
    /// <para>异步向中断 OUT 端点写入字节数据。</para>
    /// </summary>
    /// <param name="endpointAddress">The interrupt OUT endpoint address (bit 7 clear). <para>中断 OUT 端点地址（bit 7 清零）。</para></param>
    /// <param name="data">The data to write. <para>要写入的数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that resolves to the number of bytes written. <para>返回实际写入字节数的任务。</para></returns>
    public virtual Task<long> WriteInterruptAsync(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        => UsbAsyncExecution.Run(() => WriteInterrupt(endpointAddress, data, offset, length, timeoutMs), cancellationToken);

    /// <summary>
    /// Retrieves the serial number string from the device.
    /// <para>从设备获取序列号字符串。</para>
    /// </summary>
    /// <returns>Zero on success; a negative error code on failure. <para>成功时返回零；失败时返回负数错误码。</para></returns>
    public abstract int GetSerialNumber();

    /// <summary>
    /// Opens the device handle and claims the USB interface.
    /// <para>打开设备句柄并声明 USB 接口。</para>
    /// </summary>
    /// <returns>Zero on success; a non-zero error code on failure. <para>成功时返回零；失败时返回非零错误码。</para></returns>
    public abstract int CreateHandle();

    /// <summary>
    /// Resets the USB device.
    /// <para>重置 USB 设备。</para>
    /// </summary>
    public abstract void Reset();

    /// <summary>
    /// Asynchronously resets the USB device.
    /// <para>异步重置 USB 设备。</para>
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that represents the asynchronous reset operation. <para>表示异步重置操作的任务。</para></returns>
    public virtual Task ResetAsync(CancellationToken cancellationToken = default) => UsbAsyncExecution.Run(Reset, cancellationToken);

    /// <summary>
    /// Gets whether the native device handle is currently acquired (the device is open).
    /// <para>获取是否已获取原生设备句柄（设备处于打开状态）。</para>
    /// Most backends only create devices after a successful open, so the default is
    /// <c>true</c>; the libusb backend overrides it because its finder reports busy
    /// devices (occupied by another session/process) with metadata only.
    /// <para>大多数后端仅在成功打开后创建设备，因此默认值为 <c>true</c>；libusb 后端重写
    /// 该方法，因为其 finder 会以仅元数据的形式报告被占用设备（被其他会话/进程占用）。</para>
    /// </summary>
    internal virtual bool IsHandleOpen => true;

    /// <summary>
    /// Releases the device handle and all associated resources.
    /// <para>释放设备句柄及所有关联资源。</para>
    /// </summary>
    public abstract void Dispose();
}