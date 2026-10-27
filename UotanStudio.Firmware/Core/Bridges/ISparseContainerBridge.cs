namespace UotanStudio.Firmware.Core.Bridges;

/// <summary>
/// Read-only view of the logical byte range exposed by a block container, without naming the container format.
/// <para>块容器所暴露的逻辑字节范围的只读视图，不指明容器格式。</para>
/// </summary>
public interface ISparseLogicalView : IDisposable
{
    /// <summary>
    /// Gets the seekable logical stream that filesystem formats read through; chunk order is already
    /// resolved and CRC-verified when the view was produced.
    /// <para>获取文件系统格式所读取的可寻址逻辑流；视图产生时块顺序已解析且 CRC 已校验。</para>
    /// </summary>
    /// <value>The logical stream, never <see langword="null"/>. <para>逻辑流，永不为 <see langword="null"/>。</para></value>
    Stream Logical { get; }

    /// <summary>
    /// Gets the container's block size in bytes, which the caller records to reproduce the physical layout.
    /// <para>获取容器块大小（字节）；调用方记录它以复现物理布局。</para>
    /// </summary>
    /// <value>The block size declared by the container header. <para>容器头部声明的块大小。</para></value>
    uint BlockSize { get; }
}

/// <summary>
/// Seam over the sparse container utilities for a bounded context that must read a raw image through a sparse container or publish its own output inside one, instead of referencing another context's concrete classes.
/// <para>稀疏容器工具的服务接口，供需要经稀疏容器读取原始镜像、或把自身输出装入稀疏容器的有界上下文使用，避免引用其他上下文的具象类型。</para>
/// </summary>
public interface ISparseContainerBridge
{
    /// <summary>
    /// Opens the logical view of a sparse container file.
    /// <para>打开稀疏容器文件的逻辑视图。</para>
    /// </summary>
    /// <param name="imagePath">Path to the container file. <para>容器文件路径。</para></param>
    /// <returns>A view the caller must dispose. <para>调用方必须释放的视图。</para></returns>
    /// <exception cref="InvalidDataException">The file is not a readable sparse container.</exception>
    ISparseLogicalView OpenLogicalView(string imagePath);

    /// <summary>
    /// Opens the logical view of a sparse container held in an in-memory or seekable stream.
    /// <para>打开内存中或可寻址流内稀疏容器的逻辑视图。</para>
    /// </summary>
    /// <param name="container">Seekable container stream; the view reads from it and the caller keeps ownership. <para>可定位的容器流；视图从其中读取，所有权仍归调用方。</para></param>
    /// <returns>A view the caller must dispose. <para>调用方必须释放的视图。</para></returns>
    /// <exception cref="InvalidDataException">The stream is not a readable sparse container.</exception>
    ISparseLogicalView OpenLogicalView(Stream container);

    /// <summary>
    /// Flattens one or more sparse containers into a single raw image file.
    /// <para>把一个或多个稀疏容器展平为单个原始镜像文件。</para>
    /// </summary>
    /// <param name="inputFiles">Container files in concatenation order. <para>按拼接顺序排列的容器文件。</para></param>
    /// <param name="outputFile">Raw image destination. <para>原始镜像目标。</para></param>
    /// <param name="cancellationToken">Cancels the conversion. <para>取消转换。</para></param>
    void ConvertSparseToRaw(IEnumerable<string> inputFiles, string outputFile, CancellationToken cancellationToken);

    /// <summary>
    /// Converts a raw image into a sparse container and publishes it atomically after structural validation.
    /// <para>把原始镜像转换为稀疏容器，并在结构校验通过后原子发布。</para>
    /// </summary>
    /// <param name="inputFile">Raw image source. <para>原始镜像来源。</para></param>
    /// <param name="outputFile">Container destination replaced only on success. <para>仅在成功后替换的容器目标。</para></param>
    /// <param name="cancellationToken">Cancels the conversion. <para>取消转换。</para></param>
    void ConvertRawToSparseAtomic(string inputFile, string outputFile, CancellationToken cancellationToken);

    /// <summary>
    /// Converts a raw stream into a sparse container while the caller owns both streams, and validates the
    /// produced container before the caller publishes it.
    /// <para>在调用方持有两个流的前提下把原始流转换为稀疏容器，并在调用方发布之前校验生成的容器。</para>
    /// </summary>
    /// <param name="input">Raw image source. <para>原始镜像来源。</para></param>
    /// <param name="output">Container destination written by this call. <para>由本次调用写出的容器目标。</para></param>
    /// <param name="blockSize">Block size for the produced container. <para>产出的容器的块大小。</para></param>
    /// <param name="length">Expected raw length. <para>期望的原始长度。</para></param>
    /// <param name="cancellationToken">Cancels the conversion. <para>取消转换。</para></param>
    /// <param name="validationOutput">
    /// Optional readable stream holding the produced bytes for validation; <see langword="null"/> validates
    /// <paramref name="output"/> itself.
    /// <para>可选的、承载生成字节用于校验的可读流；传 <see langword="null"/> 时直接校验 <paramref name="output"/>。</para>
    /// </param>
    void ConvertRawToSparseValidated(Stream input, Stream output, uint blockSize, long length, CancellationToken cancellationToken, Stream? validationOutput);
}