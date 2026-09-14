using System.Text;
using UotanStudio.Comm;
using UotanStudio.Comm.Protocols.Hdc;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Devices;

/// <summary>Explicitly opted-in USB reads; never starts/stops a server or changes device state.</summary>
[Collection("HDC direct hardware")]
public sealed class HdcDirectReadOnlyDeviceTests
{
    [Fact]
    public async Task DirectUsb_ConcurrentShellAndHiLog_CancelThenReuse()
    {
        var serial = Environment.GetEnvironmentVariable("UOTAN_TEST_HDC_USB_SERIAL");
        if (string.IsNullOrWhiteSpace(serial)) Assert.Skip("Set UOTAN_TEST_HDC_USB_SERIAL for direct USB read-only validation; the port must be available.");
        var ct = TestContext.Current.CancellationToken;
        var targets = CommKit.Default.EnumerateUsbDevices(filter: new UsbDeviceFilter
        {
            InterfaceClass = 0xff,
            InterfaceSubClass = 0x50,
            InterfaceProtocol = 1,
        }).Where(d => d.SerialNumber == serial && d.Interfaces.Any(i => i.Endpoints.Count >= 2)).ToArray();
        Assert.True(targets.Length > 0, "The selected HDC USB interface has no readable endpoint descriptors. Check that it is present and that an official HDC server or another application does not own the port.");
        var target = Assert.Single(targets);
        using var usb = CommKit.Default.OpenUsbDeviceSessionByKey(target.DeviceKey);
        Assert.NotNull(usb);
        using var session = new HdcSession(new UsbDeviceTransport(usb), HdcAuthentication.LoadPrivateKey(HdcAuthentication.DefaultKeyFilePath));
        await session.ConnectAsync(ct);
        using var logging = CancellationTokenSource.CreateLinkedTokenSource(ct);
        logging.CancelAfter(TimeSpan.FromSeconds(3));
        long bytesRead = 0;
        var logs = Task.Run(() => session.CreateShellClient("hilog -v year").ExecuteStreaming(
            bytes => Interlocked.Add(ref bytesRead, bytes.Length), null, logging.Token), ct);
        var model = await Task.Run(() => session.ExecuteCommand("param get const.product.model"), ct);
        Assert.NotEmpty(Encoding.UTF8.GetString(model.Stdout).Trim());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await logs);
        Assert.True(bytesRead > 0);
        var ready = await Task.Run(() => session.ExecuteCommand("echo ready"), ct);
        Assert.Equal("ready", Encoding.UTF8.GetString(ready.Stdout).Trim());
        Assert.Equal(-1, ready.ExitCode);

        using var transfer = new HdcSyncClient(session.Connection);
        using var plain = new MemoryStream();
        transfer.RecvStream("/system/bin/toybox", plain, ct);
        Assert.True(plain.Length > 0);
        var remoteHash = Encoding.UTF8.GetString(session.ExecuteCommand("sha256sum /system/bin/toybox").Stdout)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        var localHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(plain.ToArray()));
        Assert.Equal(remoteHash.ToUpperInvariant(), localHash);
        transfer.Compress = true;
        using var compressed = new MemoryStream();
        transfer.RecvStream("/system/bin/toybox", compressed, ct);
        Assert.Equal(plain.ToArray(), compressed.ToArray());

        using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var cancellingTransfer = new HdcSyncClient(session.Connection);
        cancellingTransfer.Progress += (_, _) => interrupted.Cancel();
        using var partial = new MemoryStream();
        Assert.ThrowsAny<OperationCanceledException>(() => cancellingTransfer.RecvStream("/system/bin/toybox", partial, interrupted.Token));
        using var missing = new MemoryStream();
        Assert.Throws<IOException>(() => transfer.RecvStream("/system/uotan-nonexistent-" + Guid.NewGuid().ToString("N"), missing, ct));
        var afterTransfer = session.ExecuteCommand("echo ready");
        Assert.Equal("ready", Encoding.UTF8.GetString(afterTransfer.Stdout).Trim());
        _ = session.CreateDeviceServices().ListDebugProcesses();
    }
}

// Native enumeration emits global UsbTrace events; keep it outside tests replacing trace callbacks.
[CollectionDefinition("HDC direct hardware", DisableParallelization = true)]
public sealed class HdcDirectHardwareCollection;