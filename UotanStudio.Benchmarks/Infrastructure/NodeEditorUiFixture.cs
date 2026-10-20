using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace UotanStudio.Benchmarks.Infrastructure;

/// <summary>One dispatcher for benchmarks that build Avalonia controls, including third-party caches with thread affinity.<para>为构建 Avalonia 控件的基准测试提供单一调度器，包含有线程亲和性的第三方缓存。</para></summary>
public sealed class NodeEditorUiFixture : IAsyncDisposable
{
    private readonly HeadlessUnitTestSession _session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp));

    /// <summary>Keeps the isolated UI dispatcher alive while async commands post their completion.<para>在有异步命令投递完成回调时保持独立 UI 调度器存活。</para></summary>
    /// <param name="action">Work to run on the dispatcher.<para>在调度器上执行的工作。</para></param>
    /// <param name="token">Cancellation token.<para>取消标记。</para></param>
    /// <returns>The dispatched work.<para>被调度执行的工作。</para></returns>
    public Task DispatchAsync(Func<Task> action, CancellationToken token) => _session.Dispatch(() =>
    {
        var work = action();
        if (!work.IsCompleted)
        {
            using var finished = CancellationTokenSource.CreateLinkedTokenSource(token);
            _ = work.ContinueWith(_ => finished.Cancel(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Dispatcher.UIThread.MainLoop(finished.Token);
        }
        return work;
    }, token).Unwrap();

    /// <summary>Runs one synchronous action on the dispatcher.<para>在调度器上执行一个同步动作。</para></summary>
    /// <param name="action">Action to run.<para>要执行的动作。</para></param>
    /// <param name="token">Cancellation token.<para>取消标记。</para></param>
    /// <returns>The completed dispatch.<para>已完成的调度。</para></returns>
    public Task Run(Action action, CancellationToken token) => _session.Dispatch(action, token);

    /// <summary>Disposes the headless session.<para>释放无头会话。</para></summary>
    /// <returns>The asynchronous dispose.<para>异步释放。</para></returns>
    public ValueTask DisposeAsync() => _session.DisposeAsync();
}

/// <summary>The minimal Avalonia application the headless session builds its controls on.<para>无头会话构建控件所依赖的最小 Avalonia 应用。</para></summary>
public sealed class HeadlessTestApp : Application
{
}

/// <summary>Marks a type used by Avalonia headless sessions.<para>标记无头会话使用的类型。</para></summary>
internal static class HeadlessApplicationFactory
{
    /// <summary>Gets the application type a headless session starts.<para>获取无头会话启动的应用类型。</para></summary>
    internal static Type ApplicationType => typeof(HeadlessTestApp);
}
