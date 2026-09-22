using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace UotanStudio.Firmware.SharedKernel;

/// <summary>Bounded default concurrency for firmware operations. Each operation snapshots its limit.</summary>
public static partial class FirmwareParallelism
{
    /// <summary>Default number of concurrent workers, based on the detected physical core count.</summary>
    public static readonly int DefaultWorkers = NormalizeWorkerCount(DetectPhysicalCoreCount());
    /// <summary>Upper limit, keeping per-worker codec and I/O buffers bounded.</summary>
    public const int MaximumWorkers = 64;
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
            ExceptionDispatchInfo.Capture(ex.Flatten().InnerExceptions[0]).Throw();
            throw;
        }
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
            // CPU topology is optional platform information; logical processors remain a safe fallback.
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
            var relationship = (LogicalProcessorRelationship)BitConverter.ToInt32(buffer, offset);
            var recordLength = BitConverter.ToInt32(buffer, offset + 4);
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