namespace UotanStudio.Abstractions.Devices;

/// <summary>
/// <para>设备通讯协议类型。</para>
/// Device communication protocol type.
/// </summary>
public enum DeviceProtocol
{
    /// <summary><para>ADB（Android Debug Bridge，已启动系统）。</para>ADB (Android Debug Bridge, booted system).</summary>
    Adb,

    /// <summary><para>Fastboot（bootloader 模式）。</para>Fastboot (bootloader mode).</summary>
    Fastboot,

    /// <summary>
    /// <para>Fastbootd（userspace fastboot，recovery 内的 fastboot 守护进程）。</para>
    /// Fastbootd (userspace fastboot, the fastboot daemon inside recovery).
    /// </summary>
    Fastbootd,

    /// <summary><para>Recovery 模式。</para>Recovery mode.</summary>
    Recovery,

    /// <summary><para>Qualcomm EDL（Emergency Download Mode）。</para>Qualcomm EDL (Emergency Download Mode).</summary>
    EdlQcom,

    /// <summary><para>MediaTek COM（Brom / Preloader 串口下载）。</para>MediaTek COM (Brom / Preloader serial download).</summary>
    MtkCom,

    /// <summary><para>Rockchip BootROM（rkdeveloptool / upgrade_tool 协议）。</para>Rockchip BootROM (rkdeveloptool / upgrade_tool protocol).</summary>
    RkBootrom,

    /// <summary><para>未知协议。</para>Unknown protocol.</summary>
    Unknown
}

/// <summary>
/// <para>设备连接状态。</para>
/// Device connection state.
/// </summary>
public enum DeviceConnectionState
{
    /// <summary><para>未连接。</para>Disconnected.</summary>
    Disconnected,

    /// <summary><para>已连接，待识别。</para>Connected, identifying.</summary>
    Connected,

    /// <summary><para>就绪，可执行命令。</para>Ready, commands accepted.</summary>
    Ready,

    /// <summary><para>繁忙（正在执行长操作）。</para>Busy.</summary>
    Busy,

    /// <summary><para>错误状态。</para>Error.</summary>
    Error
}
