namespace UotanStudio.Comm.Protocols.Fastboot;

/// <summary>
/// Represents the state of a fastboot protocol response.
/// <para>表示 fastboot 协议响应的状态。</para>
/// </summary>
public enum FastbootResponseState
{
    /// <summary>
    /// The command completed successfully (OKAY).
    /// <para>命令成功完成（OKAY）。</para>
    /// </summary>
    Success,

    /// <summary>
    /// The device reported a failure (FAIL).
    /// <para>设备报告失败（FAIL）。</para>
    /// </summary>
    Fail,

    /// <summary>
    /// The device sent a TEXT frame.
    /// <para>设备发送了 TEXT 帧。</para>
    /// </summary>
    /// <remarks>
    /// Non-terminal marker: a response whose parsing ended on a TEXT frame is still incomplete, so
    /// <see cref="FastbootSession.ReceiveResponse"/> keeps accumulating until a terminal frame
    /// arrives and never hands this state to a caller.
    /// <para>非终止标记：解析停在 TEXT 帧上的响应尚未完成，因此
    /// <see cref="FastbootSession.ReceiveResponse"/> 会继续累积直到终止帧到达，绝不会把该状态交给
    /// 调用方。</para>
    /// </remarks>
    Text,

    /// <summary>
    /// The device sent a DATA frame announcing a payload.
    /// <para>设备发送了 DATA 帧声明后续负载。</para>
    /// </summary>
    Data,

    /// <summary>
    /// The device sent an INFO frame.
    /// <para>设备发送了 INFO 帧。</para>
    /// </summary>
    /// <remarks>
    /// Non-terminal marker: a response whose parsing ended on an INFO frame is still incomplete, so
    /// <see cref="FastbootSession.ReceiveResponse"/> keeps accumulating until a terminal frame
    /// arrives and never hands this state to a caller. Leaving the state at the default
    /// <see cref="Success"/> here instead would make the parser return after the first INFO line and
    /// truncate every multi-line reply (<c>getvar:all</c> above all).
    /// <para>非终止标记：解析停在 INFO 帧上的响应尚未完成，因此
    /// <see cref="FastbootSession.ReceiveResponse"/> 会继续累积直到终止帧到达，绝不会把该状态交给
    /// 调用方。若此处把状态留在默认的 <see cref="Success"/>，解析器会在第一行 INFO 之后就返回，从而截断
    /// 所有多行应答（尤其是 <c>getvar:all</c>）。</para>
    /// </remarks>
    Info,

    /// <summary>
    /// The device sent an unrecognized status code.
    /// <para>设备发送了无法识别的状态码。</para>
    /// </summary>
    Unknown,

    /// <summary>
    /// No response was received before the timeout.
    /// <para>超时前未收到响应。</para>
    /// </summary>
    Timeout
}

/// <summary>
/// Represents a response received from a fastboot device.
/// <para>表示从 fastboot 设备接收到的响应。</para>
/// </summary>
public class FastbootResponse
{
    /// <summary>
    /// Substring a bootloader uses to ask the host for a fastbootd reboot.
    /// <para>bootloader 用于请求宿主重启到 fastbootd 的子串。</para>
    /// </summary>
    private const string FastbootRebootHint = "reboot fastboot";

    /// <summary>
    /// Gets or sets the result state.
    /// <para>获取或设置结果状态。</para>
    /// </summary>
    /// <value>
    /// The terminal classification produced by <c>FastbootSession.ReceiveResponse</c>: Success,
    /// Fail, Data, Unknown or Timeout; INFO and TEXT frames are accumulated rather than reported
    /// as states.
    /// <para><c>FastbootSession.ReceiveResponse</c> 产生的终止分类：Success、Fail、Data、Unknown
    /// 或 Timeout；INFO 与 TEXT 帧会被累积而不作为状态报告。</para>
    /// </value>
    public FastbootResponseState State { get; set; }

    /// <summary>
    /// Gets or sets the response text: the accumulated TEXT frame content, or the terminal
    /// OKAY/FAIL payload when no TEXT frames were received.
    /// <para>获取或设置响应文本：累积的 TEXT 帧内容；未收到 TEXT 帧时为终止 OKAY/FAIL 帧的负载。</para>
    /// </summary>
    /// <value>Accumulated TEXT frame content when TEXT frames were received, otherwise the
    /// terminal OKAY/FAIL payload or a parser message such as <c>"response timeout"</c>.
    /// <para>收到 TEXT 帧时为其累积内容；否则为终止 OKAY/FAIL 负载或解析器消息
    /// （如 <c>"response timeout"</c>）。</para></value>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the data payload size (from DATA frame).
    /// <para>获取或设置数据负载大小（来自 DATA 帧）。</para>
    /// </summary>
    /// <value>The receive-buffer size in bytes parsed from the DATA frame hex field, meaningful
    /// when <see cref="State"/> is <see cref="FastbootResponseState.Data"/>; otherwise 0.
    /// <para>从 DATA 帧 16 进制字段解析的接收缓冲字节数，当 <see cref="State"/> 为
    /// <see cref="FastbootResponseState.Data"/> 时有意义；其余情况为 0。</para></value>
    public long DataSize { get; set; }

