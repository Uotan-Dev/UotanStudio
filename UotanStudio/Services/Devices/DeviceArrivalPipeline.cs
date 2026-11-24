using System.Collections.Concurrent;

namespace UotanStudio.Services.Devices;

/// <summary>
/// Source of one device-arrival signal that the pipeline classifies and handshakes.
/// <para>流水线要分类并握手的一条设备到达信号。</para>
/// </summary>
/// <param name="StableKey">Physical identity used to coalesce several arrival signals. <para>用于合并多条到达信号的物理身份。</para></param>
/// <param name="Protocol">Classified protocol, or <see langword="null"/> when classification must run first. <para>已分类的协议；需要先分类时为 <see langword="null"/>。</para></param>
/// <param name="Quality">Evidence quality; higher wins when signals race. <para>证据质量；信号竞争时数值高者胜出。</para></param>
/// <param name="NeedsHandshake">Whether this protocol must be handshaken to keep a transient port alive. <para>该协议是否需要握手以保活短暂端口。</para></param>
/// <param name="Classify">Optional pure classifier run once on the message thread. <para>可选纯分类器，在消息线程上运行一次。</para></param>
/// <param name="FallbackKey">Optional arrival path or port name used to park the lease. <para>可选的到达路径或端口名，用于暂存租约。</para></param>
public sealed record DeviceArrival(
    string StableKey,
    DeviceProtocolKind? Protocol,
    int Quality,
    bool NeedsHandshake,
    Func<DeviceProtocolKind?>? Classify = null,
    string? FallbackKey = null);

/// <summary>
/// Work the pipeline performs for one classified arrival: the bounded handshake and the best-effort detail pass.
/// <para>流水线对一次已分类到达执行的工作：有界握手与尽力而为的详情采集。</para>
/// </summary>
/// <param name="HandshakeAsync">Stabilising handshake; its completion ends the 3s critical path. <para>稳定端口的握手；其完成即结束 3s 关键路径。</para></param>
/// <param name="CollectDetailsAsync">Optional detail pass over the already-handshaken session. <para>在已握手会话上进行的可选详情采集。</para></param>
public sealed record DeviceArrivalWork(
    Func<CancellationToken, Task> HandshakeAsync,
    Func<CancellationToken, Task>? CollectDetailsAsync = null);

/// <summary>
/// Immutable outcome of one arrival operation, used by diagnostics and the real-device probe.
/// <para>一次到达操作不可变的结果，供诊断与真机探针使用。</para>
/// </summary>
/// <param name="StableKey">Stable identity of the arrival. <para>到达设备的稳定身份。</para></param>
/// <param name="Protocol">Protocol the pipeline settled on. <para>流水线最终确定的协议。</para></param>
/// <param name="HandshakeCompleted">Whether the handshake finished before the hard deadline. <para>握手是否在硬截止前完成。</para></param>
/// <param name="HandshakeMilliseconds">Milliseconds from submission to handshake completion. <para>从提交到握手完成的毫秒数。</para></param>
/// <param name="DetailsCompleted">Whether the optional detail pass finished. <para>可选详情采集是否完成。</para></param>
/// <param name="Reason">Localization-neutral explanation of a skip or failure. <para>说明跳过或失败原因的、与本地化无关的文本。</para></param>
public sealed record DeviceArrivalResult(
    string StableKey,
    DeviceProtocolKind? Protocol,
    bool HandshakeCompleted,
    long HandshakeMilliseconds,
    bool DetailsCompleted,
    string Reason);

/// <summary>
/// Coordinates device-arrival classification, bounded handshake and best-effort detail collection so a
/// short-lived port is claimed before it disappears. All device I/O is injected, keeping the ordering,
/// de-duplication and budget logic independently testable.
/// <para>协调设备到达的分类、有界握手与尽力详情采集，使短暂端口在消失前被占用。
/// 全部设备 I/O 均由外部注入，从而可独立测试顺序、去重与预算逻辑。</para>
/// </summary>
public sealed class DeviceArrivalPipeline : IDisposable
{
    /// <summary>End-to-end budget for classification plus handshake. <para>分类加握手的端到端预算。</para></summary>
    public static readonly TimeSpan HandshakeBudget = TimeSpan.FromMilliseconds(3000);

