using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using UotanStudio.Benchmarks.Infrastructure;

[assembly: ConfigSource(typeof(GcHarnessConfigSource))]

namespace UotanStudio.Benchmarks.Infrastructure;

/// <summary>
/// Exposes <see cref="GcHarness.GlobalGcConfig()"/> as the configuration source registered at assembly
/// level by <see cref="ConfigSourceAttribute"/>.
/// <para>把 <see cref="GcHarness.GlobalGcConfig()"/> 暴露为通过 <see cref="ConfigSourceAttribute"/>
/// 在程序集级注册的配置来源。</para>
/// </summary>
public sealed class GcHarnessConfigSource : IConfigSource
{
    /// <summary>
    /// Gets the shared configuration surface.
    /// <para>获取共用配置面。</para>
    /// </summary>
    /// <value>
    /// The configuration returned by <see cref="GcHarness.GlobalGcConfig()"/>.
    /// <para><see cref="GcHarness.GlobalGcConfig()"/> 返回的配置。</para>
    /// </value>
    public IConfig Config => GcHarness.GlobalGcConfig();
}

/// <summary>
/// Points BenchmarkDotNet at an <see cref="IConfigSource"/> through an assembly-level attribute.
/// <para>通过程序集级特性把 BenchmarkDotNet 指向一个 <see cref="IConfigSource"/>。</para>
/// </summary>
/// <remarks>
/// BenchmarkDotNet resolves a benchmark type's configuration only from <c>IConfigSource</c> attributes on
/// the type and on its assembly; it never scans an assembly for <c>IConfigSource</c> implementations, so a
/// source class that is merely present stays dead code. A <see cref="ConfigAttribute"/> on a benchmark type
/// cannot reach <see cref="GcHarness"/> either, because attribute arguments accept constant types only and a
/// configuration factory returns a non-constant value. This attribute is what makes one shared configuration
/// reach every benchmark class instead of one hand-written holder per file.
/// <para>BenchmarkDotNet 只从基准类型及其程序集上的 <c>IConfigSource</c> 特性解析配置，从不扫描程序集里的
/// <c>IConfigSource</c> 实现，因此仅「存在」的来源类型是死代码。基准类型上的 <see cref="ConfigAttribute"/>
/// 同样无法触达 <see cref="GcHarness"/>，因为特性实参只接受常量类型，而配置工厂返回的是非常量值。
/// 本特性正是让唯一共用配置覆盖全部基准类、而不是每个文件各写一个持有者的原因。</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
[SuppressMessage("Design", "CA1019:Define accessors for attribute arguments", Justification = "The source type is only needed when BenchmarkDotNet instantiates the source.")]
public sealed class ConfigSourceAttribute : Attribute, IConfigSource
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    private readonly Type _sourceType;

    /// <summary>
    /// Registers a configuration source type with the assembly.
    /// <para>向程序集注册一个配置来源类型。</para>
    /// </summary>
    /// <param name="sourceType">A non-abstract <see cref="IConfigSource"/> implementation with a public parameterless constructor.
    /// <para>具备公共无参构造函数、非抽象的 <see cref="IConfigSource"/> 实现。</para></param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="sourceType"/> is null.
    /// <para>当 <paramref name="sourceType"/> 为 null 时抛出。</para></exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sourceType"/> cannot be instantiated as a configuration source.
    /// <para>当 <paramref name="sourceType"/> 无法实例化为配置来源时抛出。</para></exception>
    public ConfigSourceAttribute(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type sourceType)
    {
        ArgumentNullException.ThrowIfNull(sourceType);

        if (!typeof(IConfigSource).IsAssignableFrom(sourceType))
        {
            throw new ArgumentException(
                $"{sourceType.FullName} does not implement {nameof(IConfigSource)}.",
                nameof(sourceType));
        }

        if (sourceType.IsAbstract || sourceType.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new ArgumentException(
                $"{sourceType.FullName} needs a public parameterless constructor to be used as a configuration source.",
                nameof(sourceType));
        }

        _sourceType = sourceType;
    }

    /// <summary>
    /// Gets the configuration carried by the registered source type.
    /// <para>获取被注册来源类型所承载的配置。</para>
    /// </summary>
    /// <value>
    /// The configuration returned by the registered source.
    /// <para>已注册来源返回的配置。</para>
    /// </value>
    public IConfig Config => ((IConfigSource)Activator.CreateInstance(_sourceType)!).Config;
}