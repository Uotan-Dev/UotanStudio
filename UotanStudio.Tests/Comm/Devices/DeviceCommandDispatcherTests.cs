using UotanStudio.Comm.Devices.Services;
using Xunit;

namespace UotanStudio.Tests.Comm.Devices;

public class DeviceCommandDispatcherTests
{
    [Fact]
    public async Task EnqueueAsync_RunsCommandsInFifoOrderAndReturnsResults()
    {
        await using var dispatcher = new DeviceCommandDispatcher();
        var order = new List<int>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = dispatcher.EnqueueAsync("first", async token =>
        {
            order.Add(1);
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return "one";
        }, TestContext.Current.CancellationToken);
        var second = dispatcher.EnqueueAsync("second", async token =>
        {
            order.Add(2);
            await Task.Delay(10, token);
            return 2;
        }, TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal([1], order);
        gate.SetResult();

        Assert.Equal("one", await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task EnqueueAsync_LongRunningCommandHoldsQueueUntilFinished()
    {
        await using var dispatcher = new DeviceCommandDispatcher();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var longRunning = dispatcher.EnqueueAsync("long", async token =>
        {
            started.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return "done";
        }, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var queued = dispatcher.EnqueueAsync("queued", token => Task.FromResult("late"), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(queued.IsCompleted);

        release.SetResult();
        Assert.Equal("done", await longRunning.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("late", await queued.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnqueueAsync_PropagatesExceptionsThroughTheReturnedTask()
    {
        await using var dispatcher = new DeviceCommandDispatcher();

        var failing = dispatcher.EnqueueAsync<int>("fail", _ => throw new InvalidOperationException("boom"), TestContext.Current.CancellationToken);
        var following = dispatcher.EnqueueAsync("after-failure", _ => Task.FromResult("ok"), TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("boom", error.Message);
        Assert.Equal("ok", await following.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnqueueAsync_CancellingOneSubmissionLeavesQueueOperational()
    {
        await using var dispatcher = new DeviceCommandDispatcher();
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var cancellable = dispatcher.EnqueueAsync("cancellable", async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "never";
        }, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var queued = dispatcher.EnqueueAsync("queued", _ => Task.FromResult("ok"), TestContext.Current.CancellationToken);
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => cancellable.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("ok", await queued.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeAsync_CancelsQueuedCommandsAndRejectsNewOnes()
    {
        var dispatcher = new DeviceCommandDispatcher();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = dispatcher.EnqueueAsync("running", async token =>
        {
            started.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return "ran";
        }, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var queued = dispatcher.EnqueueAsync("queued", _ => Task.FromResult("never"), TestContext.Current.CancellationToken);
        await dispatcher.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queued.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await dispatcher.EnqueueAsync("late", _ => Task.FromResult(0), TestContext.Current.CancellationToken));
    }
}