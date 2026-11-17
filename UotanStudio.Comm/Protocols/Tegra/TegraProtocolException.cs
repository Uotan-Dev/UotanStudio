namespace UotanStudio.Comm.Protocols.Tegra;

/// <summary>
/// Failure raised by the Tegra RCM or nv3p protocol layers, carrying the status the device reported when it
/// answered with one.
/// <para>Tegra RCM 或 nv3p 协议层抛出的失败，携带设备以状态应答时上报的状态值。</para>
/// </summary>
public sealed class TegraProtocolException : IOException
{
    /// <summary>
    /// Initializes a new instance carrying only a message.
    /// <para>初始化仅携带消息的新实例。</para>
    /// </summary>
    /// <param name="message">The failure description. <para>失败描述。</para></param>
    public TegraProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance carrying a message and the underlying cause.
    /// <para>初始化携带消息与底层原因的新实例。</para>
    /// </summary>
    /// <param name="message">The failure description. <para>失败描述。</para></param>
    /// <param name="innerException">The cause. <para>底层原因。</para></param>
    public TegraProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance carrying a message, a status code and the device own text.
    /// <para>初始化携带消息、状态码与设备自身文本的新实例。</para>
    /// </summary>
    /// <param name="message">The failure description. <para>失败描述。</para></param>
    /// <param name="statusCode">The status the device reported, or <see langword="null"/> when it reported none.
    /// <para>设备上报的状态；未上报时为 <see langword="null"/>。</para></param>
    /// <param name="deviceMessage">The text the device returned with the status, or <see langword="null"/>;
    /// <para>设备随状态返回的文本；没有时为 <see langword="null"/>。</para></param>
    public TegraProtocolException(string message, uint? statusCode, string? deviceMessage)
        : base(BuildMessage(message, statusCode, deviceMessage))
    {
        StatusCode = statusCode;
        DeviceMessage = deviceMessage;
    }

    /// <summary>
    /// Gets the raw status the device reported, preserving unknown values instead of folding them into a known code.
    /// <para>获取设备上报的原始状态；未知取值原样保留，不并入任何已知码。</para>
    /// </summary>
    /// <value>The reported status, or <see langword="null"/> when the failure carried none.
    /// <para>上报的状态；失败未携带状态时为 <see langword="null"/>。</para></value>
    public uint? StatusCode { get; }

    /// <summary>
    /// Gets the text the device returned alongside the status.
    /// <para>获取设备随状态一并返回的文本。</para>
    /// </summary>
    /// <value>The device text, or <see langword="null"/> when it returned none.
    /// <para>设备文本；未返回时为 <see langword="null"/>。</para></value>
    public string? DeviceMessage { get; }

    private static string BuildMessage(string message, uint? statusCode, string? deviceMessage)
    {
        if (statusCode is not { } code)
        {
            return message;
        }

        return string.IsNullOrEmpty(deviceMessage)
            ? $"{message} (status 0x{code:X8})"
            : $"{message} (status 0x{code:X8}: {deviceMessage})";
    }
}