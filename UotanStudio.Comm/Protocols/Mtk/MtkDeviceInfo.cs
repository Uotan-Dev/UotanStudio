namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Hardware identity and security flags reported by the MTK boot protocol.
/// <para>MTK 启动协议所报告的硬件身份与安全标志。</para></summary>
/// <param name="HardwareCode">The hardware code returned by command FD.
/// <para>由命令 FD 返回的硬件代码。</para></param>
/// <param name="HardwareSubCode">The hardware subcode returned by command FC.
/// <para>由命令 FC 返回的硬件子代码。</para></param>
/// <param name="HardwareVersion">The hardware revision returned by command FC.
/// <para>由命令 FC 返回的硬件修订版本。</para></param>
/// <param name="SoftwareVersion">The software revision returned by command FC.
/// <para>由命令 FC 返回的软件修订版本。</para></param>
/// <param name="TargetConfiguration">The unmodified target security bitfield.
/// <para>未经修改的目标安全位字段。</para></param>
/// <param name="BlVersion">The bootloader version byte reported by command FE, or null when the
/// target did not answer the query.
/// <para>由命令 FE 上报的 bootloader 版本字节；目标未应答查询时为 null。</para></param>
/// <param name="BromVersion">The Boot ROM version byte reported by command FF, or null when the
/// target did not answer the query.
/// <para>由命令 FF 上报的 Boot ROM 版本字节；目标未应答查询时为 null。</para></param>
/// <param name="Meid">The media device identifier reported by command E1, or null when the target
/// did not report one.
/// <para>由命令 E1 上报的媒体设备标识；目标未上报时为 null。</para></param>
/// <param name="Socid">The secure on-chip identifier reported by command E7, or null when the
/// target did not report one.
/// <para>由命令 E7 上报的安全片上标识；目标未上报时为 null。</para></param>
public sealed record MtkDeviceInfo(
    ushort HardwareCode,
    ushort HardwareSubCode,
    ushort HardwareVersion,
    ushort SoftwareVersion,
    uint TargetConfiguration,
    byte? BlVersion = null,
    byte? BromVersion = null,
    byte[]? Meid = null,
    byte[]? Socid = null)
{
    /// <summary>Gets whether secure boot is enabled.
    /// <para>获取是否已启用安全启动。</para></summary>
    /// <value><see langword="true"/> when bit 0 of the target configuration is set.
    /// <para>当目标配置的第 0 位被置位时为 <see langword="true"/>。</para></value>
    public bool SecureBootEnabled => (TargetConfiguration & 1) != 0;

    /// <summary>Gets whether serial link authentication is required.
    /// <para>获取是否要求串行链路认证。</para></summary>
    /// <value><see langword="true"/> when bit 1 of the target configuration is set.
    /// <para>当目标配置的第 1 位被置位时为 <see langword="true"/>。</para></value>
    public bool SerialLinkAuthenticationRequired => (TargetConfiguration & 2) != 0;

    /// <summary>Gets whether download agent authentication is required.
    /// <para>获取是否要求下载代理认证。</para></summary>
    /// <value><see langword="true"/> when bit 2 of the target configuration is set.
    /// <para>当目标配置的第 2 位被置位时为 <see langword="true"/>。</para></value>
    public bool DownloadAgentAuthenticationRequired => (TargetConfiguration & 4) != 0;

    /// <summary>Gets whether the boot protocol reports that a root certificate is required.
    /// <para>获取启动协议是否报告要求根证书。</para></summary>
    /// <value><see langword="true"/> when bit 4 of the target configuration is set.
    /// <para>当目标配置的第 4 位被置位时为 <see langword="true"/>。</para></value>
    public bool RootCertificateRequired => (TargetConfiguration & 0x10) != 0;
}

