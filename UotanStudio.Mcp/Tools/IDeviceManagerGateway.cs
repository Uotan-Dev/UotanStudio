using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Mcp.Tools;

/// <summary>Device enumeration and transaction ownership for MCP hosts.
/// <para>MCP 宿主的设备枚举与事务所有权。</para>
/// A gateway is the only device accessor for MCP tools: it refreshes discovery state, opens the
/// session for one transaction, and closes it before returning, so tools never hold device handles.
/// <para>网关是 MCP 工具唯一的设备访问者：它刷新发现状态、为单次事务打开会话，并在返回前关闭会话，
/// 因此工具不会持有设备句柄。</para>
/// </summary>
public interface IDeviceManagerGateway
{
    /// <summary>
    /// Enumerates the USB devices this host may drive directly.
    /// <para>枚举本宿主可直接驱动的 USB 设备。</para>
    /// Endpoints already owned by an ADB server, and HDC endpoints while an HDC server is reachable,
    /// are left out because the server holds their identity.
    /// <para>已由 ADB server 持有的端点，以及 HDC server 可达期间的 HDC 端点会被排除，因为其身份由 server 管理。</para>
    /// </summary>
    /// <returns>Inventory details for each directly routable USB endpoint; empty when none is attached.
    /// <para>每个可直接路由 USB 端点的清单信息；没有设备时为空。</para></returns>
    /// <exception cref="InvalidDataException">A routable endpoint carries no USB inventory details.
    /// <para>可路由端点缺少 USB 清单信息。</para></exception>
    IReadOnlyList<UsbDeviceInfo> ListUsbDevices();

    /// <summary>
    /// Enumerates the ADB targets known to the host, both server-managed and directly routable ones.
    /// <para>枚举宿主已知的 ADB 目标，包含 server 管理与可直接路由的目标。</para>
    /// </summary>
    /// <returns>Pairs of target identity and state. The identity is the exact value to pass as
    /// <c>serial</c>, and the state is the server report or a live probe result such as
    /// <c>device</c> or <c>offline</c>.
    /// <para>目标标识与状态组成的键值对。标识即应作为 <c>serial</c> 传入的确切值，状态为 server 报告或实测结果（如 <c>device</c>、<c>offline</c>）。</para></returns>
    IReadOnlyList<(string Serial, string State)> ListAdbTargets();

