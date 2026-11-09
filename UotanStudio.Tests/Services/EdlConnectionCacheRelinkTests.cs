using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Devices;
using UotanStudio.Tests.Protocols;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Replays the relink-offer consumption of an EDL operation run: a faulted session publishes its offer,
/// the cache waits through the configured reconnection delegate and re-runs the operation on the
/// replacement lease, while an unconfigured cache keeps the offer for a later consumer.
/// <para>回放 EDL 操作运行的重链提议消费：已失效的会话发布提议，缓存经配置的重连委托等待并在替代租约上
/// 重跑操作；未配置的缓存保留提议供后续消费者使用。</para>
/// </summary>
public sealed class EdlConnectionCacheRelinkTests
{
    [Fact]
    public async Task Run_FaultedSessionPublishesRelinkOffer_RerunsOperationOnReplacementLease()
    {
        var endpoint = new DeviceEndpoint { Source = "usb-test", Transport = "WinUsb", Address = "edl-relink-1", Protocol = "edl" };
        using var staleSession = NewFaultedSessionWithOffer(endpoint);
        using var freshSession = new EdlSession(new ScriptedFirehoseTransport());
        using var staleLease = NewLease(endpoint, staleSession);
        EdlRelinkOffer? receivedOffer = null;
        var receivedTimeout = 0;
        using var cache = new EdlConnectionCache(
            (_, _) => Task.FromResult(staleLease),
            () => { });
        cache.ReconnectWaiter = (offer, timeoutMs, _) =>
        {
            receivedOffer = offer;
            receivedTimeout = timeoutMs;
            return Task.FromResult<DeviceLease?>(NewLease(endpoint, freshSession));
        };
        var device = new DetectedDevice
        {
            Key = endpoint.Address,
            Serial = endpoint.Address,
            Protocol = DeviceProtocolKind.Edl,
            Transport = DeviceTransportKind.Usb
        };

        var state = await cache.RunAsync(device, session =>
        {
            if (ReferenceEquals(session, staleSession))
            {
                return session.ProbeProtocolAsync(TestContext.Current.CancellationToken);
            }

            return Task.FromResult(session.ProtocolState);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(EdlProtocolState.Unknown, state);
        Assert.NotNull(receivedOffer);
        Assert.Equal(endpoint.Id, receivedOffer!.PreviousEndpoint!.Id);
        Assert.True(receivedTimeout is > 0 and <= 120_000);
    }

    [Fact]
    public async Task Run_WithoutReconnectWaiter_FailurePropagatesAndOfferStaysPublished()
    {
        var endpoint = new DeviceEndpoint { Source = "usb-test", Transport = "WinUsb", Address = "edl-relink-2", Protocol = "edl" };
        using var staleSession = NewFaultedSessionWithOffer(endpoint);
        using var staleLease = NewLease(endpoint, staleSession);
        using var cache = new EdlConnectionCache(
            (_, _) => Task.FromResult(staleLease),
            () => { });
        var device = new DetectedDevice
        {
            Key = endpoint.Address,
            Serial = endpoint.Address,
            Protocol = DeviceProtocolKind.Edl,
            Transport = DeviceTransportKind.Usb
        };

        await Assert.ThrowsAsync<EdlException>(() => cache.RunAsync(device, session =>
            session.ProbeProtocolAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));

        Assert.NotNull(staleSession.LastRelinkOffer);
        Assert.Equal(EdlProtocolState.Unknown, cache.GetState(device.Key));
    }

    private static EdlSession NewFaultedSessionWithOffer(DeviceEndpoint endpoint)
    {
        var session = new EdlSession(new ScriptedFirehoseTransport());
        session.Firehose!.Invalidate();
        session.AttachRelinkContext(endpoint, null, TimeSpan.FromSeconds(2));
        return session;
    }

    private static DeviceLease NewLease(DeviceEndpoint endpoint, EdlSession session)
        => new(endpoint, new DisposableHandle(), session, () => { });

    private sealed class DisposableHandle : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}