using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend;

/// <summary>
/// Represents an abstract USB device that provides read, write, control transfer, and lifecycle operations.
/// <para>表示一个抽象 USB 设备，提供读取、写入、控制传输及生命周期操作。</para>
/// </summary>
internal abstract partial class UsbDevice : IDisposable
{
    /// <summary>
    /// Gets the default timeout in milliseconds for this device.
    /// <para>获取此设备的默认超时时间（毫秒）。</para>
    /// </summary>
    public virtual int DefaultTimeoutMs => UsbTransferPolicies.DefaultTimeoutMs;

    /// <summary>
    /// Gets or sets the device path used to open the handle.
    /// <para>获取或设置用于打开句柄的设备路径。</para>
    /// </summary>
    public string DevicePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the serial number of the device.
    /// <para>获取或设置设备的序列号。</para>
    /// </summary>
    public string? SerialNumber { get; set; }

    /// <summary>OS-provided identity shared by the interfaces of one physical device, when available. <para>操作系统提供的、同一物理设备各接口共享的身份（如可用）。</para></summary>
    public Guid? ContainerId { get; set; }

    /// <summary>Gets or sets the observed bus and physical port chain, not a device identity.
    /// <para>获取或设置观测到的总线与物理端口链，而非设备身份。</para></summary>
    public string? PhysicalPortPath { get; set; }

    /// <summary>OS device-instance identity for this USB interface, when available. <para>该 USB 接口的操作系统设备实例身份（如可用）。</para></summary>
    public string? DeviceInstanceId { get; set; }

    /// <summary>
    /// Gets or sets the USB vendor identifier.
    /// <para>获取或设置 USB 厂商标识。</para>
    /// </summary>
    public ushort VendorId { get; set; }

    /// <summary>
    /// Gets or sets the USB product identifier.
    /// <para>获取或设置 USB 产品标识。</para>
    /// </summary>
    public ushort ProductId { get; set; }

    /// <summary>
    /// Gets or sets the interface class code.
    /// <para>获取或设置接口类代码。</para>
    /// </summary>
    public byte? InterfaceClass { get; set; }

    /// <summary>
    /// Gets or sets the interface subclass code.
    /// <para>获取或设置接口子类代码。</para>
    /// </summary>
    public byte? InterfaceSubClass { get; set; }

