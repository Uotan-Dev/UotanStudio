using Microsoft.Extensions.DependencyInjection;

namespace UotanStudio.Composition;

/// <summary>
/// Holds the application service provider built by the composition root so static facades can
/// prefer the container instance while falling back to their lazy defaults before initialization.
/// <para>保存组合根构建的应用服务提供程序，使静态门面在初始化前回退惰性默认实例、初始化后优先使用容器实例。</para>
/// </summary>
internal static class ServiceHub
{
    private static IServiceProvider? _services;

    /// <summary>
    /// Gets the current provider, or null when the composition root has not initialized the hub.
    /// <para>获取当前提供程序；组合根尚未初始化时为 null。</para>
    /// </summary>
    /// <value>The composed provider, or null. <para>组合后的提供程序；未初始化时为 null。</para></value>
    internal static IServiceProvider? Services => Volatile.Read(ref _services);

    /// <summary>
    /// Stores the provider that subsequent <see cref="TryGet{T}"/> and <see cref="GetRequired{T}"/> calls read.
    /// <para>保存后续 <see cref="TryGet{T}"/> 与 <see cref="GetRequired{T}"/> 读取的提供程序。</para>
    /// </summary>
    /// <param name="services">The provider to store; must not be null. <para>要保存的提供程序；不能为 null。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null. <para><paramref name="services"/> 为 null。</para></exception>
    internal static void Initialize(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Volatile.Write(ref _services, services);
    }

    /// <summary>
    /// Clears the stored provider so tests can restore the uninitialized fallback state.
    /// <para>清除已保存的提供程序，使测试可以恢复未初始化的回退状态。</para>
    /// </summary>
    internal static void Reset() => Volatile.Write(ref _services, null);

    /// <summary>
    /// Resolves a service, or returns null when the hub is uninitialized or the service is unregistered.
    /// <para>解析服务；hub 未初始化或服务未注册时返回 null。</para>
    /// </summary>
    /// <typeparam name="T">The service type to resolve. <para>要解析的服务类型。</para></typeparam>
    /// <returns>The resolved service, or null. <para>解析得到的服务；无法解析时为 null。</para></returns>
    internal static T? TryGet<T>() where T : class => Services?.GetService(typeof(T)) as T;

    /// <summary>
    /// Resolves a required service from the container.
    /// <para>从容器解析必需的服务。</para>
    /// </summary>
    /// <typeparam name="T">The service type to resolve. <para>要解析的服务类型。</para></typeparam>
    /// <returns>The resolved service. <para>解析得到的服务。</para></returns>
    /// <exception cref="InvalidOperationException">The hub is uninitialized. <para>hub 尚未初始化。</para></exception>
    internal static T GetRequired<T>() where T : class
        => Services?.GetRequiredService<T>()
            ?? throw new InvalidOperationException("The application service container has not been initialized.");
}