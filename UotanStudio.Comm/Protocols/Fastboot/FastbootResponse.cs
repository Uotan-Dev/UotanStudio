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
    public FastbootResponseState State { get; set; }

    /// <summary>
    /// Gets or sets the response text: the accumulated TEXT frame content, or the terminal
    /// OKAY/FAIL payload when no TEXT frames were received.
    /// <para>获取或设置响应文本：累积的 TEXT 帧内容；未收到 TEXT 帧时为终止 OKAY/FAIL 帧的负载。</para>
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the data payload size (from DATA frame).
    /// <para>获取或设置数据负载大小（来自 DATA 帧）。</para>
    /// </summary>
    public long DataSize { get; set; }

    /// <summary>
    /// Gets or sets the raw data payload.
    /// <para>获取或设置原始数据负载。</para>
    /// </summary>
    public byte[]? Data { get; set; }

    /// <summary>
    /// Gets or sets the accumulated INFO lines.
    /// <para>获取或设置累积的 INFO 行。</para>
    /// </summary>
    public List<string> InfoLines { get; set; } = [];

    /// <summary>
    /// Gets or sets the content following the terminal status prefix (minus trailing NUL),
    /// e.g. the OKAY/FAIL payload. INFO frame contents are accumulated in
    /// <see cref="InfoLines"/> instead.
    /// <para>获取或设置终止状态前缀后的内容（减去尾随 NUL），如 OKAY/FAIL 负载。
    /// INFO 帧内容累积在 <see cref="InfoLines"/> 中。</para>
    /// </summary>
    public string Info { get; set; } = string.Empty;

    /// <summary>
    /// Returns <c>true</c> when the response indicates success.
    /// <para>响应指示成功时返回 <c>true</c>。</para>
    /// </summary>
    public bool IsSuccess => State == FastbootResponseState.Success;

    /// <summary>
    /// Throws an <see cref="FastbootException"/> if the response indicates a failure or timeout.
    /// <para>如果响应表示失败或超时，则抛出 <see cref="FastbootException"/>。</para>
    /// </summary>
    /// <exception cref="FastbootException">Thrown on failure or timeout. 失败或超时时抛出。</exception>
    public void ThrowIfFailed()
    {
        if (State is FastbootResponseState.Fail or FastbootResponseState.Timeout)
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
public class FastbootException(FastbootResponseState state, string message) : Exception($"fastboot {state}: {message}")
{
    /// <summary>
    /// Gets the response state.
    /// <para>获取响应状态。</para>
    /// </summary>
    public FastbootResponseState State { get; } = state;
}