    /// <summary>
    /// Runs one ADB service against the identified target and hands its stream to a callback.
    /// <para>对指定目标执行一次 ADB 服务，并把服务流交给回调。</para>
    /// The session is opened for this call only: the stream stays valid for the duration of
    /// <paramref name="operation"/> and must not be retained or returned.
    /// <para>会话仅服务于本次调用：流在 <paramref name="operation"/> 执行期间有效，不得保留或返回。</para>
    /// </summary>
    /// <typeparam name="TResult">Type of the value produced from the service stream.
    /// <para>由服务流产出的值类型。</para></typeparam>
    /// <param name="serial">Exact target identity as reported by <see cref="ListAdbTargets"/>; matching is case-sensitive.
    /// <para>由 <see cref="ListAdbTargets"/> 报出的确切目标标识；匹配区分大小写。</para></param>
    /// <param name="service">Raw ADB service string, for example <c>shell:getprop</c>, <c>sync:</c> or <c>reboot:bootloader</c>.
    /// <para>原始 ADB 服务字符串，例如 <c>shell:getprop</c>、<c>sync:</c> 或 <c>reboot:bootloader</c>。</para></param>
    /// <param name="operation">Callback that reads or writes the service stream and produces the result.
    /// <para>读写服务流并产出结果的回调。</para></param>
    /// <param name="cancellationToken">Token checked before the endpoint is leased, before the service
    /// stream is opened and by the caller's callback between packets; the ADB protocol stream itself is
    /// synchronous and cannot interrupt a single blocking read.
    /// <para>在租用端点前、打开服务流前以及调用方回调的每个数据包之间检查该令牌；ADB 协议流本身是同步的，
    /// 无法中断单次阻塞读取。</para></param>
    /// <returns>The value returned by <paramref name="operation"/>.
    /// <para><paramref name="operation"/> 返回的值。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="serial"/> is empty or only whitespace.
    /// <para><paramref name="serial"/> 为空或仅包含空白。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.
    /// <para><paramref name="operation"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="IOException">No unique target matches the identity, or the endpoint could not be
    /// leased or authenticated.
    /// <para>没有唯一目标与该标识匹配，或端点无法租用/无法通过认证。</para></exception>
    /// <exception cref="InvalidOperationException">The selected endpoint offers no supported ADB session or
    /// the connection did not reach the connected state.
    /// <para>所选端点没有受支持的 ADB 会话，或连接未进入已连接状态。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was requested.
    /// <para><paramref name="cancellationToken"/> 已请求取消。</para></exception>
    TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads an existing local file to the target through the ADB sync service.
    /// <para>通过 ADB sync 服务把已存在的本地文件上传到目标设备。</para>
    /// </summary>
    /// <param name="serial">Exact target identity as reported by <see cref="ListAdbTargets"/>.
    /// <para>由 <see cref="ListAdbTargets"/> 报出的确切目标标识。</para></param>
    /// <param name="localPath">Absolute path of the file to upload, already confined by the caller's <see cref="PathSandbox"/>.
    /// <para>待上传文件的绝对路径，已由调用方的 <see cref="PathSandbox"/> 限制。</para></param>
    /// <param name="remotePath">Device-side destination path. <para>设备侧目标路径。</para></param>
    /// <param name="cancellationToken">Token checked before the endpoint is leased and before the sync
    /// client is created; <c>push</c> itself is a synchronous protocol call without its own token.
    /// <para>在租用端点前与创建 sync 客户端前检查该令牌；<c>push</c> 本身是没有令牌的同步协议调用。</para></param>
    /// <exception cref="ArgumentException"><paramref name="serial"/> is empty or only whitespace.
    /// <para><paramref name="serial"/> 为空或仅包含空白。</para></exception>
    /// <exception cref="IOException">No unique target matches the identity, the endpoint could not be
    /// leased or authenticated, or the device rejected the upload.
    /// <para>没有唯一目标与该标识匹配、端点无法租用/无法通过认证，或设备拒绝该上传。</para></exception>
    /// <exception cref="InvalidOperationException">The selected endpoint offers no supported ADB session or
    /// the connection did not reach the connected state.
    /// <para>所选端点没有受支持的 ADB 会话，或连接未进入已连接状态。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was requested.
    /// <para><paramref name="cancellationToken"/> 已请求取消。</para></exception>
    void PushAdbFile(string serial, string localPath, string remotePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads one device file through the ADB sync client into <paramref name="localPath"/>.
    /// <para>经 ADB sync 客户端把设备文件下载到 <paramref name="localPath"/>。</para>
    /// The gateway owns endpoint leasing; the caller supplies an already sandbox-resolved destination.
    /// <para>网关负责端点租用；调用方给出已按沙箱解析的目的地。</para>
    /// </summary>
    /// <param name="serial">Device serial or device key from the inventory. <para>设备序列号或清单中的设备标识。</para></param>
    /// <param name="remotePath">Absolute on-device path to fetch. <para>要获取的设备端绝对路径。</para></param>
    /// <param name="localPath">Destination file path the gateway creates or overwrites.
    /// <para>网关创建或覆盖的目标文件路径。</para></param>
    /// <param name="cancellationToken">Checked before leasing the endpoint and before creating the sync
    /// client; the transfer itself is a synchronous protocol call without its own token.
    /// <para>在租用端点前与创建 sync 客户端前检查该令牌；传输本身是没有令牌的同步协议调用。</para></param>
    /// <exception cref="ArgumentException"><paramref name="serial"/> or <paramref name="remotePath"/> is empty
    /// or only whitespace. <para><paramref name="serial"/> 或 <paramref name="remotePath"/> 为空或仅空白。</para></exception>
    /// <exception cref="IOException">No unique target matches the identity, the endpoint could not be leased,
    /// or the device rejected the download. <para>没有唯一目标与该标识匹配、端点无法租用，或设备拒绝该下载。</para></exception>
    /// <exception cref="InvalidOperationException">The selected endpoint offers no supported ADB session.
    /// <para>所选端点没有受支持的 ADB 会话。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was requested.
    /// <para><paramref name="cancellationToken"/> 已请求取消。</para></exception>
    void PullAdbFile(string serial, string remotePath, string localPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one transaction against a Fastboot device.
    /// <para>对一个 Fastboot 设备执行一次事务。</para>
    /// The session is borrowed for the duration of <paramref name="operation"/> only and is closed
    /// afterwards, so the callback must not retain or return it.
    /// <para>会话仅在 <paramref name="operation"/> 执行期间借用，随后关闭，因此回调不得保留或返回该会话。</para>
    /// </summary>
    /// <typeparam name="TResult">Type of the value produced by the operation.
    /// <para>由操作产出的值类型。</para></typeparam>
    /// <param name="target">Fastboot serial or USB device key selecting the endpoint, as reported by the
    /// device inventory. When <see langword="null"/> or blank the call requires exactly one directly
    /// routable Fastboot endpoint and uses it.
    /// <para>用于选择端点的 Fastboot serial 或 USB 设备键（取自设备清单）。为
    /// <see langword="null"/> 或空白时要求恰好存在一个可直接路由的 Fastboot 端点并使用它。</para></param>
    /// <param name="operation">Callback that uses the borrowed Fastboot session and produces the result.
    /// <para>使用借出的 Fastboot 会话并产出结果的回调。</para></param>
    /// <param name="cancellationToken">Token checked before discovery is refreshed, before the endpoint
    /// is leased and again before the callback runs.
    /// <para>在刷新发现结果前、租用端点前以及调用回调前都会检查该令牌。</para></param>
    /// <returns>The value returned by <paramref name="operation"/>.
    /// <para><paramref name="operation"/> 返回的值。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.
    /// <para><paramref name="operation"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException"><paramref name="target"/> is absent and the directly
    /// routable Fastboot endpoints are not exactly one, or the leased endpoint offers no Fastboot session.
    /// <para><paramref name="target"/> 为空且可直接路由的 Fastboot 端点数量不恰好为一，或所租用的端点未提供
    /// Fastboot 会话。</para></exception>
    /// <exception cref="IOException">No Fastboot endpoint matches <paramref name="target"/>, several do,
    /// the leased endpoint offers no Fastboot session, or the session could not be opened.
    /// <para>没有 Fastboot 端点匹配 <paramref name="target"/>、匹配者不止一个、所租用的端点未提供 Fastboot
    /// 会话，或无法打开会话。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was requested.
    /// <para><paramref name="cancellationToken"/> 已请求取消。</para></exception>
    TResult RunFastboot<TResult>(string? target, Func<FastbootSession, TResult> operation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the one-time credentials a wireless-debugging QR pairing code carries. No device is
    /// touched, so a headless caller renders the returned payload text into a QR code itself and the
    /// phone scans it before <see cref="PairQrWirelessAsync"/> is invoked.
    /// <para>创建无线调试二维码配对所携带的一次性凭据。此过程不触碰设备：无头调用方自行把返回的负载
    /// 文本渲染成二维码，手机扫描后再调用 <see cref="PairQrWirelessAsync"/>。</para>
    /// </summary>
    /// <returns>The mDNS instance name, the pairing password and the QR payload text.
    /// <para>mDNS 实例名、配对密码与二维码负载文本。</para></returns>
    (string ServiceName, string Password, string Payload) CreateQrPairingSession();

    /// <summary>
    /// Waits for the phone to scan the QR code built from these credentials, completes pairing and
    /// connects the device. With a local adb server listening, pairing and connecting both go through
    /// the server, so the target appears in <see cref="ListAdbTargets"/> afterwards; without one the
    /// gateway pairs directly over TLS and only reports the address, which the headless ADB tools do
    /// not address themselves.
    /// <para>等待手机扫描由这些凭据生成的二维码、完成配对并连接设备。本机 adb server 在监听时，配对与
    /// 连接都经 server 完成，之后目标会出现在 <see cref="ListAdbTargets"/> 中；没有 server 时由网关直接
    /// 走 TLS 配对，只报告地址，而无头 ADB 工具本身不会寻址该地址。</para>
    /// </summary>
    /// <param name="serviceName">The instance name returned by <see cref="CreateQrPairingSession"/>.
    /// <para><see cref="CreateQrPairingSession"/> 返回的实例名。</para></param>
    /// <param name="password">The pairing password returned with it.
    /// <para>同时返回的配对密码。</para></param>
    /// <param name="timeoutSeconds">Deadline for the phone to appear and pair, in seconds.
    /// <para>手机出现并完成配对的期限，单位为秒。</para></param>
    /// <param name="cancellationToken">Cancels discovery, pairing and connecting.
    /// <para>取消发现、配对与连接。</para></param>
    /// <returns>The route the pairing completed through and the connected <c>host:port</c> address.
    /// <para>配对完成的路径与已连接的 <c>host:port</c> 地址。</para></returns>
    /// <exception cref="ArgumentException">The credentials are blank or <paramref name="timeoutSeconds"/>
    /// is out of range. <para>凭据为空或 <paramref name="timeoutSeconds"/> 超出范围。</para></exception>
    /// <exception cref="TimeoutException">The phone did not advertise the pairing or connect service in
    /// time. <para>手机未在期限内广播配对或连接服务。</para></exception>
    /// <exception cref="IOException">The pairing protocol or the server command failed.
    /// <para>配对协议或 server 命令失败。</para></exception>
    /// <exception cref="OperationCanceledException">The token was requested.
    /// <para><paramref name="cancellationToken"/> 已请求取消。</para></exception>
    Task<(string Route, string Address)> PairQrWirelessAsync(string serviceName, string password,
        int timeoutSeconds = 180, CancellationToken cancellationToken = default);
}