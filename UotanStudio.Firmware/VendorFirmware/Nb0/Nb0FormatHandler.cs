using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.VendorFirmware.Nb0.Models;

namespace UotanStudio.Firmware.VendorFirmware.Nb0;

/// <summary>
/// Format handler for FIH Mobile NB0 firmware containers. It dispatches to <c>Nb0Parser</c>,
/// <c>Nb0Extractor</c> and <c>Nb0Packer</c>, and marks the unpacked layout as directory-repackable
/// through <see cref="IRepackMetadataProvider"/>.
/// <para>FIH Mobile NB0 固件容器的格式处理器。分派到 <c>Nb0Parser</c>、<c>Nb0Extractor</c> 与 <c>Nb0Packer</c>，
/// 并通过 <see cref="IRepackMetadataProvider"/> 将解包布局标记为可按目录重打包。</para>
/// </summary>
public sealed class Nb0FormatHandler : IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
    {
        metadata.CanPackDirectory = true;
    }

    /// <summary>Display name of the handled format: <c>FIH Mobile NB0 Firmware</c>.
    /// <para>所处理格式的显示名：<c>FIH Mobile NB0 Firmware</c>。</para></summary>
    /// <value>The fixed format name. <para>固定的格式名称。</para></value>
    public string FormatName => "FIH Mobile NB0 Firmware";

    /// <summary>File extensions owned by this handler.
    /// <para>此处理器拥有的文件扩展名。</para></summary>
    /// <value>A single-entry list containing <c>nb0</c>. <para>仅含 <c>nb0</c> 的单元素列表。</para></value>
    public IReadOnlyList<string> FileExtensions => ["nb0"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">
    /// Candidate file path; a missing or unreadable path is reported as no match.
    /// <para>候选文件路径；不存在或不可读的路径视为不匹配。</para>
    /// </param>
    /// <returns><see langword="true"/> when the leading little-endian uint32 entry count lies within
    /// 1..1024, the 64-byte-per-entry header table fits in the file, and every entry's data offset plus
    /// size fits in the data section; <see langword="false"/> otherwise, including on any I/O error.
    /// <para>当前导小端 uint32 条目数位于 1..1024、每条目 64 字节的头部表能容纳在文件内，
    /// 且每个条目的数据偏移加大小落在数据区内时返回 <see langword="true"/>；否则（含任何 I/O 错误）返回 <see langword="false"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }
        try
        {

            using var stream = File.OpenRead(filePath);
            Span<byte> countBytes = stackalloc byte[4];
            if (stream.Read(countBytes) != 4)
            {
                return false;
            }
            var count = BitConverter.ToUInt32(countBytes);
            if (count is 0 or > 1024)
            {
                return false;
            }
            var required = 4L + (count * Nb0EntryHeader.StructSize);
            if (required > stream.Length)
            {
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                var entry = new byte[Nb0EntryHeader.StructSize];
                if (stream.Read(entry) != entry.Length)
                {
                    return false;
                }
                var offset = BitConverter.ToUInt32(entry[..4]);
                var size = BitConverter.ToUInt32(entry, 4);
                if ((long)offset + size > stream.Length - required)
                {
                    return false;
                }
            }

            return true;
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
    /// Path to the NB0 file. <para>NB0 文件路径。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Accepted for interface symmetry; parsing runs synchronously and does not observe it.
    /// <para>为接口对称性保留；解析同步执行且不观察该令牌。</para>
    /// </param>
    /// <returns>A completed task: a success result whose summary lists entry count, total size, an
    /// optional inferred type and MD5 record count, with entry names and the <c>EntryCount</c>,
    /// <c>TotalSize</c>, <c>InferredFirmwareType</c>, <c>Md5RecordCount</c> metadata keys; a failure
    /// result carrying the exception message when parsing throws.
    /// <para>已完成的任务：成功结果的摘要列出条目数、总大小、可选推断类型与 MD5 记录数，并带条目名及
    /// <c>EntryCount</c>、<c>TotalSize</c>、<c>InferredFirmwareType</c>、<c>Md5RecordCount</c> 元数据键；
    /// 解析抛出时返回携带异常消息的失败结果。</para></returns>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = Nb0Parser.Parse(filePath);
            var summary = $"NB0 firmware: {metadata.Entries.Count} entries, {metadata.TotalSize} bytes" +
                          (string.IsNullOrEmpty(metadata.InferredFirmwareType) ? "" : $", type: {metadata.InferredFirmwareType}") +
                          (metadata.HasMd5Records ? $", {metadata.Md5Records.Count} MD5 records" : "");
            return Task.FromResult(FirmwareOpResult.Ok(summary,
                entries: [.. metadata.Entries.Select(e => e.Name)],
                metadata: new Dictionary<string, string>
                {
                    ["EntryCount"] = metadata.Entries.Count.ToString(),
                    ["TotalSize"] = metadata.TotalSize.ToString(),
                    ["InferredFirmwareType"] = metadata.InferredFirmwareType ?? string.Empty,
                    ["Md5RecordCount"] = metadata.Md5Records.Count.ToString()
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
    /// Path to the NB0 file. <para>NB0 文件路径。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Destination directory receiving the extracted entries and the repack metadata file.
    /// <para>接收提取条目与重打包元数据文件的目标目录。</para>
    /// </param>
    /// <param name="options">
    /// Unpack options; only <c>Overwrite</c> is consumed (default <see langword="true"/>), and with it
    /// disabled a pre-existing unrecognized metadata file fails the operation before extraction.
    /// <para>解包选项；仅使用 <c>Overwrite</c>（默认 <see langword="true"/>）。关闭时，若已存在无法识别的
    /// 元数据文件，会在解包前使操作失败。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before extraction and during metadata publication; cancellation is rethrown to the caller.
    /// <para>在解包前与元数据发布期间检查；取消会重新抛给调用方。</para>
    /// </param>
    /// <returns>A success result summarizing the extracted entry count after all entries are written
    /// and repack metadata is published; a failure result when any entry failed or metadata validation
    /// rejected the destination. Per-entry extraction errors never throw and surface as a failure result.
    /// <para>所有条目写入并发布重打包元数据后的成功结果，摘要包含提取条目数；任一条目失败或元数据校验
    /// 拒绝目标目录时返回失败结果。逐条目提取错误不抛出，而是以失败结果呈现。</para></returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled before or during the operation.
    /// <para><paramref name="cancellationToken"/> 在操作前或操作中被触发。</para>
    /// </exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var extractor = new Nb0Extractor();
            var result = extractor.Extract(filePath, outputDirectory, new ExtractionOptions
            {
                Overwrite = options?.Overwrite ?? true
            });
            return !result.Success
                ? Task.FromResult(FirmwareOpResult.Fail(string.Join("; ", result.Errors)))
                : Task.FromResult(FirmwareOpResult.Ok(
                $"Extracted {result.ExtractedFileCount} entries to {outputDirectory}" +
                (result.Warnings.Count > 0 ? $" ({result.Warnings.Count} warnings)" : "")));
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
    /// Directory to pack recursively (repack metadata files skipped, slash-separated relative entry
    /// names), or a single file to store as one entry named after its file name.
    /// <para>递归打包的目录（跳过重打包元数据文件，条目名为斜杠分隔的相对路径），或作为单条目存入、
    /// 以文件名命名的单个文件。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination NB0 file; an existing file is replaced. <para>目标 NB0 文件；已存在的文件会被替换。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Accepted for interface symmetry; packing runs synchronously and does not observe it.
    /// <para>为接口对称性保留；打包同步执行且不观察该令牌。</para>
    /// </param>
    /// <returns>A completed task: a success result naming the packed file count or file name; a failure
    /// result when <paramref name="inputPath"/> exists as neither directory nor file, or when packing
    /// throws — I/O and size-mismatch errors are converted to a failure message, never rethrown.
    /// <para>已完成的任务：成功结果注明打包文件数或文件名；当 <paramref name="inputPath"/> 既不是目录也不是
    /// 文件时返回失败结果；打包抛出的 I/O 与大小不匹配错误会转换为失败消息，绝不重新抛出。</para></returns>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (Directory.Exists(inputPath))
            {
                Nb0Packer.PackFromDirectory(inputPath, outputPath);
                var count = Directory.EnumerateFiles(inputPath, "*", SearchOption.AllDirectories).Count();
                return Task.FromResult(FirmwareOpResult.Ok($"Packed {count} files to {outputPath}"));
            }
            if (File.Exists(inputPath))
            {
                var builder = Nb0Packer.CreateBuilder();
                builder.AddFile(inputPath);
                builder.Build(outputPath);
                return Task.FromResult(FirmwareOpResult.Ok($"Packed {Path.GetFileName(inputPath)} to {outputPath}"));
            }
            return Task.FromResult(FirmwareOpResult.Fail("Input path does not exist."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }
}