    /// <summary>Reserve part of the budget for classification and scheduling. <para>为分类与调度预留的部分预算。</para></summary>
    internal static readonly TimeSpan HandshakeWorkSlice = TimeSpan.FromMilliseconds(2800);

    private sealed class Operation
    {
        public required string Key { get; init; }
        public required DeviceArrival Arrival { get; set; }
        public required TaskCompletionSource<DeviceArrivalResult> Completion { get; init; }
        public required CancellationTokenSource Budget { get; init; }
        public CancellationTokenSource? DetailsBudget { get; set; }
        public int DetailsCompleted;
        public int Quality => Arrival.Quality;
    }

    private readonly ConcurrentDictionary<string, Operation> _operations = new(StringComparer.Ordinal);
    private readonly Func<DeviceArrival, DeviceArrivalWork?> _workFactory;
    private readonly Func<DeviceArrivalResult, Task> _onResult;
    private readonly Action<Exception> _onFault;
    private int _disposed;

    /// <summary>
    /// Creates a pipeline with injected device work and result sinks.
    /// <para>用注入的设备工作与结果接收器创建流水线。</para>
    /// </summary>
    /// <param name="workFactory">Builds the handshake/detail delegates for a classified arrival; <see langword="null"/> declines it. <para>为已分类到达构建握手/详情委托；返回 <see langword="null"/> 表示不处理。</para></param>
    /// <param name="onResult">Receives each completed result. <para>接收每一项完成结果。</para></param>
    /// <param name="onFault">Receives unexpected pipeline faults. <para>接收流水线内部的意外故障。</para></param>
    public DeviceArrivalPipeline(
        Func<DeviceArrival, DeviceArrivalWork?> workFactory,
        Func<DeviceArrivalResult, Task>? onResult = null,
        Action<Exception>? onFault = null)
    {
        ArgumentNullException.ThrowIfNull(workFactory);
        _workFactory = workFactory;
        _onResult = onResult ?? (_ => Task.CompletedTask);
        _onFault = onFault ?? (_ => { });
    }

    /// <summary>
    /// Submits one arrival signal. Higher-quality evidence for the same physical key supersedes a running
    /// lower-quality operation; duplicate lower-quality signals are ignored. The call itself never performs
    /// device I/O and always returns immediately.
    /// <para>提交一条到达信号。同一物理身份下更高质量的证据会抢占正在运行的低质量操作；重复的低质量信号被忽略。
    /// 本调用本身从不执行设备 I/O，始终立即返回。</para>
    /// </summary>
    /// <param name="arrival">The arrival signal. <para>到达信号。</para></param>
    /// <returns>A task completing with the result once the handshake (and any detail pass) settles.
    /// <para>握手（及可选详情采集）结束后以结果完成的任务。</para></returns>
    public Task<DeviceArrivalResult> Submit(DeviceArrival arrival)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        if (string.IsNullOrWhiteSpace(arrival.StableKey))
            throw new ArgumentException("A stable key is required.", nameof(arrival));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        DeviceProtocolKind? protocol = arrival.Protocol;
        if (protocol is null && arrival.Classify is { } classify)
        {
            // Pure descriptor work on the caller's thread: no device I/O, no wait.
            try
            {
                protocol = classify();
            }
            catch (Exception ex)
            {
                _onFault(ex);
            }
        }

