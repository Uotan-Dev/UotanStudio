using UotanStudio.Services.Devices;
using Xunit;

namespace UotanStudio.Tests.Devices;

public class DeviceArrivalPipelineTests
{
    [Fact]
    public async Task Submit_DuplicateSignal_ReusesTheRunningHandshake()
    {
        var handshakeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handshakeCalls = 0;
        using var pipeline = new DeviceArrivalPipeline(_ =>
        {
            Interlocked.Increment(ref handshakeCalls);
            return new DeviceArrivalWork(async token =>
            {
                handshakeStarted.TrySetResult();
                await release.Task.WaitAsync(token);
            });
        });

        var first = pipeline.Submit(new DeviceArrival("dev-1", DeviceProtocolKind.Mtk, 1, true));
        await handshakeStarted.Task;
        var second = pipeline.Submit(new DeviceArrival("dev-1", DeviceProtocolKind.Mtk, 1, true));

        Assert.Same(first, second);
        Assert.Equal(1, Volatile.Read(ref handshakeCalls));
        Assert.True(pipeline.IsActive("dev-1"));

        release.TrySetResult();
        await first;
        await second;
    }

    [Fact]
    public async Task Submit_HigherQualitySignal_PreemptsRunningHandshake()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCalls = 0;
        using var pipeline = new DeviceArrivalPipeline(_ =>
        {
            if (Interlocked.Increment(ref secondCalls) == 1)
            {
                return new DeviceArrivalWork(async token =>
                {
                    firstStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException)
                    {
                        firstCancelled.TrySetResult();
                        throw;
                    }
                });
            }

            return new DeviceArrivalWork(async token =>
            {
                secondStarted.TrySetResult();
                await releaseSecond.Task.WaitAsync(token);
            });
        });

        var first = pipeline.Submit(new DeviceArrival("dev-2", DeviceProtocolKind.Mtk, 1, true));
        await firstStarted.Task;
        var second = pipeline.Submit(new DeviceArrival("dev-2", DeviceProtocolKind.Mtk, 3, true));

        await firstCancelled.Task;
        await secondStarted.Task;
        var firstResult = await first;
        Assert.False(firstResult.HandshakeCompleted);
        Assert.Contains("superseded", firstResult.Reason, StringComparison.Ordinal);

        releaseSecond.TrySetResult();
        var secondResult = await second;
        Assert.True(secondResult.HandshakeCompleted);
        Assert.Equal(2, Volatile.Read(ref secondCalls));
    }

    [Fact]
    public async Task Submit_HandshakeFailure_StillCompletesAndReportsFailure()
    {
        using var pipeline = new DeviceArrivalPipeline(_ => new DeviceArrivalWork(
            _ => Task.FromException(new IOException("handshake failed"))));

        var result = await pipeline.Submit(new DeviceArrival("dev-3", DeviceProtocolKind.Mtk, 1, true));

        Assert.False(result.HandshakeCompleted);
        Assert.Contains("handshake failed", result.Reason, StringComparison.Ordinal);
        Assert.False(pipeline.IsActive("dev-3"));
    }

    [Fact]
    public async Task Submit_HandshakeSettlesBeforeDetailsFinish()
    {
        var releaseDetails = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detailsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new List<DeviceArrivalResult>();
        using var pipeline = new DeviceArrivalPipeline(
            _ => new DeviceArrivalWork(
                _ => Task.CompletedTask,
                async token =>
                {
                    detailsStarted.TrySetResult();
                    await releaseDetails.Task.WaitAsync(token);
                }),
            result =>
            {
                lock (results)
                {
                    results.Add(result);
                }
                return Task.CompletedTask;
            });

        var result = await pipeline.Submit(new DeviceArrival("dev-4", DeviceProtocolKind.Mtk, 1, true));

        Assert.True(result.HandshakeCompleted);
        Assert.False(result.DetailsCompleted);
        await detailsStarted.Task;
        Assert.False(pipeline.IsActive("dev-4"));

        releaseDetails.TrySetResult();
        await Task.Yield();
        lock (results)
        {
            Assert.Single(results);
        }
    }

    [Fact]
    public async Task Submit_DetailsCancelled_KeepsHandshakeSuccess()
    {
        var releaseDetails = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelDetails = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pipeline = new DeviceArrivalPipeline(_ => new DeviceArrivalWork(
            _ => Task.CompletedTask,
            async token =>
            {
                cancelDetails.TrySetResult();
                await releaseDetails.Task.WaitAsync(token);
            }));

        var result = await pipeline.Submit(new DeviceArrival("dev-5", DeviceProtocolKind.Mtk, 1, true));
        await cancelDetails.Task;
        pipeline.Dispose();

        Assert.True(result.HandshakeCompleted);
        Assert.False(result.DetailsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            pipeline.Submit(new DeviceArrival("dev-6", DeviceProtocolKind.Mtk, 1, true)));
    }

    [Fact]
    public void Submit_PureClassifier_RunsOnCallingThread()
    {
        var classificationThread = 0;
        using var pipeline = new DeviceArrivalPipeline(_ =>
            new DeviceArrivalWork(_ => Task.CompletedTask));
        var expected = Environment.CurrentManagedThreadId;

        _ = pipeline.Submit(new DeviceArrival("dev-7", null, 1, true, () =>
        {
            classificationThread = Environment.CurrentManagedThreadId;
            return DeviceProtocolKind.Unisoc;
        }));

        Assert.Equal(expected, classificationThread);
    }
}