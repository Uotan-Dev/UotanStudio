namespace UotanStudio.Comm.Protocols.Sunxi;

/// <summary>
/// FES tool mode reported to the device, which lets the firmware decide what to do after the last
/// transfer. <para>上报给设备的 FES 工具模式，由固件决定最后一次传输之后的行为。</para>
/// </summary>
public enum SunxiToolMode : uint
{
    /// <summary>Stay in the tool. <para>停留在工具中。</para></summary>
    Normal = 1,

    /// <summary>Reboot the device. <para>重启设备。</para></summary>
    Reboot = 2,

    /// <summary>Power the device off. <para>关闭设备电源。</para></summary>
    PowerOff = 3,

    /// <summary>Restart the update flow. <para>重新开始更新流程。</para></summary>
    ReUpdate = 4,

    /// <summary>Boot the installed system. <para>启动已安装的系统。</para></summary>
    Boot = 5
}

/// <summary>
/// Shared request plumbing of the FEL and FES sessions: exact reads, response validation and the
/// identity handshake both modes start with.
/// <para>FEL 与 FES 会话共享的请求管线：精确读取、响应校验，以及两种模式都以之开始的身份握手。</para>
/// </summary>
public abstract class SunxiEfexSession
{
    /// <summary>Creates a session over one bulk transport. <para>在一条批量传输之上创建会话。</para></summary>
    /// <param name="transport">Bulk transport the session runs on. <para>会话所使用的批量传输。</para></param>
    private protected SunxiEfexSession(ISunxiEfexTransport transport) => Transport = transport;

    /// <summary>Bulk transport the session runs on. <para>会话所使用的批量传输。</para></summary>
    private protected ISunxiEfexTransport Transport { get; }

    /// <summary>
    /// Sends <see cref="SunxiEfexCommand.VerifyDevice"/> and returns the identity the device reports.
    /// <para>发送 <see cref="SunxiEfexCommand.VerifyDevice"/> 并返回设备上报的身份信息。</para>
    /// </summary>
    /// <remarks>
    /// The identity stage is a read stage like any other: the host announces the request with a 0x11
    /// header before the device sends the 32 bytes, and both reference clients do the same. A bare read
    /// leaves host and device waiting for each other until the transport times out.
    /// <para>身份阶段与其它读阶段相同：主机在设备送出 32 字节之前先用 0x11 头声明请求，两份参考客户端都如此。
    /// 裸读会让主机与设备互相等待直至传输超时。</para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消本次交互。</para></param>
    /// <returns>The identity payload. <para>身份负载。</para></returns>
    /// <exception cref="SunxiEfexException">The device answered with a non-zero status.
    /// <para>设备以非零状态应答。</para></exception>
    /// <exception cref="InvalidDataException">The device returned an unexpected frame.
    /// <para>设备返回了异常帧。</para></exception>
    public async Task<SunxiDeviceIdentity> VerifyDeviceAsync(CancellationToken cancellationToken = default)
    {
        await SendFelRequestAsync(SunxiEfexCommand.VerifyDevice, 0, 0, cancellationToken).ConfigureAwait(false);
        var identity = new byte[SunxiEfexConstants.IdentitySize];
        await ReadStageAsync(identity, cancellationToken).ConfigureAwait(false);
        await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        return SunxiEfexFrames.ReadIdentity(identity);
    }

