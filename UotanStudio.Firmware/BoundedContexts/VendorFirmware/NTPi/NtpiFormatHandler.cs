using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi;

/// <summary>
/// Detection, metadata reading, unpacking and packing entry point for NTPI firmware packages.
/// <para>NTPI 固件包的检测、元数据读取、解包与打包入口。</para>
/// </summary>
/// <remarks>
/// Every operation returns an outcome instead of throwing: exceptions from the underlying reader and
/// writer are converted into <see cref="FirmwareOpResult.Fail(string)"/> results.
/// <para>所有操作都返回结果对象而不抛出异常：底层读取器与写入器抛出的异常会被转换为 <see cref="FirmwareOpResult.Fail(string)"/> 结果。</para>
/// </remarks>
public sealed class NtpiFormatHandler : IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
    {
        metadata.CanPackDirectory = true;
    }

    /// <summary>Display name of the NTPI package format.
    /// <para>NTPI 包格式的显示名称。</para></summary>
    /// <value>The constant <c>NTPI Firmware Package</c>, also used as the <see cref="FormatDetector"/> registration key.
    /// <para>常量 <c>NTPI Firmware Package</c>，同时作为 <see cref="FormatDetector"/> 的注册键。</para></value>
    public string FormatName => "NTPI Firmware Package";

    /// <summary>Extensions this handler claims before content inspection.
    /// <para>在内容检查之前本处理器声明认领的扩展名。</para></summary>
    /// <value><c>ntpi</c>, <c>bin</c> and <c>fw</c> without leading dots; the last two are shared with other formats, so the <c>NTPI</c> magic decides.
    /// <para>不含前导点的 <c>ntpi</c>、<c>bin</c> 与 <c>fw</c>；后两者与其他格式共用，因此以 <c>NTPI</c> 魔数为准。</para></value>
    public IReadOnlyList<string> FileExtensions => ["ntpi", "bin", "fw"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path to the candidate file; a <see langword="null"/> or blank path is reported as no match without touching the file system.
    /// <para>候选文件路径；路径为 <see langword="null"/> 或空白时直接判定为不匹配，不会访问文件系统。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the file exists and either uses the <c>.ntpi</c> extension (case-insensitive) or starts with the four ASCII bytes <c>NTPI</c>; <see langword="false"/> when the path is blank, the file is absent, fewer than four bytes could be read, or the open/read fails with any exception.
    /// <para>文件存在，且扩展名为 <c>.ntpi</c>（忽略大小写）或起始四个 ASCII 字节为 <c>NTPI</c> 时返回 <see langword="true"/>；路径为空、文件不存在、读不足四个字节或打开/读取抛出任何异常时返回 <see langword="false"/>。</para>
    /// </returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (ext == "ntpi")
        {
            return true;
        }
        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> magic = stackalloc byte[4];
            return fs.Read(magic) == 4 && magic.SequenceEqual("NTPI"u8);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
    /// <param name="filePath">
    /// NTPI package to inspect; a missing or malformed package yields a failure result rather than an exception.
    /// <para>待检查的 NTPI 包；文件缺失或格式错误时返回失败结果，而不是抛出异常。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Accepted for interface uniformity; the underlying <see cref="NtpiReader"/> is synchronous, so the read runs to completion and the token is never observed.
    /// <para>仅为保持接口一致而保留；底层 <see cref="NtpiReader"/> 是同步实现，读取会执行到底且不会检查该令牌。</para>
    /// </param>
    /// <returns>
    /// A completed task. Success carries a <c>NTPI v&amp;lt;major&amp;gt;.&amp;lt;minor&amp;gt;.&amp;lt;patch&amp;gt;</c> summary, the file-entry names in <see cref="FirmwareOpResult.Entries"/>, and the keys <c>MajorVersion</c>, <c>MinorVersion</c>, <c>PatchVersion</c>, <c>RegionCount</c> and <c>FileCount</c> in <see cref="FirmwareOpResult.Metadata"/>; any thrown error becomes a failure result whose summary is the exception message.
    /// <para>已完成的任务。成功时摘要形如 <c>NTPI v&amp;lt;major&amp;gt;.&amp;lt;minor&amp;gt;.&amp;lt;patch&amp;gt;</c>，<see cref="FirmwareOpResult.Entries"/> 为文件条目名，<see cref="FirmwareOpResult.Metadata"/> 包含 <c>MajorVersion</c>、<c>MinorVersion</c>、<c>PatchVersion</c>、<c>RegionCount</c> 与 <c>FileCount</c>；任何异常都会变成以异常消息为摘要的失败结果。</para>
    /// </returns>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            using var reader = NtpiReader.CreateDefault();
            var info = reader.ReadInfo(filePath);
            var summary = $"NTPI v{info.MajorVersion}.{info.MinorVersion}.{info.PatchVersion}: " +
                          $"{info.Regions.Count} regions, {info.FileEntries.Count} files";
            return Task.FromResult(FirmwareOpResult.Ok(summary,
                entries: [.. info.FileEntries.Select(e => e.Name)],
                metadata: new Dictionary<string, string>
                {
                    ["MajorVersion"] = info.MajorVersion.ToString(),
                    ["MinorVersion"] = info.MinorVersion.ToString(),
                    ["PatchVersion"] = info.PatchVersion.ToString(),
                    ["RegionCount"] = info.Regions.Count.ToString(),
                    ["FileCount"] = info.FileEntries.Count.ToString()
                }));
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
    /// NTPI package to unpack; the input file is opened read-only and never modified.
    /// <para>待解包的 NTPI 包；输入文件以只读方式打开，不会被修改。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Destination directory, created when absent. Each extracted file is written under its base name only, so entries that share a name collide and the last one wins.
    /// <para>目标目录，不存在时创建。每个解出的文件只按基础名写入，因此同名条目会相互覆盖，以最后一个为准。</para>
    /// </param>
    /// <param name="options">
    /// Extraction knobs; <see langword="null"/> uses the defaults. Only <see cref="UnpackOptions.Progress"/> is consulted, and it receives a percentage derived from completed files. Entry selection is not honoured: every file is extracted. <see cref="UnpackOptions.Overwrite"/> applies to the repack metadata file that is written afterwards — when it is <see langword="false"/> and that file already exists, the operation fails before any extraction.
    /// <para>解包参数；传 <see langword="null"/> 时使用默认值。只有 <see cref="UnpackOptions.Progress"/> 会被使用，其收到按已完成文件数换算的百分比。不支持条目筛选，所有文件都会被解出。<see cref="UnpackOptions.Overwrite"/> 只作用于随后写入的重打包元数据文件——为 <see langword="false"/> 且该文件已存在时，操作在任何解包之前就失败。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before extraction and rethrown to the caller; cancellation is never converted into a failure result. The synchronous reader does not poll the token mid-file.
    /// <para>在解包前检查并会重新抛给调用方；取消绝不转换为失败结果。同步读取器不会在单个文件处理过程中轮询该令牌。</para>
    /// </param>
    /// <returns>
    /// A task whose result reports the recursive count of files in <paramref name="outputDirectory"/> after extraction, including files that were already there. On success the repack metadata for the package is written into the same directory; reader faults such as a missing Region6 or an exceeded output limit become a failure result carrying the exception message.
    /// <para>返回的任务结果在成功时报告解包后 <paramref name="outputDirectory"/> 内的递归文件数（包含原有文件），并把该包的重复打包元数据写入同一目录；Region6 缺失、超出输出上限等读取器错误会变成以异常消息为摘要的失败结果。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was already signalled when the call started.
    /// <para>调用开始时 <paramref name="cancellationToken"/> 已被取消。</para>
    /// </exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            using var reader = NtpiReader.CreateDefault();
            var progress = options?.Progress is null ? null : new Progress<NtpiExtractionProgress>(p => options.Progress.Report(p.CompletedFiles * 100.0 / Math.Max(1, p.TotalFiles), p.CurrentFileName));
            reader.Unpack(filePath, outputDirectory, progress, fileFilter: null);
            var count = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories).Count();
            return Task.FromResult(FirmwareOpResult.Ok($"Extracted {count} files to {outputDirectory}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
    /// <param name="inputPath">
    /// Source directory whose files become the package contents; any other kind of path yields a failure result.
    /// <para>作为包内容来源的目录；其他类型的路径会返回失败结果。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination package file, published atomically and replaced when it already exists.
    /// <para>目标包文件，以原子方式发布，已存在时会被替换。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Accepted for interface uniformity; the underlying <see cref="NtpiWriter"/> is synchronous, so the pack runs to completion and the token is never observed.
    /// <para>仅为保持接口一致而保留；底层 <see cref="NtpiWriter"/> 是同步实现，打包会执行到底且不会检查该令牌。</para>
    /// </param>
    /// <returns>
    /// A task whose result reports the recursive count of files under <paramref name="inputPath"/>. Packing uses the default AES-CBC/LZMA pipeline and version 1.3.0; a missing directory, an unregistered version, or any writer fault produces a failure result carrying the exception message instead of an exception.
    /// <para>返回的任务结果报告 <paramref name="inputPath"/> 下的递归文件数。打包使用默认的 AES-CBC/LZMA 流水线和版本 1.3.0；目录缺失、版本未注册或写入器出错时返回以异常消息为摘要的失败结果，而不抛出异常。</para>
    /// </returns>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Directory.Exists(inputPath))
            {
                return Task.FromResult(FirmwareOpResult.Fail("Input path must be a directory for NTPI packing."));
            }
            using var writer = NtpiWriter.CreateDefault();
            writer.Pack(inputPath, outputPath);
            var count = Directory.EnumerateFiles(inputPath, "*", SearchOption.AllDirectories).Count();
            return Task.FromResult(FirmwareOpResult.Ok($"Packed {count} files to {outputPath}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }
}