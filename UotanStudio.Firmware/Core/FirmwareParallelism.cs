using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace UotanStudio.Firmware.Core;

/// <summary>Bounded default concurrency for firmware operations. Each operation snapshots its limit.
/// <para>固件操作的有界默认并发度；每个操作会快照自己所用的上限。</para></summary>
public static partial class FirmwareParallelism
{
    /// <summary>Default number of concurrent workers, based on the detected physical core count.
    /// <para>默认并发工作者数量，依据检测到的物理核心数确定。</para></summary>
    public static readonly int DefaultWorkers = NormalizeWorkerCount(DetectPhysicalCoreCount());
    /// <summary>Upper limit, keeping per-worker codec and I/O buffers bounded.
    /// <para>并发上限，用于约束每个工作者的编解码与 I/O 缓冲开销。</para></summary>
    public const int MaximumWorkers = 64;
    private static int _workers = DefaultWorkers;

    /// <summary>Maximum concurrency for new operations; changing it does not resize running batches.
    /// <para>新操作的最大并发度；修改它不会改变已在运行的批次。</para></summary>
    /// <value>
    /// A worker count between <c>1</c> and <see cref="MaximumWorkers"/>, read with volatile semantics.
    /// <para>介于 <c>1</c> 与 <see cref="MaximumWorkers"/> 之间的工作者数量，按 volatile 语义读取。</para>
    /// </value>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is below <c>1</c> or above <see cref="MaximumWorkers"/>.
    /// <para>赋值小于 <c>1</c> 或大于 <see cref="MaximumWorkers"/>。</para></exception>
    public static int Workers
    {
        get => Volatile.Read(ref _workers);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaximumWorkers);
            Volatile.Write(ref _workers, value);
        }
    }

    /// <summary>Aggregate memory budget shared by every worker of one operation, as passed to
    /// <see cref="BudgetedWorkers"/>'s <c>budgetBytes</c> parameter by the payload builder and the F2FS parser.
    /// A single work item whose worst case reaches this value therefore reduces the worker count to one.
    /// <para>单次操作中所有工作者共享的内存预算总量，由 payload 构建器与 F2FS 解析器作为
    /// <see cref="BudgetedWorkers"/> 的 <c>budgetBytes</c> 参数传入。因此当单个工作项的最坏情况达到该值时，
    /// 工作者数量会降为 1。</para></summary>
    internal const long WorkerMemoryBudgetBytes = 128L * 1024 * 1024;

    internal static void ForEach<T>(IReadOnlyList<T> items, Action<T> action, int? workers = null)
        => ForEach(items, (item, _) => action(item), workers);

    /// <summary>Runs one action per item with its index, bounded by <see cref="Workers"/>.
    /// <para>按不超过 <see cref="Workers"/> 的并发度，为每个条目连同其索引执行一次操作。</para></summary>
    /// <remarks>
    /// An exception thrown by the body surfaces as its first occurrence in the flattened aggregate, with its
    /// original type and stack, matching the single-parameter overload.
    /// <para>操作体抛出的异常以扁平化聚合中的首次出现为准，保留原始类型与堆栈，与单参数重载一致。</para>
    /// </remarks>
    /// <param name="items">Items visited exactly once; the index passed to the action is the item's position.
    /// <para>只访问一次的条目集合；传给操作的索引即条目在集合中的位置。</para></param>
    /// <param name="action">Action receiving the item and its index. <para>接收条目及其索引的操作。</para></param>
    /// <param name="workers">Optional worker count override. <para>可选的工作者数量覆盖值。</para></param>
    internal static void ForEach<T>(IReadOnlyList<T> items, Action<T, int> action, int? workers = null)
    {
        var degree = Math.Min(items.Count, workers ?? Workers);
        if (degree <= 1)
        {
            for (var i = 0; i < items.Count; i++)
            {
                action(items[i], i);
            }
            return;
        }
        try
        {
            Parallel.For(0, items.Count, new ParallelOptions { MaxDegreeOfParallelism = degree }, i => action(items[i], i));
        }
        catch (AggregateException ex)
        {
            ExceptionDispatchInfo.Capture(ex.Flatten().InnerExceptions[0]).Throw();
            throw;
        }
    }

    /// <summary>Runs one action per item with dynamic work stealing, bounded by <see cref="Workers"/>.
    /// <para>以动态窃取工作项的方式为每个条目执行一次操作，并发度不超过 <see cref="Workers"/>。</para></summary>
    /// <remarks>
    /// Unlike <see cref="ForEach{T}(IReadOnlyList{T}, Action{T}, int?)"/>, this overload does not use the range
    /// partitioner, so a handful of large items clustered at one end of the list cannot leave the other workers
    /// idle. Items are still visited exactly once, and the first flattened exception preserves its type and stack.
    /// <para>与按范围分区的重载不同，本重载不使用范围分区器，因此集中在一端的大条目不会让其余工作者空闲。
    /// 每个条目仍只访问一次，首个扁平化异常保留类型与堆栈。</para>
    /// </remarks>
    /// <param name="items">Items visited exactly once. <para>只访问一次的条目集合。</para></param>
    /// <param name="action">Action receiving one item. <para>接收单个条目的操作。</para></param>
    /// <param name="workers">Optional worker count override. <para>可选的工作者数量覆盖值。</para></param>
    internal static void ForEachBalanced<T>(IReadOnlyList<T> items, Action<T> action, int? workers = null)
    {
        var degree = Math.Min(items.Count, workers ?? Workers);
        if (degree <= 1)
        {
            for (var i = 0; i < items.Count; i++)
            {
                action(items[i]);
            }
            return;
        }

        try
        {
            var partitions = Partitioner.Create(items, EnumerablePartitionerOptions.NoBuffering);
            Parallel.ForEach(partitions, new ParallelOptions { MaxDegreeOfParallelism = degree }, action);
        }
        catch (AggregateException ex)
        {
            ExceptionDispatchInfo.Capture(ex.Flatten().InnerExceptions[0]).Throw();
            throw;
        }
    }

    /// <summary>Projects every item into an array slot addressed by its own index, bounded by <see cref="Workers"/>.
    /// <para>按不超过 <see cref="Workers"/> 的并发度把每个条目投影到其自身索引对应的数组槽位。</para></summary>
    /// <param name="items">Items projected exactly once. <para>只投影一次的条目集合。</para></param>
    /// <param name="selector">Projection receiving the item and its index; results land at that index.
    /// <para>接收条目及其索引的投影函数；结果写入该索引对应的槽位。</para></param>
    /// <param name="workers">Optional worker count override. <para>可选的工作者数量覆盖值。</para></param>
    /// <returns>An array whose element at each index is that index's projection, in item order.
    /// <para>数组，其每个索引位置的元素即该索引条目的投影，顺序与条目集合一致。</para></returns>
    internal static TOut[] SelectArray<TIn, TOut>(IReadOnlyList<TIn> items, Func<TIn, int, TOut> selector, int? workers = null)
    {
        var results = new TOut[items.Count];
        ForEach(items, (item, index) => results[index] = selector(item, index), workers);
        return results;
    }

    /// <summary>
    /// Caps a requested worker count so the per-worker buffers fit one aggregate memory budget, then
    /// clamps it to <c>1</c>..<see cref="MaximumWorkers"/>.
    /// <para>按每工作者缓冲总量不超过单一内存预算来限制请求的工作者数量，并把它钳制到
    /// <c>1</c>..<see cref="MaximumWorkers"/>。</para>
    /// </summary>
    /// <param name="bytesPerWorker">Worst-case bytes held by one worker; must be at least <c>1</c>.
    /// <para>单个工作者最坏情况下占用的字节数，至少为 <c>1</c>。</para></param>
    /// <param name="budgetBytes">Total bytes all workers may hold together; must be at least <c>1</c>.
    /// <para>所有工作者合计可占用的字节上限，至少为 <c>1</c>。</para></param>
    /// <param name="requestedWorkers">Worker count the caller would like to use. <para>调用方期望使用的工作者数量。</para></param>
    /// <returns>The worker count to run with, never below <c>1</c> and never above <see cref="MaximumWorkers"/>.
    /// When <paramref name="bytesPerWorker"/> is at least <paramref name="budgetBytes"/> the budget affords no
    /// parallelism value above one, so the result is <c>1</c> without an error or diagnostic.
    /// <para>实际使用的工作者数量，不小于 <c>1</c>，也不大于 <see cref="MaximumWorkers"/>。
    /// 当 <paramref name="bytesPerWorker"/> 不小于 <paramref name="budgetBytes"/> 时，预算不支持大于一的分工，
    /// 结果静默为 <c>1</c>，不报错也不发出诊断。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bytesPerWorker"/> or <paramref name="budgetBytes"/> is below <c>1</c>.
    /// <para><paramref name="bytesPerWorker"/> 或 <paramref name="budgetBytes"/> 小于 <c>1</c>。</para>
    /// </exception>
    internal static int BudgetedWorkers(long bytesPerWorker, long budgetBytes, int requestedWorkers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytesPerWorker, 1L);
        ArgumentOutOfRangeException.ThrowIfLessThan(budgetBytes, 1L);
        var affordable = (int)Math.Clamp(budgetBytes / bytesPerWorker, 1L, MaximumWorkers);
        return Math.Clamp(Math.Min(Math.Max(requestedWorkers, 1), affordable), 1, MaximumWorkers);
    }

    internal static int NormalizeWorkerCount(int workerCount)
        => Math.Clamp(workerCount, 1, MaximumWorkers);

    internal static int DetectPhysicalCoreCount()
    {
        try
        {
            var physicalCores = OperatingSystem.IsWindows()
                ? CountWindowsPhysicalCores()
                : OperatingSystem.IsLinux()
                    ? CountLinuxPhysicalCores()
                    : OperatingSystem.IsMacOS()
                        ? CountMacOsPhysicalCores()
                        : 0;
            return physicalCores > 0 ? physicalCores : Environment.ProcessorCount;
        }
        catch
        {
            return Environment.ProcessorCount;
        }
    }

    private static int CountWindowsPhysicalCores()
    {
        uint length = 0;
        _ = GetLogicalProcessorInformationEx(LogicalProcessorRelationship.ProcessorCore, nint.Zero, ref length);
        if (length == 0) return 0;

        var buffer = new byte[length];
        unsafe
        {
            fixed (byte* pointer = buffer)
            {
                if (!GetLogicalProcessorInformationEx(LogicalProcessorRelationship.ProcessorCore, (nint)pointer, ref length))
                    return 0;
            }
        }

        var count = 0;
        var offset = 0;
        while (offset < length)
        {
            if (length - offset < 8) return 0;
            var relationship = (LogicalProcessorRelationship)BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
            var recordLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset + 4));
            if (recordLength < 8 || recordLength > length - offset) return 0;
            if (relationship == LogicalProcessorRelationship.ProcessorCore) count++;
            offset += recordLength;
        }

        return offset == length ? count : 0;
    }

    private static int CountLinuxPhysicalCores()
    {
        const string cpuInfoPath = "/proc/cpuinfo";
        if (!File.Exists(cpuInfoPath)) return 0;

        var cores = new HashSet<string>(StringComparer.Ordinal);
        string? physicalId = null;
        string? coreId = null;
        foreach (var line in File.ReadLines(cpuInfoPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (physicalId is not null && coreId is not null) cores.Add($"{physicalId}:{coreId}");
                physicalId = null;
                coreId = null;
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator < 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Equals("physical id", StringComparison.Ordinal)) physicalId = value;
            else if (key.Equals("core id", StringComparison.Ordinal)) coreId = value;
        }

        if (physicalId is not null && coreId is not null) cores.Add($"{physicalId}:{coreId}");
        return cores.Count;
    }

    private static int CountMacOsPhysicalCores()
    {
        nuint length = sizeof(int);
        return SysctlByName("hw.physicalcpu", out var cores, ref length, nint.Zero, 0) == 0 && length == sizeof(int)
            ? cores
            : 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(LogicalProcessorRelationship relationship, nint buffer, ref uint returnedLength);

    [LibraryImport("libc", EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SysctlByName(string name, out int value, ref nuint length, nint newValue, nuint newValueLength);

    private enum LogicalProcessorRelationship
    {
        ProcessorCore
    }
}