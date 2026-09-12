using System.Runtime.ExceptionServices;

namespace UotanStudio.Firmware.SharedKernel;

/// <summary>Bounded default concurrency for firmware operations. Each operation snapshots its limit.</summary>
public static class FirmwareParallelism
{
    /// <summary>Default number of concurrent workers.</summary>
    public const int DefaultWorkers = 4;
    /// <summary>Upper limit, keeping per-worker codec and I/O buffers bounded.</summary>
    public const int MaximumWorkers = 8;
    private static int _workers = DefaultWorkers;

    /// <summary>Maximum concurrency for new operations; changing it does not resize running batches.</summary>
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

    internal static void ForEach<T>(IReadOnlyList<T> items, Action<T> action, int? workers = null)
    {
        var degree = Math.Min(items.Count, workers ?? Workers);
        if (degree <= 1)
        {
            foreach (var item in items) action(item);
            return;
        }
        try
        {
            Parallel.For(0, items.Count, new ParallelOptions { MaxDegreeOfParallelism = degree }, i => action(items[i]));
        }
        catch (AggregateException ex)
        {
            // All workers have stopped; preserve the original format/I/O exception contract.
            ExceptionDispatchInfo.Capture(ex.Flatten().InnerExceptions[0]).Throw();
            throw;
        }
    }
}