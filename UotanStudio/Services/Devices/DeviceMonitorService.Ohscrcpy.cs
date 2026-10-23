using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Mirroring;
using UotanStudio.Services.Scrcpy;

namespace UotanStudio.Services.Devices;

/// <summary>How the host starts an HDC ohscrcpy mirror session. <para>宿主启动 HDC ohscrcpy 投屏会话的方式。</para></summary>
/// <param name="LaunchService">Whether to start the installed <c>ScrcpyService</c> before connecting.
/// <para>连接前是否启动已安装的 <c>ScrcpyService</c>。</para></param>
/// <param name="LocalForwardPort">Loopback port for the HDC forward; zero picks a free port.
/// <para>HDC 转发的本机回环端口；为零时自动选择空闲端口。</para></param>
/// <param name="ConnectTimeoutMs">Budget for the service start, the forward and the tunnel connect.
/// <para>服务启动、转发建立与通道连接的总预算。</para></param>
public sealed record OhscrcpyLaunchOptions(
    bool LaunchService = true,
    int LocalForwardPort = 0,
    int ConnectTimeoutMs = 20_000);

public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// Launches and connects an ohscrcpy mirror session on an HDC target. The device package must already be
    /// installed; use <see cref="IsOhscrcpyInstalled"/> to check and <see cref="InstallOhscrcpyHap"/> to install it.
    /// <para>在 HDC 目标上启动并连接 ohscrcpy 投屏会话。设备包必须已安装；可用
    /// <see cref="IsOhscrcpyInstalled"/> 检查，用 <see cref="InstallOhscrcpyHap"/> 安装。</para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session borrows the HDC lease, runs <c>aa start -b com.ohos.scrcpy.server</c> over the HDC session,
    /// registers a loopback <c>fport</c> mapping to port 53535 and returns a
    /// <see cref="ManagedOhscrcpySession"/> whose frames reuse <see cref="ScrcpyVideoFrame"/>. Call
    /// <see cref="ManagedOhscrcpySession.Connect"/> after assigning it to the mirror view model, mirroring the
    /// scrcpy start flow.
    /// </para>
    /// <para>
    /// 会话借用 HDC 租约，经 HDC 会话执行 <c>aa start -b com.ohos.scrcpy.server</c>，登记到 53535 端口的回环
    /// <c>fport</c> 映射，并返回帧类型复用 <see cref="ScrcpyVideoFrame"/> 的
    /// <see cref="ManagedOhscrcpySession"/>。把它交给投屏视图模型后再调用
    /// <see cref="ManagedOhscrcpySession.Connect"/>，与 scrcpy 启动流程一致。
    /// </para>
    /// </remarks>
    /// <param name="device">The HDC target to mirror. <para>要投屏的 HDC 目标。</para></param>
    /// <param name="options">Optional launch overrides. <para>可选的启动覆盖项。</para></param>
    /// <returns>The unconnected session; the caller connects it after wiring the decode loop.
    /// <para>尚未连接的会话；调用方在接入解码循环后再连接。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.
    /// <para><paramref name="device"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="device"/> is not an HDC target.
    /// <para><paramref name="device"/> 不是 HDC 目标。</para></exception>
    /// <exception cref="InvalidOperationException">The ohscrcpy package is not installed on the target.
    /// <para>目标上未安装 ohscrcpy 包。</para></exception>
    /// <exception cref="IOException">The HDC lease, the service start or the port forward failed.
    /// <para>HDC 租约、服务启动或端口转发失败。</para></exception>
    public ManagedOhscrcpySession StartOhscrcpyMirror(DetectedDevice device, OhscrcpyLaunchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Protocol != DeviceProtocolKind.Hdc)
        {
            throw new ArgumentException("An HDC target is required. 需要 HDC 目标。", nameof(device));
        }

        options ??= new OhscrcpyLaunchOptions();
        var pair = OpenHdcLease(device)
            ?? throw new InvalidOperationException("The selected device is not an HDC target.");
        var (lease, session) = pair;
        try
        {
            return MirrorSessionFacade.StartOhscrcpyOverHdcSession(session, device.Serial,
                options.LaunchService, options.LocalForwardPort, options.ConnectTimeoutMs, lease,
                text => LogService.Instance.Write(LogLevel.Info, "ScreenMirror", text));
        }
        catch
        {
            session.Dispose();
            CloseLease(lease);
            throw;
        }
    }

    /// <summary>Checks whether the ohscrcpy device package is installed on an HDC target.
    /// <para>检查 HDC 目标上是否已安装 ohscrcpy 设备包。</para>
    /// </summary>
    /// <param name="device">The HDC target to inspect. <para>要检查的 HDC 目标。</para></param>
    /// <returns><see langword="true"/> when the bundle manager reports the package.
    /// <para>包管理器报告该包时为 <see langword="true"/>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="device"/> is not an HDC target.
    /// <para><paramref name="device"/> 不是 HDC 目标。</para></exception>
    /// <exception cref="IOException">The HDC lease could not be opened.
    /// <para>无法打开 HDC 租约。</para></exception>
    public bool IsOhscrcpyInstalled(DetectedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Protocol != DeviceProtocolKind.Hdc)
        {
            throw new ArgumentException("An HDC target is required. 需要 HDC 目标。", nameof(device));
        }

        var pair = OpenHdcLease(device)
            ?? throw new InvalidOperationException("The selected device is not an HDC target.");
        var (lease, session) = pair;
        try
        {
            return MirrorSessionFacade.IsOhscrcpyInstalled(session);
        }
        finally
        {
            session.Dispose();
            CloseLease(lease);
        }
    }

    /// <summary>
    /// Installs the provisioned ohscrcpy HAP on an HDC target. Only the ohscrcpy bundle is touched; call
    /// <see cref="HdcDeviceServices"/> uninstall for removal after a verification run.
    /// <para>在 HDC 目标上安装已供给的 ohscrcpy HAP。只触碰 ohscrcpy 包；验证结束后用
    /// <see cref="HdcDeviceServices"/> 的卸载路径移除。</para>
    /// </summary>
    /// <param name="device">The HDC target to install on. <para>要安装到的 HDC 目标。</para></param>
    /// <param name="hapPath">Absolute path of the signed HAP. <para>已签名 HAP 的绝对路径。</para></param>
    /// <returns>The bundle-manager completion text. <para>包管理器的完成文本。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="device"/> is not an HDC target.
    /// <para><paramref name="device"/> 不是 HDC 目标。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="hapPath"/> does not exist.
    /// <para><paramref name="hapPath"/> 不存在。</para></exception>
    /// <exception cref="NotSupportedException">The target is served through the HDC server, whose route does not
    /// expose the package-transfer channel; install it once with <c>hdc -t &lt;serial&gt; install -r &lt;hap&gt;</c>.
    /// <para>目标经 HDC server 服务，其路由不暴露包传输通道；请先用
    /// <c>hdc -t &lt;serial&gt; install -r &lt;hap&gt;</c> 安装一次。</para></exception>
    /// <exception cref="IOException">The install failed. <para>安装失败。</para></exception>
    public string InstallOhscrcpyHap(DetectedDevice device, string hapPath)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(hapPath);
        if (device.Protocol != DeviceProtocolKind.Hdc)
        {
            throw new ArgumentException("An HDC target is required. 需要 HDC 目标。", nameof(device));
        }

        if (!File.Exists(hapPath)) throw new FileNotFoundException("The ohscrcpy HAP does not exist.", hapPath);
        var pair = OpenHdcLease(device)
            ?? throw new InvalidOperationException("The selected device is not an HDC target.");
        var (lease, session) = pair;
        try
        {
            return MirrorSessionFacade.InstallOhscrcpyHap(session, hapPath);
        }
        catch (NotSupportedException error)
        {
            throw new NotSupportedException(
                $"The HDC server route cannot transfer {OhscrcpyServerService.HapFileName}; run "
                + $"'hdc -t {device.Serial} install -r \"{hapPath}\"' once, then retry the mirror.",
                error);
        }
        finally
        {
            session.Dispose();
            CloseLease(lease);
        }
    }
}