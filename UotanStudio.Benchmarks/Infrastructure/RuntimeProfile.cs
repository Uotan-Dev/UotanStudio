using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace UotanStudio.Benchmarks.Infrastructure;

/// <summary>
/// In-process runtime profiler: stop-the-world pause time, per-generation collection counts, allocated
/// bytes, per-type allocation samples, and the runtime's own CPU and GC-time counters, all captured while a
/// workload runs.
/// <para>进程内运行时剖析器：在工作负载运行期间采集停顿时间、逐代回收次数、已分配字节、按类型分解的分配采样，
/// 以及运行时自带的 CPU 与 GC 时间计数器。</para>
/// <para>BenchmarkDotNet's memory diagnoser already reports bytes-per-operation and collections-per-operation,
/// but it reports neither how long the runtime actually stalled the mutator nor what share of the wall clock
/// was spent inside the collector, and those two are what a memory-for-CPU trade-off has to be judged on.
/// This type closes that gap by subscribing to the runtime's own events rather than by sampling from outside.</para>
/// <para>BenchmarkDotNet 的内存诊断器已经报告每次操作的字节数与回收次数，但它既不报告运行时真正让应用线程停顿了
/// 多久，也不报告壁钟时间里有多大比例花在收集器内部，而这两项正是「用内存换 CPU」这类取舍必须被判断的部分。
/// 本类型通过订阅运行时自身的事件而不是从外部采样来补上这个缺口。</para>
/// </summary>
/// <remarks>
/// The subscription itself costs time, so absolute numbers from this recorder are not comparable to an
/// unprofiled run; only two runs taken with the same recorder are comparable with each other.
/// <para>订阅本身有开销，因此本记录器的绝对数值不可与未剖析的运行直接比较；只有同一记录器下的两次运行之间可比。</para>
/// </remarks>
public sealed class RuntimeProfile : EventListener
{
    private const EventKeywords GcKeywords = (EventKeywords)(-1);

    private readonly Lock _gate = new();
    private readonly TimeSpan _cpuAtStart = Process.GetCurrentProcess().TotalProcessorTime;
    private readonly long _allocatedAtStart = GC.GetTotalAllocatedBytes(precise: true);
    private readonly int _gen0AtStart = GC.CollectionCount(0);
    private readonly int _gen1AtStart = GC.CollectionCount(1);
    private readonly int _gen2AtStart = GC.CollectionCount(2);
    private readonly Dictionary<string, long> _allocatedBytesByType = new(StringComparer.Ordinal);
    private readonly Dictionary<ushort, PauseWindow> _openWindows = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _eventsSeen;
    private long _suspendEvents;
    private long _pauseNanos;
    private long _gcWorkNanos;
    private long _blockingCollections;
    private long _concurrentCollections;
    private long _allocationSampleCount;
    private bool _stopped;

    private RuntimeProfile()
    {
    }

    /// <summary>Starts capturing now; the recorder must be disposed once the workload is finished.
    /// <para>立即开始采集；工作负载结束后必须释放该记录器。</para></summary>
    /// <returns>A live recorder. <para>已启动的记录器。</para></returns>
    public static RuntimeProfile Start() => new();

