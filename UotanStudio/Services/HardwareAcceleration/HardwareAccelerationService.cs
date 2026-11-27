using System.Runtime.InteropServices;
using UotanStudio.Hash.Simd;

namespace UotanStudio.Services.HardwareAcceleration;

/// <summary>
/// One selectable byte-block dispatch target of the running machine: an instruction family this processor can execute,
/// or the scalar software loop that every processor can run.
/// <para>当前机器可选的字节块分派目标之一：当前处理器可以执行的指令族，或任何处理器都能运行的标量软件循环。</para>
/// </summary>
/// <remarks>
/// The entries come from <see cref="SimdCapabilities.SupportedIsas"/>, so the settings page only ever offers
/// implementations the device actually has. External code outside this assembly observes the choice through the
/// settings view model instead of constructing instances.
/// <para>条目取自 <see cref="SimdCapabilities.SupportedIsas"/>，因此设置页只提供设备实际具备的实现。本程序集之外的
/// 调用方经设置视图模型观察该选择，而不是自行构造实例。</para>
/// </remarks>
internal sealed record HardwareAccelerationTarget
{
    /// <summary>Initializes one dispatch target.
    /// <para>初始化一个分派目标。</para>
    /// </summary>
    /// <param name="isa">The instruction family.<para>指令族。</para></param>
    /// <param name="titleKey">The localized title key.<para>本地化标题键。</para></param>
    /// <param name="descriptionKey">The localized description key.<para>本地化说明键。</para></param>
    /// <param name="widthBytes">Bytes consumed per vector iteration.<para>每轮向量迭代消费的字节数。</para></param>
    internal HardwareAccelerationTarget(SimdIsa isa, string titleKey, string descriptionKey, int widthBytes)
    {
        Isa = isa;
        TitleKey = titleKey;
        DescriptionKey = descriptionKey;
        WidthBytes = widthBytes;
    }

    /// <summary>The instruction family.<para>指令族。</para></summary>
    internal SimdIsa Isa { get; }

    /// <summary>The localized title key.<para>本地化标题键。</para></summary>
    internal string TitleKey { get; }

    /// <summary>The localized description key.<para>本地化说明键。</para></summary>
    internal string DescriptionKey { get; }

    /// <summary>Bytes one vector iteration of this family consumes; <c>1</c> for the scalar loop.
    /// <para>该指令族一轮向量迭代消费的字节数；标量循环为 <c>1</c>。</para></summary>
    internal int WidthBytes { get; }
}

/// <summary>
/// Reports the hardware acceleration targets of the running machine and the dispatch policy currently installed on the
/// process.
/// <para>报告当前机器的硬件加速目标，以及当前已安装到进程上的分派策略。</para>
/// </summary>
/// <remarks>
/// The choice applies immediately: byte-block dispatch resolves the process ceiling on every call, so writing
/// <see cref="SimdOps.ProcessIsaCeiling"/> switches the running session without a restart.
/// <see cref="AppSettings.ApplyHardwareAccelerationPolicy"/> installs the persisted policy at load time, and the
/// settings page republishes it the moment the user picks another target.
/// <para>该选择立即生效：字节块分派在每次调用时解析进程上限，因此写入 <see cref="SimdOps.ProcessIsaCeiling"/>
/// 即可切换当前会话，无需重启。<see cref="AppSettings.ApplyHardwareAccelerationPolicy"/> 在加载时安装持久化
/// 策略，设置页在用户改选目标时重新发布它。</para>
/// </remarks>
internal static class HardwareAccelerationService
{
    /// <summary>
    /// The locale key of the automatic entry, whose text combines "automatic" with the family the processor would
    /// pick on its own.
    /// <para>自动项的语言资源键，其文本把“自动”与处理器自行选择的指令族组合起来。</para>
    /// </summary>
    internal const string AutoTitleKey = "Settings_HardwareAccelerationPathAuto";

    /// <summary>
    /// Gets the instruction family that acts as the upper bound for dispatch, or <see cref="SimdIsa.None"/> when the
    /// processor's widest implementation is allowed.
    /// <para>获取作为分派上限的指令族；允许处理器最宽实现时为 <see cref="SimdIsa.None"/>。</para>
    /// </summary>
    /// <value>The persisted ceiling as currently installed on the process.
    /// <para>当前已安装到进程上的持久化上限。</para></value>
    internal static SimdIsa DowngradeTarget => SimdOps.ProcessIsaCeiling;

