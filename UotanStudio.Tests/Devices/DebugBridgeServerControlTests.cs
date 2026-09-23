using System.Net;
using System.Net.Sockets;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Services.Devices;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Devices;

public class DebugBridgeServerControlTests
{
    [Fact]
    public async Task StopAndVerifyAsync_Absent_DoesNotInvokeStop()
    {
        await DebugBridgeServerControl.StopAndVerifyAsync(_ => Task.FromResult(false),
            _ => throw new InvalidOperationException("Must not launch a client for an absent server."), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopAndVerifyAsync_Present_VerifiesRefusalAfterStop()
    {
        var stopped = false;
        var probes = 0;
        await DebugBridgeServerControl.StopAndVerifyAsync(_ =>
        {
            probes++;
            return Task.FromResult(!stopped);
        }, _ =>
        {
            stopped = true;
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        Assert.True(stopped);
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task StopAndVerifyAsync_UnknownState_PropagatesWithoutStopping()
    {
        await Assert.ThrowsAsync<IOException>(() => DebugBridgeServerControl.StopAndVerifyAsync(
            _ => throw new IOException("Unknown endpoint state"),
            _ => throw new InvalidOperationException("Must not stop an unidentified endpoint."), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StopAndVerifyAsync_StopFails_DoesNotReportSuccess()
    {
        await Assert.ThrowsAsync<IOException>(() => DebugBridgeServerControl.StopAndVerifyAsync(
            _ => Task.FromResult(true), _ => throw new IOException("Stop rejected"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StopAndVerifyAsync_ServerStillListening_CanBeCanceled()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DebugBridgeServerControl.StopAndVerifyAsync(
            _ => Task.FromResult(true), async _ => await cancellation.CancelAsync(), cancellation.Token));
    }

    [Fact]
    public async Task InvalidateProbe_StoppedListener_DiscardsCachedPresence()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(await DebugBridgeRoute.IsListeningAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
        listener.Stop();
        DebugBridgeRoute.InvalidateProbe("127.0.0.1", port);
        Assert.False(await DebugBridgeRoute.IsListeningAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenAdb_ServerTarget_UsesServerWithoutDirectAccess()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var reply = ServeTargetsAsync(listener, "unauthorized-serial\tunauthorized\n");
        using var session = DebugBridgeRoute.OpenAdb("unauthorized-serial", () =>
            throw new InvalidOperationException("Direct USB must not be opened."), "127.0.0.1", port);
        Assert.True(session!.Connection.UsesServer);
        await reply;
    }

    [Fact]
    public async Task OpenAdb_UnmatchedTarget_UsesDirectFactoryWhileServerRuns()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var reply = ServeTargetsAsync(listener, "server-owned               device product:fixture transport_id:1\n");
        var invoked = false;
        Assert.Null(DebugBridgeRoute.OpenAdb("other-device", () =>
        {
            invoked = true;
            return null;
        }, "127.0.0.1", port));
        Assert.True(invoked);
        await reply;
    }

    [Fact]
    public async Task AdbServerOwns_UnmatchedSerial_DoesNotBlockOtherUsbTarget()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var reply = ServeTargetsAsync(listener, "server-owned\tdevice\n");
        Assert.False(DebugBridgeRoute.AdbServerOwns("other-device", "127.0.0.1", port));
        await reply;
    }

    [Fact]
    public async Task AdbServerOwns_UnknownSerial_ProtectsManagedUsbTransport()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var reply = ServeTargetsAsync(listener, "server-owned\toffline\n");
        Assert.True(DebugBridgeRoute.AdbServerOwns(null, "127.0.0.1", port));
        await reply;
    }

    [Fact]
    public async Task ConnectAsync_CanceledWhileWaitingForCnxn_ReleasesWaitPromptly()
    {
        using var transport = new ScriptedCommTransport();
        using var session = new AdbSession(transport, AdbAuthentication.CreateNew());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ConnectAsync(cancellation.Token));
    }

    [Fact]
    public void OpenAdb_ServerRefused_InvokesDirectFactory()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var invoked = false;
        Assert.Null(DebugBridgeRoute.OpenAdb("serial", () => { invoked = true; return null; }, "127.0.0.1", port));
        Assert.True(invoked);
    }

    [Fact]
    public void OpenHdc_ServerPresent_DoesNotAcquireDirectSession()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var session = DebugBridgeRoute.OpenHdc("missing-or-unauthorized", () =>
            throw new InvalidOperationException("Direct USB must not be opened."), "127.0.0.1", port);
        Assert.NotNull(session);
    }

    [Fact]
    public void OpenHdc_ServerRefused_InvokesDirectFactory()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var directFailure = new IOException("Direct factory invoked");
        Assert.Same(directFailure, Assert.Throws<IOException>(() => DebugBridgeRoute.OpenHdc("serial",
            () => throw directFailure, "127.0.0.1", port)));
    }

    [Fact]
    public async Task OpenAdb_ServerRejectsTarget_PropagatesFailureWithoutDirectFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var listing = ServeTargetsAsync(listener, "unauthorized-serial\tunauthorized\n");
        using var session = DebugBridgeRoute.OpenAdb("unauthorized-serial", () =>
            throw new InvalidOperationException("Direct USB must not be opened."), "127.0.0.1", port);
        await listing;
        var fakeServer = Task.Run(async () =>
        {
            using var request = await listener.AcceptTcpClientAsync(ct);
            var stream = request.GetStream();
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, ct);
            var length = Convert.ToInt32(System.Text.Encoding.ASCII.GetString(header), 16);
            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, ct);
            Assert.Equal("host:transport:unauthorized-serial", System.Text.Encoding.UTF8.GetString(payload));
            await stream.WriteAsync("FAIL000cunauthorized"u8.ToArray(), ct);
        }, ct);
        await Assert.ThrowsAsync<IOException>(() => session!.ConnectAsync(ct));
        await fakeServer;
    }

    private static async Task ServeTargetsAsync(TcpListener listener, string listing)
    {
        var ct = TestContext.Current.CancellationToken;
        using var probe = await listener.AcceptTcpClientAsync(ct);
        using var request = await listener.AcceptTcpClientAsync(ct);
        var stream = request.GetStream();
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = Convert.ToInt32(System.Text.Encoding.ASCII.GetString(header), 16);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        Assert.Equal("host:devices-l", System.Text.Encoding.UTF8.GetString(payload));
        var bytes = System.Text.Encoding.UTF8.GetBytes(listing);
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"OKAY{bytes.Length:X4}"), ct);
        await stream.WriteAsync(bytes, ct);
    }

    [Fact]
    public void RequireUniqueUsbIdentity_MissingOrAmbiguous_RejectsGuessing()
    {
        Assert.Throws<IOException>(() => DeviceMonitorService.RequireUniqueUsbIdentity([]));
        Assert.Throws<IOException>(() => DeviceMonitorService.RequireUniqueUsbIdentity([new(), new()]));
        var device = new UsbDeviceInfo { DeviceKey = "physical-key" };
        Assert.Same(device, DeviceMonitorService.RequireUniqueUsbIdentity([device]));
    }
}