namespace UotanStudio.Firmware.Core.Repacking;

/// <summary>
/// Supplies format-owned rebuild configuration and optional preservation artifacts.
/// The shared metadata lifecycle owns context resolution and publishes the JSON only after capture succeeds.
/// <para>由具体格式提供重建配置与可选的保留产物。共享元数据生命周期负责上下文解析，并在捕获成功后才发布 JSON。</para>
/// </summary>
public interface IRepackMetadataProvider
{
    /// <summary>
    /// Sets configuration, packing options and directory support from the source format.
    /// Packing notes supplied here are appended to the common capability description.
    /// <para>根据来源格式写入配置、打包选项与目录支持标记。此处提供的打包说明会追加到通用能力描述之后。</para>
    /// </summary>
    /// <param name="metadata">The metadata instance being assembled before publication.
    /// <para>发布前正在组装的元数据实例。</para></param>
    /// <param name="sourcePath">
    /// Path of the source image being extracted. <para>正在解包的源镜像路径。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Extraction output directory. <para>解包输出目录。</para>
    /// </param>
    void ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory);

    /// <summary>
    /// Captures format-specific artifacts before metadata publication, using the already resolved directory.
    /// Formats without preservation artifacts use the default implementation.
    /// <para>在元数据发布之前，使用已解析的目录捕获格式专属产物。无保留产物的格式使用默认实现。</para>
    /// </summary>
    /// <param name="metadata">The metadata instance whose preservation fields the capture fills.
    /// <para>由捕获流程填充保留字段的元数据实例。</para></param>
    /// <param name="sourcePath">
    /// Path of the source image being extracted. <para>正在解包的源镜像路径。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Extraction output directory. <para>解包输出目录。</para>
    /// </param>
    /// <param name="metadataDirectory">
    /// Resolved directory in which the metadata JSON will be published. <para>元数据 JSON 即将发布到的已解析目录。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels artifact capture. <para>取消产物捕获。</para>
    /// </param>
    /// <returns>
    /// A task completed once the artifacts are captured; the default implementation completes immediately.
    /// <para>产物捕获完成后结束的任务；默认实现立即完成。</para>
    /// </returns>
    Task CaptureRepackMetadataAsync(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Gets whether the format captures its rebuild configuration from the source container alone, so that
    /// <see cref="UnpackOptions.MetadataOnly"/> can publish metadata without writing partition files.
    /// Formats whose capture reads the already extracted directory keep the default <see langword="false"/>.
    /// <para>获取该格式是否只依赖来源容器即可完成重建配置捕获，从而允许 <see cref="UnpackOptions.MetadataOnly"/>
    /// 在不写出分区文件的情况下发布元数据。捕获过程会读取已解出目录的格式保持默认的 <see langword="false"/>。</para>
    /// </summary>
    /// <value><see langword="true"/> when a metadata-only unpack is complete and truthful, <see langword="false"/> otherwise.
    /// <para>仅元数据解包既完整又属实时为 <see langword="true"/>，否则为 <see langword="false"/>。</para></value>
    bool SupportsMetadataOnlyUnpack => false;
}