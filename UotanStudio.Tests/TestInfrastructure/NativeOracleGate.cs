using Xunit;

namespace UotanStudio.Tests.TestInfrastructure;

/// <summary>
/// Single place that decides whether oracle, native-tool and large-corpus tests may run, so every skip reason
/// names the exact environment variable a maintainer has to set. Discovery-time gates are exposed as static
/// properties for <c>[Fact(Skip = "...", SkipUnless = nameof(...))]</c>; <see cref="SkipUnless(bool, string[])"/>
/// covers decisions that can only be taken while the test executes, such as a binary that exists but cannot be
/// started.
/// <para>统一决定 oracle、原生工具与大数据量测试能否运行的位置，使每条跳过原因都点明维护者需要设置的具体环境变量。
/// 发现期门控以静态属性形式提供，供 <c>[Fact(Skip = "...", SkipUnless = nameof(...))]</c> 使用；
/// <see cref="SkipUnless(bool, string[])"/> 负责只能在测试执行时才能判断的场景，例如文件存在但当前系统无法启动。</para>
/// </summary>
public static class NativeOracleGate
{
    /// <summary>Environment variable naming a runnable magiskboot reference binary.
    /// <para>指定可运行 magiskboot 参考二进制的环境变量名。</para></summary>
    public const string MagiskBootVariable = MagiskBootOracle.ExecutableVariable;

    /// <summary>Environment variable set to <c>1</c> to allow external native tool invocations.
    /// <para>设为 <c>1</c> 时允许调用外部原生工具的环境变量名。</para></summary>
    public const string NativeToolsVariable = "UOTAN_NATIVE_TOOLS";

    /// <summary>Environment variable set to <c>1</c> to allow terabyte-scale corpus builds.
    /// <para>设为 <c>1</c> 时允许构造 TB 级语料的环境变量名。</para></summary>
    public const string LargeDataVariable = "UOTAN_LARGE_DATA";

    /// <summary>Whether the magiskboot reference binary is present and startable here.
    /// <para>magiskboot 参考二进制是否存在且能在当前系统启动。</para></summary>
    public static bool MagiskBootOracleAvailable => MagiskBootOracle.IsAvailable;

    /// <summary>Whether native external-tool interoperability is enabled for this run.
    /// <para>本次运行是否启用外部原生工具互操作。</para></summary>
    public static bool NativeToolsEnabled => IsFlagEnabled(NativeToolsVariable);

    /// <summary>Whether the large-corpus builds are enabled for this run.
    /// <para>本次运行是否启用大数据量语料构造。</para></summary>
    public static bool LargeDataEnabled => IsFlagEnabled(LargeDataVariable);

    /// <summary>Whether the Samsung merge-partition oracle binary is present.
    /// <para>Samsung 合并分区 oracle 二进制是否存在。</para></summary>
    public static bool SamsungDatMergeOracleAvailable =>
        RealSampleIndex.IsAvailable(RealSampleIndex.SamsungDatMergeOracleVariable);

    /// <summary>Whether the Samsung payload-merge oracle binary is present.
    /// <para>Samsung payload 合并 oracle 二进制是否存在。</para></summary>
    public static bool SamsungPayloadMergeOracleAvailable =>
        RealSampleIndex.IsAvailable(RealSampleIndex.SamsungPayloadMergeOracleVariable);

    /// <summary>Whether the HarmonyOS <c>UPDATE.APP</c> corpus is present.
    /// <para>HarmonyOS <c>UPDATE.APP</c> 语料是否存在。</para></summary>
    public static bool HarmonyUpdateAppSampleAvailable =>
        RealSampleIndex.IsAvailable(RealSampleIndex.HarmonyUpdateAppVariable);

    /// <summary>Whether a switch variable is set to the literal <c>1</c>.
    /// <para>开关变量是否被设为字面量 <c>1</c>。</para></summary>
    public static bool IsFlagEnabled(string variableName) =>
        Environment.GetEnvironmentVariable(variableName) == "1";

    /// <summary>Skips the running test when <paramref name="condition"/> is <see langword="false"/>, reporting
    /// the variables that have to be set.
    /// <para>当 <paramref name="condition"/> 为 <see langword="false"/> 时跳过正在运行的测试，并报告需要设置哪些变量。</para></summary>
    /// <param name="condition">When <see langword="true"/>, the test keeps running. 为 <see langword="true"/> 时测试继续执行。</param>
    /// <param name="variableNames">Environment variables the maintainer must configure. 维护者需要配置的环境变量名。</param>
    public static void SkipUnless(bool condition, params string[] variableNames)
    {
        Assert.SkipUnless(condition, DescribeMissing(variableNames));
    }

    /// <summary>Skips the running test when the magiskboot oracle is missing or cannot be started here.
    /// <para>当 magiskboot oracle 缺失或在当前系统无法启动时跳过正在运行的测试。</para></summary>
    public static void SkipUnlessMagiskBootOracle() => SkipUnless(MagiskBootOracleAvailable, MagiskBootVariable);

    /// <summary>Skips the running test when native tool interoperability is disabled.
    /// <para>当未启用原生工具互操作时跳过正在运行的测试。</para></summary>
    public static void SkipUnlessNativeTools() => SkipUnless(NativeToolsEnabled, NativeToolsVariable);

    /// <summary>Skips the running test when large-corpus builds are disabled.
    /// <para>当未启用大数据量语料构造时跳过正在运行的测试。</para></summary>
    public static void SkipUnlessLargeData() => SkipUnless(LargeDataEnabled, LargeDataVariable);

    /// <summary>Skips the running test when the magiskboot oracle is unavailable or native tools are disabled.
    /// <para>当 magiskboot oracle 不可用或未启用原生工具时跳过正在运行的测试。</para></summary>
    public static void SkipUnlessMagiskBootInterop() =>
        SkipUnless(MagiskBootOracleAvailable && NativeToolsEnabled, MagiskBootVariable, NativeToolsVariable);

    /// <summary>Builds a skip reason that names each required variable, adding <c>=1</c> for switch variables.
    /// <para>构造点明每个所需变量的跳过原因，并对开关变量附加 <c>=1</c>。</para></summary>
    public static string DescribeMissing(params string[] variableNames)
    {
        ArgumentNullException.ThrowIfNull(variableNames);
        var requirements = variableNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name is NativeToolsVariable or LargeDataVariable ? $"{name}=1" : name)
            .Distinct(StringComparer.Ordinal);
        var joined = string.Join(" and ", requirements);
        return string.IsNullOrEmpty(joined)
            ? "The required test environment is unavailable."
            : $"Set {joined} to run this comparison against the reference implementation.";
    }
}