using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Bridges;
using UotanStudio.Firmware.Core.Repacking;

namespace UotanStudio.Firmware.FileSystem.Rebuilding;

/// <summary>
/// Publishes the preserved-filesystem lifecycle to the sparse container context. The container handler defers its
/// unpack, metadata capture and preserved rebuild here, so the container context keeps unpacking through the
/// registered filesystem handlers instead of referencing the preservation classes directly.
/// <para>向稀疏容器上下文发布保留文件系统生命周期。容器处理器把解包、元数据捕获与受保留重建都委派到这里，
/// 使容器上下文继续通过已注册的文件系统处理器完成解包，而不直接引用保留相关类。</para>
/// </summary>
internal sealed class FileSystemSparsePreservation : ISparsePreservationBridge
{
    /// <summary>
    /// Gets the provider registered alongside the filesystem format handlers.
    /// <para>获取随文件系统格式处理器一同注册的提供方。</para>
    /// </summary>
    /// <value>A shared instance; the type holds no state. <para>共享实例；该类型不持有状态。</para></value>
    internal static FileSystemSparsePreservation Instance { get; } = new();

    /// <inheritdoc/>
    public Task<FirmwareOpResult> UnpackPreservedAsync(IReadonlyFormatHandler handler, string sourcePath,
        string outputDirectory, UnpackOptions? options, CancellationToken cancellationToken,
        Func<string, Task<FirmwareOpResult>> unpack)
        => FileSystemPreservation.UnpackAsync(handler, sourcePath, outputDirectory, options, cancellationToken, unpack);

    /// <inheritdoc/>
    public void ConfigurePreservedMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
        => FileSystemRepackMetadata.Configure(metadata, sourcePath, outputDirectory, sparse: true);

    /// <inheritdoc/>
    public Task CapturePreservedMetadataAsync(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken)
        => FileSystemRepackMetadata.CaptureAsync(metadata, sourcePath, outputDirectory, metadataDirectory,
            cancellationToken, sparse: true);

    /// <inheritdoc/>
    public bool TryRestorePreserved(string inputDirectory, string outputPath, string format,
        CancellationToken cancellationToken, Action<IReadOnlyDictionary<string, string>>? rebuildReport)
        => FileSystemPreservation.TryRestore(inputDirectory, outputPath, format, cancellationToken, rebuildReport);
}