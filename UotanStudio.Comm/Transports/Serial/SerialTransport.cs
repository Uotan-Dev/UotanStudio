using System.IO.Ports;
using UotanStudio.Comm.Transports.Abstractions;

namespace UotanStudio.Comm.Transports.Serial;

/// <summary>
/// An <see cref="ICommTransport"/> implementation backed by a system serial port.
/// <para>基于系统串口的 <see cref="ICommTransport"/> 实现。</para>
/// Uses <see cref="SerialPort"/> and supports runtime baud-rate changes.
/// <para>使用 <see cref="SerialPort"/>，并支持运行时切换波特率。</para>
/// </summary>
/// <remarks>
/// Initializes a new <see cref="SerialTransport"/> for the specified port.
/// <para>为指定端口初始化新的 <see cref="SerialTransport"/>。</para>
/// </remarks>
/// <param name="portName">The serial port name (e.g. "COM3" or "/dev/ttyUSB0"). <para>串口名称（如 "COM3" 或 "/dev/ttyUSB0"）。</para></param>
/// <param name="baudRate">The baud rate. Default is 3000000. <para>波特率。默认 3000000。</para></param>
/// <param name="parity">The parity setting. Default is <see cref="Parity.None"/>. <para>奇偶校验设置。默认 <see cref="Parity.None"/>。</para></param>
/// <param name="dataBits">The number of data bits. Default is 8. <para>数据位数。默认 8。</para></param>
/// <param name="stopBits">The stop bits setting. Default is <see cref="StopBits.One"/>. <para>停止位设置。默认 <see cref="StopBits.One"/>。</para></param>
/// <param name="handshake">The handshake setting. Default is <see cref="Handshake.RequestToSend"/>. <para>握手设置。默认 <see cref="Handshake.RequestToSend"/>。</para></param>
public sealed class SerialTransport(
    string portName,
    int baudRate = 3000000,
    Parity parity = Parity.None,
    int dataBits = 8,
    StopBits stopBits = StopBits.One,
    Handshake handshake = Handshake.RequestToSend) : ICommTransport
{
    private SerialPort? _port;
    private int _baudRate = baudRate;
    private readonly Parity _parity = parity;
    private readonly int _dataBits = dataBits;
    private readonly StopBits _stopBits = stopBits;
    private readonly Handshake _handshake = handshake;
    private volatile bool _disposed;
    private readonly Lock _lifecycleGate = new();
    private readonly Func<SerialPort> _portFactory = static () => new SerialPort();

    internal SerialTransport(string portName, Func<SerialPort> portFactory) : this(portName)
    {
        _portFactory = portFactory ?? throw new ArgumentNullException(nameof(portFactory));
    }

    /// <summary>
    /// Enumerates the serial ports currently available on the system.
    /// <para>枚举系统当前可用的串口。</para>
    /// </summary>
    /// <returns>The available port names. <para>可用的串口名称列表。</para></returns>
    public static IReadOnlyList<string> GetAvailablePorts() => SerialPort.GetPortNames();

    /// <summary>
    /// Gets a value indicating whether the serial port is open.
    /// <para>获取串口是否已打开。</para>
    /// </summary>
    /// <value><c>true</c> while the underlying port handle is open; <c>false</c> after close or
    /// before <see cref="Connect"/>. <para>底层端口句柄打开期间为 <c>true</c>；关闭后至
    /// <see cref="Connect"/> 之前为 <c>false</c>。</para></value>
    public bool IsConnected => _port is { IsOpen: true };

    /// <summary>
    /// Gets or sets the default timeout in milliseconds used when a caller omits one.
    /// <para>获取或设置调用方未显式指定超时时使用的默认超时（毫秒）。</para>
    /// </summary>
    /// <value>The fallback read/write timeout in milliseconds applied to newly opened ports and
    /// <see cref="GetStream"/>; 5000 by default. <para>应用于新打开端口和 <see cref="GetStream"/>
    /// 的回退读写超时（毫秒）；默认 5000。</para></value>
    public int DefaultTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Gets the serial port name this transport was configured for.
    /// <para>获取此传输配置的串口名称。</para>
    /// </summary>
    /// <value>The OS port name (e.g. "COM3" or "/dev/ttyUSB0"); fixed for the transport's lifetime.
    /// <para>操作系统端口名称（如 "COM3" 或 "/dev/ttyUSB0"）；在传输生命周期内固定不变。</para></value>
    public string PortName { get; } = portName ?? throw new ArgumentNullException(nameof(portName));

    /// <summary>
    /// Gets the baud rate currently in effect (may differ from the configured one after
    /// <see cref="SetBaudRate"/>).
    /// <para>获取当前生效的波特率（调用 <see cref="SetBaudRate"/> 后可能与构造时配置不同）。</para>
    /// </summary>
    /// <value>The open port's live baud rate, or the last configured value while the port is closed.
    /// <para>已打开端口的实时波特率；端口关闭时为最近一次配置值。</para></value>
    public int BaudRate => _port is { IsOpen: true } port ? port.BaudRate : _baudRate;

    /// <summary>
    /// Opens the serial port. No-op if already open.
    /// <para>打开串口。若已打开则为空操作。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The port is in use. <para>端口已被占用。</para></exception>
    /// <exception cref="IOException">The port could not be opened. <para>端口打开失败。</para></exception>
    /// <exception cref="ArgumentException">The port configuration is invalid. <para>端口配置无效。</para></exception>
    public void Connect()
    {
        lock (_lifecycleGate)
        {
            ThrowIfDisposed();
            if (_port is { IsOpen: true })
            {
                return;
            }
            DisposePortOwned();
            var port = _portFactory();
            try
            {
                port.PortName = PortName;
                port.BaudRate = _baudRate;
                port.Parity = _parity;
                port.DataBits = _dataBits;
                port.StopBits = _stopBits;
                port.Handshake = _handshake;
                port.ReadTimeout = DefaultTimeoutMs;
                port.WriteTimeout = DefaultTimeoutMs;
                try
                {
                    port.ReadBufferSize = 1048576;
                    port.WriteBufferSize = 1048576;
                }
                catch (PlatformNotSupportedException)
                {
                }

                port.Open();
                _port = port;
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException($"Serial port {PortName} is in use.", ex);
            }
            catch (IOException ex)
            {
                throw new IOException($"Failed to open serial port {PortName}.", ex);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Invalid serial port configuration: {PortName}.", ex);
            }
            finally
            {
                if (!ReferenceEquals(_port, port))
                {
                    port.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Opens the serial port asynchronously. No-op if already open.
    /// <para>异步打开串口。若已打开则为空操作。</para>
    /// </summary>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task representing the asynchronous open operation. <para>表示异步打开操作的任务。</para></returns>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (IsConnected)
        {
            return;
        }

        await Task.Run(Connect, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the serial port and clears the internal port reference.
    /// <para>关闭串口并清除内部端口引用。</para>
    /// </summary>
    public void Disconnect()
    {
        lock (_lifecycleGate)
        {
            DisposePortOwned();
        }
    }

    private void DisposePortOwned()
    {
        var port = _port;
        _port = null;
        try
        {
            port?.Dispose();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Resets the serial transport by discarding both the receive and transmit buffers.
    /// <para>通过丢弃接收与发送缓冲区来重置串口传输。</para>
    /// </summary>
    public void Reset()
    {
        var port = EnsureConnected();
        try
        {
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Dynamically switches the serial baud rate at runtime.
    /// <para>运行时动态切换串口波特率。</para>
    /// </summary>
    /// <param name="baudRate">The target baud rate (must be positive). <para>目标波特率（必须为正）。</para></param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="baudRate"/> is not positive. <para>波特率非正。</para></exception>
    public void SetBaudRate(int baudRate)
    {
        lock (_lifecycleGate)
        {
            SetBaudRateOwned(baudRate);
        }
    }

    private void SetBaudRateOwned(int baudRate)
    {
        var port = EnsureConnected();
        if (baudRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baudRate), "Baud rate must be positive.");
        }

        if (port.BaudRate == baudRate)
        {
            return;
        }

        try
        {
            port.BaudRate = baudRate;
            _baudRate = baudRate;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ArgumentOutOfRangeException(nameof(baudRate), baudRate, $"Unsupported baud rate {baudRate} for port {PortName}: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new IOException($"Failed to switch baud rate on {PortName} to {baudRate}: {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Port {PortName} is not open: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reads up to <paramref name="length"/> bytes into a caller-provided buffer. Returns as
    /// soon as any data is available; throws <see cref="TimeoutException"/> if no data arrives
    /// within <paramref name="timeoutMs"/>.
    /// <para>将最多 <paramref name="length"/> 个字节读取到调用方提供的缓冲区。一旦有数据即返回；
    /// 若在 <paramref name="timeoutMs"/> 内无数据到达则抛出 <see cref="TimeoutException"/>。</para>
    /// </summary>
    /// <param name="buffer">The destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="offset">The destination offset. <para>目标偏移量。</para></param>
    /// <param name="length">The maximum number of bytes to read. <para>最多读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes read. <para>实际读取的字节数。</para></returns>
    /// <exception cref="TimeoutException">No data arrived within the timeout. <para>超时时间内无数据到达。</para></exception>
    public int Read(byte[] buffer, int offset, int length, int timeoutMs)
    {
        var port = EnsureConnected();
        return ReadPort(port, buffer, offset, length, timeoutMs);
    }

    private static int ReadPort(SerialPort port, byte[] buffer, int offset, int length, int timeoutMs)
    {
        if (length <= 0)
        {
            return 0;
        }

        port.ReadTimeout = timeoutMs;
        try
        {
            var n = port.Read(buffer, offset, length);
            return n > 0 ? n : 0;
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Serial read timed out after {timeoutMs}ms.");
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Serial read error: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new IOException($"Serial connection lost during read: {ex.Message}", ex);
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
        var port = EnsureConnected();
        return length <= 0 ? 0 : await Task.Run(() => ReadPort(port, buffer, offset, length, timeoutMs), cancellationToken).ConfigureAwait(false);
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
    /// <exception cref="TimeoutException">The write did not complete within the timeout. <para>写入未在超时时间内完成。</para></exception>
    public long Write(byte[] data, int offset, int length, int timeoutMs)
    {
        var port = EnsureConnected();
        return WritePort(port, data, offset, length, timeoutMs);
    }

    private static long WritePort(SerialPort port, byte[] data, int offset, int length, int timeoutMs)
    {
        if (length <= 0)
        {
            return 0;
        }

        port.WriteTimeout = timeoutMs;
        try
        {
            port.Write(data, offset, length);
            return length;
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Serial write timed out after {timeoutMs}ms.");
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Serial write error: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new IOException($"Serial connection lost during write: {ex.Message}", ex);
        }
    }

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
    public async Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        var port = EnsureConnected();
        return length <= 0 ? 0 : await Task.Run(() => WritePort(port, data, offset, length, timeoutMs), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the underlying <see cref="Stream"/> of the open serial port. The caller is
    /// responsible for the returned stream's lifetime; it is invalidated when the port closes.
    /// <para>返回已打开串口底层的 <see cref="Stream"/>。调用方负责返回流的生命周期；
    /// 端口关闭后该流失效。</para>
    /// </summary>
    /// <returns>A stream over the serial port. <para>串口上的流。</para></returns>
    public Stream GetStream()
    {
        var port = EnsureConnected();
        var stream = port.BaseStream;
        stream.ReadTimeout = DefaultTimeoutMs;
        stream.WriteTimeout = DefaultTimeoutMs;
        return stream;
    }

    /// <summary>
    /// Disposes the transport, closing the serial port if it is open.
    /// <para>释放传输，若串口已打开则将其关闭。</para>
    /// </summary>
    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            DisposePortOwned();
        }
    }

    private SerialPort EnsureConnected()
    {
        ThrowIfDisposed();
        var port = _port;
        if (port is not { IsOpen: true })
        {
            throw new InvalidOperationException("Serial port is not connected.");
        }
        return port;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }
}