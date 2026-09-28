using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Bridges;

namespace UotanStudio.Firmware.FileSystem;

/// <summary>
/// Single resolution point through which this bounded context reaches the sparse container utilities.
/// Filesystem code reads raw images through a sparse container and publishes its own output inside one
/// without referencing another context's concrete classes; the owning context registers the bridge
/// implementation as part of its format handler, so resolution consults that star registry rather than a
/// hard-coded type.
/// <para>本限界上下文访问稀疏容器工具的唯一解析入口。文件系统代码在稀疏容器之上读取原始镜像，也把自己的
/// 输出发布到稀疏容器内，而不引用另一个上下文的具体类；桥接实现由所属上下文作为其格式处理器的一部分注册，
/// 因此解析只查询该注册表，而不是硬编码具体类型。</para>
/// </summary>
internal static class SparseContainerAccess
{
    /// <summary>
    /// Returns the registered sparse container bridge.
    /// <para>返回已注册的稀疏容器桥接实现。</para>
    /// </summary>
    /// <returns>The bridge instance published by the owning format handler.</returns>
    /// <exception cref="InvalidOperationException">No registered format handler implements
    /// <see cref="ISparseContainerBridge"/>, so the sparse container utilities are unavailable.
    /// <para>没有已注册的格式处理器实现 <see cref="ISparseContainerBridge"/>，稀疏容器工具不可用。</para></exception>
    internal static ISparseContainerBridge Resolve()
        => FormatDetector.RegisteredHandlers.OfType<ISparseContainerBridge>().FirstOrDefault()
           ?? throw new InvalidOperationException(
               "No format handler registered with FormatDetector implements ISparseContainerBridge; "
               + "register the sparse container format handler through FirmwareFormatRegistry.RegisterAll().");
}