    /// <summary>Stops capturing and folds everything observed into one report.
    /// <para>停止采集并把观察到的全部内容折算为一份报告。</para></summary>
    /// <returns>The profile of the window between <see cref="Start"/> and this call.
    /// <para><see cref="Start"/> 到本次调用之间这段时间窗口的剖析结果。</para></returns>
    public ProfileReport Stop()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                throw new InvalidOperationException("This profile has already been stopped.");
            }

            _stopped = true;
        }

        Dispose();
        var wall = _clock.Elapsed;
        var cpu = Process.GetCurrentProcess();
        cpu.Refresh();
        var cpuUsed = cpu.TotalProcessorTime - _cpuAtStart;
        lock (_gate)
        {
            return new ProfileReport(
                wall,
                cpuUsed,
                TimeSpan.FromTicks(_pauseNanos / 100),
                TimeSpan.FromTicks(_gcWorkNanos / 100),
                _suspendEvents,
                _blockingCollections,
                _concurrentCollections,
                GC.CollectionCount(0) - _gen0AtStart,
                GC.CollectionCount(1) - _gen1AtStart,
                GC.CollectionCount(2) - _gen2AtStart,
                GC.GetTotalAllocatedBytes(precise: true) - _allocatedAtStart,
                _allocationSampleCount,
                [.. _allocatedBytesByType.OrderByDescending(pair => pair.Value)
                    .Select(pair => (pair.Key, pair.Value))],
                _eventsSeen);
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name.Equals("Microsoft-Windows-DotNETRuntime", StringComparison.Ordinal))
        {
            EnableEvents(eventSource, EventLevel.Informational, GcKeywords);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        Interlocked.Increment(ref _eventsSeen);
        var eventName = e.EventName;
        if (eventName is null)
        {
            return;
        }

        switch (e.EventSource.Name)
        {
            case "Microsoft-Windows-DotNETRuntime" when
                eventName.Equals("AllocationSampled", StringComparison.Ordinal)
                || eventName.StartsWith("GC", StringComparison.Ordinal):
                RecordGcEvent(e, eventName);
                return;
            default:
                return;
        }
    }

    private void RecordGcEvent(EventWrittenEventArgs e, string eventName)
    {
        switch (eventName)
        {
            case "GCSuspendEEBegin_V1":
                OpenWindow(e).SuspensionStarted = true;
                return;
            case "GCStart_V2":
                {
                    var window = OpenWindow(e);
                    window.GcStarted = true;
                    window.Depth = PayloadUInt(e, "Depth");
                    window.IsConcurrent = PayloadUInt(e, "Type") != 0;
                    return;
                }

            case "GCRestartEEEnd_V1":
                CloseRestartWindow(e);
                return;
            case "GCEnd_V1":
                CloseGcWindow(e);
                return;
            case "AllocationSampled":
                RecordAllocation(e);
                return;
            default:
                return;
        }
    }

    private PauseWindow OpenWindow(EventWrittenEventArgs e)
    {
        var id = PayloadUInt16(e, "ClrInstanceID");
        lock (_gate)
        {
            if (_openWindows.TryGetValue(id, out var existing))
            {
                return existing;
            }

            return _openWindows[id] = new PauseWindow { StartedAt = e.TimeStamp };
        }
    }

    private void CloseRestartWindow(EventWrittenEventArgs e)
    {
        var id = PayloadUInt16(e, "ClrInstanceID");
        lock (_gate)
        {
            if (_openWindows.TryGetValue(id, out var window) && window.SuspensionStarted)
            {
                _pauseNanos += (e.TimeStamp - window.StartedAt).Ticks * 100;
                _suspendEvents++;
                _blockingCollections += window.IsConcurrent ? 0 : 1;
                _concurrentCollections += window.IsConcurrent ? 1 : 0;
                window.SuspensionStarted = false;
                if (!window.GcStarted)
                {
                    _openWindows.Remove(id);
                }
            }
        }
    }

    private void CloseGcWindow(EventWrittenEventArgs e)
    {
        var id = PayloadUInt16(e, "ClrInstanceID");
        lock (_gate)
        {
            if (_openWindows.TryGetValue(id, out var window) && window.GcStarted)
            {
                _gcWorkNanos += (e.TimeStamp - window.StartedAt).Ticks * 100;
                window.GcStarted = false;
                if (!window.SuspensionStarted)
                {
                    _openWindows.Remove(id);
                }
            }
        }
    }

    private void RecordAllocation(EventWrittenEventArgs e)
    {
        var size = PayloadInt64(e, "ObjectSize");
        var type = PayloadString(e, "TypeName");
        if (size <= 0 || string.IsNullOrEmpty(type))
        {
            return;
        }

        lock (_gate)
        {
            _allocatedBytesByType[type] = _allocatedBytesByType.TryGetValue(type, out var prior)
                ? prior + size
                : size;
            _allocationSampleCount += size;
        }
    }

    private static ushort PayloadUInt16(EventWrittenEventArgs e, string name)
        => (ushort)PayloadInt64(e, name);

    private static uint PayloadUInt(EventWrittenEventArgs e, string name)
        => (uint)PayloadInt64(e, name);

    private static string? PayloadString(EventWrittenEventArgs e, string name)
    {
        var index = IndexOf(e, name);
        return index < 0 ? null : e.Payload?[index] as string;
    }

    private static long PayloadInt64(EventWrittenEventArgs e, string name)
    {
        var index = IndexOf(e, name);
        if (index < 0 || e.Payload?[index] is not IConvertible value)
        {
            return 0;
        }

        return value.ToInt64(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int IndexOf(EventWrittenEventArgs e, string name)
    {
        var names = e.PayloadNames;
        if (names is null)
        {
            return -1;
        }

        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class PauseWindow
    {
        internal DateTime StartedAt;
        internal bool SuspensionStarted;
        internal bool GcStarted;
        internal bool IsConcurrent;
        internal uint Depth;
    }
}

/// <summary>One measured profile window.
/// <para>一次实测的时间窗口。</para></summary>
/// <param name="Wall">Time from recorder start to stop. <para>记录器从启动到停止的壁钟时间。</para></param>
/// <param name="Cpu">Process-wide CPU time consumed between start and stop, across all cores.
/// <para>启动与停止之间全进程在所有核心上消耗的 CPU 总时间。</para></param>
/// <param name="Pause">Total suspend-EE through restart-EE time, i.e. the mutator stall.
/// <para>从挂起 EE 到恢复 EE 的总时长，即应用线程被停住的总时间。</para></param>
/// <param name="GcWork">Total GCStart through GCEnd time, including concurrent phases.
/// <para>从 GCStart 到 GCEnd 的总时长，包含并发阶段。</para></param>
/// <param name="PauseCount">Number of completed suspension windows. <para>已完成的挂起窗口数量。</para></param>
/// <param name="BlockingCollections">Collections that ran without a concurrent phase.
/// <para>不含并发阶段的回收次数。</para></param>
/// <param name="ConcurrentCollections">Collections reported by the runtime as concurrent or incremental.
/// <para>运行时报告为并发或增量的回收次数。</para></param>
/// <param name="Gen0">Gen0 collections completed inside the window. <para>窗口内完成的 Gen0 回收次数。</para></param>
/// <param name="Gen1">Gen1 collections completed inside the window. <para>窗口内完成的 Gen1 回收次数。</para></param>
/// <param name="Gen2">Gen2 (full) collections completed inside the window.
/// <para>窗口内完成的 Gen2（完全）回收次数。</para></param>
/// <param name="AllocatedBytes">Bytes allocated inside the window. <para>窗口内分配的字节数。</para></param>
/// <param name="SampledBytes">Bytes attributed by allocation samples; an estimate, not a census.
/// <para>分配采样所归因的字节数；这是估计值，不是全量清点。</para></param>
/// <param name="TopAllocators">Sampled bytes per type, largest first. <para>按类型汇总的采样字节，从大到小。</para></param>
/// <param name="EventsSeen">Runtime events the recorder actually received; zero means the subscription did
/// not take effect and every other figure in this report is unmeasured.
/// <para>记录器实际收到的运行时事件数量；为零表示订阅并未生效，此时本报告中其他所有数值都不是实测值。</para></param>
public readonly record struct ProfileReport(
    TimeSpan Wall,
    TimeSpan Cpu,
    TimeSpan Pause,
    TimeSpan GcWork,
    long PauseCount,
    long BlockingCollections,
    long ConcurrentCollections,
    int Gen0,
    int Gen1,
    int Gen2,
    long AllocatedBytes,
    long SampledBytes,
    (string Type, long Bytes)[] TopAllocators,
    long EventsSeen);