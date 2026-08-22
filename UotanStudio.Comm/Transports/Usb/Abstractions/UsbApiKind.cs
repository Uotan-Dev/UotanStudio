namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Selects the USB backend family.
/// <para>选择 USB 后端类型。</para>
/// </summary>
public enum UsbApiKind
{
    /// <summary>
    /// Automatically selects the available backend(s).
    /// <para>自动选择可用后端。</para>
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Uses the native platform backend.
    /// <para>使用平台原生后端。</para>
    /// </summary>
    Native = 1,

    /// <summary>
    /// Uses the LibUsbDotNet backend.
    /// <para>使用 LibUsbDotNet 后端。</para>
    /// </summary>
    LibUsbDotNet = 2,

    /// <summary>
    /// Represents a custom backend registration.
    /// <para>表示自定义后端注册类型。</para>
    /// </summary>
    Custom = 3,

    /// <summary>
    /// Uses the HarmonyOS USBManager backend via IPC bridge service.
    /// <para>使用基于 IPC 桥接服务的 HarmonyOS USBManager 后端。</para>
    /// </summary>
    HarmonyOS = 4
}
