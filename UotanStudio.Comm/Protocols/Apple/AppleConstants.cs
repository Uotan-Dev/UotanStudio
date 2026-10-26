namespace UotanStudio.Comm.Protocols.Apple;

/// <summary>
/// Wire constants shared by the Apple protocol family (usbmuxd, lockdownd, relays and AFC).
/// <para>Apple 协议族（usbmuxd、lockdownd、日志中继与 AFC）共享的线协议常量。</para>
/// </summary>
public static class AppleConstants
{
    /// <summary>Gets the Apple USB vendor ID. <para>获取 Apple 的 USB 厂商 ID。</para></summary>
    public const ushort AppleVendorId = 0x05AC;

    /// <summary>Gets the lockdownd TCP port exposed on the device. <para>获取设备上 lockdownd 监听的 TCP 端口。</para></summary>
    public const int LockdownPort = 62078;

    /// <summary>Gets the Apple USB multiplexer interface class (vendor specific). <para>获取 Apple USB 复用接口的类代码（厂商自定义）。</para></summary>
    public const byte UsbmuxdInterfaceClass = 0xFF;

    /// <summary>Gets the Apple USB multiplexer interface subclass. <para>获取 Apple USB 复用接口的子类代码。</para></summary>
    public const byte UsbmuxdInterfaceSubClass = 0xFE;

    /// <summary>Gets the Apple USB multiplexer interface protocol. <para>获取 Apple USB 复用接口的协议代码。</para></summary>
    public const byte UsbmuxdInterfaceProtocol = 0x02;

    /// <summary>Gets the TCP port the Windows Apple Mobile Device Service exposes usbmuxd on. <para>获取 Windows 上 Apple Mobile Device Service 暴露 usbmuxd 的 TCP 端口。</para></summary>
    public const int UsbmuxdWindowsPort = 27015;

    /// <summary>Gets the environment variable overriding the usbmuxd endpoint (<c>host:port</c> or a Unix socket path). <para>获取覆盖 usbmuxd 端点的环境变量名（<c>host:port</c> 或 Unix 套接字路径）。</para></summary>
    public const string UsbmuxdSocketAddressEnvironmentVariable = "USBMUXD_SOCKET_ADDRESS";

    /// <summary>Gets the default usbmuxd Unix socket path on macOS and Linux. <para>获取 macOS 与 Linux 上默认的 usbmuxd Unix 套接字路径。</para></summary>
    public const string UsbmuxdUnixSocketPath = "/var/run/usbmuxd";

    /// <summary>Gets the binary (version 0) usbmuxd protocol version. <para>获取 usbmuxd 二进制协议（version 0）版本号。</para></summary>
    public const uint UsbmuxdProtocolVersionBinary = 0;

    /// <summary>Gets the plist (version 1) usbmuxd protocol version. <para>获取 usbmuxd plist 协议（version 1）版本号。</para></summary>
    public const uint UsbmuxdProtocolVersionPlist = 1;

    /// <summary>Gets the upper bound for a single usbmuxd message including its payload. <para>获取单条 usbmuxd 消息（含载荷）的大小上限。</para></summary>
    public const uint UsbmuxdMaxMessageBytes = 8 * 1024 * 1024;

    /// <summary>Gets the MESSAGE_RESULT message type. <para>获取 MESSAGE_RESULT 消息类型。</para></summary>
    public const uint UsbmuxdMessageResult = 1;

    /// <summary>Gets the MESSAGE_CONNECT message type. <para>获取 MESSAGE_CONNECT 消息类型。</para></summary>
    public const uint UsbmuxdMessageConnect = 2;

    /// <summary>Gets the MESSAGE_LISTEN message type. <para>获取 MESSAGE_LISTEN 消息类型。</para></summary>
    public const uint UsbmuxdMessageListen = 3;

    /// <summary>Gets the MESSAGE_DEVICE_ADD message type. <para>获取 MESSAGE_DEVICE_ADD 消息类型。</para></summary>
    public const uint UsbmuxdMessageDeviceAdd = 4;

    /// <summary>Gets the MESSAGE_DEVICE_REMOVE message type. <para>获取 MESSAGE_DEVICE_REMOVE 消息类型。</para></summary>
    public const uint UsbmuxdMessageDeviceRemove = 5;

    /// <summary>Gets the MESSAGE_DEVICE_PAIRED message type. <para>获取 MESSAGE_DEVICE_PAIRED 消息类型。</para></summary>
    public const uint UsbmuxdMessageDevicePaired = 6;

    /// <summary>Gets the MESSAGE_PLIST message type. <para>获取 MESSAGE_PLIST 消息类型。</para></summary>
    public const uint UsbmuxdMessagePlist = 8;

    /// <summary>Gets the usbmuxd result code for success. <para>获取 usbmuxd 的成功结果码。</para></summary>
    public const uint UsbmuxdResultOk = 0;

    /// <summary>Gets the usbmuxd result code for a rejected command. <para>获取 usbmuxd 的命令被拒结果码。</para></summary>
    public const uint UsbmuxdResultBadCommand = 1;

    /// <summary>Gets the usbmuxd result code for an unknown device. <para>获取 usbmuxd 的未知设备结果码。</para></summary>
    public const uint UsbmuxdResultBadDevice = 2;

    /// <summary>Gets the usbmuxd result code for a refused connection. <para>获取 usbmuxd 的连接被拒结果码。</para></summary>
    public const uint UsbmuxdResultConnectionRefused = 3;

    /// <summary>Gets the usbmuxd result code for a version mismatch. <para>获取 usbmuxd 的版本不匹配结果码。</para></summary>
    public const uint UsbmuxdResultBadVersion = 6;

    /// <summary>Gets the lockdown media directory file relay service name. <para>获取 lockdown 媒体目录文件中继服务名。</para></summary>
    public const string ServiceAfc = "com.apple.afc";

    /// <summary>Gets the lockdown jail-free file relay service name. <para>获取 lockdown 无越狱文件中继服务名。</para></summary>
    public const string ServiceAfc2 = "com.apple.afc2";

    /// <summary>Gets the legacy syslog relay service name. <para>获取旧版 syslog 中继服务名。</para></summary>
    public const string ServiceSyslogRelay = "com.apple.syslog_relay";

    /// <summary>Gets the os_trace (tracev3) relay service name. <para>获取 os_trace（tracev3）中继服务名。</para></summary>
    public const string ServiceOsTraceRelay = "com.apple.os_trace_relay";

    /// <summary>Gets the crash report copy (AFC) service name. <para>获取崩溃日志拷贝（AFC）服务名。</para></summary>
    public const string ServiceCrashReportCopyMobile = "com.apple.crashreportcopymobile";

    /// <summary>Gets the crash report mover service name. <para>获取崩溃日志搬运服务名。</para></summary>
    public const string ServiceCrashReportMover = "com.apple.crashreportmover";

    /// <summary>Gets the device screenshot service name. <para>获取设备截图服务名。</para></summary>
    public const string ServiceScreenshotr = "com.apple.mobile.screenshotr";

    /// <summary>Gets the iOS 17.4+ CoreDevice proxy lockdown service name. <para>获取 iOS 17.4+ 的 CoreDevice 代理 lockdown 服务名。</para></summary>
    public const string ServiceCoreDeviceProxy = "com.apple.internal.devicecompute.CoreDeviceProxy";

    /// <summary>Gets the RemoteXPC syslog relay shim service name exposed over an established tunnel. <para>获取已建立隧道上暴露的 RemoteXPC syslog 中继 shim 服务名。</para></summary>
    public const string ServiceSyslogRelayShimRemote = "com.apple.syslog_relay.shim.remote";
}