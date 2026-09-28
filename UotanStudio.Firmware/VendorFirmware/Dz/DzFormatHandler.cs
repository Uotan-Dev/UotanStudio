using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.VendorFirmware.Dz;

namespace UotanStudio.Firmware.VendorFirmware.Dz;

/// <summary>Reads and extracts LG DZ/KDZ firmware containers through the shared format-handler contract.
/// <para>通过共享格式处理器契约读取并提取 LG DZ/KDZ 固件容器。</para></summary>
internal sealed partial class DzFormatHandler : IFormatHandler, IRepackMetadataProvider
{
    /// <summary>The display name reported for this handler.
    /// <para>此处理器报告的可读格式名。</para></summary>
    /// <value>The fixed string <c>LG DZ/KDZ Firmware</c>.
    /// <para>固定字符串 <c>LG DZ/KDZ Firmware</c>。</para></value>
    public string FormatName => "LG DZ/KDZ Firmware";

    /// <summary>The lowercase extensions this handler advertises.
    /// <para>此处理器声明的小写扩展名列表。</para></summary>
    /// <value><c>dz</c>, <c>kdz</c> and <c>bin</c>; paths with other extensions can still be accepted when their magic matches.
    /// <para><c>dz</c>、<c>kdz</c> 与 <c>bin</c>；扩展名不同的路径在魔数匹配时仍可被接受。</para></value>
    public IReadOnlyList<string> FileExtensions => ["dz", "kdz", "bin"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">Path to test; blank or missing paths return <see langword="false"/>. <para>待测试的路径；空白或不存在的路径返回 <see langword="false"/>。</para></param>
    /// <returns><see langword="true"/> when the extension is <c>dz</c>/<c>kdz</c> or the leading bytes match a DZ or KDZ magic; <see langword="false"/> for anything else, including read errors.
    /// <para>扩展名为 <c>dz</c>／<c>kdz</c>，或开头字节匹配 DZ/KDZ 魔数时返回 <see langword="true"/>；其余情况（包括读取错误）返回 <see langword="false"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (ext is "dz" or "kdz")
        {
            return true;
        }
        return HeaderProbe.ProbeMagic(filePath, DzConstants.DzFileHeader.Length, 0, DzConstants.DzFileHeader)
            || HeaderProbe.ProbeMagic(filePath, DzConstants.KdzHeaderV0.Length, 0, DzConstants.KdzHeaderV0)
            || HeaderProbe.ProbeMagic(filePath, DzConstants.KdzHeaderV1.Length, 0, DzConstants.KdzHeaderV1)
            || HeaderProbe.ProbeMagic(filePath, DzConstants.KdzHeaderV2.Length, 0, DzConstants.KdzHeaderV2);
    }

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
    /// <param name="filePath">The DZ or KDZ file to inspect. <para>要检查的 DZ 或 KDZ 文件。</para></param>
    /// <param name="cancellationToken">Cancels the read; cancellation is rethrown, never converted to a failure result. <para>取消读取；取消异常会重新抛出，绝不转换为失败结果。</para></param>
    /// <returns>On success, a summary plus entry names and header metadata (KDZ: header type and partition count; DZ: device and chunk count); on parse errors, a failure result carrying the exception message.
    /// <para>成功时返回摘要、条目名与头部元数据（KDZ：头部版本与分区数；DZ：设备与块数）；解析出错时返回携带异常信息的失败结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is signaled. <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsKdz(filePath))
            {
                var (headerType, partitions) = KdzExtractor.Parse(filePath);
                var kdzSummary = $"LG KDZ (header v{headerType}): {partitions.Count} partitions";
                return Task.FromResult(FirmwareOpResult.Ok(kdzSummary,
                    entries: [.. partitions.Select(p => p.Name)],
                    metadata: new Dictionary<string, string>
                    {
                        ["HeaderType"] = headerType.ToString(),
                        ["PartitionCount"] = partitions.Count.ToString()
                    }));
            }

            var (header, chunks) = DzExtractor.Parse(filePath);
            var summary = $"LG DZ: {chunks.Count} chunks (device: {header.GetDevice()})";
            return Task.FromResult(FirmwareOpResult.Ok(summary,
                entries: [.. chunks.Select(c => c.Name)],
                metadata: new Dictionary<string, string>
                {
                    ["Device"] = header.GetDevice(),
                    ["ChunkCount"] = chunks.Count.ToString()
                }));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    /// <param name="filePath">The DZ or KDZ source file. <para>DZ 或 KDZ 源文件。</para></param>
    /// <param name="outputDirectory">Directory receiving the extracted files. <para>接收解出文件的目录。</para></param>
    /// <param name="options">Extraction knobs; <c>null</c> uses defaults, and <c>SelectedEntries</c> filters by exact chunk or partition name. <para>解包选项；为 <c>null</c> 时使用默认值，<c>SelectedEntries</c> 按块或分区的精确名称筛选。</para></param>
    /// <param name="cancellationToken">Cancels extraction; cancellation is rethrown and the previous outputs stay untouched. <para>取消解包；取消异常会重新抛出，已有输出保持不变。</para></param>
    /// <returns>Extraction result published together with unified repack metadata on success; a failure result carrying the exception message when extraction or the metadata pre-check fails.
    /// <para>成功时解包结果会与统一重打包元数据一起发布；解包或元数据预检查失败时返回携带异常信息的失败结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is signaled. <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.Core.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kdz = IsKdz(filePath);
            var selected = options?.CreateSelectedEntrySet();
            Func<string, bool>? filter = selected is null ? null : selected.Contains;
            var count = kdz
                ? KdzExtractor.Extract(filePath, outputDirectory, filter,
                    overwrite: options?.Overwrite ?? true, cancellationToken: cancellationToken)
                : DzExtractor.Extract(filePath, outputDirectory, filter,
                    overwrite: options?.Overwrite ?? true, cancellationToken: cancellationToken);
            var kind = kdz ? "embedded files" : "decompressed chunks";
            return Task.FromResult(FirmwareOpResult.Ok($"Extracted {count} {kind} to {outputDirectory}"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
    /// <param name="inputPath">Extracted directory with complete DZ 2.1 or KDZ v2 preservation metadata.
    /// <para>带完整 DZ 2.1 或 KDZ v2 保留元数据的解包目录。</para></param>
    /// <param name="outputPath">Output container, replaced only after verification.
    /// <para>输出容器，仅在验证成功后替换。</para></param>
    /// <param name="cancellationToken">Cancels reconstruction and publication.
    /// <para>取消重建与发布。</para></param>
    /// <returns>The reconstruction result; unsupported generations and incomplete metadata are rejected.
    /// <para>重建结果；拒绝不支持的版本和不完整的元数据。</para></returns>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => Task.Run(() => RepackKdz(inputPath, outputPath, cancellationToken), cancellationToken);

    private static bool IsKdz(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[8];
        stream.ReadExactly(magic);
        return magic.SequenceEqual(DzConstants.KdzHeaderV0) || magic.SequenceEqual(DzConstants.KdzHeaderV1)
            || magic.SequenceEqual(DzConstants.KdzHeaderV2);
    }
}