    /// <summary>
    /// Gets or sets the raw data payload.
    /// <para>获取或设置原始数据负载。</para>
    /// </summary>
    /// <value>Raw payload bytes held alongside the response; the session parser records only the
    /// announced <see cref="DataSize"/> and leaves this <see langword="null"/>, because DATA
    /// payload bytes are streamed separately by the transfer helpers.
    /// <para>与响应一起保存的原始负载字节；会话解析器只记录声明的 <see cref="DataSize"/> 并将
    /// 此项留为 <see langword="null"/>，DATA 负载字节由传输辅助方法单独流式处理。</para></value>
    public byte[]? Data { get; set; }

    /// <summary>
    /// Gets or sets the accumulated INFO lines.
    /// <para>获取或设置累积的 INFO 行。</para>
    /// </summary>
    /// <value>All INFO frame contents received before the terminal frame; a device sending more
    /// than 10000 INFO lines turns the response into Fail.
    /// <para>终止帧之前收到的全部 INFO 内容；设备发送超过 10000 行 INFO 时响应转为 Fail。</para></value>
    public List<string> InfoLines { get; set; } = [];

    /// <summary>
    /// Gets or sets the content following the terminal status prefix (minus trailing NUL),
    /// e.g. the OKAY/FAIL payload. INFO frame contents are accumulated in
    /// <see cref="InfoLines"/> instead.
    /// <para>获取或设置终止状态前缀后的内容（减去尾随 NUL），如 OKAY/FAIL 负载。
    /// INFO 帧内容累积在 <see cref="InfoLines"/> 中。</para>
    /// </summary>
    /// <value>The terminal OKAY/FAIL payload with a single trailing NUL trimmed, or the parser's
    /// error description when it synthesizes a Fail or Unknown result; empty on Data and Timeout
    /// results.
    /// <para>终止 OKAY/FAIL 负载并去除单个尾随 NUL；解析器合成 Fail 或 Unknown 结果时为错误描述；
    /// Data 与 Timeout 结果为空。</para></value>
    public string Info { get; set; } = string.Empty;

    /// <summary>
    /// Returns <c>true</c> when the response indicates success.
    /// <para>响应指示成功时返回 <c>true</c>。</para>
    /// </summary>
    /// <value><see langword="true"/> exactly when <see cref="State"/> is
    /// <see cref="FastbootResponseState.Success"/>.
    /// <para>当且仅当 <see cref="State"/> 为 <see cref="FastbootResponseState.Success"/> 时为
    /// <see langword="true"/>。</para></value>
    public bool IsSuccess => State == FastbootResponseState.Success;

    /// <summary>
    /// Gets whether the device asked the host to reboot into fastbootd before the requested action can complete. The hint is exposed for the caller to act on; the session never reboots the device itself.
    /// <para>获取设备是否要求宿主先重启进入 fastbootd 才能完成所请求的操作。该提示暴露给调用方自行处置，会话自身不重启设备。</para>
    /// </summary>
    /// <value><see langword="true"/> when the terminal payload or any INFO/TEXT line carries the reboot hint (case-insensitive); <see langword="false"/> otherwise.
    /// <para>终止负载或任一 INFO/TEXT 行带有重启提示（不区分大小写）时为 <see langword="true"/>；其余情况为 <see langword="false"/>。</para></value>
    public bool RequestsFastbootReboot
        => Info.Contains(FastbootRebootHint, StringComparison.OrdinalIgnoreCase)
           || Text.Contains(FastbootRebootHint, StringComparison.OrdinalIgnoreCase)
           || InfoLines.Exists(line => line.Contains(FastbootRebootHint, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Throws a <see cref="FastbootException"/> unless the response is a confirmed OKAY.
    /// <para>除确认的 OKAY 外一律抛出 <see cref="FastbootException"/>。</para>
    /// </summary>
    /// <remarks>
    /// Only <c>OKAY</c> is a trustworthy terminal frame: <c>FAIL</c>, a timeout, or an unrecognized
    /// status must all fail the operation, because every destructive call site (download, flash, boot,
    /// update-super) gates the next command on this check, and a silently-passed garbage reply would let a
    /// flash proceed on a device that never acknowledged the payload.
    /// <para>只有 <c>OKAY</c> 是可信的终态帧：<c>FAIL</c>、超时或无法识别的状态都必须使操作失败，因为
    /// 每个破坏性调用点（download、flash、boot、update-super）都以本检查作为下一条命令的闸门；被静默
    /// 放行的垃圾应答会让刷写从未确认负载的设备上继续。</para>
    /// </remarks>
    /// <exception cref="FastbootException">The response is anything other than an OKAY status.
    /// <para>响应不是 OKAY 状态时抛出。</para></exception>
    public void ThrowIfFailed()
    {
        if (State != FastbootResponseState.Success)
        {
            throw new FastbootException(State, string.IsNullOrEmpty(Info) ? Text : Info);
        }
    }
}

/// <summary>
/// Exception thrown when a fastboot command fails, carrying the terminal response state.
/// <para>fastboot 命令失败时抛出的异常，携带终止响应状态。</para>
/// </summary>
/// <param name="state">The response state that caused the failure.
/// <para>导致失败的响应状态。</para></param>
/// <param name="message">The error message.
/// <para>错误消息。</para></param>
public class FastbootException(FastbootResponseState state, string message) : Exception($"fastboot {state}: {message}")
{
    /// <summary>
    /// Gets the response state.
    /// <para>获取响应状态。</para>
    /// </summary>
    public FastbootResponseState State { get; } = state;
}