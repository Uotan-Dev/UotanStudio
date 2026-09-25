using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>
    /// Sets explicit XML runtime parameters and advances only after the complete END/START exchange.
    /// <para>设置显式的 XML 运行时参数，并在完整的 END/START 交换结束后才推进状态。</para>
    /// </summary>
    /// <remarks>The target may act on InitializeDram; the caller must authorize this operation.
    /// Completion is not independent proof of working DRAM, a started DA2 or storage geometry.
    /// <para>目标设备可能会执行 InitializeDram；调用方必须授权本次操作。完成并不意味着 DRAM 正常工作、
    /// DA2 已启动或存储几何信息已得到独立验证。</para></remarks>
    /// <param name="options">
    /// Immutable caller-selected runtime policy.
    /// <para>调用方选定的不可变运行时策略。</para>
    /// </param>
    /// <param name="timeoutMs">
    /// Cooperative whole-exchange deadline, from 1 ms to 24 hours.
    /// <para>整个交换过程的协作式截止时间，范围从 1 毫秒到 24 小时。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation for waiting and the control exchange.
    /// <para>用于等待和控制交换的取消令牌。</para>
    /// </param>
    /// <returns>
    /// A task that completes once the runtime configuration command has been acknowledged.
    /// <para>在运行时配置命令被确认之后完成的任务。</para>
    /// </returns>
    public Task ConfigureXmlRuntimeAsync(MtkXmlRuntimeOptions options, int timeoutMs = 120000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateXmlSetupTimeout(timeoutMs);
        return ExecuteXmlControlCommandsAsync([options.BuildCommand()], MtkSessionState.XmlControlReady,
            MtkSessionState.XmlRuntimeConfigured, cancellationToken, timeoutMs);
    }

    /// <summary>
    /// Advertises implemented callbacks and requests hardware initialization under one operation lease.
    /// <para>在一次操作租约下声明已实现的回调，并请求硬件初始化。</para>
    /// </summary>
    /// <remarks>
    /// Only progress-report handling is advertised by default. When
    /// <see cref="MtkSession.HostFileStore"/> is attached, the file transfer and filesystem
    /// size callbacks are also declared and device-initiated requests are served through it;
    /// otherwise an unexpected request faults the session.
    /// Both complete command exchanges must succeed before advancing. This is the agent's
    /// acknowledgement, not a storage readiness or hardware interoperability guarantee.
    /// <para>默认只声明进度上报处理能力。当附加了 <see cref="MtkSession.HostFileStore"/> 时，
    /// 文件传输和文件系统大小回调也会被声明，设备发起的请求会通过它得到服务；否则意外请求会使会话进入故障状态。
    /// 两次完整的命令交换都必须成功才能推进状态。这只是代理的确认，并非存储就绪或硬件互操作性的保证。</para>
    /// </remarks>
    /// <param name="timeoutMs">
    /// Cooperative deadline shared by both exchanges, from 1 ms to 24 hours.
    /// <para>两次交换共享的协作式截止时间，范围从 1 毫秒到 24 小时。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation for both command exchanges.
    /// <para>用于两次命令交换的取消令牌。</para>
    /// </param>
    /// <returns>
    /// A task that completes once both capability and initialization exchanges have been acknowledged.
    /// <para>在能力声明与初始化两次交换均被确认之后完成的任务。</para>
    /// </returns>
    public Task InitializeXmlHardwareAsync(int timeoutMs = 120000, CancellationToken cancellationToken = default)
    {
        ValidateXmlSetupTimeout(timeoutMs);
        return ExecuteXmlControlCommandsAsync(XmlHardwareCommands(), MtkSessionState.XmlRuntimeConfigured,
            MtkSessionState.XmlHardwareInitialized, cancellationToken, timeoutMs);
    }

    /// <summary>
    /// Requests the newly started XML DA2's hardware initialization without enabling storage access.
    /// <para>请求对新启动的 XML DA2 执行硬件初始化，但不启用存储访问。</para>
    /// </summary>
    /// <remarks>Only progress callbacks are accepted during initialization. Authentication and storage
    /// discovery remain separate; acknowledgement does not independently verify hardware effects.
    /// <para>初始化期间仅接受进度回调。认证与存储发现仍是独立步骤；确认并不能独立验证硬件效果。</para></remarks>
    /// <param name="timeoutMs">
    /// Cooperative deadline shared by both exchanges, from 1 ms to 24 hours.
    /// <para>两次交换共享的协作式截止时间，范围从 1 毫秒到 24 小时。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation for capability and initialization exchanges.
    /// <para>用于能力声明与初始化交换的取消令牌。</para>
    /// </param>
    /// <returns>
    /// A task that completes once the second-stage capability and initialization exchanges have been acknowledged.
    /// <para>在第二阶段的能力声明与初始化交换均被确认之后完成的任务。</para>
    /// </returns>
    public Task InitializeXmlStageTwoHardwareAsync(int timeoutMs = 120000, CancellationToken cancellationToken = default)
    {
        ValidateXmlSetupTimeout(timeoutMs);
        return ExecuteXmlControlCommandsAsync(XmlHardwareCommands(), MtkSessionState.XmlStageTwoStarted,
            MtkSessionState.XmlStageTwoHardwareInitialized, cancellationToken, timeoutMs);
    }

    private XElement[] XmlHardwareCommands() =>
    [
        XmlHostCapabilities(
            download: HostFileStore is not null,
            upload: HostFileStore is not null,
            fileSize: HostFileStore is not null),
        new("da", new XElement("version", "1.0"), new XElement("command", "CMD:NOTIFY-INIT-HW"))
    ];

    private static XElement XmlHostCapabilities(bool download, bool upload = false, bool fileSize = false) => new("da", new XElement("version", "1.0"),
        new XElement("command", "CMD:HOST-SUPPORTED-COMMANDS"),
        new XElement("arg", new XElement("host_capability",
            (download ? "CMD:DOWNLOAD-FILE^1@" : "") + (fileSize ? "CMD:FILE-SYS-OPERATION^1@" : "")
            + "CMD:PROGRESS-REPORT^1@" + (upload ? "CMD:UPLOAD-FILE^1@" : ""))));

    private static void ValidateXmlSetupTimeout(int timeoutMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeoutMs, 86400000);
    }
}