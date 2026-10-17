using UotanStudio.Firmware.Core.Repacking;

namespace UotanStudio.Firmware.Core.Bridges;

/// <summary>
/// Seam over the preserved-filesystem lifecycle for the sparse container context, which unpacks and repacks the
/// inner filesystem and records its repack metadata without referencing the filesystem context's concrete classes.
/// The filesystem context registers the implementation through its sparse format handler, so resolution consults
/// the registered format handlers rather than a hard-coded type.
/// <para>稀疏容器上下文使用的保留文件系统生命周期服务接口：它解包并重打包内部文件系统、记录其重打包元数据，
/// 而无需引用文件系统上下文的具体类型。实现由文件系统上下文随其稀疏格式处理器一同注册，因此解析只查询已注册的
/// 格式处理器，而不是硬编码具体类型。</para>
/// </summary>
internal interface ISparsePreservationBridge
{
    /// <summary>
    /// Unpacks a container image under the preserved-filesystem lifecycle, so extraction, inspection and the
    /// retained record all observe the same frozen input bytes.
    /// <para>在保留文件系统生命周期下解包容器镜像，使解包、检查与保留记录都看到同一份冻结输入字节。</para>
    /// </summary>
    /// <param name="handler">Format handler whose metadata describes the source.
    /// <para>元数据描述该源镜像的格式处理器。</para></param>
    /// <param name="sourcePath">Source container file. <para>源容器文件。</para></param>
    /// <param name="outputDirectory">Directory that receives the extraction output.
    /// <para>接收解包输出的目录。</para></param>
    /// <param name="options">Unpack options forwarded to the lifecycle; may be <see langword="null"/>.
    /// <para>转发给生命周期的解包选项；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels snapshot copying and extraction.
    /// <para>取消快照复制与解包。</para></param>
    /// <param name="unpack">Extraction delegate invoked with the frozen copy path instead of the original.
    /// <para>以冻结副本路径而非原始路径调用的解包委托。</para></param>
    /// <returns>The delegate's result with the snapshot path replaced by the original source path.
    /// <para>委托结果，其中快照路径被替换为原始源路径。</para></returns>
    Task<FirmwareOpResult> UnpackPreservedAsync(IReadonlyFormatHandler handler, string sourcePath,
        string outputDirectory, UnpackOptions? options, CancellationToken cancellationToken,
        Func<string, Task<FirmwareOpResult>> unpack);

    /// <summary>
    /// Writes format-owned rebuild configuration into the container's repack metadata.
    /// <para>把格式自有的重建配置写入容器的重打包元数据。</para>
    /// </summary>
    /// <param name="metadata">Metadata instance being assembled before publication.
    /// <para>发布前正在组装的元数据实例。</para></param>
    /// <param name="sourcePath">Path of the source container image.
    /// <para>源容器镜像的路径。</para></param>
    /// <param name="outputDirectory">Extraction output directory.
    /// <para>解包输出目录。</para></param>
    void ConfigurePreservedMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory);

    /// <summary>
    /// Captures format-owned preservation artifacts before metadata publication.
    /// <para>在元数据发布之前捕获格式自有的保留产物。</para>
    /// </summary>
    /// <param name="metadata">Metadata instance whose preservation fields the capture fills.
    /// <para>由捕获流程填充保留字段的元数据实例。</para></param>
    /// <param name="sourcePath">Path of the source container image.
    /// <para>源容器镜像的路径。</para></param>
    /// <param name="outputDirectory">Extraction output directory.
    /// <para>解包输出目录。</para></param>
    /// <param name="metadataDirectory">Directory in which the metadata JSON will be published.
    /// <para>元数据 JSON 即将发布到的目录。</para></param>
    /// <param name="cancellationToken">Cancels artifact capture.
    /// <para>取消产物捕获。</para></param>
    /// <returns>A task completed once the artifacts are captured.
    /// <para>产物捕获完成后结束的任务。</para></returns>
    Task CapturePreservedMetadataAsync(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Rebuilds an extracted directory from its preserved source attributes into the container image.
    /// <para>依据保留的源属性把已解包目录重建为容器镜像。</para>
    /// </summary>
    /// <param name="inputDirectory">Extracted tree carrying the preservation payload.
    /// <para>携带保留负载的解包目录。</para></param>
    /// <param name="outputPath">Container image to write atomically.
    /// <para>以原子方式写出的容器镜像。</para></param>
    /// <param name="format">Container format name the rebuild must match.
    /// <para>重建必须匹配的容器格式名。</para></param>
    /// <param name="cancellationToken">Cancels manifest reads, hashing, rebuilding and verification.
    /// <para>取消清单读取、哈希、重建与校验。</para></param>
    /// <param name="rebuildReport">Optional callback receiving the post-publish equivalence report.
    /// <para>可选回调；接收发布后的等价报告。</para></param>
    /// <returns><see langword="true"/> when the image was rebuilt and verified; <see langword="false"/> when no
    /// preservation payload exists, leaving the caller to a fresh directory build.
    /// <para>成功重建并校验时为 <see langword="true"/>；无保留负载时为 <see langword="false"/>，由调用方执行全新目录构建。</para></returns>
    bool TryRestorePreserved(string inputDirectory, string outputPath, string format,
        CancellationToken cancellationToken, Action<IReadOnlyDictionary<string, string>>? rebuildReport);
}