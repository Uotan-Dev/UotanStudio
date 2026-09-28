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
    /// Throws a <see cref="FastbootException"/> unless the response is a confirmed OKAY. Only
    /// <c>OKAY</c> is a trustworthy terminal frame: <c>FAIL</c>, a timeout, or an unrecognized
    /// status must all fail the operation, because every destructive call site (download, flash,
    /// boot, update-super) gates the next command on this check, and a silently-passed garbage
    /// reply would let a flash proceed on a device that never acknowledged the payload.
    /// <para>除确认的 OKAY 外一律抛出 <see cref="FastbootException"/>。只有 <c>OKAY</c> 是可信的
    /// 终态帧：<c>FAIL</c>、超时或无法识别的状态都必须使操作失败，因为每个破坏性调用点
    /// （download、flash、boot、update-super）都以本检查作为下一条命令的闸门；被静默放行的
    /// 垃圾应答会让刷写从未确认负载的设备上继续。</para>
    /// </summary>
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
/// Exception thrown when a fastboot command fails.
/// <para>fastboot 命令失败时抛出的异常。</para>
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="FastbootException"/> class.
/// <para>初始化 <see cref="FastbootException"/> 类的新实例。</para>
/// </remarks>
/// <param name="state">The response state. 响应状态。</param>
/// <param name="message">The error message. 错误消息。</param>
internal class FastbootException(FastbootResponseState state, string message) : Exception($"fastboot {state}: {message}")
{
    /// <summary>
    /// Gets the response state.
    /// <para>获取响应状态。</para>
    /// </summary>
    public FastbootResponseState State { get; } = state;
}