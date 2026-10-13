using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.BlockImage.Gpt;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Builds the firmware-format MCP tools backed by <see cref="FirmwareFormatRegistry"/> and
/// <see cref="FormatDetector"/>, covering every registered handler with one generic surface.
/// <para>基于 <see cref="FirmwareFormatRegistry"/> 与 <see cref="FormatDetector"/> 构建固件格式
/// MCP 工具，用一组通用工具覆盖全部已注册格式处理器。</para>
/// Every delegate ends with the ambient <see cref="IProgress{T}"/> and
/// <see cref="CancellationToken"/> parameters the MCP server binds itself; neither appears in the
/// emitted <c>inputSchema</c>, so clients cannot pass them but cancellation and progress propagate
/// into the format handlers that support them.
/// <para>每个委托末尾都是 MCP 服务器自行绑定的环境参数 <see cref="IProgress{T}"/> 与
/// <see cref="CancellationToken"/>；两者都不出现在生成的 <c>inputSchema</c> 中，客户端无法传入，
/// 但取消与进度会传导到支持它们的格式处理器。</para>
/// </summary>
internal static class FirmwareToolFactory
{
    internal static McpServerTool CreateListFormats() =>
        McpServerTool.Create(
            (CancellationToken cancellationToken) => ToolGuard.RunAsync(
                static token => Task.FromResult(FirmwareToolHandlers.ListFormats()), cancellationToken), new McpServerToolCreateOptions
                {
                    Name = "list_formats",
                    Description = "List every firmware format handler registered in UotanStudio "
                    + "(name, file extensions, read/pack capability).",
                    ReadOnly = true,
                    Idempotent = true,
                    OpenWorld = false,
                    Destructive = false
                });

