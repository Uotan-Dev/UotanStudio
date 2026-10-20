using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Explicit target runtime policy; acknowledging it does not prove DRAM or storage readiness.
/// <para>显式的目标端运行时策略；确认该策略并不证明 DRAM 或存储已经就绪。</para>
/// </summary>
public sealed record MtkXmlRuntimeOptions
{
    /// <summary>
    /// Gets the explicitly selected target checksum policy.
    /// <para>获取显式选择的目标端校验和策略。</para>
    /// </summary>
    /// <value>
    /// The checksum level requested from the download agent; host-side readback verification is not implied.
    /// <para>向下载代理请求的校验和级别；不意味着主机侧会进行读取回验。</para>
    /// </value>
    public required MtkXmlChecksumLevel Checksum { get; init; }
    /// <summary>
    /// Gets the explicit host OS identifier. The reference always asks the target for the Linux runtime,
    /// so this value is validated but no longer changes the serialized <c>system_os</c> token.
    /// <para>获取显式的主机操作系统标识。参考实现始终向目标请求 Linux 运行时，因此该值只做校验，
    /// 不再改变序列化的 <c>system_os</c> 标记。</para>
    /// </summary>
    /// <value>
    /// The caller's host identifier; the runtime command always serializes <c>LINUX</c> as
    /// <c>system_os</c>.
    /// <para>调用方的主机标识；运行时命令始终以 <c>LINUX</c> 序列化 <c>system_os</c>。</para>
    /// </value>
    public required MtkXmlHostOs HostOs { get; init; }
    /// <summary>
    /// Gets whether the caller authorizes the target's DRAM initialization request.
    /// <para>获取调用方是否授权目标端的 DRAM 初始化请求。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> writes <c>YES</c> into the command's <c>adv</c> element; authorization does not
    /// prove that initialization succeeds.
    /// <para>为 <see langword="true"/> 时向命令的 <c>adv</c> 元素写入 <c>YES</c>；授权并不证明初始化一定成功。</para>
    /// </value>
    public required bool InitializeDram { get; init; }
    /// <summary>
    /// Gets the target battery-presence policy.
    /// <para>获取目标端电池在位策略。</para>
    /// </summary>
    /// <value>
    /// The policy serialized as <c>battery_exist</c>; <see cref="MtkXmlBatterySetting.AutoDetect"/> is the default.
    /// <para>以 <c>battery_exist</c> 序列化的策略；默认值为 <see cref="MtkXmlBatterySetting.AutoDetect"/>。</para>
    /// </value>
    public MtkXmlBatterySetting Battery { get; init; } = MtkXmlBatterySetting.AutoDetect;
    /// <summary>
    /// Gets the target log verbosity.
    /// <para>获取目标端日志详细程度。</para>
    /// </summary>
    /// <value>
    /// The level written as <c>da_log_level</c>; <see cref="MtkXmlLogLevel.Info"/> is the default.
    /// <para>以 <c>da_log_level</c> 写入的级别；默认值为 <see cref="MtkXmlLogLevel.Info"/>。</para>
    /// </value>
    public MtkXmlLogLevel LogLevel { get; init; } = MtkXmlLogLevel.Info;
    /// <summary>
    /// Gets the target log channel.
    /// <para>获取目标端日志通道。</para>
    /// </summary>
    /// <value>
    /// The destination written as <c>log_channel</c>; <see cref="MtkXmlLogChannel.Uart"/> is the default.
    /// <para>以 <c>log_channel</c> 写入的目标通道；默认值为 <see cref="MtkXmlLogChannel.Uart"/>。</para>
    /// </value>
    public MtkXmlLogChannel LogChannel { get; init; } = MtkXmlLogChannel.Uart;

