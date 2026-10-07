namespace UotanStudio.Tests.TestInfrastructure;

/// <summary>
/// Single place that decides whether oracle, native-tool and large-corpus tests may run. All gated tests that
/// consumed these gates have been removed; the helper types <c>MagiskBootOracle</c> and <c>RealSampleIndex</c>
/// remain available for any future re-introduction of environment-gated comparisons.
/// <para>统一决定 oracle、原生工具与大数据量测试能否运行的位置。所有消费这些门控的测试已被移除；
/// 辅助类型 <c>MagiskBootOracle</c> 与 <c>RealSampleIndex</c> 保留，供将来重新引入环境门控比较时使用。</para>
/// </summary>
public static class NativeOracleGate
{
}