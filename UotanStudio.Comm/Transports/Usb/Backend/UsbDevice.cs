using System.Diagnostics;
using System.Runtime.InteropServices;
using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb.Backend;

/// <summary>
/// Represents an abstract USB device that provides read, write, control transfer, and lifecycle operations.
/// <para>表示一个抽象 USB 设备，提供读取、写入、控制传输及生命周期操作。</para>
/// </summary>
internal abstract class UsbDevice : IDisposable
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
    /// Reads data from the device using the default timeout.
    /// <para>使用默认超时时间从设备读取数据。</para>
    /// </summary>
    /// <param name="length">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <returns>The received data. <para>接收到的数据。</para></returns>
    public abstract byte[] Read(int length);

    /// <summary>
    /// Reads data from the device with a specified timeout.
    /// <para>使用指定超时时间从设备读取数据。</para>
    /// </summary>
    /// <param name="length">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The received data. <para>接收到的数据。</para></returns>
    public virtual byte[] Read(int length, int timeoutMs) => Read(length);

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
        // Skip allocating the event object entirely when no subscriber is registered;
        // the transfer hot path otherwise constructs one per chunk.
        // <para>未注册订阅者时完全跳过事件对象分配；否则传输热路径每块都会构造一个。</para>
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

    /// <summary>
    /// Reads data from the device directly into the specified buffer with a specified timeout.
    /// <para>使用指定超时时间将数据直接读入指定的缓冲区。</para>
    /// <b>Timeout semantics:</b> the timeout applies per chunk (see <see cref="MaxChunkSize"/>),
    /// not to the whole operation - a large read spanning several chunks can take up to
    /// chunkCount × timeoutMs. Use <c>ReadExact</c> when a total-budget read is needed.
    /// <para><b>超时语义：</b>超时作用于每块（见 <see cref="MaxChunkSize"/>）而非整个操作——
    /// 跨多块的大读取最多耗时 块数 × timeoutMs。需要整体预算读取时使用 <c>ReadExact</c>。</para>
    /// </summary>
    /// <param name="buffer">The target buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The byte offset within the buffer. <para>缓冲区内的字节偏移量。</para></param>
    /// <param name="length">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes actually read. <para>实际读取的字节数。</para></returns>
    public virtual int ReadInto(byte[] buffer, int offset, int length, int timeoutMs) => ReadPacket(buffer, offset, length, timeoutMs).Count;

#if NET8_0_OR_GREATER
    /// <summary>
    /// Reads bytes into a caller-provided span with a timeout (net8.0+ only).
    /// <para>在指定超时时间内将数据读取到调用方提供的跨度（仅 net8.0+）。</para>
    /// The span is bridged through a temporary array; use the byte[] overloads for
    /// zero-copy transfers on hot paths.
    /// <para>跨度通过临时数组桥接；热路径的零拷贝传输请使用 byte[] 重载。</para>
    /// </summary>
    /// <param name="buffer">The destination span. <para>目标跨度。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes read. <para>实际读取的字节数。</para></returns>
    public virtual int ReadInto(Span<byte> buffer, int timeoutMs)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        var temp = new byte[buffer.Length];
        var count = ReadInto(temp, 0, temp.Length, timeoutMs);
        if (count > 0)
        {
            temp.AsSpan(0, count).CopyTo(buffer);
        }

        return count;
    }