    /// <summary>
    /// Gets or sets the interface protocol code.
    /// <para>获取或设置接口协议代码。</para>
    /// </summary>
    public byte? InterfaceProtocol { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether interface metadata has been observed from the device descriptor.
    /// <para>获取或设置一个值，指示是否已从设备描述符中观测到接口元数据。</para>
    /// </summary>
    public bool InterfaceMetadataObserved { get; set; }

    /// <summary>
    /// Gets or sets the USB transfer speed reported by the backend, when available.
    /// <para>获取或设置后端报告的 USB 传输速度（若可用）。</para>
    /// </summary>
    public UsbDeviceSpeed Speed { get; set; }

    /// <summary>
    /// Gets or sets the interfaces (and their endpoints) observed for this device.
    /// <para>获取或设置观测到的设备接口（及其端点）列表。</para>
    /// </summary>
    public IReadOnlyList<UsbInterfaceInfo> Interfaces { get; set; } = [];

    /// <summary>
    /// Gets the bulk IN endpoint address bound to this session (bit 7 set), or 0 when
    /// the backend does not expose endpoint-level binding (e.g. a legacy driver).
    /// <para>获取绑定到本会话的批量 IN 端点地址（bit 7 置位），后端不暴露端点绑定时为 0
    /// （例如旧式驱动）。</para>
    /// Subclasses override this with their concrete endpoint field so sessions can
    /// report which endpoints a protocol layer actually talks to.
    /// <para>子类用各自的具体端点字段重写此属性，使会话能够报告协议层实际通信的端点。</para>
    /// </summary>
    public virtual byte EndpointIn => 0;

    /// <summary>
    /// Gets the bulk OUT endpoint address bound to this session (bit 7 clear), or 0 when
    /// the backend does not expose endpoint-level binding (e.g. a legacy driver).
    /// <para>获取绑定到本会话的批量 OUT 端点地址（bit 7 清零），后端不暴露端点绑定时为 0
    /// （例如旧式驱动）。</para>
    /// </summary>
    public virtual byte EndpointOut => 0;

    /// <summary>
    /// Gets or sets the platform-specific USB device type.
    /// <para>获取或设置平台特定的 USB 设备类型。</para>
    /// </summary>
    public UsbDeviceType UsbDeviceType { get; set; }

    /// <summary>
    /// Gets the backend tag used in transfer trace events.
    /// <para>获取传输跟踪事件中使用的后端标签。</para>
    /// </summary>
    protected abstract string BackendName { get; }

    /// <summary>
    /// Gets the maximum payload size for a single chunk transfer.
    /// <para>获取单次分块传输的最大载荷大小。</para>
    /// </summary>
    protected virtual int MaxChunkSize => UsbTransferPolicies.MaxChunkSize;

    /// <summary>
    /// Gets a value indicating whether the device handle is open and ready for I/O.
    /// <para>获取一个值，指示设备句柄是否已打开并可用于 I/O。</para>
    /// </summary>
    protected abstract bool IsOpen { get; }

    /// <summary>
    /// Performs a single chunked bulk read.
    /// <para>执行单次分块批量读取。</para>
    /// </summary>
    /// <param name="buffer">The pinned target buffer pointer. <para>已固定的目标缓冲区指针。</para></param>
    /// <param name="length">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The chunk transfer result. <para>分块传输结果。</para></returns>
    protected abstract UsbChunkResult ReadChunk(IntPtr buffer, int length, int timeoutMs);

    /// <summary>
    /// Performs a single chunked bulk write.
    /// <para>执行单次分块批量写入。</para>
    /// </summary>
    /// <param name="buffer">The pinned source buffer pointer. <para>已固定的源缓冲区指针。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The chunk transfer result. <para>分块传输结果。</para></returns>
    protected abstract UsbChunkResult WriteChunk(IntPtr buffer, int length, int timeoutMs);

    /// <summary>
    /// Asynchronously performs a single chunked bulk read.
    /// <para>异步执行单次分块批量读取。</para>
    /// Backends with a native asynchronous transfer path (WinUSB overlapped, Linux usbfs
    /// URBs, libusb transfers) override this to avoid blocking a thread-pool thread; the
    /// default implementation runs <see cref="ReadChunk"/> on the thread pool.
    /// <para>具有原生异步传输路径的后端（WinUSB 重叠 I/O、Linux usbfs URB、libusb 传输）
    /// 覆盖此方法以避免阻塞线程池线程；默认实现在线程池上运行 <see cref="ReadChunk"/>。</para>
    /// </summary>
    /// <param name="buffer">The pinned destination buffer pointer. <para>已固定的目标缓冲区指针。</para></param>
    /// <param name="length">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that resolves to the chunk transfer result. <para>分块传输结果任务。</para></returns>
    protected virtual Task<UsbChunkResult> ReadChunkAsync(IntPtr buffer, int length, int timeoutMs, CancellationToken cancellationToken)
        => UsbAsyncExecution.Run(() => ReadChunk(buffer, length, timeoutMs), cancellationToken);

    /// <summary>
    /// Asynchronously performs a single chunked bulk write.
    /// <para>异步执行单次分块批量写入。</para>
    /// Backends with a native asynchronous transfer path override this; the default
    /// implementation runs <see cref="WriteChunk"/> on the thread pool.
    /// <para>具有原生异步传输路径的后端覆盖此方法；默认实现在线程池上运行 <see cref="WriteChunk"/>。</para>
    /// </summary>
    /// <param name="buffer">The pinned source buffer pointer. <para>已固定的源缓冲区指针。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that resolves to the chunk transfer result. <para>分块传输结果任务。</para></returns>
    protected virtual Task<UsbChunkResult> WriteChunkAsync(IntPtr buffer, int length, int timeoutMs, CancellationToken cancellationToken)
        => UsbAsyncExecution.Run(() => WriteChunk(buffer, length, timeoutMs), cancellationToken);

    /// <summary>
    /// Creates the exception thrown when a read fails with a fatal native error.
    /// <para>创建在读取遇到致命原生错误时抛出的异常。</para>
    /// </summary>
    protected virtual Exception CreateReadFatalException(int nativeError)
    {
        return IsDisconnectionError(nativeError)
            ? new UsbDeviceDisconnectedException($"USB device disconnected during read (native error: 0x{nativeError:X}).", nativeError)
            : (Exception)new IOException($"USB read failed with fatal error: 0x{nativeError:X}");
    }

    /// <summary>
    /// Creates the exception thrown when a write fails with a fatal native error.
    /// <para>创建在写入遇到致命原生错误时抛出的异常。</para>
    /// </summary>
    protected virtual Exception CreateWriteFatalException(int nativeError)
    {
        return IsDisconnectionError(nativeError)
            ? new UsbDeviceDisconnectedException($"USB device disconnected during write (native error: 0x{nativeError:X}).", nativeError)
            : (Exception)new IOException($"USB write failed with fatal error: 0x{nativeError:X}");
    }

    /// <summary>
    /// Determines whether a native error code indicates the device was unplugged/disconnected.
    /// <para>判断给定原生错误码是否表示设备已拔出/断开。</para>
    /// Backends override this with their platform-specific disconnection codes so the shared
    /// read/write loops throw <see cref="UsbDeviceDisconnectedException"/> instead of a generic
    /// <see cref="IOException"/>, letting upper-layer protocols trigger re-enumeration.
    /// <para>后端以各自平台的断开错误码覆盖此方法，使共享读写循环抛出
    /// <see cref="UsbDeviceDisconnectedException"/> 而非通用 <see cref="IOException"/>，
    /// 便于上层协议触发重新枚举。</para>
    /// </summary>
    /// <param name="nativeError">The native error code. <para>原生错误码。</para></param>
    /// <returns><c>true</c> when the code indicates a disconnection. <para>该错误码表示断开时返回 <c>true</c>。</para></returns>
    protected virtual bool IsDisconnectionError(int nativeError) => false;

    /// <summary>
    /// Describes the outcome of a single chunk transfer.
    /// <para>描述单次分块传输的结果。</para>
    /// </summary>
    protected readonly struct UsbChunkResult(UsbChunkStatus status, int transferred, int nativeError = 0, int retryCount = 0)
    {
        public UsbChunkStatus Status { get; } = status;
        public int Transferred { get; } = transferred;
        public int NativeError { get; } = nativeError;
        public int RetryCount { get; } = retryCount;

        public static UsbChunkResult Success(int transferred) => new(UsbChunkStatus.Success, transferred);
        public static UsbChunkResult Timeout(int nativeError) => new(UsbChunkStatus.Timeout, 0, nativeError);
        public static UsbChunkResult Fatal(int nativeError) => new(UsbChunkStatus.FatalError, 0, nativeError);
        public static UsbChunkResult Error(int nativeError) => new(UsbChunkStatus.Error, 0, nativeError);
    }

    /// <summary>
    /// Classifies a chunk transfer outcome.
    /// <para>对分块传输结果进行分类。</para>
    /// </summary>
    protected enum UsbChunkStatus
    {
        Success,
        Timeout,
        FatalError,
        Error
    }

    /// <summary>
    /// Validates that the specified buffer range is within bounds.
    /// <para>验证指定的缓冲区范围是否在边界内。</para>
    /// </summary>
    /// <param name="buffer">The buffer to validate. <para>要验证的缓冲区。</para></param>
    /// <param name="offset">The byte offset within the buffer. <para>缓冲区内的字节偏移量。</para></param>
    /// <param name="length">The number of bytes. <para>字节数。</para></param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="buffer"/> is <c>null</c>. <para>当 <paramref name="buffer"/> 为 <c>null</c> 时抛出。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="offset"/> or <paramref name="length"/> is out of range. <para>当 <paramref name="offset"/> 或 <paramref name="length"/> 超出范围时抛出。</para></exception>
    internal static void ValidateBufferRange(byte[] buffer, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        if (length < 0 || length > buffer.Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
    }

    /// <summary>
    /// Validates that the write data buffer is not null and the length is within bounds.
    /// <para>验证写入数据缓冲区不为 null 且长度在边界内。</para>
    /// </summary>
    /// <param name="data">The data buffer to validate. <para>要验证的数据缓冲区。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <c>null</c>. <para>当 <paramref name="data"/> 为 <c>null</c> 时抛出。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="length"/> is out of range. <para>当 <paramref name="length"/> 超出范围时抛出。</para></exception>
    internal static void ValidateWriteData(byte[] data, int length)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (length < 0 || length > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
    }

    /// <summary>
    /// Validates that the write data buffer is not null and the offset/length range is within bounds.
    /// <para>验证写入数据缓冲区不为 null 且偏移/长度范围在边界内。</para>
    /// </summary>
    /// <param name="data">The data buffer to validate. <para>要验证的数据缓冲区。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    internal static void ValidateWriteData(byte[] data, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(data);

        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        if (length < 0 || length > data.Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
    }

    /// <summary>
    /// Reads data from the device directly into the specified buffer using the default timeout.
    /// <para>使用默认超时时间将数据直接读入指定的缓冲区。</para>
    /// </summary>
    /// <param name="buffer">The target buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The byte offset within the buffer. <para>缓冲区内的字节偏移量。</para></param>
    /// <param name="length">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <returns>The number of bytes actually read. <para>实际读取的字节数。</para></returns>
    public virtual int ReadInto(byte[] buffer, int offset, int length)
    {
        if (length <= 0)
        {
            return 0;
        }

        ValidateBufferRange(buffer, offset, length);

        var data = Read(length);
        if (data.Length == 0)
        {
            return 0;
        }

        Buffer.BlockCopy(data, 0, buffer, offset, data.Length);
        return data.Length;
    }

    /// <summary>
    /// Snapshots the transferred payload bytes when opt-in frame capture is enabled.
    /// <para>开启可选抓帧时对已传输载荷字节做快照。</para>
    /// </summary>
    private static byte[]? CapturePayload(byte[] buffer, int offset, int count)
    {
        if (!UsbTrace.CaptureFrames || count <= 0)
        {
            return null;
        }

        var cap = Math.Min(count, UsbTrace.MaxCaptureBytes);
        var frame = new byte[cap];
        Buffer.BlockCopy(buffer, offset, frame, 0, cap);
        return frame;
    }

    /// <summary>
    /// Emits a transfer trace event using the backend tag.
    /// <para>使用后端标签发出传输跟踪事件。</para>
    /// </summary>
    private void EmitTransfer(UsbTransferOperation operation, int requestedBytes, int transferredBytes, int timeoutMs, int retryCount, int? nativeErrorCode, long elapsedMs, UsbTransferOutcome outcome, byte[]? payload = null)
    {
        if (!UsbTrace.HasTransferSubscribers)
        {
            return;
        }

        UsbTrace.EmitTransfer(new UsbTransferEvent
        {
            Backend = BackendName,
            DevicePath = DevicePath,
            Operation = operation,
            RequestedBytes = requestedBytes,
            TransferredBytes = transferredBytes,
            TimeoutMs = timeoutMs,
            RetryCount = retryCount,
            NativeErrorCode = nativeErrorCode,
            ElapsedMs = elapsedMs,
            Outcome = outcome,
            Payload = payload
        });
    }
}

/// <summary>
/// Identifies the platform-specific USB device backend type.
/// <para>标识平台特定的 USB 设备后端类型。</para>
/// </summary>
internal enum UsbDeviceType
{
    /// <summary>
    /// Windows legacy USB backend.
    /// <para>Windows 传统 USB 后端。</para>
    /// </summary>
    WinLegacy = 0,

    /// <summary>
    /// Windows WinUSB backend.
    /// <para>Windows WinUSB 后端。</para>
    /// </summary>
    WinUSB = 1,

    /// <summary>
    /// Linux usbfs backend.
    /// <para>Linux usbfs 后端。</para>
    /// </summary>
    Linux = 2,

    /// <summary>
    /// LibUSB cross-platform backend.
    /// <para>LibUSB 跨平台后端。</para>
    /// </summary>
    LibUSB = 3,

    /// <summary>
    /// macOS native backend.
    /// <para>macOS 原生后端。</para>
    /// </summary>
    MacOS = 4,

    /// <summary>
    /// HarmonyOS USBManager backend (via IPC bridge).
    /// <para>HarmonyOS USBManager 后端（通过 IPC 桥接）。</para>
    /// </summary>
    HarmonyOS = 5,

    /// <summary>
    /// Android USB Host backend (android.hardware.usb).
    /// <para>Android USB Host 后端（android.hardware.usb）。</para>
    /// </summary>
    Android = 6
}