        var classified = arrival with { Protocol = protocol };
        while (true)
        {
            if (_operations.TryGetValue(classified.StableKey, out var existing))
            {
                if (classified.Quality <= existing.Quality)
                    return existing.Completion.Task;

                var replaced = arrival with { Protocol = protocol };
                var successor = CreateOperation(replaced);
                if (!_operations.TryUpdate(classified.StableKey, successor, existing))
                    continue;

                CancelQuietly(existing.DetailsBudget);
                CancelQuietly(existing.Budget);
                existing.Completion.TrySetResult(new DeviceArrivalResult(
                    existing.Key, existing.Arrival.Protocol, false, 0, false,
                    "superseded by higher-quality evidence"));
                _ = RunAsync(successor);
                return successor.Completion.Task;
            }

            var created = CreateOperation(classified);
            if (!_operations.TryAdd(classified.StableKey, created))
                continue;

            _ = RunAsync(created);
            return created.Completion.Task;
        }
    }

    /// <summary>
    /// Reports whether an operation is still active for the stable key.
    /// <para>报告该稳定身份是否仍有进行中的操作。</para>
    /// </summary>
    /// <param name="stableKey">The identity to query. <para>要查询的身份。</para></param>
    public bool IsActive(string stableKey)
        => stableKey is not null && _operations.ContainsKey(stableKey);

    private Operation? OperationFor(string key)
        => _operations.TryGetValue(key, out var operation) ? operation : null;

    private Operation CreateOperation(DeviceArrival arrival) => new()
    {
        Key = arrival.StableKey,
        Arrival = arrival,
        Completion = new TaskCompletionSource<DeviceArrivalResult>(TaskCreationOptions.RunContinuationsAsynchronously),
        Budget = new CancellationTokenSource(HandshakeBudget),
    };

    private async Task RunAsync(Operation operation)
    {
        var started = Environment.TickCount64;
        var handshakeCompleted = false;
        long handshakeMilliseconds = 0;
        var reason = string.Empty;
        try
        {
            var work = _workFactory(operation.Arrival);
            if (work is null)
            {
                reason = "no work for this arrival";
            }
            else
            {
                using var handshakeBudget = CancellationTokenSource.CreateLinkedTokenSource(
                    operation.Budget.Token, CancellationToken.None);
                handshakeBudget.CancelAfter(HandshakeWorkSlice);
                try
                {
                    await work.HandshakeAsync(handshakeBudget.Token).ConfigureAwait(false);
                    handshakeCompleted = true;
                    handshakeMilliseconds = Environment.TickCount64 - started;
                }
                catch (OperationCanceledException) when (operation.Budget.IsCancellationRequested)
                {
                    reason = "superseded or disposed";
                }
                catch (Exception ex)
                {
                    reason = "handshake failed: " + ex.GetType().Name;
                    _onFault(ex);
                }

                if (handshakeCompleted && work.CollectDetailsAsync is not null)
                {
                    var detailsCts = new CancellationTokenSource();
                    operation.DetailsBudget = detailsCts;
                    _ = CollectDetailsAsync(operation, work.CollectDetailsAsync, detailsCts);
                }
            }
        }
        catch (Exception ex)
        {
            reason = "pipeline fault: " + ex.GetType().Name;
            _onFault(ex);
        }
        finally
        {
            if (!operation.Completion.Task.IsCompleted)
            {
                var result = new DeviceArrivalResult(operation.Key, operation.Arrival.Protocol,
                    handshakeCompleted, handshakeMilliseconds, (Volatile.Read(ref operation.DetailsCompleted) != 0),
                    reason);
                operation.Completion.TrySetResult(result);
                _ = NotifyAsync(operation, result);
            }
            operation.Budget.Dispose();
        }
    }

    private async Task CollectDetailsAsync(Operation operation, Func<CancellationToken, Task> collect,
        CancellationTokenSource detailsCts)
    {
        try
        {
            // Details never hold the 3s critical path: they run detached and share only the already-handshaken
            // session. The hard budget still bounds them so a silent device cannot keep this operation alive.
            detailsCts.CancelAfter(HandshakeBudget);
            await collect(detailsCts.Token).ConfigureAwait(false);
            Volatile.Write(ref operation.DetailsCompleted, 1);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _onFault(ex);
        }
        finally
        {
            detailsCts.Dispose();
            if (ReferenceEquals(operation.DetailsBudget, detailsCts))
                operation.DetailsBudget = null;
        }
    }

    internal static void CancelQuietly(CancellationTokenSource? source)
    {
        if (source is null)
            return;
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    private async Task NotifyAsync(Operation operation, DeviceArrivalResult result)
    {
        try
        {
            await _onResult(result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _onFault(ex);
        }
        finally
        {
            // A higher-quality arrival may have replaced this operation while its result was being delivered.
            ((ICollection<KeyValuePair<string, Operation>>)_operations)
                .Remove(new KeyValuePair<string, Operation>(operation.Key, operation));
        }
    }

    /// <summary>Disposes every budget and declines further submissions; in-flight operations are cancelled.
    /// <para>释放所有预算并拒绝后续提交；进行中的操作被取消。</para></summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (var operation in _operations.Values)
        {
            CancelQuietly(operation.DetailsBudget);
            CancelQuietly(operation.Budget);
            operation.Completion.TrySetResult(new DeviceArrivalResult(
                operation.Key, operation.Arrival.Protocol, false, 0, false, "pipeline was disposed"));
        }
        _operations.Clear();
    }
}