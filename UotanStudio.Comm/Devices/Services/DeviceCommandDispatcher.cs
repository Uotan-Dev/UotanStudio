using System.Diagnostics;
using System.Threading.Channels;
using UotanStudio.Comm.Diagnostics;

namespace UotanStudio.Comm.Devices.Services;

/// <summary>
/// Per-device command pool: commands run one at a time in FIFO order and their results reach
/// submitters through the returned task. Cancelling a submission affects only that command,
/// never the queue.
/// <para>每设备指令池：指令按 FIFO 逐个执行，结果经返回的任务回传给提交方。取消某次提交只影响
/// 该指令，不影响队列。</para>
/// </summary>
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
    private int _pending;

    /// <summary>
    /// Starts the background worker that drains the queue in FIFO order.
    /// <para>启动后台工作任务，按 FIFO 顺序消费队列。</para>
    /// </summary>
    public DeviceCommandDispatcher()
    {
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Enqueues a command and returns a task that completes with its result once the command has run. The <paramref name="cancellationToken"/> only cancels this command.
    /// <para>将指令入队并返回携带其结果的任务。<paramref name="cancellationToken"/> 仅取消该指令。</para></summary>
    /// <param name="name">Nonempty label identifying the command.<para>标识该指令的非空名称。</para></param>
    /// <param name="work">Command body, invoked once on the worker with a token tied to this command and the dispatcher lifetime.
    /// <para>指令主体，仅在工作任务上调用一次，接收同时绑定该指令与调度器生命周期的标记。</para></param>
    /// <param name="cancellationToken">Cancels only this command, never the queue.
    /// <para>仅取消该指令，绝不影响队列。</para></param>
    /// <returns>A task that completes with the command result, or faults or cancels with it.
    /// <para>随指令结果完成、或随之失败/取消的任务。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty, or whitespace.
    /// <para><paramref name="name"/> 为 null、空白或仅含空白字符。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> is null.
    /// <para><paramref name="work"/> 为 null。</para></exception>
    /// <exception cref="ObjectDisposedException">The dispatcher has been disposed, including when disposal wins a concurrent race.
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
        var stopwatch = Stopwatch.StartNew();

        async Task Run(CancellationToken token)
        {
            try
            {
                if (token.IsCancellationRequested)
                {
                    CommTrace.Debug("command-pool",
                        $"'{name}' cancelled while queued after {stopwatch.ElapsedMilliseconds} ms.");
                    completion.TrySetCanceled(token);
                    return;
                }

                CommTrace.Debug("command-pool", $"'{name}' started after {stopwatch.ElapsedMilliseconds} ms in the queue.");
                var result = await work(token).ConfigureAwait(false);
                CommTrace.Debug("command-pool",
                    $"'{name}' completed in {stopwatch.ElapsedMilliseconds} ms (result: {DescribeResult(result)}).");
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == token || token.IsCancellationRequested)
            {
                CommTrace.Debug("command-pool", $"'{name}' cancelled after {stopwatch.ElapsedMilliseconds} ms.");
                completion.TrySetCanceled(token);
            }
            catch (Exception ex)
            {
                CommTrace.Error("command-pool",
                    $"'{name}' failed after {stopwatch.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
                completion.TrySetException(ex);
            }
        }

        if (!_channel.Writer.TryWrite(new Invocation(Run, linked)))
        {
            linked.Dispose();
            throw new ObjectDisposedException(nameof(DeviceCommandDispatcher));
        }

        var pending = Interlocked.Increment(ref _pending);
        CommTrace.Debug("command-pool", $"queued '{name}' (depth {pending} waiting).");
        return completion.Task;
    }

    /// <summary>Enqueues a command that returns no value.<para>入队无返回值的指令。</para></summary>
    /// <param name="name">Nonempty label identifying the command.<para>标识该指令的非空名称。</para></param>
    /// <param name="work">Command body run on the worker.<para>在工作任务上执行的指令主体。</para></param>
    /// <param name="cancellationToken">Cancels only this command, never the queue.
    /// <para>仅取消该指令，绝不影响队列。</para></param>
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

        CommTrace.Debug("command-pool",
            $"Disposing; {Volatile.Read(ref _pending)} queued command(s) are cancelled and the running command is awaited.");
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
            Interlocked.Decrement(ref _pending);
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

    /// <summary>Renders one command result for the debug log without dumping arbitrary content.
    /// <para>为调试日志渲染指令结果，且不倾倒任意内容。</para>
    /// </summary>
    /// <param name="value">The value the command completed with. <para>指令完成时的值。</para></param>
    /// <returns>A short descriptor: payload lengths for text and arrays, otherwise the type name.
    /// <para>简短描述：文本与数组给出长度，其它给出类型名。</para></returns>
    private static string DescribeResult(object? value) => value switch
    {
        null => "null",
        string text => $"string({text.Length} chars)",
        Array array => $"{value.GetType().Name}({array.Length} items)",
        _ => value.GetType().Name,
    };
}