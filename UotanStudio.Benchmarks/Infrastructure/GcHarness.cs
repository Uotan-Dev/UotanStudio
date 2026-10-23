using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;

namespace UotanStudio.Benchmarks.Infrastructure;

/// <summary>
/// Shared BenchmarkDotNet configuration surface for the benchmark baseline.
/// <para>基准测试基线共用的 BenchmarkDotNet 配置面。</para>
/// </summary>
/// <remarks>
/// The members express garbage-collection and reporting settings; BenchmarkDotNet applies the job GC
/// settings in the generated child process, so nothing here claims to have verified them in-process.
/// <para>这里的成员只负责表达垃圾回收与报告设置；作业的 GC 设置由 BenchmarkDotNet 在生成的子进程中应用，
/// 因此本类型不声称已在进程内验证过这些设置。</para>
/// </remarks>
public static class GcHarness
{
    private const string BenchmarksProjectFile = "UotanStudio.Benchmarks.csproj";
    private const string WorkstationMode = "workstation";
    private const string ServerMode = "server";
    private const string ServerConcurrentMode = "server-concurrent";
    private const string WorkstationNonConcurrentMode = "workstation-nonconcurrent";

    private static readonly string[] Modes =
    [
        WorkstationMode,
        ServerMode,
        ServerConcurrentMode,
        WorkstationNonConcurrentMode
    ];

    /// <summary>
    /// GC mode names a comparison reports, in reporting order, each with the server and concurrent
    /// settings <see cref="WithGcMode(Job, string)"/> applies.
    /// <para>一次对比应当报告的 GC 模式名称，顺序即报告顺序；每个名称都给出
    /// <see cref="WithGcMode(Job, string)"/> 应用的服务端与并发设置。</para>
    /// </summary>
    /// <value>
    /// The four names <see cref="WithGcMode(Job, string)"/> accepts: <c>workstation</c> (server off,
    /// concurrent off), <c>server</c> (server on, concurrent off), <c>server-concurrent</c> (server on,
    /// concurrent on) and <c>workstation-nonconcurrent</c> (server off, concurrent off). All four force a
    /// collection after each invocation.
    /// <para><see cref="WithGcMode(Job, string)"/> 接受的四个名称：<c>workstation</c>（服务端关闭、并发关闭）、
    /// <c>server</c>（服务端开启、并发关闭）、<c>server-concurrent</c>（服务端开启、并发开启）与
    /// <c>workstation-nonconcurrent</c>（服务端关闭、并发关闭）。四者都在每次调用后强制回收。</para>
    /// </value>
    public static string[] GcModes { get; } = Modes;

    /// <summary>
    /// Builds the baseline job a later default-GC versus Satori-GC comparison measures against:
    /// workstation GC, concurrent GC off, forced collection after each invocation.
    /// <para>构建后续「默认 GC 对比 Satori GC」所对照的基线作业：工作站 GC、关闭并发 GC、每次调用后强制回收。</para>
    /// </summary>
    /// <returns>The baseline job, identified as <c>workstation</c>.
    /// <para>标识为 <c>workstation</c> 的基线作业。</para></returns>
    public static Job BaselineGc()
        => NewJob(WorkstationMode);

    /// <summary>
    /// Builds a server-GC job with concurrent collection off, the throughput-oriented server shape.
    /// <para>构建关闭并发回收的服务端 GC 作业，即面向吞吐的服务端形态。</para>
    /// </summary>
    /// <returns>The server job, identified as <c>server</c>.
    /// <para>标识为 <c>server</c> 的服务端作业。</para></returns>
    public static Job ServerGc()
        => NewJob(ServerMode);

    /// <summary>
    /// Builds a workstation job with concurrent collection off, the throughput-oriented desktop shape.
    /// <para>构建关闭并发回收的工作站作业，即面向吞吐的桌面形态。</para>
    /// </summary>
    /// <returns>The workstation throughput job, identified as <c>workstation-nonconcurrent</c>.
    /// <para>标识为 <c>workstation-nonconcurrent</c> 的工作站吞吐作业。</para></returns>
    public static Job WorkstationNonConcurrentGc()
        => NewJob(WorkstationNonConcurrentMode);