/// <summary>Boot protocol stages, distinct from download-agent storage readiness.
/// <para>启动协议阶段，与下载代理的存储就绪状态相区别。</para></summary>
public enum MtkSessionState
{
    /// <summary>No boot handshake has been completed.
    /// <para>尚未完成任何引导握手。</para></summary>
    Created,
    /// <summary>The boot handshake and identity queries completed.
    /// <para>引导握手与身份查询已完成。</para></summary>
    Bootloader,
    /// <summary>A download agent was uploaded and its checksum verified.
    /// <para>下载代理已上传并通过校验和验证。</para></summary>
    AgentUploaded,
    /// <summary>The bootloader acknowledged the jump; DA-specific initialization is still required.
    /// <para>引导程序已确认跳转；仍需针对 DA 的初始化。</para></summary>
    AgentStarted,
    /// <summary>An interrupted or invalid exchange requires a new transport session.
    /// <para>被中断或无效的交互需要新的传输会话。</para></summary>
    Faulted,
    /// <summary>The session has released its owned transport.
    /// <para>会话已释放其所持有的传输。</para></summary>
    Disposed,
    /// <summary>XFlash DA1 setup completed; DRAM, DA2 and storage are not yet initialized.
    /// <para>XFlash DA1 设置已完成；DRAM、DA2 与存储尚未初始化。</para></summary>
    XFlashStageOneReady,
    /// <summary>DA2 upload and execution were acknowledged; storage discovery is still required.
    /// <para>DA2 上传与执行已获确认；仍需进行存储发现。</para></summary>
    XFlashStageTwoStarted,
    /// <summary>Selected block-storage geometry and bounded packet sizes were queried successfully.
    /// <para>已成功查询所选块存储几何与有界数据包大小。</para></summary>
    XFlashStorageReady,
    /// <summary>The XFlash agent acknowledged shutdown; actual power/boot transition is unverified.
    /// <para>XFlash 代理已确认关闭；实际的断电/引导转换未经证实。</para></summary>
    XFlashShutdownAcknowledged,
    /// <summary>The XML DA START was acknowledged; hardware, DRAM, DA2 and storage remain uninitialized.
    /// <para>XML DA START 已获确认；硬件、DRAM、DA2 与存储仍未初始化。</para></summary>
    XmlControlReady,
    /// <summary>XML runtime parameters were acknowledged; hardware and storage readiness are not implied.
    /// <para>XML 运行时参数已获确认；并不表示硬件与存储已就绪。</para></summary>
    XmlRuntimeConfigured,
    /// <summary>The XML agent acknowledged hardware initialization; DA2 and storage remain uninitialized.
    /// <para>XML 代理已确认硬件初始化；DA2 与存储仍未初始化。</para></summary>
    XmlHardwareInitialized,
    /// <summary>XML DA2 upload and END/START were acknowledged; DA2 setup and storage discovery remain required.
    /// <para>XML DA2 上传及 END/START 已获确认；仍需 DA2 设置与存储发现。</para></summary>
    XmlStageTwoStarted,
    /// <summary>DA2 acknowledged hardware initialization; authentication and storage discovery remain separate.
    /// <para>DA2 已确认硬件初始化；认证与存储发现仍需单独进行。</para></summary>
    XmlStageTwoHardwareInitialized,
    /// <summary>Bounded XML block-storage geometry was received; access authorization is not implied.
    /// <para>已接收到有界的 XML 块存储几何；并不表示已获访问授权。</para></summary>
    XmlStorageGeometryKnown,
    /// <summary>The XML agent acknowledged reboot; the actual boot transition remains unverified.
    /// <para>XML 代理已确认重启；实际启动转换仍未经验证。</para></summary>
    XmlRebootAcknowledged,
    /// <summary>The non-IoT Legacy DA1 inventory matched the selected profile.
    /// <para>非 IoT Legacy DA1 清单与所选配置匹配。</para></summary>
    LegacyStageOneReady,
    /// <summary>The Legacy runtime and any requested DRAM initialization completed.
    /// <para>Legacy 运行时和任何请求的 DRAM 初始化已完成。</para></summary>
    LegacyRuntimeReady,
    /// <summary>The Legacy DA2 completed startup and reported validated eMMC geometry.
    /// <para>Legacy DA2 已完成启动并上报经过校验的 eMMC 几何信息。</para></summary>
    LegacyStorageReady,
    /// <summary>The Legacy agent acknowledged shutdown; actual boot or power state is unverified.
    /// <para>Legacy 代理已确认关闭；实际启动或电源状态未经验证。</para></summary>
    LegacyShutdownAcknowledged
}

/// <summary>The boot stage reported by the XFlash download agent.
/// <para>由 XFlash 下载代理报告的引导阶段。</para></summary>
public enum MtkXFlashConnectionAgent
{
    /// <summary>The agent was entered from Boot ROM; DRAM configuration may be required.
    /// <para>该代理是从 Boot ROM 进入的；可能需要 DRAM 配置。</para></summary>
    BootRom,
    /// <summary>The agent was entered from a preloader; this alone does not prove storage readiness.
    /// <para>该代理是从 preloader 进入的；仅此并不能证明存储已就绪。</para></summary>
    Preloader
}

/// <summary>Explicit target boot request made when ending an XFlash DA session.
/// <para>结束 XFlash DA 会话时发出的显式目标引导请求。</para></summary>
public enum MtkXFlashShutdownMode
{
    /// <summary>Request the agent's normal shutdown behavior.
    /// <para>请求该代理的正常关闭行为。</para></summary>
    PowerOff = 0,
    /// <summary>Request normal system startup; successful boot is not implied by an ACK.
    /// <para>请求正常系统启动；ACK 并不表示引导成功。</para></summary>
    NormalBoot = 1,
    /// <summary>Request Fastboot; availability and re-enumeration depend on the target.
    /// <para>请求 Fastboot；可用性与重新枚举取决于目标。</para></summary>
    Fastboot = 2
}