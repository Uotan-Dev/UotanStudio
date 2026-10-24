using UotanStudio.Mirroring;

namespace UotanStudio.Services.Devices;

public sealed partial class DeviceMonitorService
{
    /// <summary>
    /// Launches an ohscrcpy mirror session on an HDC target. The facade probes the device UI Test capabilities,
    /// pushes the capture extension, starts the control and video daemons and returns the unconnected session.
    /// <para>在 HDC 目标上启动 ohscrcpy 投屏会话。门面探测设备 UI Test 能力、推送采集扩展、启动控制与视频
    /// 守护进程，并返回尚未连接的会话。</para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session borrows the HDC lease opened by <see cref="OpenHdcLease"/>: when the local hdc server owns
    /// the target the lease carries a server-backed session, otherwise it carries the exact USB or TCP session.
    /// The facade probes UI Test, pushes and starts the video extension, forwards the daemon sockets to loopback
    /// ports and returns a <see cref="ManagedOhscrcpySession"/> whose frames reuse
    /// <see cref="ScrcpyVideoFrame"/>. Call <see cref="ManagedOhscrcpySession.Connect"/> after assigning it to
    /// the mirror view model, mirroring the scrcpy start flow. A failed start disposes the session and releases
    /// the lease immediately; otherwise the lease is released with the returned session.
    /// </para>
    /// <para>
    /// 会话借用 <see cref="OpenHdcLease"/> 打开的 HDC 租约：本机 hdc server 拥有该目标时租约携带 server 会话，
    /// 否则携带精确的 USB 或 TCP 会话。门面探测 UI Test、推送并启动视频扩展、把守护进程 socket 转发到回环端口，
    /// 并返回帧类型复用 <see cref="ScrcpyVideoFrame"/> 的 <see cref="ManagedOhscrcpySession"/>。把它交给投屏
    /// 视图模型后再调用 <see cref="ManagedOhscrcpySession.Connect"/>，与 scrcpy 启动流程一致。启动失败会立即
    /// 释放会话与租约；否则租约随返回的会话一同释放。
    /// </para>
    /// </remarks>
    /// <param name="device">The HDC target to mirror. <para>要投屏的 HDC 目标。</para></param>
    /// <param name="options">Capture parameters; <see langword="null"/> uses the defaults of
    /// <see cref="OhscrcpyLaunchOptions"/>. <para>采集参数；为 <see langword="null"/> 时使用
    /// <see cref="OhscrcpyLaunchOptions"/> 的默认值。</para></param>
    /// <returns>The unconnected session; the caller connects it after wiring the decode loop.
    /// <para>尚未连接的会话；调用方在接入解码循环后再连接。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.
    /// <para><paramref name="device"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="device"/> is not an HDC target.
    /// <para><paramref name="device"/> 不是 HDC 目标。</para></exception>
    /// <exception cref="IOException">The HDC lease, the extension push, the daemon start or the port forward
    /// failed, including ServerOnly mode without a listening hdc server. <para>HDC 租约、扩展推送、守护进程启动或
    /// 端口转发失败，也包括仅 server 模式但没有 hdc server 监听时。</para></exception>
    public ManagedOhscrcpySession StartOhscrcpyMirror(DetectedDevice device, OhscrcpyLaunchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Protocol != DeviceProtocolKind.Hdc)
        {
            throw new ArgumentException("An HDC target is required. 需要 HDC 目标。", nameof(device));
        }

        var opened = AcquireForMirror(() => OpenHdcLease(device), () => ReleaseMirrorEndpointHolders(device));
        if (opened is null)
        {
            throw new ArgumentException("An HDC target is required. 需要 HDC 目标。", nameof(device));
        }

        var (lease, session) = opened.Value;
        try
        {
            var mirrorSession = lease.SharesConnection ? session.CreateLease() : session;
            var started = MirrorSessionFacade.StartOhscrcpyOverHdcSession(mirrorSession, device.Serial,
                options ?? new(), connectTimeoutMs: 20_000, lease: lease,
                logSink: text => LogService.Instance.Write(LogLevel.Info, "ScreenMirror", text));
            TrackMirrorSession(device, started);
            return started;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }
}