#endif

    /// <summary>
    /// Reads bytes into the specified buffer and reports the transfer outcome.
    /// <para>将数据读取到指定缓冲区并报告传输结果。</para>
    /// <see cref="UsbReadResult.IsTimeout"/> is set when no data arrived before the deadline;
    /// <see cref="UsbReadResult.IsShortPacket"/> is set when the device ended the transfer with
    /// fewer bytes than requested (the USB Message boundary). Protocol layers use these flags to
    /// distinguish "waiting for more data" from "Message complete".
    /// <para><see cref="UsbReadResult.IsTimeout"/> 表示在期限内没有数据到达；
    /// <see cref="UsbReadResult.IsShortPacket"/> 表示设备以少于请求的字节数结束传输
    /// （USB 消息边界）。协议层用这些标志区分"等待更多数据"与"消息已完成"。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The number of bytes to read. <para>读取字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The read result with byte count and outcome flags. <para>包含字节数与结果标志的读取结果。</para></returns>
    public virtual UsbReadResult ReadPacket(byte[] buffer, int offset, int length, int timeoutMs)
        => ReadPacket(buffer, offset, length, timeoutMs, progress: null);

    /// <summary>
    /// Reads bytes into the specified buffer and reports the transfer outcome, reporting
    /// cumulative bytes read after each completed chunk when <paramref name="progress"/> is set.
    /// <para>将数据读取到指定缓冲区并报告传输结果；设置 <paramref name="progress"/> 时
    /// 在每个分块完成后报告累计读取字节数。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The number of bytes to read. <para>读取字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="progress">Receives the cumulative transferred byte count after each chunk. <para>每块完成后接收累计传输字节数。</para></param>
    /// <returns>The read result with byte count and outcome flags. <para>包含字节数与结果标志的读取结果。</para></returns>
    public virtual UsbReadResult ReadPacket(byte[] buffer, int offset, int length, int timeoutMs, IProgress<long>? progress)
    {
        var stopwatch = Stopwatch.StartNew();
        int? lastError = null;
        var outcome = UsbTransferOutcome.Success;

        if (!IsOpen)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }
        if (length <= 0)
        {
            return new UsbReadResult(0, false, false);
        }

        ValidateBufferRange(buffer, offset, length);

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs);
        var lenRemaining = length;
        var count = 0;
        var retryCount = 0;

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            while (lenRemaining > 0)
            {
                var lenToRead = Math.Min(lenRemaining, MaxChunkSize);
                var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset + count);

                var chunk = ReadChunk(ptr, lenToRead, effectiveTimeoutMs);
                retryCount += chunk.RetryCount;

                if (chunk.Status == UsbChunkStatus.Timeout)
                {
                    lastError = chunk.NativeError;
                    outcome = UsbTransferOutcome.Timeout;
                    break;
                }

                if (chunk.Status == UsbChunkStatus.FatalError)
                {
                    lastError = chunk.NativeError;
                    EmitTransfer(UsbTransferOperation.Read, length, count, effectiveTimeoutMs, retryCount, lastError, stopwatch.ElapsedMilliseconds, UsbTransferOutcome.FatalError, CapturePayload(buffer, offset, count));
                    throw CreateReadFatalException(chunk.NativeError);
                }

                if (chunk.Status == UsbChunkStatus.Error)
                {
                    lastError = chunk.NativeError;
                    outcome = UsbTransferOutcome.Error;
                    break;
                }

                var transferred = chunk.Transferred;
                count += transferred;
                lenRemaining -= transferred;
                progress?.Report(count);

                if (transferred < lenToRead)
                {
                    break;
                }
            }
        }
        finally
        {
            handle.Free();
        }

        if (outcome == UsbTransferOutcome.Success && count > 0 && count < length)
        {
            outcome = UsbTransferOutcome.ShortTransfer;
        }

        EmitTransfer(UsbTransferOperation.Read, length, count, effectiveTimeoutMs, retryCount, lastError, stopwatch.ElapsedMilliseconds, outcome, CapturePayload(buffer, offset, count));
        return new UsbReadResult(
            count,
            isTimeout: outcome == UsbTransferOutcome.Timeout,
            isShortPacket: outcome == UsbTransferOutcome.ShortTransfer);
    }

    /// <summary>
    /// Asynchronously reads data from the device.
    /// <para>异步从设备读取数据。</para>
    /// </summary>
    /// <param name="length">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that represents the asynchronous read operation. <para>表示异步读取操作的任务。</para></returns>
    public virtual async Task<byte[]> ReadAsync(int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (length > UsbTransferPolicies.MaxReadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), $"length exceeds the safety cap of {UsbTransferPolicies.MaxReadLength} bytes; clamp device-provided frame lengths.");
        }

        if (length == 0)
        {
            return [];
        }

        var buffer = new byte[length];
        var count = await ReadIntoAsync(buffer, 0, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        if (count == length)
        {
            return buffer;
        }

        if (count == 0)
        {
            return [];
        }

        var realData = new byte[count];
        Buffer.BlockCopy(buffer, 0, realData, 0, count);
        return realData;
    }

    /// <summary>
    /// Asynchronously reads data directly into the specified buffer.
    /// <para>异步将数据直接读入指定的缓冲区。</para>
    /// </summary>
    /// <param name="buffer">The target buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The byte offset within the buffer. <para>缓冲区内的字节偏移量。</para></param>
    /// <param name="length">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that represents the asynchronous read operation with the number of bytes read. <para>表示异步读取操作并返回已读取字节数的任务。</para></returns>
    public virtual async Task<int> ReadIntoAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        var result = await ReadPacketAsync(buffer, offset, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        return result.Count;
    }

    /// <summary>
    /// Asynchronously reads bytes into the specified buffer and reports the transfer outcome.
    /// <para>异步将数据读取到指定缓冲区并报告传输结果。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The number of bytes to read. <para>读取字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that resolves to the read result. <para>返回读取结果的任务。</para></returns>
    public virtual Task<UsbReadResult> ReadPacketAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        => ReadPacketAsync(buffer, offset, length, timeoutMs, progress: null, cancellationToken);

    /// <summary>
    /// Asynchronously reads bytes into the specified buffer and reports the transfer outcome,
    /// reporting cumulative bytes read after each completed chunk.
    /// <para>异步将数据读取到指定缓冲区并报告传输结果，在每个分块完成后报告累计读取字节数。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The number of bytes to read. <para>读取字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="progress">Receives the cumulative transferred byte count after each chunk. <para>每块完成后接收累计传输字节数。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that resolves to the read result. <para>返回读取结果的任务。</para></returns>
    public virtual async Task<UsbReadResult> ReadPacketAsync(byte[] buffer, int offset, int length, int timeoutMs, IProgress<long>? progress, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        int? lastError = null;
        var outcome = UsbTransferOutcome.Success;
        var retryCount = 0;

        if (!IsOpen)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }
        if (length <= 0)
        {
            return new UsbReadResult(0, false, false);
        }

        ValidateBufferRange(buffer, offset, length);

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs);
        var lenRemaining = length;
        var count = 0;

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            while (lenRemaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var lenToRead = Math.Min(lenRemaining, MaxChunkSize);
                var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset + count);

                var chunk = await ReadChunkAsync(ptr, lenToRead, effectiveTimeoutMs, cancellationToken).ConfigureAwait(false);
                retryCount += chunk.RetryCount;

                if (chunk.Status == UsbChunkStatus.Timeout)
                {
                    lastError = chunk.NativeError;
                    outcome = UsbTransferOutcome.Timeout;
                    break;
                }

                if (chunk.Status == UsbChunkStatus.FatalError)
                {
                    lastError = chunk.NativeError;
                    EmitTransfer(UsbTransferOperation.Read, length, count, effectiveTimeoutMs, retryCount, lastError, stopwatch.ElapsedMilliseconds, UsbTransferOutcome.FatalError, CapturePayload(buffer, offset, count));
                    throw CreateReadFatalException(chunk.NativeError);
                }

                if (chunk.Status == UsbChunkStatus.Error)
                {
                    lastError = chunk.NativeError;
                    outcome = UsbTransferOutcome.Error;
                    break;
                }

                var transferred = chunk.Transferred;
                count += transferred;
                lenRemaining -= transferred;
                progress?.Report(count);

                if (transferred < lenToRead)
                {
                    break;
                }
            }
        }
        finally
        {
            handle.Free();
        }

        if (outcome == UsbTransferOutcome.Success && count > 0 && count < length)
        {
            outcome = UsbTransferOutcome.ShortTransfer;
        }

        EmitTransfer(UsbTransferOperation.Read, length, count, effectiveTimeoutMs, retryCount, lastError, stopwatch.ElapsedMilliseconds, outcome, CapturePayload(buffer, offset, count));
        return new UsbReadResult(
            count,
            isTimeout: outcome == UsbTransferOutcome.Timeout,
            isShortPacket: outcome == UsbTransferOutcome.ShortTransfer);
    }

    /// <summary>
    /// Writes data to the device using the default timeout.
    /// <para>使用默认超时时间向设备写入数据。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>要写入的数据。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    /// <returns>The number of bytes actually written. <para>实际写入的字节数。</para></returns>
    public abstract long Write(byte[] data, int length);

    /// <summary>
    /// Writes data to the device with a specified timeout.
    /// <para>使用指定超时时间向设备写入数据。</para>
    /// <b>Timeout semantics:</b> the timeout applies per chunk (see <see cref="MaxChunkSize"/>),
    /// not to the whole operation - a large write spanning several chunks can take up to
    /// chunkCount × timeoutMs.
    /// <para><b>超时语义：</b>超时作用于每块（见 <see cref="MaxChunkSize"/>）而非整个操作——
    /// 跨多块的大写入最多耗时 块数 × timeoutMs。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>要写入的数据。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes actually written. <para>实际写入的字节数。</para></returns>
    public virtual long Write(byte[] data, int length, int timeoutMs) => Write(data, 0, length, timeoutMs);

    /// <summary>
    /// Writes data to the device starting at the specified offset, with a specified timeout.
    /// <para>使用指定超时时间从指定偏移量向设备写入数据。</para>
    /// <b>Timeout semantics:</b> the timeout applies per chunk (see <see cref="MaxChunkSize"/>),
    /// not to the whole operation - a large write spanning several chunks can take up to
    /// chunkCount × timeoutMs.
    /// <para><b>超时语义：</b>超时作用于每块（见 <see cref="MaxChunkSize"/>）而非整个操作——
    /// 跨多块的大写入最多耗时 块数 × timeoutMs。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>要写入的数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes actually written. <para>实际写入的字节数。</para></returns>
    public virtual long Write(byte[] data, int offset, int length, int timeoutMs)
        => Write(data, offset, length, timeoutMs, progress: null);

    /// <summary>
    /// Writes data to the device starting at the specified offset, with a specified timeout,
    /// reporting cumulative bytes written after each completed chunk when
    /// <paramref name="progress"/> is set.
    /// <para>使用指定超时时间从指定偏移量向设备写入数据；设置 <paramref name="progress"/> 时
    /// 在每个分块完成后报告累计写入字节数。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>要写入的数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="progress">Receives the cumulative transferred byte count after each chunk. <para>每块完成后接收累计传输字节数。</para></param>
    /// <returns>The number of bytes actually written. <para>实际写入的字节数。</para></returns>
    public virtual long Write(byte[] data, int offset, int length, int timeoutMs, IProgress<long>? progress)
    {
        var stopwatch = Stopwatch.StartNew();
        int? lastError = null;
        var outcome = UsbTransferOutcome.Success;
        var retryCount = 0;

        if (!IsOpen)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }
        ValidateWriteData(data, offset, length);

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs);

        if (length == 0)
        {
            EmitTransfer(UsbTransferOperation.Write, 0, 0, effectiveTimeoutMs, 0, null, stopwatch.ElapsedMilliseconds, UsbTransferOutcome.Success);
            return 0;
        }

        var lenRemaining = length;
        var count = 0;

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            while (lenRemaining > 0)
            {
                var lenToSend = Math.Min(lenRemaining, MaxChunkSize);
                var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset + count);

                var chunk = WriteChunk(ptr, lenToSend, effectiveTimeoutMs);
                retryCount += chunk.RetryCount;

                if (chunk.Status == UsbChunkStatus.Timeout)
                {
                    lastError = chunk.NativeError;
                    outcome = UsbTransferOutcome.Timeout;
                    break;
                }

                if (chunk.Status == UsbChunkStatus.FatalError)
                {
                    lastError = chunk.NativeError;
                    EmitTransfer(UsbTransferOperation.Write, length, count, effectiveTimeoutMs, retryCount, lastError, stopwatch.ElapsedMilliseconds, UsbTransferOutcome.FatalError, CapturePayload(data, offset, count));
                    throw CreateWriteFatalException(chunk.NativeError);
                }

                if (chunk.Status == UsbChunkStatus.Error)
                {
                    lastError = chunk.NativeError;
                    outcome = UsbTransferOutcome.Error;
                    break;
                }

                var transferred = chunk.Transferred;
                count += transferred;
                lenRemaining -= transferred;
                progress?.Report(count);

                if (transferred < lenToSend)
                {
                    break;
                }
            }
        }
        finally
        {
            handle.Free();
        }

        if (outcome == UsbTransferOutcome.Success && count > 0 && count < length)
        {
            outcome = UsbTransferOutcome.ShortTransfer;
        }

        EmitTransfer(UsbTransferOperation.Write, length, count, effectiveTimeoutMs, retryCount, lastError, stopwatch.ElapsedMilliseconds, outcome, CapturePayload(data, offset, count));
        return count;
    }

    /// <summary>
    /// Asynchronously writes data to the device.
    /// <para>异步向设备写入数据。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>要写入的数据。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that represents the asynchronous write operation with the number of bytes written. <para>表示异步写入操作并返回已写入字节数的任务。</para></returns>
    public virtual Task<long> WriteAsync(byte[] data, int length, int timeoutMs, CancellationToken cancellationToken = default)
        => WriteAsync(data, 0, length, timeoutMs, cancellationToken);

    /// <summary>
    /// Asynchronously writes data to the device starting at the specified offset.
    /// <para>异步从指定偏移量向设备写入数据。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>要写入的数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that represents the asynchronous write operation with the number of bytes written. <para>表示异步写入操作并返回已写入字节数的任务。</para></returns>
    public virtual Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        => WriteAsync(data, offset, length, timeoutMs, progress: null, cancellationToken);

    /// <summary>
    /// Asynchronously writes data to the device starting at the specified offset, reporting
    /// cumulative bytes written after each completed chunk.
    /// <para>异步从指定偏移量向设备写入数据，并在每个分块完成后报告累计写入字节数。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>要写入的数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>要写入的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="progress">Receives the cumulative transferred byte count after each chunk. <para>每块完成后接收累计传输字节数。</para></param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests. <para>用于监视取消请求的令牌。</para></param>
    /// <returns>A task that represents the asynchronous write operation with the number of bytes written. <para>表示异步写入操作并返回已写入字节数的任务。</para></returns>
    public virtual async Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, IProgress<long>? progress, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        int? lastError = null;
        var outcome = UsbTransferOutcome.Success;
        var retryCount = 0;

        if (!IsOpen)
        {
            throw new UsbDeviceHandleClosedException("Device handle is closed.");
        }
        ValidateWriteData(data, offset, length);

        var effectiveTimeoutMs = UsbTransferPolicies.NormalizeTimeout(timeoutMs, DefaultTimeoutMs);

        if (length == 0)
        {
            EmitTransfer(UsbTransferOperation.Write, 0, 0, effectiveTimeoutMs, 0, null, stopwatch.ElapsedMilliseconds, UsbTransferOutcome.Success);
            return 0;
        }

        var lenRemaining = length;
        var count = 0;

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            while (lenRemaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var lenToSend = Math.Min(lenRemaining, MaxChunkSize);
                var ptr = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + offset + count);

                var chunk = await WriteChunkAsync(ptr, lenToSend, effectiveTimeoutMs, cancellationToken).ConfigureAwait(false);
                retryCount += chunk.RetryCount;

                if (chunk.Status == UsbChunkStatus.Timeout)
                {
                    lastError = chunk.NativeError;
                    outcome = UsbTransferOutcome.Timeout;
                    break;
                }

                if (chunk.Status == UsbChunkStatus.FatalError)
                {
                    lastError = chunk.NativeError;
                    EmitTransfer(UsbTransferOperation.Write, length, count, effectiveTimeoutMs, retryCount, lastError, stopwatch.ElapsedMilliseconds, UsbTransferOutcome.FatalError, CapturePayload(data, offset, count));
                    throw CreateWriteFatalException(chunk.NativeError);
                }

                if (chunk.Status == UsbChunkStatus.Error)
                {
                    lastError = chunk.NativeError;
                    outcome = UsbTransferOutcome.Error;
                    break;
                }

                var transferred = chunk.Transferred;
                count += transferred;
                lenRemaining -= transferred;
                progress?.Report(count);

                if (transferred < lenToSend)
                {
                    break;
                }
            }
        }
        finally
        {
            handle.Free();
        }

        if (outcome == UsbTransferOutcome.Success && count > 0 && count < length)
        {
            outcome = UsbTransferOutcome.ShortTransfer;
        }

        EmitTransfer(UsbTransferOperation.Write, length, count, effectiveTimeoutMs, retryCount, lastError, stopwatch.ElapsedMilliseconds, outcome, CapturePayload(data, offset, count));
        return count;
    }

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
            // Host-to-device, standard request, recipient = interface.
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
            // Host-to-device, standard request, recipient = device.
            RequestType = 0x00,
            Request = 0x09,
            Value = configuration,
            Index = 0,
            Length = 0
        };
        _ = ControlTransfer(setup, null, 0, 0, DefaultTimeoutMs);
    }

    /// <summary>
    /// Reads bytes from an interrupt IN endpoint into a caller-provided buffer.
    /// <para>从中断 IN 端点将数据读取到调用方提供的缓冲区。</para>
    /// The default implementation reports the capability as unsupported; backends that can
    /// perform interrupt transfers (usbfs/libusb/WinUSB) override this method.
    /// <para>默认实现报告该能力不受支持；可执行中断传输的后端（usbfs/libusb/WinUSB）
    /// 覆盖此方法。</para>
    /// </summary>
    /// <param name="endpointAddress">The interrupt IN endpoint address (bit 7 set). <para>中断 IN 端点地址（bit 7 置位）。</para></param>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">Maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">The timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The read result with byte count and outcome flags. <para>包含字节数与结果标志的读取结果。</para></returns>
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
    HarmonyOS = 5
}