    internal static McpServerTool CreateDetectFormat(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("Absolute path inside the sandbox, or sandbox-relative path.")] string path,
             CancellationToken cancellationToken) => ToolGuard.RunAsync(
                token => Task.FromResult(FirmwareToolHandlers.DetectFormat(sandbox, path)), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "detect_format",
                Description = "Detect which firmware format handler can process a file "
                    + "(extension match first, then header sniffing).",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateReadMetadata(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("Absolute path inside the sandbox, or sandbox-relative path.")] string path,
             CancellationToken cancellationToken) => ToolGuard.RunAsync(
                token => FirmwareToolHandlers.ReadMetadataAsync(sandbox, path, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "read_metadata",
                Description = "Read format metadata for a firmware file without unpacking it.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateReadGpt(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("Absolute path inside the sandbox, or sandbox-relative path.")] string path,
             [Description("Logical block size; defaults to 512.")] int blockSize = 512,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => FirmwareToolHandlers.ReadGptAsync(sandbox, path, blockSize, token), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "read_gpt",
                Description = "Validate both GPT headers and entry arrays of a whole-device image and "
                    + "return the partition list (index, name, type/id GUIDs, LBA range, byte offset and "
                    + "length) without reading partition contents.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateUnpackFirmware(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("Absolute path inside the sandbox, or sandbox-relative path.")] string inputPath,
             [Description("Output directory; created when missing. Must stay inside the sandbox.")] string outputDirectory,
             [Description("Per-file decompression budget in bytes for formats without a trusted output size.")] long maximumOutputBytes = 64L * 1024 * 1024 * 1024,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => FirmwareToolHandlers.UnpackAsync(sandbox, inputPath, outputDirectory, maximumOutputBytes, token),
                cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "unpack_firmware",
                Description = "Unpack a firmware file (boot image, sparse, ext4/erofs/f2fs image, "
                    + "payload, APK/DEX, vendor formats, ...) into an output directory. "
                    + "Send a progress token to receive notifications/progress and cancel the request to stop the write.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreatePackFirmware(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("Input directory or file, depending on the detected format.")] string inputPath,
             [Description("Target file path for the packed image. Must stay inside the sandbox.")] string outputPath,
             IProgress<ProgressNotificationValue>? progress = null,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => FirmwareToolHandlers.PackAsync(sandbox, inputPath, outputPath, progress, token),
                cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "pack_firmware",
                Description = "Pack an unpacked directory/file back into the image format "
                    + "detected from the input (only writable format handlers support this). "
                    + "Filesystem handlers report byte progress and honor cancellation.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateExtractContainerEntries(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("Container file path inside the sandbox (super image, payload.bin, or ZIP).")] string containerPath,
             [Description("Inner entry names to extract: super/payload partition names, or full ZIP member paths.")] string[] entries,
             [Description("Output directory; created when missing. Must stay inside the sandbox.")] string outputDirectory,
             [Description("Set when a ZIP's payload partitions alone are selected.")] bool fromPayloadOnly = false,
             [Description("Decompressed output budget in bytes; exceeded is rejected.")] long maximumOutputBytes = ContainerEntryExtractor.DefaultMaximumOutputBytes,
             IProgress<ProgressNotificationValue>? progress = null,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => FirmwareToolHandlers.ExtractAsync(sandbox, containerPath, entries, outputDirectory,
                    fromPayloadOnly, maximumOutputBytes, progress, token),
                cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "extract_container_entries",
                Description = "Extract selected inner entries from a container (super logical partitions, "
                    + "payload partitions, or ZIP members) into standalone image files. "
                    + "Reports byte-level progress and honors cancellation.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateMergeSparseChunks(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("Any numbered member of a *.sparsechunk.NN set; siblings are resolved from its name.")] string selectedChunkPath,
             [Description("Destination image path. Must stay inside the sandbox.")] string outputPath,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => FirmwareToolHandlers.MergeSparseChunksAsync(sandbox, selectedChunkPath, outputPath, token),
                cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "merge_sparse_chunks",
                Description = "Merge a *.sparsechunk.NN flash set (raw slices or self-contained sparse pieces) "
                    + "into one image, publishing through a temporary file and honoring cancellation.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });

    internal static McpServerTool CreateSplitSparseChunks(PathSandbox sandbox) =>
        McpServerTool.Create(
            ([Description("Raw image path inside the sandbox; sparse containers are rejected.")] string inputPath,
             [Description("Piece name prefix; the two-digit index is appended after .sparsechunk.")] string basePath,
             [Description("Physical bytes per piece except the final remainder.")] long chunkSizeBytes,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => FirmwareToolHandlers.SplitSparseChunksAsync(sandbox, inputPath, basePath, chunkSizeBytes, token),
                cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "split_sparse_chunks",
                Description = "Split a raw image into fixed-size *.sparsechunk.NN pieces, each published atomically. "
                    + "Self-contained sparse inputs are rejected; re-split them through the sparse converter.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });
}

/// <summary>
/// Implementations for the firmware tool delegates; kept separate for direct unit testing.
/// <para>固件工具委托的实现；独立存放便于直接单元测试。</para>
/// All handlers return the <see cref="ToolResult"/> envelope and accept the caller's
/// <see cref="CancellationToken"/>; <see cref="ToolGuard"/> turns the envelope into the protocol result.
/// <para>所有处理器返回 <see cref="ToolResult"/> 信封并接受调用方的 <see cref="CancellationToken"/>；
/// 由 <see cref="ToolGuard"/> 把信封转为协议结果。</para>
/// </summary>
internal static class FirmwareToolHandlers
{
    public static ToolResult ListFormats()
    {
        FirmwareFormatRegistry.RegisterAll();
        var formats = FormatDetector.RegisteredHandlers
            .Select(h => new FormatListEntryPayload(h.FormatName, h.FileExtensions, h is IWritableFormatHandler))
            .ToList();
        return new ToolResult(true, $"{formats.Count} format handler(s) registered.", ToolJson.Data(formats));
    }

    public static ToolResult DetectFormat(PathSandbox sandbox, string path)
    {
        FirmwareFormatRegistry.RegisterAll();
        var resolved = sandbox.Resolve(path, mustExist: true);
        var handler = FormatDetector.Detect(resolved);
        return handler is null
            ? new ToolResult(false, $"No registered handler can process: {resolved}")
            : new ToolResult(true, handler.FormatName, ToolJson.Data(
                new DetectedFormatPayload(handler.FormatName, handler.FileExtensions,
                    handler is IWritableFormatHandler)));
    }

    public static async Task<ToolResult> ReadMetadataAsync(
        PathSandbox sandbox, string path, CancellationToken cancellationToken)
    {
        FirmwareFormatRegistry.RegisterAll();
        var resolved = sandbox.Resolve(path, mustExist: true);
        var handler = FormatDetector.Detect(resolved);
        if (handler is null)
        {
            return new ToolResult(false, $"No registered handler can process: {resolved}");
        }

        return ToolResult.From(await handler.ReadMetadataAsync(resolved, cancellationToken).ConfigureAwait(false));
    }

    public static async Task<ToolResult> ReadGptAsync(
        PathSandbox sandbox, string path, int blockSize, CancellationToken cancellationToken)
    {
        var resolved = sandbox.Resolve(path, mustExist: true);
        return await Task.Run(() =>
        {
            using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var blockCount = (ulong)(stream.Length / blockSize);
            var table = GptReader.Read(stream, blockSize, blockCount, cancellationToken);
            var partitions = table.Partitions
                .Select(partition => new GptPartitionEntryPayload(
                    partition.Index, partition.Name, partition.TypeId.ToString("D"),
                    partition.Id.ToString("D"), partition.FirstLba, partition.LastLba,
                    partition.ByteOffset, partition.ByteLength))
                .ToArray();
            return new ToolResult(true,
                $"GPT with {partitions.Length} partition(s), block size {table.BlockSize}.",
                ToolJson.Data(new GptTablePayload(resolved, table.DiskId.ToString("D"),
                    table.BlockSize, table.BlockCount, table.MetadataSha256, partitions)));
        }, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ToolResult> UnpackAsync(
        PathSandbox sandbox, string inputPath, string outputDirectory, long maximumOutputBytes,
        CancellationToken cancellationToken)
    {
        FirmwareFormatRegistry.RegisterAll();
        var resolvedInput = sandbox.Resolve(inputPath, mustExist: true);
        var resolvedOutput = sandbox.ResolveOutput(outputDirectory);
        var handler = FormatDetector.Detect(resolvedInput);
        if (handler is null)
        {
            return new ToolResult(false, $"No registered handler can process: {resolvedInput}");
        }

        var options = new UnpackOptions { MaximumOutputBytes = maximumOutputBytes };
        return ToolResult.From(await handler.UnpackAsync(
            resolvedInput, resolvedOutput, options, cancellationToken).ConfigureAwait(false));
    }

    public static async Task<ToolResult> PackAsync(
        PathSandbox sandbox, string inputPath, string outputPath,
        IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        FirmwareFormatRegistry.RegisterAll();
        var resolvedInput = sandbox.Resolve(inputPath, mustExist: true);
        var resolvedOutput = sandbox.ResolveOutput(outputPath);
        var handler = FormatDetector.Detect(resolvedInput);
        if (handler is not IWritableFormatHandler writable)
        {
            return new ToolResult(false,
                handler is null
                    ? $"No registered handler can process: {resolvedInput}"
                    : $"Format {handler.FormatName} does not support packing.");
        }

        return ToolResult.From(await writable.PackAsync(
            resolvedInput, resolvedOutput, ToByteProgress(progress), cancellationToken).ConfigureAwait(false));
    }

    public static async Task<ToolResult> ExtractAsync(
        PathSandbox sandbox, string containerPath, string[] entries, string outputDirectory,
        bool fromPayloadOnly, long maximumOutputBytes,
        IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        FirmwareFormatRegistry.RegisterAll();
        var resolvedInput = sandbox.Resolve(containerPath, mustExist: true);
        var resolvedOutput = sandbox.ResolveOutput(outputDirectory);
        var structure = ContainerStructureAnalyzer.Analyze(resolvedInput);
        if (structure.Kind == ContainerKind.None)
        {
            return new ToolResult(false, $"Not a supported container (super/payload/zip): {resolvedInput}");
        }

        var result = await ContainerEntryExtractor.ExtractAsync(
            resolvedInput, entries, fromPayloadOnly, cancellationToken,
            resolvedOutput, progress: null, byteProgress: ToTupleProgress(progress),
            maximumOutputBytes: maximumOutputBytes).ConfigureAwait(false);
        return new ToolResult(true,
            $"Extracted {result.Entries.Count} entry(ies) from {structure.Kind}.",
            ToolJson.Data(new ContainerExtractionPayload(structure.Kind.ToString(), result.Entries, result.OutputPaths)));
    }

    public static async Task<ToolResult> MergeSparseChunksAsync(
        PathSandbox sandbox, string selectedChunkPath, string outputPath, CancellationToken cancellationToken)
    {
        var resolvedInput = sandbox.Resolve(selectedChunkPath, mustExist: true);
        var resolvedOutput = sandbox.ResolveOutput(outputPath);
        var set = await SparseChunkSplitter.MergeAsync(resolvedInput, resolvedOutput,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return ToolResult.From(FirmwareOpResult.Ok(
            $"Merged {set.Parts.Count} sparsechunk piece(s) into '{resolvedOutput}'.",
            [.. set.Parts.Select(path => Path.GetFileName(path) ?? path)],
            new Dictionary<string, string> { ["variant"] = set.Variant.ToString() }));
    }

    public static async Task<ToolResult> SplitSparseChunksAsync(
        PathSandbox sandbox, string inputPath, string basePath, long chunkSizeBytes,
        CancellationToken cancellationToken)
    {
        var resolvedInput = sandbox.Resolve(inputPath, mustExist: true);
        var resolvedBase = sandbox.ResolveOutput(basePath);
        var pieces = await SparseChunkSplitter.SplitRawAsync(resolvedInput, resolvedBase, chunkSizeBytes,
            cancellationToken).ConfigureAwait(false);
        return ToolResult.From(FirmwareOpResult.Ok(
            $"Split '{resolvedInput}' into {pieces.Count} sparsechunk piece(s).",
            [.. pieces.Select(path => Path.GetFileName(path) ?? path)]));
    }

    /// <summary>
    /// Converts the protocol progress sink into the firmware byte-progress sink; without a progress
    /// token this returns <see langword="null"/> so backing implementations skip reporting overhead.
    /// <para>把协议进度目标转为固件的字节进度接收端；没有进度令牌时返回 <see langword="null"/>，
    /// 底层实现因此不会消耗上报开销。</para>
    /// </summary>
    /// <param name="progress">The MCP-bound progress instance, possibly <see langword="null"/>. <para>MCP 绑定的进度实例，可能为 <see langword="null"/>。</para></param>
    /// <returns>A byte-progress sink for firmware handlers to report to. <para>供固件处理器上报字节进度的接收端。</para></returns>
    internal static IProgress<FileSystemByteProgress>? ToByteProgress(IProgress<ProgressNotificationValue>? progress) =>
        progress is null
            ? null
            : new Progress<FileSystemByteProgress>(p => progress.Report(new ProgressNotificationValue
            {
                Progress = p.Processed,
                Total = p.Total > 0 ? p.Total : null,
                Message = $"{p.Stage} {p.Processed}/{p.Total} bytes"
            }));

    /// <summary>
    /// Converts the protocol progress sink into the container extractor's byte-count sink; without a
    /// progress token this returns <see langword="null"/>.
    /// <para>把协议进度目标转为容器抽取的字节计数接收端；没有进度令牌时返回 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="progress">The MCP-bound progress instance, possibly <see langword="null"/>. <para>MCP 绑定的进度实例，可能为 <see langword="null"/>。</para></param>
    /// <returns>A processed/total byte sink for the extractor. <para>供容器抽取器上报已处理/总字节数的接收端。</para></returns>
    internal static IProgress<(long Processed, long Total)>? ToTupleProgress(IProgress<ProgressNotificationValue>? progress) =>
        progress is null
            ? null
            : new Progress<(long Processed, long Total)>(p => progress.Report(new ProgressNotificationValue
            {
                Progress = p.Processed,
                Total = p.Total > 0 ? p.Total : null,
                Message = $"{p.Processed}/{p.Total} bytes"
            }));
}