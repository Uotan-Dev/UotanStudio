using UotanStudio.Comm.Routing;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm;

/// <summary>
/// Shared USB policy and local debug-server discovery. USB policy never overrides server ownership.
/// <para>共享的 USB 策略与本机调试 server 发现。USB 策略永远不会覆盖 server 的归属。</para>
/// </summary>
public sealed record DeviceCommunicationConfiguration
{
    private static DeviceCommunicationConfiguration _current = new();

    /// <summary>
    /// Process configuration, applied before device discovery starts.
    /// <para>进程级配置，须在设备发现启动前应用。</para>
    /// </summary>
    /// <value>
    /// Each discovery and transport opener reads this instance with volatile semantics; replacing
    /// it keeps captured snapshots.
    /// <para>由各发现与传输开启器以 volatile 语义读取的实例；替换实例不会重写已捕获的快照。</para>
    /// </value>
    /// <exception cref="ArgumentNullException">The assigned value is <see langword="null"/>.
    /// <para>所赋的值为 <see langword="null"/>。</para></exception>
    public static DeviceCommunicationConfiguration Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>
    /// Default USB backend. Auto retains platform defaults; forced modes do not fall back.
    /// <para>默认 USB 后端。Auto 保留平台默认值；强制指定的模式不会回退。</para>
    /// </summary>
    /// <value>
    /// <see cref="UsbApiKind.Auto"/>, <see cref="UsbApiKind.Native"/> or
    /// <see cref="UsbApiKind.LibUsbDotNet"/>; any other value normalizes to <c>Auto</c>.
    /// <para>取值为 <see cref="UsbApiKind.Auto"/>、<see cref="UsbApiKind.Native"/> 或
    /// <see cref="UsbApiKind.LibUsbDotNet"/>；其他值都会归一为 <c>Auto</c>。</para>
    /// </value>
    public UsbApiKind UsbBackend
    {
        get;
        init => field = value is UsbApiKind.Auto or UsbApiKind.Native or UsbApiKind.LibUsbDotNet
            ? value : UsbApiKind.Auto;
    }

    /// <summary>
    /// Local ADB server host; the single definition lives in <see cref="DebugBridgeEndpoints"/>.
    /// <para>本机 ADB server 主机；唯一定义位于 <see cref="DebugBridgeEndpoints"/>。</para>
    /// </summary>
    public const string ServerHost = DebugBridgeEndpoints.ServerHost;

    /// <summary>
    /// Local ADB server port; the single definition lives in <see cref="DebugBridgeEndpoints"/>.
    /// <para>本机 ADB server 端口；唯一定义位于 <see cref="DebugBridgeEndpoints"/>。</para>
    /// </summary>
    public const int AdbServerPort = DebugBridgeEndpoints.AdbServerPort;

    /// <summary>
    /// Checks ADB endpoint ownership; only connection refusal means absent.
    /// <para>检查 ADB 端点的归属；只有连接被拒绝才表示不存在。</para>
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels the probe; an internal 3-second deadline also aborts a stalled connect.
    /// <para>取消本次探测；内部 3 秒截止时间也会中止停滞的连接。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the server accepts the connection, a result cached for a bounded
    /// period; <see langword="false"/> only when the connection is refused.
    /// <para>server 接受连接时返回 <see langword="true"/>，该结果会在有界时间内缓存；仅在连接被拒绝时
    /// 返回 <see langword="false"/>。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled or the 3-second deadline elapsed.
    /// <para><paramref name="cancellationToken"/> 被触发，或 3 秒截止时间已到。</para>
    /// </exception>
    /// <exception cref="System.Net.Sockets.SocketException">
    /// The connect failed with an error other than refusal.
    /// <para>连接因拒绝以外的错误而失败。</para>
    /// </exception>
    public static Task<bool> ProbeAdbServerAsync(CancellationToken cancellationToken = default)
        => DebugBridgeRoute.IsListeningAsync(ServerHost, AdbServerPort, cancellationToken);

    /// <summary>
    /// Checks HDC endpoint ownership using the HDC environment/default port.
    /// <para>使用 HDC 环境变量或默认端口检查 HDC 端点的归属。</para>
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels the probe; an internal 3-second deadline also aborts a stalled connect.
    /// <para>取消本次探测；内部 3 秒截止时间也会中止停滞的连接。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the server accepts the connection, a result cached for a bounded
    /// period; <see langword="false"/> only when the connection is refused.
    /// <para>server 接受连接时返回 <see langword="true"/>，该结果会在有界时间内缓存；仅在连接被拒绝时
    /// 返回 <see langword="false"/>。</para>
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// <c>OHOS_HDC_SERVER_PORT</c> is set but is not a decimal port within [1, 65535].
    /// <para><c>OHOS_HDC_SERVER_PORT</c> 已设置，但既非十进制端口，也不在 [1, 65535] 范围内。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled or the 3-second deadline elapsed.
    /// <para><paramref name="cancellationToken"/> 被触发，或 3 秒截止时间已到。</para>
    /// </exception>
    /// <exception cref="System.Net.Sockets.SocketException">
    /// The connect failed with an error other than refusal.
    /// <para>连接因拒绝以外的错误而失败。</para>
    /// </exception>
    public static Task<bool> ProbeHdcServerAsync(CancellationToken cancellationToken = default)
        => DebugBridgeRoute.IsListeningAsync(ServerHost, DebugBridgeEndpoints.HdcServerPort, cancellationToken);
}