    /// <summary>
    /// Gets the instruction family the byte-block kernels dispatch to right now.
    /// <para>获取字节块内核当前分派到的指令族。</para>
    /// </summary>
    /// <value>The effective family; <see cref="SimdIsa.Scalar"/> when dispatch is capped at the scalar loop.
    /// <para>实际生效的指令族；分派被限制在标量循环时为 <see cref="SimdIsa.Scalar"/>。</para></value>
    internal static SimdIsa ActiveIsa => SimdCapabilities.EffectiveIsa;

    /// <summary>
    /// Gets the instruction family and block width the hardware would use without any ceiling.
    /// <para>获取不设上限时硬件将会使用的指令族与块宽。</para>
    /// </summary>
    /// <value>The detected family; <see cref="SimdIsa.Scalar"/> on processors without vector support.
    /// <para>检测到的指令族；没有向量支持的处理器上为 <see cref="SimdIsa.Scalar"/>。</para></value>
    internal static SimdIsa DetectedIsa => SimdCapabilities.ActiveIsa;

    /// <summary>
    /// Gets whether the running process has no vector implementation to dispatch to.
    /// <para>获取当前进程是否有任何可分派的向量实现。</para>
    /// </summary>
    /// <value><see langword="true"/> when the processor reports no usable vector family.
    /// <para>处理器没有报告任何可用向量指令族时为 <see langword="true"/>。</para></value>
    internal static bool LacksVectorSupport => !SimdCapabilities.HardwareAccelerated;

    /// <summary>
    /// Gets the number of bytes one vector iteration consumes on the given family.
    /// <para>获取给定指令族每轮向量迭代消费的字节数。</para>
    /// </summary>
    /// <param name="isa">The instruction family.<para>指令族。</para></param>
    /// <returns>The block width in bytes, <c>1</c> for the scalar loop.
    /// <para>块宽（字节），标量循环为 <c>1</c>。</para></returns>
    internal static int WidthOf(SimdIsa isa) => isa switch
    {
        SimdIsa.Scalar => 1,
        SimdIsa.Sse2 or SimdIsa.AdvSimd or SimdIsa.LoongArchLsx => 16,
        SimdIsa.Avx2 or SimdIsa.LoongArchLasx => 32,
        SimdIsa.Avx512 or SimdIsa.Avx10 => 64,
        _ => SimdCapabilities.ActiveVectorWidthBytes,
    };

    /// <summary>
    /// Builds the selectable dispatch targets of the running machine: the automatic entry, then every family this
    /// processor actually reports, widest first, ending with the scalar software loop.
    /// <para>构建当前机器可选的派发目标：自动项、当前处理器实际报告的全部指令族（从宽到窄），并以标量软件循环
    /// 收尾。</para>
    /// </summary>
    /// <returns>The targets in display order; the first entry is the automatic choice and the last one is always
    /// <see cref="SimdIsa.Scalar"/>.
    /// <para>按显示顺序排列的目标；首项是自动选择，末项始终是 <see cref="SimdIsa.Scalar"/>。</para></returns>
    /// <remarks>
    /// Families are filtered by <see cref="SimdCapabilities.SupportedIsas"/> rather than by kernel usability: the ARM
    /// families map to the shared 128-bit kernel, so a usability probe alone would list them on x64.
    /// <para>指令族按 <see cref="SimdCapabilities.SupportedIsas"/> 过滤，而不是按内核可用性：ARM 系列映射到共用的
    /// 128 位内核，仅靠可用性探测会在 x64 上把它们列出。</para>
    /// </remarks>
    internal static IReadOnlyList<HardwareAccelerationTarget> Targets()
    {
        List<HardwareAccelerationTarget> targets =
        [
            new HardwareAccelerationTarget(SimdIsa.None, AutoTitleKey, AutoTitleKey, SimdCapabilities.ActiveVectorWidthBytes),
        ];

        foreach (var isa in SimdOps.DispatchOrder)
        {
            if (isa != SimdIsa.Scalar && IsReportedByProcessor(isa))
            {
                targets.Add(new HardwareAccelerationTarget(isa, TitleKeyOf(isa), DescriptionKeyOf(isa), WidthOf(isa)));
            }
        }

        targets.Add(new HardwareAccelerationTarget(
            SimdIsa.Scalar,
            "Settings_HardwareAccelerationPathScalar",
            "Settings_HardwareAccelerationPathScalarHint",
            1));
        return targets;
    }

