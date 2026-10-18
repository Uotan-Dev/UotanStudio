using System.IO.Ports;

namespace UotanStudio.Comm.Transports.Serial;

/// <summary>Explicit serial connection settings; no port enumeration, profile guessing or automatic retries.
/// <para>显式串口连接设置；不进行端口枚举、配置猜测或自动重试。</para></summary>
public sealed record SerialConnectionOptions
{
    /// <summary>Gets the exact caller-selected COM/TTY name.
    /// <para>获取调用方精确选定的 COM/TTY 名称。</para></summary>
    /// <value>The port name opened verbatim, without enumeration or fallback.
    /// <para>原样打开的端口名称，不进行枚举或回退。</para></value>
    public required string PortName { get; init; }
    /// <summary>Gets the explicitly selected positive baud rate.
    /// <para>获取显式选定的正波特率。</para></summary>
    /// <value>A strictly positive baud rate; zero and negative values are rejected by validation.
    /// <para>严格为正的波特率；零和负值会被校验拒绝。</para></value>
    public required int BaudRate { get; init; }
    /// <summary>Gets the explicitly selected flow-control policy.
    /// <para>获取显式选定的流控策略。</para></summary>
    /// <value>The handshake policy applied to the port; any value defined by the enumeration is accepted.
    /// <para>应用于端口的握手策略；接受该枚举定义的任意值。</para></value>
    public required Handshake Handshake { get; init; }
    /// <summary>Gets the parity policy.
    /// <para>获取校验位策略。</para></summary>
    /// <value>The parity setting for each frame, defaulting to no parity.
    /// <para>每帧的校验设置，默认为无校验。</para></value>
    public Parity Parity { get; init; } = Parity.None;
    /// <summary>Gets the number of data bits, from five through eight.
    /// <para>获取数据位数，取值范围为五至八。</para></summary>
    /// <value>An integer from 5 through 8 inclusive; validation rejects other values. Defaults to 8.
    /// <para>取值 5 至 8（含）的整数；校验会拒绝其他值。默认为 8。</para></value>
    public int DataBits { get; init; } = 8;
    /// <summary>Gets the stop-bit policy.
    /// <para>获取停止位策略。</para></summary>
    /// <value>The stop-bit setting; the value representing no stop bits is rejected. Defaults to one stop bit.
    /// <para>停止位设置；表示无停止位的值会被拒绝。默认为一个停止位。</para></value>
    public StopBits StopBits { get; init; } = StopBits.One;
    /// <summary>Gets the initial finite read/write timeout in milliseconds.
    /// <para>获取初始的有限读/写超时时间（毫秒）。</para></summary>
    /// <value>A strictly positive timeout applied to the opened transport; defaults to 5000.
    /// <para>应用于已打开传输的严格正超时值；默认为 5000。</para></value>
    public int TimeoutMs { get; init; } = 5000;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PortName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BaudRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TimeoutMs);
        if (!Enum.IsDefined(Handshake) || !Enum.IsDefined(Parity) || !Enum.IsDefined(StopBits)
            || StopBits == StopBits.None || DataBits is < 5 or > 8)
        {
            throw new ArgumentException("The serial framing or flow-control settings are invalid.");
        }
    }

    /// <summary>Opens only the selected port and transfers ownership to the caller on success.
    /// <para>仅打开所选端口，并在成功后将所有权移交给调用方。</para></summary>
    /// <remarks>Native opening is not cancellable mid-call; cancellation is checked again before publishing the transport.
    /// <para>原生打开过程在调用中途不可取消；在发布传输之前会再次检查取消。</para></remarks>
    /// <param name="cancellationToken">Cancels the attempt before the native open and again before the transport is published; it does not abort an open already in progress.
    /// <para>在原生打开之前以及发布传输之前各检查一次取消；不会中断正在进行的打开操作。</para></param>
    /// <returns>The connected serial transport, now owned by the caller.
    /// <para>已连接的串口传输，此时由调用方持有。</para></returns>
    /// <exception cref="ArgumentException">The port name is null or blank, or the framing or flow-control settings are invalid.
    /// <para>端口名称为 null 或空白，或成帧、流控设置无效。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The baud rate or the timeout is not positive.
    /// <para>波特率或超时值不是正数。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled before the transport was published.
    /// <para>在发布传输之前 <paramref name="cancellationToken"/> 已触发。</para></exception>
    public async Task<SerialTransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var transport = new SerialTransport(PortName, BaudRate, Parity, DataBits, StopBits, Handshake)
        {
            DefaultTimeoutMs = TimeoutMs
        };
        try
        {
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return transport;
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }
}