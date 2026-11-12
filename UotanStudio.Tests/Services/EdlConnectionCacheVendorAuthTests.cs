using System.Text;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Devices;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Covers the read-only EDL probe on a program that demands authentication: the refusal is recorded on the
/// detail snapshot and the same session is not configured again on every refresh.
/// <para>覆盖要求认证的程序上的只读 EDL 探测：拒绝被记入详情快照，且同一会话不会在每次刷新时重复 configure。</para>
/// </summary>
public sealed class EdlConnectionCacheVendorAuthTests
{
    private const string AuthenticationRefusal =
        "ERROR: Only nop and sig tag can be recevied before authentication.";

    private static byte[] Document(string body) => Encoding.UTF8.GetBytes(
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<data>\n{body}\n</data>\n");

    private static FirehoseConfiguration Configuration() => new()
    {
        AutoDetectSectorSize = false,
        SectorSize = 512,
        ResponseTimeoutSeconds = 1,
        ConfigureRetryIntervalMs = 1,
        ConfigureResponseTimeoutMs = 500,
        ConfigureFirstAttemptTimeoutMs = 500
    };

    [Fact]
    public async Task Probe_AuthenticationRefusal_IsRecordedOnceAndNotRepeated()
    {
        var endpoint = new DeviceEndpoint
        {
            Source = "usb-test",
            Transport = "WinUsb",
            Address = "edl-vendor-auth-1",
            Protocol = "edl"
        };
        var configures = 0;
        var transport = new ScriptedCommTransport();
        transport.OnWrite = bytes =>
        {
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("<configure", StringComparison.Ordinal))
            {
                configures++;
                return [Document($"<log value=\"{AuthenticationRefusal}\" />"), Document("<response value=\"NAK\" />")];
            }

            return [Document("<response value=\"NAK\" />")];
        };
        using var session = new EdlSession(transport, Configuration());
        using var lease = new DeviceLease(endpoint, new DisposableHandle(), session, () => { });
        using var cache = new EdlConnectionCache((_, _) => Task.FromResult(lease), () => { });
        var device = new DetectedDevice
        {
            Key = endpoint.Address,
            Serial = endpoint.Address,
            Protocol = DeviceProtocolKind.Edl,
            Transport = DeviceTransportKind.Usb
        };
        var cancellationToken = TestContext.Current.CancellationToken;

        var state = await cache.ProbeAsync(device, cancellationToken);

        Assert.Equal(EdlProtocolState.Firehose, state);
        Assert.True(session.VendorAuthenticationRequired);
        var details = cache.GetDetails(device.Key);
        Assert.NotNull(details);
        Assert.True(details!.RequiresVendorAuthentication);
        Assert.Equal(1, configures);

        await cache.ProbeAsync(device, cancellationToken);

        Assert.Equal(1, configures);
    }

    private sealed class DisposableHandle : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}