    /// <summary>
    /// Writes the 32-byte bulk header for <paramref name="length"/> payload bytes.
    /// <para>为 <paramref name="length"/> 个负载字节写入 32 字节批量头。</para>
    /// </summary>
    /// <param name="request">Request type byte. <para>请求类型字节。</para></param>
    /// <param name="length">Payload bytes that follow. <para>随后跟随的负载字节数。</para></param>
    /// <param name="cancellationToken">Cancels the write. <para>取消本次写入。</para></param>
    /// <param name="timeoutMilliseconds">Transport timeout for the write, or <see langword="null"/> to use the
    /// session default. <para>本次写入的传输超时；<see langword="null"/> 表示使用会话默认值。</para></param>
    /// <returns>A task that completes when the header is on the wire. <para>头发送完成的任务。</para></returns>
    protected async Task WriteRequestHeaderAsync(byte request, int length, CancellationToken cancellationToken,
        int? timeoutMilliseconds = null)
    {
        var header = new byte[SunxiEfexConstants.RequestHeaderSize];
        SunxiEfexFrames.WriteRequestHeader(header, request, length);
        await Transport.WriteAsync(header, cancellationToken, timeoutMilliseconds).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a 16-byte FEL request wrapped in a write stage and validates the bulk response.
    /// <para>在写阶段中发送 16 字节 FEL 请求并校验批量响应。</para>
    /// </summary>
    /// <param name="command">FEL command. <para>FEL 命令。</para></param>
    /// <param name="address">Target address. <para>目标地址。</para></param>
    /// <param name="length">Length the command applies to. <para>命令作用的长度。</para></param>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消本次交互。</para></param>
    /// <returns>A task that completes when the device acknowledged the request.
    /// <para>设备确认请求后完成的任务。</para></returns>
    protected async Task SendFelRequestAsync(SunxiEfexCommand command, uint address, uint length,
        CancellationToken cancellationToken)
    {
        await WriteRequestHeaderAsync(SunxiEfexFrames.WriteRequest, SunxiEfexConstants.FelRequestSize,
            cancellationToken).ConfigureAwait(false);
        var request = new byte[SunxiEfexConstants.FelRequestSize];
        SunxiEfexFrames.WriteFelRequest(request, command, address, length);
        await Transport.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a payload under a write stage and validates the bulk response.
    /// <para>在写阶段中写入负载并校验批量响应。</para>
    /// </summary>
    /// <param name="data">Payload to write. <para>待写入的负载。</para></param>
    /// <param name="cancellationToken">Cancels the write. <para>取消本次写入。</para></param>
    /// <returns>A task that completes when the device accepted the payload.
    /// <para>设备接受负载后完成的任务。</para></returns>
    protected async Task WriteStageAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await WriteRequestHeaderAsync(SunxiEfexFrames.WriteRequest, data.Length, cancellationToken)
            .ConfigureAwait(false);
        await Transport.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a payload under a read stage and validates the bulk response.
    /// <para>在读阶段中读取负载并校验批量响应。</para>
    /// </summary>
    /// <param name="buffer">Destination buffer that the stage fills. <para>由该阶段填满的目标缓冲区。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>A task that completes when the payload arrived. <para>负载到达后完成的任务。</para></returns>
    protected async Task ReadStageAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        await WriteRequestHeaderAsync(SunxiEfexFrames.ReadRequest, buffer.Length, cancellationToken)
            .ConfigureAwait(false);
        await ReadExactAsync(buffer, "read stage payload", cancellationToken).ConfigureAwait(false);
        await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the 8-byte status payload that follows a FEL command.
    /// <para>读取 FEL 命令之后的 8 字节状态负载。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>A task that completes when the status arrived. <para>状态到达后完成的任务。</para></returns>
    protected async Task ReadStatusAsync(CancellationToken cancellationToken)
    {
        var status = new byte[SunxiEfexConstants.StatusSize];
        await ReadStageAsync(status, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads and validates one 13-byte bulk response, failing when the device reports a non-zero status.
    /// <para>读取并校验一条 13 字节批量响应；设备上报非零状态时失败。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <param name="timeoutMilliseconds">Transport timeout for the read, or <see langword="null"/> to use the
    /// session default. <para>本次读取的传输超时；<see langword="null"/> 表示使用会话默认值。</para></param>
    /// <returns>The validated response. <para>已校验的响应。</para></returns>
    /// <exception cref="InvalidDataException">The response is short or does not carry the AWUS magic.
    /// <para>响应过短或未携带 AWUS 魔数。</para></exception>
    /// <exception cref="SunxiEfexException">The device reported a non-zero status.
    /// <para>设备上报了非零状态。</para></exception>
    protected async Task<SunxiUsbResponse> ReadResponseAsync(CancellationToken cancellationToken,
        int? timeoutMilliseconds = null)
    {
        var response = new byte[SunxiEfexConstants.ResponseSize];
        await ReadExactAsync(response, "bulk response", cancellationToken, timeoutMilliseconds)
            .ConfigureAwait(false);
        var parsed = SunxiEfexFrames.ReadResponse(response);
        if (parsed.Status != 0)
        {
            throw new SunxiEfexException(parsed.Status);
        }

        return parsed;
    }

    /// <summary>
    /// Fills the buffer or throws; a short read means the device stopped a stage early.
    /// <para>填满缓冲区或抛出；短读表示设备提前终止了某个阶段。</para>
    /// </summary>
    /// <param name="buffer">Destination buffer. <para>目标缓冲区。</para></param>
    /// <param name="what">Stage name used in the failure message. <para>失败消息中使用的阶段名。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <param name="timeoutMilliseconds">Transport timeout for each read, or <see langword="null"/> to use the
    /// session default. <para>每次读取的传输超时；<see langword="null"/> 表示使用会话默认值。</para></param>
    /// <returns>A task that completes when the buffer is full. <para>缓冲区填满后完成的任务。</para></returns>
    /// <exception cref="EndOfStreamException">The device delivered no bytes before the buffer was full.
    /// <para>在缓冲区填满之前设备没有送出字节。</para></exception>
    protected async Task ReadExactAsync(Memory<byte> buffer, string what, CancellationToken cancellationToken,
        int? timeoutMilliseconds = null)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await Transport.ReadAsync(buffer[filled..], cancellationToken, timeoutMilliseconds)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                throw new EndOfStreamException($"The device returned no more bytes while reading the {what}.");
            }

            filled += read;
        }
    }
}