namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// <para>当 USB 设备句柄已关闭或无效时抛出的异常。</para>
/// Thrown when a USB device handle is closed or invalid.
/// </summary>
internal sealed class UsbDeviceHandleClosedException : InvalidOperationException
{
    /// <summary>
    /// <para>使用指定错误消息初始化异常。</para>
    /// Initializes the exception with the specified error message.
    /// </summary>
    /// <param name="message">
    /// <para>描述错误的消息。</para>
    /// The message that describes the error.
    /// </param>
    public UsbDeviceHandleClosedException(string message) : base(message)
    {
    }

    /// <summary>
    /// <para>使用指定错误消息和内部异常初始化异常。</para>
    /// Initializes the exception with the specified error message and inner exception.
    /// </summary>
    /// <param name="message">
    /// <para>描述错误的消息。</para>
    /// The message that describes the error.
    /// </param>
    /// <param name="innerException">
    /// <para>导致当前异常的内部异常。</para>
    /// The inner exception that caused the current exception.
    /// </param>
    public UsbDeviceHandleClosedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// <para>当 USB 传输操作失败时抛出的异常。</para>
/// Thrown when a USB transfer operation fails.
/// </summary>
internal sealed class UsbTransferException : IOException
{
    /// <summary>
    /// <para>获取或设置后端名称。</para>
    /// Gets or sets the backend name.
    /// </summary>
    public string? Backend { get; set; }

    /// <summary>
    /// <para>获取或设置原生错误码。</para>
    /// Gets or sets the native error code.
    /// </summary>
    public int? NativeErrorCode { get; set; }

    /// <summary>
    /// <para>使用指定错误消息初始化异常。</para>
    /// Initializes the exception with the specified error message.
    /// </summary>
    /// <param name="message">
    /// <para>描述错误的消息。</para>
    /// The message that describes the error.
    /// </param>
    public UsbTransferException(string message) : base(message)
    {
    }

    /// <summary>
    /// <para>使用指定错误消息和原生错误码初始化异常。</para>
    /// Initializes the exception with the specified error message and native error code.
    /// </summary>
    /// <param name="message">
    /// <para>描述错误的消息。</para>
    /// The message that describes the error.
    /// </param>
    /// <param name="nativeErrorCode">
    /// <para>原生错误码。</para>
    /// The native error code.
    /// </param>
    public UsbTransferException(string message, int nativeErrorCode) : base(message)
    {
        NativeErrorCode = nativeErrorCode;
    }

    /// <summary>
    /// <para>使用指定错误消息和内部异常初始化异常。</para>
    /// Initializes the exception with the specified error message and inner exception.
    /// </summary>
    /// <param name="message">
    /// <para>描述错误的消息。</para>
    /// The message that describes the error.
    /// </param>
    /// <param name="innerException">
    /// <para>导致当前异常的内部异常。</para>
    /// The inner exception that caused the current exception.
    /// </param>
    public UsbTransferException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// <para>当 USB 设备打开失败时抛出的异常。</para>
/// Thrown when a USB device fails to open.
/// </summary>
internal sealed class UsbDeviceOpenException : InvalidOperationException
{
    /// <summary>
    /// <para>获取或设置设备路径。</para>
    /// Gets or sets the device path.
    /// </summary>
    public string? DevicePath { get; set; }

    /// <summary>
    /// <para>获取或设置原生错误码。</para>
    /// Gets or sets the native error code.
    /// </summary>
    public int? NativeErrorCode { get; set; }

    /// <summary>
    /// <para>使用指定错误消息初始化异常。</para>
    /// Initializes the exception with the specified error message.
    /// </summary>
    /// <param name="message">
    /// <para>描述错误的消息。</para>
    /// The message that describes the error.
    /// </param>
    public UsbDeviceOpenException(string message) : base(message)
    {
    }

    /// <summary>
    /// <para>使用指定错误消息、设备路径和原生错误码初始化异常。</para>
    /// Initializes the exception with the specified error message, device path, and native error code.
    /// </summary>
    /// <param name="message">
    /// <para>描述错误的消息。</para>
    /// The message that describes the error.
    /// </param>
    /// <param name="devicePath">
    /// <para>设备路径。</para>
    /// The device path.
    /// </param>
    /// <param name="nativeErrorCode">
    /// <para>原生错误码。</para>
    /// The native error code.
    /// </param>
    public UsbDeviceOpenException(string message, string devicePath, int nativeErrorCode) : base(message)
    {
        DevicePath = devicePath;
        NativeErrorCode = nativeErrorCode;
    }
}

/// <summary>
/// <para>当 USB 设备在传输期间被拔出或断开时抛出的异常。</para>
/// Thrown when the USB device is unplugged or disconnected during a transfer.
/// <para>上层协议（adb/fastboot/EDL 等）可捕获此异常以触发重枚举/重连逻辑，
/// 它与一般 I/O 错误（<see cref="UsbTransferException"/>、IOException）相区分。</para>
/// Upper-layer protocols (adb/fastboot/EDL etc.) can catch this exception to trigger
/// re-enumeration / reconnect logic; it is distinct from ordinary I/O errors.
/// </summary>
internal sealed class UsbDeviceDisconnectedException : IOException
{
    /// <summary>
    /// <para>获取或设置后端名称。</para>
    /// Gets or sets the backend name.
    /// </summary>
    public string? Backend { get; set; }

    /// <summary>
    /// <para>获取或设置原生错误码。</para>
    /// Gets or sets the native error code.
    /// </summary>
    public int? NativeErrorCode { get; set; }

    /// <summary>
    /// <para>使用指定错误消息初始化异常。</para>
    /// Initializes the exception with the specified error message.
    /// </summary>
    /// <param name="message">
    /// <para>描述错误的消息。</para>
    /// The message that describes the error.
    /// </param>
    public UsbDeviceDisconnectedException(string message) : base(message)
    {
    }

    /// <summary>
    /// <para>使用指定错误消息和原生错误码初始化异常。</para>
    /// Initializes the exception with the specified error message and native error code.
    /// </summary>
    /// <param name="message">
    /// <para>描述错误的消息。</para>
    /// The message that describes the error.
    /// </param>
    /// <param name="nativeErrorCode">
    /// <para>原生错误码。</para>
    /// The native error code.
    /// </param>
    public UsbDeviceDisconnectedException(string message, int nativeErrorCode) : base(message)
    {
        NativeErrorCode = nativeErrorCode;
    }

    /// <summary>
    /// <para>使用指定错误消息和内部异常初始化异常。</para>
    /// Initializes the exception with the specified error message and inner exception.
    /// </summary>
    /// <param name="message">
    /// <para>描述错误的消息。</para>
    /// The message that describes the error.
    /// </param>
    /// <param name="innerException">
    /// <para>导致当前异常的内部异常。</para>
    /// The inner exception that caused the current exception.
    /// </param>
    public UsbDeviceDisconnectedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}