using System.Runtime.InteropServices;
using System.Text;

namespace UotanStudio.Hash.Simd;

/// <summary>
/// Reads the LoongArch64 and RiscV64 processor feature flags that gate the native vector kernels.
/// <para>读取用于门控原生向量内核的 LoongArch64 与 RiscV64 处理器特性标志。</para>
/// </summary>
/// <remarks>
/// Detection reads <c>getauxval(AT_HWCAP)</c> through libc and falls back to parsing <c>/proc/cpuinfo</c> when the
/// helper or the libc export is unavailable. On LoongArch64 the hardware-capability word reports LSX at bit 4 and
/// LASX at bit 5; on RiscV64 the letter-coded bit for <c>V</c> (bit 21) reports the vector extension. The RISC-V
/// vector register width is not read from a table: the caller measures it by executing an emitted
/// <c>vsetvli</c> probe through <see cref="NativeVectorKernels"/>, so the value always reflects the running hart.
/// All methods return <see langword="false"/> on other architectures.
/// <para>检测经 libc 读取 <c>getauxval(AT_HWCAP)</c>，辅助函数或 libc 导出不可用时回退解析 <c>/proc/cpuinfo</c>。
/// LoongArch64 的 hwcap 字在第 4 位报告 LSX、第 5 位报告 LASX；RiscV64 使用字母编码的 <c>V</c> 位（第 21 位）
/// 报告向量扩展。RISC-V 向量寄存器宽度不查表：调用方经 <see cref="NativeVectorKernels"/> 执行发射的
/// <c>vsetvli</c> 探针实测，取值始终反映当前运行的核心。其余架构上所有方法返回 <see langword="false"/>。</para>
/// </remarks>
internal static class NativeCpuFeatures
{
    private const ulong AuxiliaryHardwareCapabilities = 16;
    private const ulong LoongArchLsxBit = 1UL << 4;
    private const ulong LoongArchLasxBit = 1UL << 5;
    private const ulong RiscVVectorBit = 1UL << ('V' - 'A');

    [DllImport("libc", SetLastError = false)]
    private static extern ulong getauxval(ulong type);

    /// <summary>
    /// Reports whether the LoongArch64 host advertises LSX and LASX.
    /// <para>报告 LoongArch64 主机是否宣告了 LSX 与 LASX。</para>
    /// </summary>
    /// <param name="lsx">Receives whether LSX is advertised.<para>接收 LSX 是否宣告。</para></param>
    /// <param name="lasx">Receives whether LASX is advertised.<para>接收 LASX 是否宣告。</para></param>
    /// <returns><see langword="true"/> when the flags could be read on a LoongArch64 processor.
    /// <para>在 LoongArch64 处理器上成功读取标志时为 <see langword="true"/>。</para></returns>
    internal static bool TryGetLoongArchFeatures(out bool lsx, out bool lasx)
    {
        lsx = false;
        lasx = false;
        if (RuntimeInformation.ProcessArchitecture != Architecture.LoongArch64)
        {
            return false;
        }

        if (TryReadHardwareCapabilities(out var capabilities))
        {
            lsx = (capabilities & LoongArchLsxBit) != 0;
            lasx = (capabilities & LoongArchLasxBit) != 0;
            return true;
        }

        return TryReadLoongArchCpuInfo(out lsx, out lasx);
    }

    /// <summary>
    /// Reports whether the RiscV64 host advertises the vector extension.
    /// <para>报告 RiscV64 主机是否宣告了向量扩展。</para>
    /// </summary>
    /// <param name="vector">Receives whether the <c>V</c> extension is advertised.
    /// <para>接收 <c>V</c> 扩展是否宣告。</para></param>
    /// <returns><see langword="true"/> when the flags could be read on a RiscV64 processor.
    /// <para>在 RiscV64 处理器上成功读取标志时为 <see langword="true"/>。</para></returns>
    internal static bool TryGetRiscVVectorFlag(out bool vector)
    {
        vector = false;
        if (RuntimeInformation.ProcessArchitecture != Architecture.RiscV64)
        {
            return false;
        }

        if (TryReadHardwareCapabilities(out var capabilities))
        {
            vector = (capabilities & RiscVVectorBit) != 0;
            return true;
        }

        return TryReadRiscVCpuInfo(out vector);
    }

    private static bool TryReadHardwareCapabilities(out ulong capabilities)
    {
        capabilities = 0;
        try
        {
            capabilities = getauxval(AuxiliaryHardwareCapabilities);
            return capabilities != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryReadLoongArchCpuInfo(out bool lsx, out bool lasx)
    {
        lsx = false;
        lasx = false;
        var cpuInfo = ReadCpuInfo();
        if (cpuInfo is null)
        {
            return false;
        }

        foreach (var line in cpuInfo.Split('\n'))
        {
            if (!line.StartsWith("Features", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var token in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Equals("lsx", StringComparison.Ordinal))
                {
                    lsx = true;
                }
                else if (token.Equals("lasx", StringComparison.Ordinal))
                {
                    lasx = true;
                }
            }

            return lsx || lasx;
        }

        return false;
    }

    private static bool TryReadRiscVCpuInfo(out bool vector)
    {
        vector = false;
        var cpuInfo = ReadCpuInfo();
        if (cpuInfo is null)
        {
            return false;
        }

        foreach (var line in cpuInfo.Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator < 0 || !line.AsSpan(0, separator).Trim().Equals("isa".AsSpan(), StringComparison.Ordinal))
            {
                continue;
            }

            var isa = line[(separator + 1)..].Trim();
            // Base ISA letters sit between the "rv64" prefix and the first "_" extension separator; the vector
            // extension is present when the letter "v" appears among them.
            var underscore = isa.IndexOf('_');
            var baseLetters = isa.AsSpan(4, (underscore < 0 ? isa.Length : underscore) - 4);
            vector = baseLetters.IndexOf('v') >= 0;
            return true;
        }

        return false;
    }

    private static string? ReadCpuInfo()
    {
        try
        {
            if (!OperatingSystem.IsLinux() || !File.Exists("/proc/cpuinfo"))
            {
                return null;
            }

            return File.ReadAllText("/proc/cpuinfo", Encoding.UTF8);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}