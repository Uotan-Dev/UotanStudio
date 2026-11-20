using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Services.Devices;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Replays the MTK connection-cache contract: the first handshake parks a lease with a completed boot
/// handshake, a caller reuses the parked session without re-exchanging bytes, and a dead parked lease is
/// replaced instead of consumed.
/// <para>回放 MTK 连接缓存契约：首次握手暂存已完成 boot 握手的租约，调用方复用暂存会话且不再交换字节，
/// 失效的暂存租约被替换而不是被消费。</para>
/// </summary>
public sealed class MtkConnectionCacheTests
{
    [Fact]
    public async Task RunAsync_FirstCallHandshakesAndSecondCallReusesParkedSession()
    {
        var device = NewDevice("mtk-cache-1");
        var opened = 0;
        MtkBootScriptDevice? scripted = null;
        using var cache = new MtkConnectionCache((_, _) =>
        {
            opened++;
            scripted = new MtkBootScriptDevice();
            return Task.FromResult(NewLease(device, new MtkSession(scripted.Transport)));
        }, () => { });

        await cache.RunAsync(device, session => Wrap(session), TestContext.Current.CancellationToken);
        Assert.Equal(1, opened);
        Assert.NotNull(scripted);
        Assert.True(scripted.HandshakeComplete);
        var trafficAfterFirst = scripted.HandshakeReceived.Count + scripted.CommandsReceived.Count;

        await cache.RunAsync(device, session => Wrap(session), TestContext.Current.CancellationToken);

        Assert.Equal(1, opened);
        Assert.Equal(trafficAfterFirst, scripted.HandshakeReceived.Count + scripted.CommandsReceived.Count);
    }

    [Fact]
    public async Task RunAsync_DeadParkedLeaseIsReplacedWithFreshHandshake()
    {
        var device = NewDevice("mtk-cache-2");
        var opened = 0;
        MtkBootScriptDevice? live = null;
        var deadDevice = new MtkBootScriptDevice { RejectedHandshakeAttempts = 50 };
        using var deadSession = new MtkSession(deadDevice.Transport, timeoutMs: 1_000, handshakeTimeoutMs: 1_000);
        await Assert.ThrowsAsync<MtkProtocolException>(
            () => deadSession.InitializeAsync(TestContext.Current.CancellationToken));
        using var cache = new MtkConnectionCache((_, _) =>
        {
            opened++;
            if (opened == 1)
            {
                return Task.FromResult(NewLease(device, deadSession));
            }

            live = new MtkBootScriptDevice();
            return Task.FromResult(NewLease(device, new MtkSession(live.Transport)));
        }, () => { });

        await cache.RunAsync(device, session => Wrap(session), TestContext.Current.CancellationToken);
        await cache.RunAsync(device, session => Wrap(session), TestContext.Current.CancellationToken);

        Assert.Equal(2, opened);
        Assert.NotNull(live);
        Assert.True(live.HandshakeComplete);
    }

    [Fact]
    public async Task RunAsync_FailedOperationDisposesLeaseInsteadOfParkingIt()
    {
        var device = NewDevice("mtk-cache-3");
        var opened = 0;
        using var cache = new MtkConnectionCache((_, _) =>
        {
            opened++;
            var scripted = new MtkBootScriptDevice();
            return Task.FromResult(NewLease(device, new MtkSession(scripted.Transport)));
        }, () => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.RunAsync(device,
            static _ => Task.FromException<MtkSession>(new InvalidOperationException("boom")),
            TestContext.Current.CancellationToken));

        await cache.RunAsync(device, session => Wrap(session), TestContext.Current.CancellationToken);

        Assert.Equal(2, opened);
    }

    [Fact]
    public async Task HandshakeVcomAsync_ParksHandshakenSession_ForLaterRun()
    {
        var device = NewDevice("mtk-cache-vcom");
        var scripted = new MtkBootScriptDevice();
        var session = new MtkSession(scripted.Transport);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        var opened = 0;
        using var cache = new MtkConnectionCache(
            (_, _) => Task.FromResult(NewLease(device, session)),
            () => { },
            (_, _, _) =>
            {
                opened++;
                return Task.FromResult<DeviceLease?>(NewLease(device, session));
            });
        var info = new UsbDeviceInfo { DeviceKey = device.Key, VendorId = 0x0E8D, ProductId = 0x2000 };

        var lease = await cache.HandshakeVcomAsync(info, device, 115200, TestContext.Current.CancellationToken);

        Assert.NotNull(lease);
        Assert.Equal(1, opened);
        var reused = await cache.RunAsync(device, s => Wrap(s), TestContext.Current.CancellationToken);
        Assert.Same(session, reused);
        Assert.Equal(1, opened);
    }
    private static Task<MtkSession> Wrap(MtkSession session) => Task.FromResult(session);

    private static DetectedDevice NewDevice(string key) => new()
    {
        Key = key,
        Serial = key,
        Protocol = DeviceProtocolKind.Mtk,
        Transport = DeviceTransportKind.Usb
    };

    private static DeviceLease NewLease(DetectedDevice device, MtkSession session)
    {
        var endpoint = new DeviceEndpoint { Source = "usb-test", Transport = "WinUsb", Address = device.Key, Protocol = "mtk" };
        return new DeviceLease(endpoint, new DisposableHandle(), session, () => { });
    }

    private sealed class DisposableHandle : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}