namespace UotanStudio.Firmware.Core.Bridges;

/// <summary>
/// Single resolution point through which the sparse container context reaches the preserved-filesystem lifecycle.
/// The filesystem context registers its provider at module load, so the container context resolves the lifecycle
/// without naming another context's concrete classes.
/// <para>稀疏容器上下文访问保留文件系统生命周期的唯一解析入口。文件系统上下文在模块加载时注册其提供方，因此容器
/// 上下文无需指名另一个上下文的具体类型即可解析该生命周期。</para>
/// </summary>
internal static class SparsePreservationAccess
{
    private static ISparsePreservationBridge? registered;

    /// <summary>
    /// Publishes the preserved-filesystem provider. Called once by the owning context; the last registration wins.
    /// <para>发布保留文件系统提供方。由属主上下文调用一次；后注册者覆盖先注册者。</para>
    /// </summary>
    /// <param name="provider">Provider implementing the preserved-filesystem lifecycle.
    /// <para>实现保留文件系统生命周期的提供方。</para></param>
    internal static void Register(ISparsePreservationBridge provider) => registered = provider;

    /// <summary>
    /// Returns the registered preserved-filesystem provider.
    /// <para>返回已注册的保留文件系统提供方。</para>
    /// </summary>
    /// <returns>The provider published by the filesystem context. <para>由文件系统上下文发布的提供程序。</para></returns>
    /// <exception cref="InvalidOperationException">No provider was registered, so preserved rebuilds are
    /// unavailable. <para>没有已注册的提供方，保留式重建不可用。</para></exception>
    internal static ISparsePreservationBridge Resolve()
        => registered ?? throw new InvalidOperationException(
            "No ISparsePreservationBridge provider was registered; the filesystem context must register its "
            + "preserved-filesystem lifecycle provider at module load.");
}