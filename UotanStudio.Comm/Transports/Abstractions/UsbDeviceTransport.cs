using UotanStudio.Comm.Diagnostics;
using UotanStudio.Comm.Transports.Usb;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Abstractions;

/// <summary>
/// An <see cref="ICommTransport"/> adapter that wraps an <see cref="IUsbDeviceSession"/>,
/// forwarding generic transport operations to the underlying USB session.
/// <para>包装 <see cref="IUsbDeviceSession"/> 的 <see cref="ICommTransport"/> 适配器，
/// 将通用传输操作转发到底层 USB 会话。</para>
/// The transport does NOT own the session — callers remain responsible for the session lifetime.
/// <para>传输不拥有会话——调用方仍负责会话的生命周期。</para>
/// </summary>
/// <remarks>
/// Initializes a new <see cref="UsbDeviceTransport"/> over the specified session.
/// <para>在指定会话上初始化新的 <see cref="UsbDeviceTransport"/>。</para>
/// </remarks>
/// <param name="session">The opened USB device session. <para>已打开的 USB 设备会话。</para></param>
internal sealed class UsbDeviceTransport(IUsbDeviceSession session) : ICommTransport
{
    /// <summary>
    /// Gets the underlying USB device session, exposed for protocol layers that need
    /// USB-specific operations (ControlTransfer, WriteZlp, interrupt endpoints, ...).
    /// <para>获取底层 USB 设备会话，供需要 USB 专有操作（ControlTransfer、WriteZlp、
    /// 中断端点等）的协议层使用。</para>
    /// </summary>
    public IUsbDeviceSession Session { get; } = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>
    /// Gets a value indicating whether the transport is currently connected.
    /// The USB session is handed in already opened, so the transport starts connected.
    /// <para>获取传输当前是否已连接。USB 会话传入时已打开，因此传输默认处于已连接状态。</para>
    /// </summary>
    public bool IsConnected { get; private set; } = true;

    /// <summary>
    /// Connects the transport. The USB session is already opened when handed in, so this is a no-op
    /// beyond recording the connected state.
    /// <para>连接传输。USB 会话传入时已打开，因此本方法除记录连接状态外为空操作。</para>
    /// </summary>
    public void Connect() => IsConnected = true;

    /// <summary>
    /// Disconnects the transport. The USB session is owned by the caller, so this only records the
    /// disconnected state without closing the session.
    /// <para>断开传输。USB 会话由调用方所有，因此本方法仅记录断开状态而不会关闭会话。</para>
    /// </summary>
    public void Disconnect() => IsConnected = false;

    /// <summary>
    /// Resets the underlying USB transport.
    /// <para>重置底层 USB 传输。</para>
    /// </summary>
    public void Reset() => Session.Reset();

    /// <summary>
    /// Reads up to <paramref name="length"/> bytes into a caller-provided buffer.
    /// <para>将最多 <paramref name="length"/> 个字节读取到调用方提供的缓冲区。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes read. <para>实际读取的字节数。</para></returns>
    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
    {
        BeginRead();
        try
        {
            return Session.ReadInto(buffer, offset, length, timeoutMs);
        }
        finally
        {
            EndRead();
        }
    }

    /// <summary>
    /// Reads up to <paramref name="length"/> bytes into a caller-provided buffer asynchronously.
    /// <para>将最多 <paramref name="length"/> 个字节异步读取到调用方提供的缓冲区。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the number of bytes read. <para>返回实际读取字节数的任务。</para></returns>
    public async Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        BeginRead();
        try
        {
            return await Session.ReadIntoAsync(buffer, offset, length, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndRead();
        }
    }

    /// <summary>
    /// Writes up to <paramref name="length"/> bytes from a caller-provided buffer.
    /// <para>从调用方提供的缓冲区写入最多 <paramref name="length"/> 个字节。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>待写入数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>写入字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes written. <para>实际写入的字节数。</para></returns>
    public long Write(byte[] data, int offset, int length, int timeoutMs)
        => Session.Write(data, offset, length, timeoutMs);

    /// <summary>
    /// Writes up to <paramref name="length"/> bytes from a caller-provided buffer asynchronously.
    /// <para>从调用方提供的缓冲区异步写入最多 <paramref name="length"/> 个字节。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>待写入数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>写入字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the number of bytes written. <para>返回实际写入字节数的任务。</para></returns>
    public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        => Session.WriteAsync(data, offset, length, timeoutMs, cancellationToken);

    /// <summary>
    /// Returns a <see cref="Stream"/> over the underlying USB session.
    /// <para>返回底层 USB 会话之上的 <see cref="Stream"/>。</para>
    /// </summary>
    /// <returns>A stream adapter over the USB session. <para>USB 会话上的流适配器。</para></returns>
    public Stream GetStream() => new UsbStream(Session);

    /// <summary>
    /// Disposes the underlying USB session.
    /// <para>释放底层 USB 会话。</para>
    /// </summary>
    public void Dispose() => Session.Dispose();

    private static int _nextTransportId;

    private readonly int _transportId = Interlocked.Increment(ref _nextTransportId);

    private int _activeReads;

    private void BeginRead()
    {
        var active = Interlocked.Increment(ref _activeReads);
        if (active <= 1)
        {
            return;
        }

        var info = Session.DeviceInfo;
        CommTrace.Debug("usb",
            $"transport #{_transportId} ({info.VendorId:X4}:{info.ProductId:X4} serial {info.SerialNumber ?? "-"}) now has " +
            $"{active} concurrent inbound reads on the same bulk pipe; one ADB stream is being split between readers, " +
            "so each reader sees the other's mid-frame bytes.");
    }

    private void EndRead() => Interlocked.Decrement(ref _activeReads);
}