    /// <summary>
    /// Reports whether the running processor reports one instruction family, so the settings page only lists families
    /// this machine really has.
    /// <para>报告当前处理器是否报告了某个指令族，使设置页只列出本机真实具备的指令族。</para>
    /// </summary>
    /// <param name="isa">The instruction family to test.<para>待检测的指令族。</para></param>
    /// <returns><see langword="true"/> when the family appears in <see cref="SimdCapabilities.SupportedIsas"/>.
    /// <para>该族出现在 <see cref="SimdCapabilities.SupportedIsas"/> 中时为 <see langword="true"/>。</para></returns>
    private static bool IsReportedByProcessor(SimdIsa isa) => (SimdCapabilities.SupportedIsas & isa) == isa;

    /// <summary>
    /// Gets the architecture token shown next to the detected family.
    /// <para>获取显示在检测到的指令族旁的架构标识。</para>
    /// </summary>
    /// <value>An architecture name such as <c>X64</c>.<para>形如 <c>X64</c> 的架构名称。</para></value>
    internal static string Architecture => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        System.Runtime.InteropServices.Architecture.Arm64 => "Arm64",
        System.Runtime.InteropServices.Architecture.Arm => "Arm",
        System.Runtime.InteropServices.Architecture.LoongArch64 => "LoongArch64",
        System.Runtime.InteropServices.Architecture.RiscV64 => "RiscV64",
        System.Runtime.InteropServices.Architecture.Wasm => "Wasm",
        _ => RuntimeInformation.ProcessArchitecture.ToString(),
    };

    /// <summary>
    /// Gets the locale key naming one family.
    /// <para>获取命名某个指令族的语言资源键。</para>
    /// </summary>
    /// <param name="isa">The instruction family.<para>指令族。</para></param>
    /// <returns>The title key.<para>标题键。</para></returns>
    internal static string TitleKeyOf(SimdIsa isa) => isa switch
    {
        SimdIsa.Avx10 => "Settings_HardwareAccelerationPathAvx10",
        SimdIsa.Avx512 => "Settings_HardwareAccelerationPathAvx512",
        SimdIsa.Avx2 => "Settings_HardwareAccelerationPathAvx2",
        SimdIsa.Sse2 => "Settings_HardwareAccelerationPathSse2",
        SimdIsa.Sve2 => "Settings_HardwareAccelerationPathSve2",
        SimdIsa.Sve => "Settings_HardwareAccelerationPathSve",
        SimdIsa.AdvSimd => "Settings_HardwareAccelerationPathAdvSimd",
        SimdIsa.ArmCrc => "Settings_HardwareAccelerationPathArmCrc",
        SimdIsa.ArmCrypto => "Settings_HardwareAccelerationPathArmCrypto",
        SimdIsa.LoongArchLsx => "Settings_HardwareAccelerationPathLoongArchLsx",
        SimdIsa.LoongArchLasx => "Settings_HardwareAccelerationPathLoongArchLasx",
        SimdIsa.RiscVVector => "Settings_HardwareAccelerationPathRiscVVector",
        SimdIsa.PortableVectors => "Settings_HardwareAccelerationPathPortableVectors",
        _ => "Settings_HardwareAccelerationPathScalar",
    };

    /// <summary>
    /// Gets the locale key describing one family.
    /// <para>获取描述某个指令族的语言资源键。</para>
    /// </summary>
    /// <param name="isa">The instruction family.<para>指令族。</para></param>
    /// <returns>The description key.<para>说明键。</para></returns>
    internal static string DescriptionKeyOf(SimdIsa isa) => isa switch
    {
        SimdIsa.Avx10 => "Settings_HardwareAccelerationPathAvx10Hint",
        SimdIsa.Avx512 => "Settings_HardwareAccelerationPathAvx512Hint",
        SimdIsa.Avx2 => "Settings_HardwareAccelerationPathAvx2Hint",
        SimdIsa.Sse2 => "Settings_HardwareAccelerationPathSse2Hint",
        SimdIsa.Sve2 => "Settings_HardwareAccelerationPathSve2Hint",
        SimdIsa.Sve => "Settings_HardwareAccelerationPathSveHint",
        SimdIsa.AdvSimd => "Settings_HardwareAccelerationPathAdvSimdHint",
        SimdIsa.ArmCrc => "Settings_HardwareAccelerationPathArmCrcHint",
        SimdIsa.ArmCrypto => "Settings_HardwareAccelerationPathArmCryptoHint",
        SimdIsa.LoongArchLsx => "Settings_HardwareAccelerationPathLoongArchLsxHint",
        SimdIsa.LoongArchLasx => "Settings_HardwareAccelerationPathLoongArchLasxHint",
        SimdIsa.RiscVVector => "Settings_HardwareAccelerationPathRiscVVectorHint",
        SimdIsa.PortableVectors => "Settings_HardwareAccelerationPathPortableVectorsHint",
        _ => "Settings_HardwareAccelerationPathScalarHint",
    };
}