    internal XElement BuildCommand()
    {
        var checksum = Checksum switch
        {
            MtkXmlChecksumLevel.None => "NONE",
            MtkXmlChecksumLevel.Usb => "USB",
            MtkXmlChecksumLevel.Storage => "STORAGE",
            MtkXmlChecksumLevel.UsbAndStorage => "USB-STORAGE",
            _ => throw new ArgumentOutOfRangeException(nameof(Checksum))
        };
        var host = HostOs switch
        {
            MtkXmlHostOs.Windows or MtkXmlHostOs.Linux => "LINUX",
            _ => throw new ArgumentOutOfRangeException(nameof(HostOs))
        };
        var battery = Battery switch
        {
            MtkXmlBatterySetting.AutoDetect => "AUTO-DETECT",
            MtkXmlBatterySetting.Present => "YES",
            MtkXmlBatterySetting.Absent => "NO",
            _ => throw new ArgumentOutOfRangeException(nameof(Battery))
        };
        var level = LogLevel switch
        {
            MtkXmlLogLevel.Trace => "TRACE",
            MtkXmlLogLevel.Debug => "DEBUG",
            MtkXmlLogLevel.Info => "INFO",
            MtkXmlLogLevel.Warn => "WARN",
            MtkXmlLogLevel.Error => "ERROR",
            _ => throw new ArgumentOutOfRangeException(nameof(LogLevel))
        };
        var channel = LogChannel switch
        {
            MtkXmlLogChannel.Uart => "UART",
            MtkXmlLogChannel.Usb => "USB",
            MtkXmlLogChannel.Both => "BOTH",
            _ => throw new ArgumentOutOfRangeException(nameof(LogChannel))
        };
        return new XElement("da", new XElement("version", "1.1"),
            new XElement("command", "CMD:SET-RUNTIME-PARAMETER"),
            new XElement("arg", new XElement("checksum_level", checksum), new XElement("battery_exist", battery),
                new XElement("da_log_level", level), new XElement("log_channel", channel), new XElement("system_os", host)),
            new XElement("adv", new XElement("initialize_dram", InitializeDram ? "YES" : "NO")));
    }
}

/// <summary>
/// Target runtime checksum requests; these do not add host-side readback verification.
/// <para>目标端运行时校验和请求；这些请求不会增加主机侧的读取回验。</para>
/// </summary>
public enum MtkXmlChecksumLevel
{
    /// <summary>No target checksum request. <para>不请求目标端校验和。</para></summary>
    None,
    /// <summary>Request USB checksums. <para>请求 USB 校验和。</para></summary>
    Usb,
    /// <summary>Request storage checksums. <para>请求存储校验和。</para></summary>
    Storage,
    /// <summary>Request both USB and storage checksums. <para>同时请求 USB 与存储校验和。</para></summary>
    UsbAndStorage
}

/// <summary>
/// Host OS tokens supported by the XML runtime profile.
/// <para>XML 运行时配置所支持的主机操作系统标记。</para>
/// </summary>
public enum MtkXmlHostOs
{
    /// <summary>Windows host. <para>Windows 主机。</para></summary>
    Windows,
    /// <summary>Linux host. <para>Linux 主机。</para></summary>
    Linux
}

/// <summary>
/// Target battery-presence policy.
/// <para>目标端电池在位策略。</para>
/// </summary>
public enum MtkXmlBatterySetting
{
    /// <summary>Ask the agent to detect battery presence. <para>要求代理自行检测电池是否在位。</para></summary>
    AutoDetect,
    /// <summary>Declare a battery present. <para>声明电池在位。</para></summary>
    Present,
    /// <summary>Declare a battery absent. <para>声明电池不在位。</para></summary>
    Absent
}

/// <summary>
/// Target log verbosity.
/// <para>目标端日志详细程度。</para>
/// </summary>
public enum MtkXmlLogLevel
{
    /// <summary>Trace messages. <para>跟踪级消息。</para></summary>
    Trace,
    /// <summary>Debug messages. <para>调试级消息。</para></summary>
    Debug,
    /// <summary>Informational messages. <para>信息级消息。</para></summary>
    Info,
    /// <summary>Warnings. <para>警告级消息。</para></summary>
    Warn,
    /// <summary>Errors. <para>错误级消息。</para></summary>
    Error
}

/// <summary>
/// Target logging destination.
/// <para>目标端日志输出目的地。</para>
/// </summary>
public enum MtkXmlLogChannel
{
    /// <summary>UART logging. <para>通过 UART 输出日志。</para></summary>
    Uart,
    /// <summary>USB logging. <para>通过 USB 输出日志。</para></summary>
    Usb,
    /// <summary>Both UART and USB logging. <para>同时通过 UART 与 USB 输出日志。</para></summary>
    Both
}