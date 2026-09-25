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

    public DeviceCommandDispatcher()
    {
        _worker = Task.Run(RunAsync);
    }

    /// <summary>
    /// Enqueues a command and returns a task that completes with its result once the command
    /// has run. The <paramref name="cancellationToken"/> only cancels this command.
    /// <para>将指令入队并返回携带其结果的任务。<paramref name="cancellationToken"/> 仅取消该指令。</para>
    /// </summary>
    public Task<T> EnqueueAsync<T>(string name, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Run(CancellationToken token)
        {
            try
            {
                if (token.IsCancellationRequested)
                {
                    // Disposal or caller cancellation landed before the command started; skip
                    // it instead of running work that might ignore its token.
                    completion.TrySetCanceled(token);
                    return;
                }

                var result = await work(token).ConfigureAwait(false);
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
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

    /// <summary>Enqueues a command that returns no value. 入队无返回值的指令。</summary>
    public Task EnqueueAsync(string name, Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
        => EnqueueAsync<object?>(name, async token => { await work(token).ConfigureAwait(false); return null; }, cancellationToken);

    /// <summary>
    /// Stops accepting commands, cancels everything still queued and waits for the running
    /// command to settle before returning.
    /// <para>停止接收新指令，取消仍在排队的指令，并等待正在执行的指令结束后返回。</para>
    /// </summary>
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
            // The worker never faults; guard defensively so lease disposal cannot be blocked.
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
                // Settle logic inside Run completes the caller's task; never let the worker die.
            }
            finally
            {
                invocation.Linked.Dispose();
            }
        }
    }
}