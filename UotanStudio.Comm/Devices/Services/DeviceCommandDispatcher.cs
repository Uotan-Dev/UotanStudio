using System.Threading.Channels;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Per-device command pool: submitted commands are executed one at a time in FIFO order and
/// each result (or exception) is returned to its submitter through the returned task.
/// Long-running operations (flashing, large transfers) hold the queue until they finish;
/// cancelling a submission only affects that command, never the queue itself.
/// <para>每设备指令池：提交的指令按 FIFO 逐个执行，结果（或异常）经返回的 Task 回传给提交方。
/// 长操作（刷写、大文件传输）执行期间独占队列；取消某次提交只影响该指令，不影响队列本身。</para>
/// </summary>
/// <remarks>
/// Streaming work (logcat, shell streaming, mirroring) must not go through the pool; it opens
/// its stream directly on the session. Protocol-internal mutexes stay in place as a second
/// line of defence.
/// <para>流式工作（logcat、shell 流、投屏流）不得经过指令池，应直接在会话上打开流。
/// 协议内部互斥保留为第二道防线。</para>
/// </remarks>
public sealed class DeviceCommandDispatcher : IAsyncDisposable
{
    private sealed record Invocation(Func<CancellationToken, Task> Run, CancellationTokenSource Linked);

    private readonly Channel<Invocation> _channel = Channel.CreateUnbounded<Invocation>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private int _disposed;

    /// <summary>
    /// Starts the background worker that drains the queue in FIFO order.
    /// <para>启动后台工作任务，按 FIFO 顺序消费队列。</para>
    /// </summary>
    public DeviceCommandDispatcher()
    {
        _worker = Task.Run(RunAsync);
    }

    /// <summary>
    /// Enqueues a command and returns a task that completes with its result once the command
    /// has run. The <paramref name="cancellationToken"/> only cancels this command.
    /// <para>将指令入队并返回携带其结果的任务。<paramref name="cancellationToken"/> 仅取消该指令。</para>
    /// </summary>
    /// <param name="name">
    /// Nonempty label identifying the command. <para>标识该指令的非空名称。</para>
    /// </param>
    /// <param name="work">
    /// Command body, invoked once on the worker with a token tied to this command and the dispatcher lifetime.
    /// <para>指令主体，仅在工作任务上调用一次，接收同时绑定该指令与调度器生命周期的标记。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels only this command, never the queue. <para>仅取消该指令，绝不影响队列。</para>
    /// </param>
    /// <returns>A task that completes with the command result, or faults or cancels with it.
    /// <para>随指令结果完成、或随之失败/取消的任务。</para></returns>
    /// <remarks>
    /// The returned task is reported as cancelled only when the command's own linked token is
    /// cancelled (caller token or dispatcher lifetime). An <see cref="OperationCanceledException"/>
    /// raised with an unrelated token, including a bare one from the device layer, is surfaced as a
    /// fault carrying the original exception instead of being mistaken for user cancellation.
    /// <para>仅当指令自身的链接标记被取消（调用方标记或调度器生命周期）时，返回的任务才报告为已取消。
    /// 携带无关标记抛出的 <see cref="OperationCanceledException"/>（包括设备层抛出的裸异常）会以保留原异常的
    /// 失败形式呈现，而不会被误当成用户取消。</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is null, empty, or whitespace. <para><paramref name="name"/> 为 null、空白或仅含空白字符。</para></exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="work"/> is null. <para><paramref name="work"/> 为 null。</para></exception>
    /// <exception cref="ObjectDisposedException">
    /// The dispatcher has been disposed, including when disposal wins a concurrent race.
    /// <para>调度器已被释放，包括并发竞争中释放先到达的情况。</para></exception>
    public Task<T> EnqueueAsync<T>(string name, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        CancellationTokenSource linked;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        }
        catch (ObjectDisposedException)
        {
            throw new ObjectDisposedException(nameof(DeviceCommandDispatcher));
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Run(CancellationToken token)
        {
            try
            {
                if (token.IsCancellationRequested)
                {
                    completion.TrySetCanceled(token);
                    return;
                }

                var result = await work(token).ConfigureAwait(false);
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == token || token.IsCancellationRequested)
            {
                completion.TrySetCanceled(token);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }

        if (!_channel.Writer.TryWrite(new Invocation(Run, linked)))
        {
            linked.Dispose();
            throw new ObjectDisposedException(nameof(DeviceCommandDispatcher));
        }

        return completion.Task;
    }

    /// <summary>
    /// Enqueues a command that returns no value.
    /// <para>入队无返回值的指令。</para>
    /// </summary>
    /// <param name="name">
    /// Nonempty label identifying the command. <para>标识该指令的非空名称。</para>
    /// </param>
    /// <param name="work">
    /// Command body run on the worker. <para>在工作任务上执行的指令主体。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels only this command, never the queue. <para>仅取消该指令，绝不影响队列。</para>
    /// </param>
    /// <returns>A task that completes when the command finishes, faults or is cancelled.
    /// <para>随指令完成、失败或取消而结束的任务。</para></returns>
    public Task EnqueueAsync(string name, Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
        => EnqueueAsync<object?>(name, async token => { await work(token).ConfigureAwait(false); return null; }, cancellationToken);

    /// <summary>
    /// Stops accepting commands, cancels everything still queued and waits for the running
    /// command to settle before returning.
    /// <para>停止接收新指令，取消仍在排队的指令，并等待正在执行的指令结束后返回。</para>
    /// </summary>
    /// <returns>A task that completes once the worker has drained and stopped.
    /// <para>工作任务消费完队列并停止后完成的任务。</para></returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _channel.Writer.TryComplete();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private async Task RunAsync()
    {
        await foreach (var invocation in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await invocation.Run(invocation.Linked.Token).ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                invocation.Linked.Dispose();
            }
        }
    }
}