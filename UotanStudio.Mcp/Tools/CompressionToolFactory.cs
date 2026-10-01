using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UotanStudio.Firmware.Compression;
using UotanStudio.Firmware.Compression.Files;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Builds the compression MCP tools backed by <see cref="CompressionService"/> and
/// <see cref="CompressionFormats"/>, covering zstd/brotli/lz4/lzma/xz/lzop/gzip/deflate and more.
/// <para>基于 <see cref="CompressionService"/> 与 <see cref="CompressionFormats"/> 构建压缩 MCP 工具，
/// 覆盖 zstd/brotli/lz4/lzma/xz/lzop/gzip/deflate 等算法。</para>
/// The compress delegate ends with the ambient <see cref="IProgress{T}"/> and
/// <see cref="CancellationToken"/> parameters the MCP server binds itself and the decompress delegate
/// ends with the ambient <see cref="CancellationToken"/>; none appears in the emitted
/// <c>inputSchema</c>, so clients cannot pass them but cancellation and progress propagate into the
/// compression file APIs that support them.
/// <para>压缩委托以 MCP 服务器自行绑定的环境参数 <see cref="IProgress{T}"/> 与
/// <see cref="CancellationToken"/> 结尾，解压委托以环境参数 <see cref="CancellationToken"/> 结尾；
/// 它们都不出现在生成的 <c>inputSchema</c> 中，客户端无法传入，但取消与进度会传导到支持它们的压缩文件 API。</para>
/// </summary>
internal static class CompressionToolFactory
{
    internal static IReadOnlyList<McpServerTool> CreateTools(PathSandbox sandbox)
    {
        var list = McpServerTool.Create(
            (CancellationToken cancellationToken) => ToolGuard.RunAsync(
                static token => Task.FromResult(CompressionToolHandlers.ListAlgorithms()), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "list_compression_algorithms",
                Description = "List every compression algorithm supported by UotanStudio "
                    + "(names, aliases, extensions, compress/decompress and streaming capability).",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

        var compress = McpServerTool.Create(
            ([Description("Input file path inside the sandbox.")] string inputPath,
             [Description("Output file path inside the sandbox.")] string outputPath,
             [Description("Algorithm name, e.g. zstd, brotli, lz4, lz4legacy, lzma, xz, lzop, gzip, deflate, zlib.")] string algorithm,
             [Description("Optional compression level; algorithm-specific meaning.")] int? level = null,
             [Description("Optional dictionary size (lzma/xz family).")] uint? dictionarySize = null,
             IProgress<ProgressNotificationValue>? progress = null,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => CompressionToolHandlers.Compress(sandbox, inputPath, outputPath, algorithm, level,
                    dictionarySize, progress, token),
                cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "compress_file",
                Description = "Compress a file with the chosen algorithm. Send a progress token to receive "
                    + "notifications/progress and to let cancellation interrupt the source read; cancelling "
                    + "keeps the previous output file.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });

        var decompress = McpServerTool.Create(
            ([Description("Input file path inside the sandbox.")] string inputPath,
             [Description("Output file path inside the sandbox.")] string outputPath,
             [Description("Optional algorithm name; auto-detected from magic bytes when omitted.")] string? algorithm = null,
             [Description("Maximum decompressed output size in bytes (default 64 GiB).")] long maximumOutputBytes = 64L * 1024 * 1024 * 1024,
             CancellationToken cancellationToken = default) => ToolGuard.RunAsync(
                token => CompressionToolHandlers.Decompress(sandbox, inputPath, outputPath, algorithm,
                    maximumOutputBytes, token),
                cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "decompress_file",
                Description = "Decompress a file, auto-detecting the algorithm from magic bytes "
                    + "when not specified explicitly. Cancel the request to abort the copy and keep the "
                    + "destination's prior content.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });

        return [list, compress, decompress];
    }
}

/// <summary>
/// Implementations for the compression tool delegates; kept separate for direct unit testing.
/// <para>压缩工具委托的实现；独立存放便于直接单元测试。</para>
/// CompressionService's file APIs are synchronous; heavy work is pushed to the thread pool so the
/// MCP transport thread is never blocked, and the thread-pool hand-off observes the caller's
/// <see cref="CancellationToken"/>. <c>CompressFile</c> checks that token while the source bytes are
/// read and <c>DecompressFile</c> checks it during the bounded decoded copy; a cancelled run leaves
/// the destination's prior content in place because the temporary file is discarded.
/// <para>CompressionService 的文件 API 是同步的；重活推到线程池执行，避免阻塞 MCP 传输线程，
/// 线程池交接会观察调用方的 <see cref="CancellationToken"/>。<c>CompressFile</c> 在读取源字节时检查该令牌，
/// <c>DecompressFile</c> 在有界解压复制中检查；被取消的运行保留目标文件原有内容，因为临时文件会被丢弃。</para>
/// All handlers return the <see cref="ToolResult"/> envelope; <see cref="ToolGuard"/> turns the
/// envelope into the protocol result.
/// <para>所有处理器返回 <see cref="ToolResult"/> 信封；由 <see cref="ToolGuard"/> 把信封转为协议结果。</para>
/// </summary>
internal static class CompressionToolHandlers
{
    public static ToolResult ListAlgorithms()
    {
        var algorithms = CompressionFormats.All
            .Select(info => new CompressionAlgorithmPayload(
                info.Format.ToString(),
                info.Name,
                info.Aliases,
                info.Extensions,
                info.CanCompress,
                info.CanDecompress,
                info.IsStreaming))
            .ToList();
        return new ToolResult(true, $"{algorithms.Count} compression algorithm(s) available.", ToolJson.Data(algorithms));
    }

    public static async Task<ToolResult> Compress(
        PathSandbox sandbox, string inputPath, string outputPath, string algorithm, int? level,
        uint? dictionarySize, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
        => await Task.Run(() =>
        {
            var resolvedInput = sandbox.Resolve(inputPath, mustExist: true);
            var resolvedOutput = sandbox.ResolveOutput(outputPath);
            if (!CompressionFormats.TryParse(algorithm, out var format))
            {
                return new ToolResult(false, $"Unknown compression algorithm: {algorithm}");
            }

            var options = new CompressionOptions { Level = level, DictionarySize = dictionarySize };
            CompressionFile.CompressFile(resolvedInput, resolvedOutput, format, options,
                ToTupleProgress(progress), cancellationToken);
            var outputInfo = new FileInfo(resolvedOutput);
            var inputInfo = new FileInfo(resolvedInput);
            return new ToolResult(true,
                $"Compressed with {CompressionFormats.GetDisplayName(format)}.",
                ToolJson.Data(new CompressionIoPayload(
                    resolvedInput,
                    resolvedOutput,
                    inputInfo.Length,
                    outputInfo.Length)));
        }, cancellationToken).ConfigureAwait(false);

    public static async Task<ToolResult> Decompress(
        PathSandbox sandbox, string inputPath, string outputPath, string? algorithm, long maximumOutputBytes,
        CancellationToken cancellationToken)
        => await Task.Run(() =>
        {
            var resolvedInput = sandbox.Resolve(inputPath, mustExist: true);
            var resolvedOutput = sandbox.ResolveOutput(outputPath);

            CompressionFormat format;
            if (algorithm is null)
            {
                format = CompressionFile.DetectFile(resolvedInput);
                if (format == CompressionFormat.None)
                {
                    return new ToolResult(false,
                        $"Cannot detect a compression format from: {resolvedInput}");
                }
            }
            else if (!CompressionFormats.TryParse(algorithm, out format))
            {
                return new ToolResult(false, $"Unknown compression algorithm: {algorithm}");
            }

            CompressionFile.DecompressFile(resolvedInput, resolvedOutput, format, maximumOutputBytes,
                cancellationToken);
            var outputInfo = new FileInfo(resolvedOutput);
            var inputInfo = new FileInfo(resolvedInput);
            return new ToolResult(true,
                $"Decompressed {CompressionFormats.GetDisplayName(format)}.",
                ToolJson.Data(new CompressionIoPayload(
                    resolvedInput,
                    resolvedOutput,
                    inputInfo.Length,
                    outputInfo.Length)));
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// <para>把协议进度目标转为压缩文件的字节计数接收端；没有进度令牌时返回 <see langword="null"/>，
    /// 底层实现因此不会消耗上报开销。</para>
    /// Converts the protocol progress sink into the compression file API's byte-count sink; without a
    /// progress token this returns <see langword="null"/> so the compressor skips reporting overhead.
    /// </summary>
    /// <param name="progress"><para>MCP 绑定的进度实例，可能为 <see langword="null"/>。</para>The MCP-bound progress instance, possibly <see langword="null"/>.</param>
    /// <returns><para>供压缩文件 API 上报已处理/总字节数的接收端。</para>A processed/total byte sink for the compression file API.</returns>
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