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
    /// <returns>The value returned by <paramref name="operation"/>.
    /// <para><paramref name="operation"/> 返回的值。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="serial"/> is empty or only whitespace.
    /// <para><paramref name="serial"/> 为空或仅包含空白。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.
    /// <para><paramref name="operation"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="IOException">No unique target matches the identity, or ADB authentication did not complete.
    /// <para>没有唯一目标与该标识匹配，或 ADB 授权未完成。</para></exception>
    /// <exception cref="InvalidOperationException">The selected endpoint offers no supported ADB session.
    /// <para>所选端点没有受支持的 ADB 会话。</para></exception>
    TResult RunAdbService<TResult>(string serial, string service, Func<Stream, TResult> operation);

    /// <summary>
    /// Uploads an existing local file to the target through the ADB sync service.
    /// <para>通过 ADB sync 服务把已存在的本地文件上传到目标设备。</para>
    /// </summary>
    /// <param name="serial">Exact target identity as reported by <see cref="ListAdbTargets"/>.
    /// <para>由 <see cref="ListAdbTargets"/> 报出的确切目标标识。</para></param>
    /// <param name="localPath">Absolute path of the file to upload, already confined by the caller's <see cref="PathSandbox"/>.
    /// <para>待上传文件的绝对路径，已由调用方的 <see cref="PathSandbox"/> 限制。</para></param>
    /// <param name="remotePath">Device-side destination path. <para>设备侧目标路径。</para></param>
    /// <exception cref="ArgumentException"><paramref name="serial"/> is empty or only whitespace.
    /// <para><paramref name="serial"/> 为空或仅包含空白。</para></exception>
    /// <exception cref="IOException">No unique target matches the identity, or the device rejected the upload.
    /// <para>没有唯一目标与该标识匹配，或设备拒绝该上传。</para></exception>
    /// <exception cref="InvalidOperationException">The selected endpoint offers no supported ADB session.
    /// <para>所选端点没有受支持的 ADB 会话。</para></exception>
    void PushAdbFile(string serial, string localPath, string remotePath);

    /// <summary>
    /// Runs one transaction against the single connected Fastboot device.
    /// <para>对唯一接入的 Fastboot 设备执行一次事务。</para>
    /// The session is borrowed for the duration of <paramref name="operation"/> only and is closed
    /// afterwards, so the callback must not retain or return it.
    /// <para>会话仅在 <paramref name="operation"/> 执行期间借用，随后关闭，因此回调不得保留或返回该会话。</para>
    /// </summary>
    /// <typeparam name="TResult">Type of the value produced by the operation.
    /// <para>由操作产出的值类型。</para></typeparam>
    /// <param name="operation">Callback that uses the borrowed Fastboot session and produces the result.
    /// <para>使用借出的 Fastboot 会话并产出结果的回调。</para></param>
    /// <returns>The value returned by <paramref name="operation"/>.
    /// <para><paramref name="operation"/> 返回的值。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.
    /// <para><paramref name="operation"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">Fastboot devices are not exactly one, or the selected device does not expose Fastboot.
    /// <para>Fastboot 设备数量不恰好为一，或所选设备未暴露 Fastboot。</para></exception>
    /// <exception cref="IOException">The Fastboot session could not be opened.
    /// <para>无法打开 Fastboot 会话。</para></exception>
    TResult RunFastboot<TResult>(Func<FastbootSession, TResult> operation);
}