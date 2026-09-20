using System.ComponentModel;
using ModelContextProtocol.Server;
using UotanStudio.Firmware.BoundedContexts.Compression;
using UotanStudio.Firmware.BoundedContexts.Compression.Files;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Builds the compression MCP tools backed by <see cref="CompressionService"/> and
/// <see cref="CompressionFormats"/>, covering zstd/brotli/lz4/lzma/xz/lzop/gzip/deflate and more.
/// <para>基于 <see cref="CompressionService"/> 与 <see cref="CompressionFormats"/> 构建压缩 MCP 工具，
/// 覆盖 zstd/brotli/lz4/lzma/xz/lzop/gzip/deflate 等算法。</para>
/// </summary>
internal static class CompressionToolFactory
{
    internal static IReadOnlyList<McpServerTool> CreateTools(PathSandbox sandbox)
    {
        var list = McpServerTool.Create(
            () => CompressionToolHandlers.ListAlgorithms(),
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
             [Description("Optional dictionary size (lzma/xz family).")] uint? dictionarySize = null) => CompressionToolHandlers.Compress(sandbox, inputPath, outputPath, algorithm, level, dictionarySize),
            new McpServerToolCreateOptions
            {
                Name = "compress_file",
                Description = "Compress a file with the chosen algorithm.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });

        var decompress = McpServerTool.Create(
            ([Description("Input file path inside the sandbox.")] string inputPath,
             [Description("Output file path inside the sandbox.")] string outputPath,
             [Description("Optional algorithm name; auto-detected from magic bytes when omitted.")] string? algorithm = null,
             [Description("Maximum decompressed output size in bytes (default 64 GiB).")] long maximumOutputBytes = 64L * 1024 * 1024 * 1024) => CompressionToolHandlers.Decompress(sandbox, inputPath, outputPath, algorithm, maximumOutputBytes),
            new McpServerToolCreateOptions
            {
                Name = "decompress_file",
                Description = "Decompress a file, auto-detecting the algorithm from magic bytes "
                    + "when not specified explicitly.",
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
/// MCP transport thread is never blocked.
/// <para>CompressionService 的文件 API 是同步的；重活推到线程池执行，避免阻塞 MCP 传输线程。</para>
/// </summary>
internal static class CompressionToolHandlers
{
    public static Task<string> ListAlgorithms()
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
        return Task.FromResult(ToolJson.Serialize(
            new ToolResult(true, $"{algorithms.Count} compression algorithm(s) available.", ToolJson.Data(algorithms))));
    }

    public static Task<string> Compress(
        PathSandbox sandbox, string inputPath, string outputPath, string algorithm, int? level, uint? dictionarySize)
        => Task.Run(() =>
        {
            var resolvedInput = sandbox.Resolve(inputPath, mustExist: true);
            var resolvedOutput = sandbox.ResolveOutput(outputPath);
            if (!CompressionFormats.TryParse(algorithm, out var format))
            {
                return ToolJson.Serialize(new ToolResult(false, $"Unknown compression algorithm: {algorithm}"));
            }

            var options = new CompressionOptions { Level = level, DictionarySize = dictionarySize };
            CompressionFile.CompressFile(resolvedInput, resolvedOutput, format, options);
            var outputInfo = new FileInfo(resolvedOutput);
            var inputInfo = new FileInfo(resolvedInput);
            return ToolJson.Serialize(new ToolResult(true,
                $"Compressed with {CompressionFormats.GetDisplayName(format)}.",
                ToolJson.Data(new CompressionIoPayload(
                    resolvedInput,
                    resolvedOutput,
                    inputInfo.Length,
                    outputInfo.Length))));
        });

    public static Task<string> Decompress(
        PathSandbox sandbox, string inputPath, string outputPath, string? algorithm, long maximumOutputBytes)
        => Task.Run(() =>
        {
            var resolvedInput = sandbox.Resolve(inputPath, mustExist: true);
            var resolvedOutput = sandbox.ResolveOutput(outputPath);

            CompressionFormat format;
            if (algorithm is null)
            {
                format = CompressionFile.DetectFile(resolvedInput);
                if (format == CompressionFormat.None)
                {
                    return ToolJson.Serialize(new ToolResult(false,
                        $"Cannot detect a compression format from: {resolvedInput}"));
                }
            }
            else if (!CompressionFormats.TryParse(algorithm, out format))
            {
                return ToolJson.Serialize(new ToolResult(false, $"Unknown compression algorithm: {algorithm}"));
            }

            CompressionFile.DecompressFile(resolvedInput, resolvedOutput, format, maximumOutputBytes);
            var outputInfo = new FileInfo(resolvedOutput);
            var inputInfo = new FileInfo(resolvedInput);
            return ToolJson.Serialize(new ToolResult(true,
                $"Decompressed {CompressionFormats.GetDisplayName(format)}.",
                ToolJson.Data(new CompressionIoPayload(
                    resolvedInput,
                    resolvedOutput,
                    inputInfo.Length,
                    outputInfo.Length))));
        });
}