namespace UotanStudio.Comm.Protocols.Fastboot;

/// <summary>
/// Fastboot wire protocol constants (AOSP fastboot.h).
/// <para>Fastboot 线上协议常量（AOSP fastboot.h）。</para>
/// </summary>
public static class FastbootConstants
{
    /// <summary>
    /// Maximum fastboot command length (AOSP FB_COMMAND_SZ = 4096).
    /// <para>Fastboot 命令最大长度（AOSP FB_COMMAND_SZ = 4096）。</para>
    /// </summary>
    public const int MaxCommandLength = 4096;

    /// <summary>
    /// Response buffer size (AOSP FB_RESPONSE_SZ = 256).
    /// <para>响应缓冲区大小（AOSP FB_RESPONSE_SZ = 256）。</para>
    /// </summary>
    public const int ResponseBufferSize = 256;

    /// <summary>
    /// Maximum per-transfer write size.
    /// <para>单次传输最大写入大小。</para>
    /// </summary>
    public const int TransferChunkSize = 512 * 1024;

    /// <summary>
    /// Maximum download size from DATA field (32-bit hex).
    /// <para>DATA 字段最大下载大小（16 进制 8 位）。</para>
    /// </summary>
    public const long MaxDownloadSize = uint.MaxValue;

    /// <summary>
    /// Fallback max download size when device doesn't report one.
    /// <para>设备未报告最大下载大小时的回退值。</para>
    /// </summary>
    public const long FallbackMaxDownloadSize = 256L * 1024 * 1024;

    /// <summary>
    /// Default response timeout in seconds.
    /// <para>默认响应超时（秒）。</para>
    /// </summary>
    public const int DefaultResponseTimeoutSeconds = 30;

    // Response prefix strings (4-char status codes).
    /// <summary>
    /// OKAY response prefix.
    /// <para>OKAY 响应前缀。</para>
    /// </summary>
    public const string Okay = "OKAY";

    /// <summary>
    /// FAIL response prefix.
    /// <para>FAIL 响应前缀。</para>
    /// </summary>
    public const string Fail = "FAIL";

    /// <summary>
    /// INFO response prefix.
    /// <para>INFO 响应前缀。</para>
    /// </summary>
    public const string Info = "INFO";

    /// <summary>
    /// TEXT response prefix.
    /// <para>TEXT 响应前缀。</para>
    /// </summary>
    public const string Text = "TEXT";

    /// <summary>
    /// DATA response prefix.
    /// <para>DATA 响应前缀。</para>
    /// </summary>
    public const string Data = "DATA";

    // USB class/subclass/protocol for fastboot devices.
    /// <summary>
    /// USB interface class for fastboot devices.
    /// <para>Fastboot 设备的 USB 接口类。</para>
    /// </summary>
    public const byte FastbootInterfaceClass = 0xFF;

    /// <summary>
    /// USB interface subclass for fastboot devices.
    /// <para>Fastboot 设备的 USB 接口子类。</para>
    /// </summary>
    public const byte FastbootInterfaceSubclass = 0x42;

    /// <summary>
    /// USB interface protocol for fastboot devices.
    /// <para>Fastboot 设备的 USB 接口协议。</para>
    /// </summary>
    public const byte FastbootInterfaceProtocol = 0x03;

    // Common fastboot commands.
    /// <summary>
    /// getvar command prefix.
    /// <para>getvar 命令前缀。</para>
    /// </summary>
    public const string CommandGetVar = "getvar:";

    /// <summary>
    /// download command prefix.
    /// <para>download 命令前缀。</para>
    /// </summary>
    public const string CommandDownload = "download:";

    /// <summary>
    /// flash command prefix.
    /// <para>flash 命令前缀。</para>
    /// </summary>
    public const string CommandFlash = "flash:";

    /// <summary>
    /// erase command prefix.
    /// <para>erase 命令前缀。</para>
    /// </summary>
    public const string CommandErase = "erase:";

    /// <summary>
    /// format command prefix.
    /// <para>format 命令前缀。</para>
    /// </summary>
    public const string CommandFormat = "format:";

    /// <summary>
    /// reboot command.
    /// <para>reboot 命令。</para>
    /// </summary>
    public const string CommandReboot = "reboot";

    /// <summary>
    /// continue command.
    /// <para>continue 命令。</para>
    /// </summary>
    public const string CommandContinue = "continue";

    /// <summary>
    /// set_active command prefix.
    /// <para>set_active 命令前缀。</para>
    /// </summary>
    public const string CommandSetActive = "set_active:";
}
