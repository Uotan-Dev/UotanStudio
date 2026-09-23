namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Hardware identity and security flags reported by the MTK boot protocol.</summary>
/// <param name="HardwareCode">The hardware code returned by command FD.</param>
/// <param name="HardwareSubCode">The hardware subcode returned by command FC.</param>
/// <param name="HardwareVersion">The hardware revision returned by command FC.</param>
/// <param name="SoftwareVersion">The software revision returned by command FC.</param>
/// <param name="TargetConfiguration">The unmodified target security bitfield.</param>
public sealed record MtkDeviceInfo(
    ushort HardwareCode,
    ushort HardwareSubCode,
    ushort HardwareVersion,
    ushort SoftwareVersion,
    uint TargetConfiguration)
{
    /// <summary>Gets whether secure boot is enabled.</summary>
    public bool SecureBootEnabled => (TargetConfiguration & 1) != 0;

    /// <summary>Gets whether serial link authentication is required.</summary>
    public bool SerialLinkAuthenticationRequired => (TargetConfiguration & 2) != 0;

    /// <summary>Gets whether download agent authentication is required.</summary>
    public bool DownloadAgentAuthenticationRequired => (TargetConfiguration & 4) != 0;

    /// <summary>Gets whether the boot protocol reports that a root certificate is required.</summary>
    public bool RootCertificateRequired => (TargetConfiguration & 0x10) != 0;
}

/// <summary>Boot protocol stages, distinct from download-agent storage readiness.</summary>
public enum MtkSessionState
{
    /// <summary>No boot handshake has been completed.</summary>
    Created,
    /// <summary>The boot handshake and identity queries completed.</summary>
    Bootloader,
    /// <summary>A download agent was uploaded and its checksum verified.</summary>
    AgentUploaded,
    /// <summary>The bootloader acknowledged the jump; DA-specific initialization is still required.</summary>
    AgentStarted,
    /// <summary>An interrupted or invalid exchange requires a new transport session.</summary>
    Faulted,
    /// <summary>The session has released its owned transport.</summary>
    Disposed,
    /// <summary>XFlash DA1 setup completed; DRAM, DA2 and storage are not yet initialized.</summary>
    XFlashStageOneReady,
    /// <summary>DA2 upload and execution were acknowledged; storage discovery is still required.</summary>
    XFlashStageTwoStarted,
    /// <summary>Selected block-storage geometry and bounded packet sizes were queried successfully.</summary>
    XFlashStorageReady,
    /// <summary>The XFlash agent acknowledged shutdown; actual power/boot transition is unverified.</summary>
    XFlashShutdownAcknowledged,
    /// <summary>The XML DA START was acknowledged; hardware, DRAM, DA2 and storage remain uninitialized.</summary>
    XmlControlReady,
    /// <summary>XML runtime parameters were acknowledged; hardware and storage readiness are not implied.</summary>
    XmlRuntimeConfigured,
    /// <summary>The XML agent acknowledged hardware initialization; DA2 and storage remain uninitialized.</summary>
    XmlHardwareInitialized,
    /// <summary>XML DA2 upload and END/START were acknowledged; DA2 setup and storage discovery remain required.</summary>
    XmlStageTwoStarted,
    /// <summary>DA2 acknowledged hardware initialization; authentication and storage discovery remain separate.</summary>
    XmlStageTwoHardwareInitialized,
    /// <summary>Bounded XML block-storage geometry was received; access authorization is not implied.</summary>
    XmlStorageGeometryKnown
}

/// <summary>The boot stage reported by the XFlash download agent.</summary>
public enum MtkXFlashConnectionAgent
{
    /// <summary>The agent was entered from Boot ROM; DRAM configuration may be required.</summary>
    BootRom,
    /// <summary>The agent was entered from a preloader; this alone does not prove storage readiness.</summary>
    Preloader
}

/// <summary>Explicit target boot request made when ending an XFlash DA session.</summary>
public enum MtkXFlashShutdownMode
{
    /// <summary>Request the agent's normal shutdown behavior.</summary>
    PowerOff = 0,
    /// <summary>Request normal system startup; successful boot is not implied by an ACK.</summary>
    NormalBoot = 1,
    /// <summary>Request Fastboot; availability and re-enumeration depend on the target.</summary>
    Fastboot = 2
}