    /// <summary>
    /// Applies one of the <see cref="GcModes"/> to a job and gives it a readable identifier.
    /// <para>把 <see cref="GcModes"/> 中的一个模式应用到作业上，并赋予其可读标识符。</para>
    /// </summary>
    /// <param name="job">The job to configure; it is copied, never mutated.
    /// <para>要配置的作业；该作业会被复制，不会被修改。</para></param>
    /// <param name="mode">One of <see cref="GcModes"/>.
    /// <para><see cref="GcModes"/> 中的一个模式。</para></param>
    /// <returns>A job whose identifier is the mode name itself, so result tables stay legible without
    /// decoding BenchmarkDotNet's generated defaults.
    /// <para>标识符即模式名称的作业，使结果表格无需解码 BenchmarkDotNet 生成的默认值即可阅读。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="mode"/> is not a known GC mode; the exception message names the rejected value.
    /// <para>当 <paramref name="mode"/> 不是已知的 GC 模式时抛出；异常信息中会写出被拒绝的值。</para></exception>
    public static Job WithGcMode(this Job job, string mode)
        => job.WithId(IdFor(mode)).WithGcServer(IsServer(mode)).WithGcConcurrent(IsConcurrent(mode)).WithGcForce(true);

    /// <summary>
    /// Builds the global configuration every benchmark type reports through: memory diagnoser, artifacts
    /// under <c>UotanStudio.Benchmarks/artifacts</c>, and BenchmarkDotNet's default exporters, loggers
    /// and column providers.
    /// <para>构建所有基准类型共用的全局配置：内存诊断器、位于 <c>UotanStudio.Benchmarks/artifacts</c> 的产物目录，
    /// 以及 BenchmarkDotNet 的默认导出器、日志器与列提供器。</para>
    /// </summary>
    /// <returns>A configuration carrying the shared reporting surface, and no job unless the caller adds one.
    /// <para>只承载共用报告面的配置；除调用方自行添加外不含任何作业。</para></returns>
    public static IConfig GlobalGcConfig()
    {
        var defaults = DefaultConfig.Instance;
        return ManualConfig.CreateEmpty()
            .AddColumnProvider(defaults.GetColumnProviders().ToArray())
            .AddLogger(defaults.GetLoggers().ToArray())
            .AddExporter(defaults.GetExporters().ToArray())
            .AddAnalyser(defaults.GetAnalysers().ToArray())
            .AddValidator(defaults.GetValidators().ToArray())
            .AddHardwareCounters(defaults.GetHardwareCounters().ToArray())
            .AddDiagnoser(MemoryDiagnoser.Default)
            .WithArtifactsPath(ArtifactsDirectory);
    }

    private static string ArtifactsDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, BenchmarksProjectFile)))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory?.FullName ?? AppContext.BaseDirectory, "artifacts");
        }
    }

    /// <summary>
    /// Combines <see cref="GlobalGcConfig"/> with one GC-mode job, for the cases where a benchmark type
    /// must declare its job through a <c>[Config]</c> attribute instead of taking the assembly-level source.
    /// <para>把 <see cref="GlobalGcConfig"/> 与一个 GC 模式作业合并，用于基准类型必须通过 <c>[Config]</c>
    /// 特性声明作业、而非采用程序集级来源的场景。</para>
    /// </summary>
    /// <param name="mode">One of <see cref="GcModes"/>; defaults to the baseline mode.
    /// <para><see cref="GcModes"/> 中的一个模式；默认为基线模式。</para></param>
    /// <returns>A configuration carrying the shared reporting surface plus exactly one job.
    /// <para>承载共用报告面并恰好包含一个作业的配置。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="mode"/> is not a known GC mode.
    /// <para>当 <paramref name="mode"/> 不是已知的 GC 模式时抛出。</para></exception>
    public static IConfig GcModeConfig(string mode = WorkstationMode)
        => ((ManualConfig)GlobalGcConfig()).AddJob(Job.Dry.WithGcMode(mode));

    private static Job NewJob(string mode)
        => Job.Dry
            .WithId(IdFor(mode))
            .WithGcServer(IsServer(mode))
            .WithGcConcurrent(IsConcurrent(mode))
            .WithGcForce(true);

    private static string IdFor(string mode)
        => mode switch
        {
            WorkstationMode => "workstation",
            ServerMode => "server",
            ServerConcurrentMode => "server-concurrent",
            WorkstationNonConcurrentMode => "workstation-nonconcurrent",
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                $"Unknown GC mode '{mode}'. Known modes: {string.Join(", ", Modes)}.")
        };

    private static bool IsServer(string mode)
        => mode switch
        {
            ServerMode or ServerConcurrentMode => true,
            WorkstationMode or WorkstationNonConcurrentMode => false,
            _ => throw Unknown(mode)
        };

    private static bool IsConcurrent(string mode)
        => mode switch
        {
            ServerConcurrentMode => true,
            WorkstationMode or ServerMode or WorkstationNonConcurrentMode => false,
            _ => throw Unknown(mode)
        };

    private static ArgumentOutOfRangeException Unknown(string mode)
        => new(nameof(mode), mode, $"Unknown GC mode '{mode}'. Known modes: {string.Join(", ", Modes)}.");
}