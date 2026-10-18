namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Thrown when a USB device handle is closed or invalid.
/// <para>当 USB 设备句柄已关闭或无效时抛出的异常。</para>
/// </summary>
internal sealed class UsbDeviceHandleClosedException : InvalidOperationException
{
    /// <summary>
    /// Initializes the exception with the specified error message.
    /// <para>使用指定错误消息初始化异常。</para>
    /// </summary>
    /// <param name="message">
    /// The message that describes the error.
    /// <para>描述错误的消息。</para>
    /// </param>
    public UsbDeviceHandleClosedException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes the exception with the specified error message and inner exception.
    /// <para>使用指定错误消息和内部异常初始化异常。</para>
    /// </summary>
    /// <param name="message">
    /// The message that describes the error.
    /// <para>描述错误的消息。</para>
    /// </param>
    /// <param name="innerException">
    /// The inner exception that caused the current exception.
    /// <para>导致当前异常的内部异常。</para>
    /// </param>
    public UsbDeviceHandleClosedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a USB transfer operation fails.
/// <para>当 USB 传输操作失败时抛出的异常。</para>
/// </summary>
internal sealed class UsbTransferException : IOException
{
    /// <summary>
    /// Gets or sets the backend name.
    /// <para>获取或设置后端名称。</para>
    /// </summary>
    public string? Backend { get; set; }

    /// <summary>
    /// Gets or sets the native error code.
    /// <para>获取或设置原生错误码。</para>
    /// </summary>
    public int? NativeErrorCode { get; set; }

    /// <summary>
    /// Initializes the exception with the specified error message.
    /// <para>使用指定错误消息初始化异常。</para>
    /// </summary>
    /// <param name="message">
    /// The message that describes the error.
    /// <para>描述错误的消息。</para>
    /// </param>
    public UsbTransferException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes the exception with the specified error message and native error code.
    /// <para>使用指定错误消息和原生错误码初始化异常。</para>
    /// </summary>
    /// <param name="message">
    /// The message that describes the error.
    /// <para>描述错误的消息。</para>
    /// </param>
    /// <param name="nativeErrorCode">
    /// The native error code.
    /// <para>原生错误码。</para>
    /// </param>
    public UsbTransferException(string message, int nativeErrorCode) : base(message)
    {
        NativeErrorCode = nativeErrorCode;
    }

    /// <summary>
    /// Initializes the exception with the specified error message and inner exception.
    /// <para>使用指定错误消息和内部异常初始化异常。</para>
    /// </summary>
    /// <param name="message">
    /// The message that describes the error.
    /// <para>描述错误的消息。</para>
    /// </param>
    /// <param name="innerException">
    /// The inner exception that caused the current exception.
    /// <para>导致当前异常的内部异常。</para>
    /// </param>
    public UsbTransferException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a USB device fails to open.
/// <para>当 USB 设备打开失败时抛出的异常。</para>
/// </summary>
internal sealed class UsbDeviceOpenException : InvalidOperationException
{
    /// <summary>
    /// Gets or sets the device path.
    /// <para>获取或设置设备路径。</para>
    /// </summary>
    public string? DevicePath { get; set; }

    /// <summary>
    /// Gets or sets the native error code.
    /// <para>获取或设置原生错误码。</para>
    /// </summary>
    public int? NativeErrorCode { get; set; }

    /// <summary>
    /// Initializes the exception with the specified error message.
    /// <para>使用指定错误消息初始化异常。</para>
    /// </summary>
    /// <param name="message">
    /// The message that describes the error.
    /// <para>描述错误的消息。</para>
    /// </param>
    public UsbDeviceOpenException(string message) : base(message)
    {
    }

    /// <summary>Initializes the exception with the specified error message, device path, and native error code.
    /// <para>使用指定错误消息、设备路径和原生错误码初始化异常。</para></summary>
    /// <param name="message">The message that describes the error.<para>描述错误的消息。</para></param>
    /// <param name="devicePath">The device path.<para>设备路径。</para></param>
    /// <param name="nativeErrorCode">The native error code.<para>原生错误码。</para></param>
    public UsbDeviceOpenException(string message, string devicePath, int nativeErrorCode) : base(message)
    {
        DevicePath = devicePath;
        NativeErrorCode = nativeErrorCode;
    }
}

/// <summary>
/// Thrown when the USB device is unplugged or disconnected during a transfer.
/// Upper-layer protocols (adb/fastboot/EDL etc.) can catch this exception to trigger
/// re-enumeration / reconnect logic; it is distinct from ordinary I/O errors.
/// <para>当 USB 设备在传输期间被拔出或断开时抛出的异常。</para>
/// <para>上层协议（adb/fastboot/EDL 等）可捕获此异常以触发重枚举/重连逻辑，
/// 它与一般 I/O 错误（<see cref="UsbTransferException"/>、IOException）相区分。</para>
/// </summary>
internal sealed class UsbDeviceDisconnectedException : IOException
{
    /// <summary>
    /// Gets or sets the backend name.
    /// <para>获取或设置后端名称。</para>
    /// </summary>
    public string? Backend { get; set; }

    /// <summary>
    /// Gets or sets the native error code.
    /// <para>获取或设置原生错误码。</para>
    /// </summary>
    public int? NativeErrorCode { get; set; }

    /// <summary>
    /// Initializes the exception with the specified error message.
    /// <para>使用指定错误消息初始化异常。</para>
    /// </summary>
    /// <param name="message">
    /// The message that describes the error.
    /// <para>描述错误的消息。</para>
    /// </param>
    public UsbDeviceDisconnectedException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes the exception with the specified error message and native error code.
    /// <para>使用指定错误消息和原生错误码初始化异常。</para>
    /// </summary>
    /// <param name="message">
    /// The message that describes the error.
    /// <para>描述错误的消息。</para>
    /// </param>
    /// <param name="nativeErrorCode">
    /// The native error code.
    /// <para>原生错误码。</para>
    /// </param>
    public UsbDeviceDisconnectedException(string message, int nativeErrorCode) : base(message)
    {
        NativeErrorCode = nativeErrorCode;
    }

    /// <summary>
    /// Initializes the exception with the specified error message and inner exception.
    /// <para>使用指定错误消息和内部异常初始化异常。</para>
    /// </summary>
    /// <param name="message">
    /// The message that describes the error.
    /// <para>描述错误的消息。</para>
    /// </param>
    /// <param name="innerException">
    /// The inner exception that caused the current exception.
    /// <para>导致当前异常的内部异常。</para>
    /// </param>
    public UsbDeviceDisconnectedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}