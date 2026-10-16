using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;

namespace UotanStudio.Firmware.VendorFirmware.Pac;

/// <summary>
/// Handles SPRD/UNISOC PAC firmware packages for metadata reading, extraction and packing through
/// <see cref="PacBuilder"/>; the packed output is a canonical unsigned PAC rather than a byte-for-byte
/// reproduction of the OEM package.
/// <para>处理 SPRD/UNISOC PAC 固件包的元数据读取、解包与经 <see cref="PacBuilder"/> 的打包；
/// 打包产出的是规范的无签名 PAC，而不是 OEM 原包的逐字节复现。</para>
/// </summary>
internal sealed class PacFormatHandler : IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
    {
        metadata.CanPackDirectory = true;
        metadata.PackingNotes = "Creates a canonical unsigned PAC from the extracted directory; original OEM header fields and device acceptance are not preserved.";
    }

    /// <summary>Display name of the handled format.
    /// <para>所处理格式的显示名称。</para></summary>
    /// <value>The fixed string <c>"SPRD/UNISOC PAC Firmware"</c>.
    /// <para>固定字符串 <c>"SPRD/UNISOC PAC Firmware"</c>。</para></value>
    public string FormatName => "SPRD/UNISOC PAC Firmware";

    /// <summary>File extensions associated with this format.
    /// <para>与此格式关联的文件扩展名。</para></summary>
    /// <value><c>"pac"</c> and <c>"bin"</c>.
    /// <para><c>"pac"</c> 与 <c>"bin"</c>。</para></value>
    public IReadOnlyList<string> FileExtensions => ["pac", "bin"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path of the file to probe; null, blank or missing paths are reported as unmatched.
    /// <para>要探测的文件路径；路径为空、空白或文件不存在时视为不匹配。</para>
    /// </param>
    /// <returns><see langword="true"/> when the extension is <c>.pac</c> (accepted without opening the file)
    /// or the file is long enough to carry the trailer signature <c>0xFFFAFFFA</c> at offset
    /// <c>SprdHead.Size - 8</c>; any read error yields <see langword="false"/>.
    /// <para>当扩展名为 <c>.pac</c>（不经打开文件即接受），或文件长度足以在偏移 <c>SprdHead.Size - 8</c>
    /// 处携带尾部签名 <c>0xFFFAFFFA</c> 时返回 <see langword="true"/>；任何读取错误都返回 <see langword="false"/>。</para>
    /// </returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (ext == "pac")
        {
            return true;
        }
        return HeaderProbe.ProbeMagic(filePath, SprdHead.Size, SprdHead.Size - 8, 0xFFFAFFFAu);
    }

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
    /// <param name="filePath">
    /// PAC package to list; read errors are folded into the returned result.
    /// <para>要列出条目的 PAC 包；读取错误会折叠进返回结果。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Accepted for interface uniformity; the synchronous entry listing does not observe it.
    /// <para>仅为接口一致性而保留；同步的条目枚举不会检查它。</para>
    /// </param>
    /// <returns>
    /// On success, a summary reporting the entry count, per-entry names and a <c>FileCount</c> metadata value.
    /// On any read failure, a failed result carrying the exception message; the method never throws.
    /// <para>成功时返回包含条目数量摘要、各条目名称与 <c>FileCount</c> 元数据值的结果；
    /// 任何读取失败都会返回携带异常消息的失败结果，本方法不会抛出异常。</para>
    /// </returns>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var entries = PacExtractor.List(filePath);
            var summary = $"SPRD PAC: {entries.Count} files";
            return Task.FromResult(FirmwareOpResult.Ok(summary,
                entries: [.. entries.Select(e => e.Name).Where(n => !string.IsNullOrEmpty(n))],
                metadata: new Dictionary<string, string> { ["FileCount"] = entries.Count.ToString() }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    /// <param name="filePath">
    /// Source PAC package; the file stays read-only to this operation and is closed when extraction ends.
    /// <para>源 PAC 包；本操作只读取该文件，提取结束后即关闭。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Directory that receives the extracted entries and, on success, the unified repack metadata record.
    /// <para>接收提取条目以及成功后统一重打包元数据记录的目录。</para>
    /// </param>
    /// <param name="options">
    /// Optional extraction settings; when <c>VerifyChecksums</c> is set, header and data checksums must both pass
    /// before any file is written, and a selected-entry filter restricts which entries are extracted.
    /// <para>可选提取设置；设置 <c>VerifyChecksums</c> 时，必须先通过头部与数据校验和才会写入任何文件；
    /// 条目选择过滤器可限制实际提取的条目。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Closes the extraction early; a cancellation observed during extraction is converted into a failed result,
    /// while one raised before the run starts or during metadata publication faults the returned task with
    /// <see cref="OperationCanceledException"/>.
    /// <para>使提取提前结束：提取过程中捕获的取消会转换为失败结果，
    /// 运行开始前或元数据发布期间触发的取消则让返回的任务以 <see cref="OperationCanceledException"/> 失败。</para>
    /// </param>
    /// <returns>
    /// A result reporting the number of extracted files, or a failure carrying the exception message;
    /// apart from the cancellation case above the method does not throw.
    /// <para>报告已提取文件数量的结果，或携带异常消息的失败结果；除上述取消情形外，本方法不会抛出异常。</para>
    /// </returns>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options?.VerifyChecksums == true)
            {
                var (headOk, dataOk) = PacExtractor.Check(filePath, cancellationToken);
                if (!headOk || !dataOk)
                {
                    return Task.FromResult(FirmwareOpResult.Fail(
                        $"PAC checksum verification failed (header: {headOk}, data: {dataOk})."));
                }
            }

            var selected = options?.CreateSelectedEntrySet();
            var count = PacExtractor.Extract(filePath, outputDirectory,
                selected is null ? null : selected.Contains, cancellationToken);
            return Task.FromResult(FirmwareOpResult.Ok($"Extracted {count} files to {outputDirectory}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Builds a canonical unsigned PAC package from the extracted input directory.
    /// <para>由解包后的输入目录构建规范的无签名 PAC 包。</para>
    /// </summary>
    /// <remarks>
    /// <see cref="PacPackOptions"/> stays unpinned here: callers get the builder defaults, because no host or
    /// MCP entry exposes per-package PAC options yet.
    /// <para>这里不指定 <see cref="PacPackOptions"/>：调用方获得构建器默认值，因为目前没有宿主或 MCP 入口暴露 PAC 的逐包选项。</para>
    /// </remarks>
    /// <param name="inputPath">
    /// Extracted directory that <see cref="PacBuilder"/> walks to assemble the package.
    /// <para><see cref="PacBuilder"/> 遍历以组装包的解包目录。</para>
    /// </param>
    /// <param name="outputPath">
    /// PAC file created or overwritten by the builder.
    /// <para>由构建器创建或覆盖的 PAC 文件。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Observed by the builder between file copies; cancellation is rethrown.
    /// <para>构建器在文件复制之间检查该令牌；取消会重新抛出。</para>
    /// </param>
    /// <returns>
    /// A result reporting the packed file entry count, or a failure result carrying the underlying error message.
    /// <para>结果报告打包的条目数量；失败时携带底层错误信息。</para>
    /// </returns>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var count = PacBuilder.Build(inputPath, outputPath, cancellationToken: cancellationToken);
            return Task.FromResult(FirmwareOpResult.Ok($"Packed {count} files to {outputPath}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }
}