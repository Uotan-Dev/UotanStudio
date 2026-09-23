using AdvancedSharpAdbClient;
using UotanStudio.Comm;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Hdc;

namespace UotanStudio.Services.Devices;

/// <summary>Explicit host-server shutdown. Success requires connection refusal, not just command completion.</summary>
internal static class DebugBridgeServerControl
{
    /// <summary>Stops ADB through its host protocol without starting an executable.</summary>
    public static Task StopAdbAsync(CancellationToken cancellationToken = default)
        => StopAsync(DeviceCommunicationConfiguration.AdbServerPort,
            token => new AdbClient().KillAdbAsync(token), cancellationToken);

    /// <summary>Stops HDC through the official client resolved from PATH or DevEco.</summary>
    public static Task StopHdcAsync(CancellationToken cancellationToken = default)
    {
        var port = HdcServerClient.DefaultServerPort;
        return StopAsync(port, async token =>
        {
            await HdcCommandClient.RunAsync(HdcDeviceService.ResolveExecutable(),
                ["-s", $"{DeviceCommunicationConfiguration.ServerHost}:{port}", "kill"], token, 10000).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static async Task StopAsync(int port, Func<CancellationToken, Task> stop, CancellationToken ct)
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
            await DeviceMonitorService.Instance.ForceRefreshAsync().ConfigureAwait(false);
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