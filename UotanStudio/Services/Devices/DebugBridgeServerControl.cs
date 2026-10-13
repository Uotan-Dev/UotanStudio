using UotanStudio.Comm;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Routing;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Explicit host-server shutdown. Success requires connection refusal, not just command completion.
/// <para>显式关闭调试桥服务；成功以连接被拒绝为准，而非命令执行完成。</para>
/// </summary>
internal static class DebugBridgeServerControl
{
    /// <summary>
    /// Stops ADB through its host protocol without starting an executable.
    /// <para>通过 ADB 的主机协议停止服务，不启动任何可执行文件。</para>
    /// </summary>
    /// <param name="monitor">Device monitor refreshed after the shutdown. <para>关闭完成后用于刷新设备的监视器。</para></param>
    /// <param name="cancellationToken">Token cancelling the shutdown. <para>用于取消关闭操作的令牌。</para></param>
    public static Task StopAdbAsync(DeviceMonitorService monitor, CancellationToken cancellationToken = default)
        => StopAsync(monitor, DeviceCommunicationConfiguration.AdbServerPort,
            token => AdbServerServices.KillAsync(DeviceCommunicationConfiguration.ServerHost,
                DeviceCommunicationConfiguration.AdbServerPort, token), cancellationToken);

    /// <summary>
    /// Stops HDC through its host channel protocol; the hdc executable is never started.
    /// <para>通过 HDC 的主机通道协议停止服务，不会启动 hdc 可执行文件。</para>
    /// </summary>
    /// <param name="monitor">Device monitor refreshed after the shutdown. <para>关闭完成后用于刷新设备的监视器。</para></param>
    /// <param name="cancellationToken">Token cancelling the shutdown. <para>用于取消关闭操作的令牌。</para></param>
    public static Task StopHdcAsync(DeviceMonitorService monitor, CancellationToken cancellationToken = default)
    {
        var port = HdcServerClient.DefaultServerPort;
        return StopAsync(monitor, port, async token =>
        {
            using var server = new HdcServerClient(DeviceCommunicationConfiguration.ServerHost, port);
            await server.KillAsync(10000, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static async Task StopAsync(DeviceMonitorService monitor, int port, Func<CancellationToken, Task> stop, CancellationToken ct)
    {
        async Task<bool> Probe(CancellationToken token)
        {
            DebugBridgeRoute.InvalidateProbe(DeviceCommunicationConfiguration.ServerHost, port);
            return await DebugBridgeRoute.IsListeningAsync(DeviceCommunicationConfiguration.ServerHost, port, token).ConfigureAwait(false);
        }
        try
        {
            await StopAndVerifyAsync(Probe, stop, ct).ConfigureAwait(false);
        }
        finally
        {
            DebugBridgeRoute.InvalidateProbe(DeviceCommunicationConfiguration.ServerHost, port);
            DebugBridgeRoute.InitializeAdbServerProbe();
            DebugBridgeRoute.InitializeHdcServerProbe();
            await monitor.ForceRefreshAsync().ConfigureAwait(false);
        }
    }

    internal static async Task StopAndVerifyAsync(Func<CancellationToken, Task<bool>> probe,
        Func<CancellationToken, Task> stop, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            if (!await probe(deadline.Token).ConfigureAwait(false))
            {
                return;
            }
            await stop(deadline.Token).ConfigureAwait(false);
            while (await probe(deadline.Token).ConfigureAwait(false))
            {
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The debug bridge server did not stop before the deadline.");
        }
    }
}