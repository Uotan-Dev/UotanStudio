using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace UotanStudio.Compress.Internal;

/// <summary>
/// Physical core count of the host, used as the ceiling for every parallel compression entry point.
/// <para>宿主机物理核心数，用作所有并行压缩入口的上限。</para>
/// </summary>
/// <remarks>
/// The count is detected once from the operating system because <see cref="Environment.ProcessorCount"/> reports
/// logical processors, and running one compression worker per hyperthread oversubscribes memory-heavy codecs.
/// Detection failure falls back to the logical count, and the result is clamped to <c>1..64</c> so the caller's
/// per-worker buffers stay bounded.
/// <para>该计数在操作系统层面只探测一次，因为 <see cref="Environment.ProcessorCount"/> 报告的是逻辑处理器，
/// 按超线程数量启动压缩工作者会让内存密集的编解码器超额占用。探测失败时退回逻辑数量，并把结果钳制到
/// <c>1..64</c>，使调用方的每工作者缓冲区保持有界。</para>
/// </remarks>
internal static partial class PhysicalCoreCount
{
    private const int MaximumCount = 64;

    private static readonly int Detected = Detect();

    /// <summary>
    /// Gets the detected physical core count, already clamped to the supported range.
    /// <para>获取探测到的物理核心数，已钳制到受支持区间。</para>
    /// </summary>
    /// <value>A value between <c>1</c> and <c>64</c>.
    /// <para>介于 <c>1</c> 与 <c>64</c> 之间的值。</para></value>
    internal static int Value => Detected;

    private static int Detect()
    {
        try
        {
            var count = OperatingSystem.IsWindows()
                ? CountWindowsCores()
                : OperatingSystem.IsLinux()
                    ? CountLinuxCores()
                    : OperatingSystem.IsMacOS()
                        ? CountMacOsCores()
                        : 0;
            return Math.Clamp(count > 0 ? count : Environment.ProcessorCount, 1, MaximumCount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return Math.Clamp(Environment.ProcessorCount, 1, MaximumCount);
        }
    }

    private static unsafe int CountWindowsCores()
    {
        const int RelationProcessorCore = 0;
        uint length = 0;
        _ = GetLogicalProcessorInformationEx(RelationProcessorCore, nint.Zero, ref length);
        if (length == 0)
        {
            return 0;
        }

        var buffer = new byte[length];
        fixed (byte* pointer = buffer)
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, (nint)pointer, ref length))
            {
                return 0;
            }
        }

        var count = 0;
        var offset = 0;
        while (length - offset >= 8)
        {
            var relationship = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset));
            var recordLength = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset + 4));
            if (recordLength < 8 || recordLength > length - offset)
            {
                return 0;
            }

            if (relationship == RelationProcessorCore)
            {
                count++;
            }

            offset += recordLength;
        }

        return offset == length ? count : 0;
    }

    private static int CountLinuxCores()
    {
        const string CpuInfoPath = "/proc/cpuinfo";
        if (!File.Exists(CpuInfoPath))
        {
            return 0;
        }

        var cores = new HashSet<string>(StringComparer.Ordinal);
        string? packageId = null;
        string? coreId = null;
        foreach (var line in File.ReadLines(CpuInfoPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (packageId is not null && coreId is not null)
                {
                    cores.Add($"{packageId}:{coreId}");
                }

                packageId = null;
                coreId = null;
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Equals("physical id", StringComparison.Ordinal))
            {
                packageId = value;
            }
            else if (key.Equals("core id", StringComparison.Ordinal))
            {
                coreId = value;
            }
        }

        if (packageId is not null && coreId is not null)
        {
            cores.Add($"{packageId}:{coreId}");
        }

        return cores.Count;
    }

    private static int CountMacOsCores()
    {
        nuint length = sizeof(int);
        return SysctlByName("hw.physicalcpu", out var cores, ref length, nint.Zero, 0) == 0 && length == sizeof(int)
            ? cores
            : 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationship, nint buffer, ref uint returnedLength);

    [LibraryImport("libc", EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SysctlByName(string name, out int value, ref nuint length, nint newValue, nuint newValueLength);
}