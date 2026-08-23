namespace UotanStudio.Comm.Transports.Abstractions;

/// <summary>
/// Abstraction for a device transport (USB, serial, TCP/UDP, ...), decoupling protocol logic
/// from the concrete transport implementation.
/// <para>设备传输（USB、串口、TCP/UDP 等）的抽象，使协议逻辑与具体传输实现解耦。</para>
/// </summary>
public interface ICommTransport : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the transport is currently connected.
    /// <para>获取传输当前是否已连接。</para>
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Connects the transport.
    /// <para>连接传输。</para>
    /// </summary>
    void Connect();

    /// <summary>
    /// Disconnects the transport.
    /// <para>断开传输。</para>
    /// </summary>
    void Disconnect();

    /// <summary>
    /// Resets the transport (e.g. a USB port reset, serial line toggle, or socket reconnect).
    /// <para>重置传输（例如 USB 端口重置、串口线路切换或套接字重连）。</para>
    /// </summary>
    void Reset();

    /// <summary>
    /// Reads up to <paramref name="length"/> bytes into a caller-provided buffer.
    /// <para>将最多 <paramref name="length"/> 个字节读取到调用方提供的缓冲区。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes read. <para>实际读取的字节数。</para></returns>
    int Read(byte[] buffer, int offset, int length, int timeoutMs);

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
    Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes up to <paramref name="length"/> bytes from a caller-provided buffer.
    /// <para>从调用方提供的缓冲区写入最多 <paramref name="length"/> 个字节。</para>
    /// </summary>
    /// <param name="data">The data to write. <para>待写入数据。</para></param>
    /// <param name="offset">The offset into <paramref name="data"/> at which to start. <para><paramref name="data"/> 中的起始偏移量。</para></param>
    /// <param name="length">The number of bytes to write. <para>写入字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes written. <para>实际写入的字节数。</para></returns>
    long Write(byte[] data, int offset, int length, int timeoutMs);

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
    Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a <see cref="Stream"/> over this transport.
    /// <para>返回该传输之上的 <see cref="Stream"/>。</para>
    /// </summary>
    /// <returns>A stream adapter over the transport. <para>传输上的流适配器。</para></returns>
    /// <exception cref="NotSupportedException">Thrown when the transport cannot expose a stream. <para>当传输无法提供流时抛出。</para></exception>
    Stream GetStream();
}