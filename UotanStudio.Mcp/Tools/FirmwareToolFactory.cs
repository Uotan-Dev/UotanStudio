using System.ComponentModel;
using ModelContextProtocol.Server;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Builds the firmware-format MCP tools backed by <see cref="FirmwareFormatRegistry"/> and
/// <see cref="FormatDetector"/>, covering every registered handler with one generic surface.
/// <para>基于 <see cref="FirmwareFormatRegistry"/> 与 <see cref="FormatDetector"/> 构建固件格式
/// MCP 工具，用一组通用工具覆盖全部已注册格式处理器。</para>
/// </summary>
internal static class FirmwareToolFactory
{
    internal static IReadOnlyList<McpServerTool> CreateTools(PathSandbox sandbox)
    {
        var list = McpServerTool.Create(
            () => FirmwareToolHandlers.ListFormatsAsync(),
            new McpServerToolCreateOptions
            {
                Name = "list_formats",
                Description = "List every firmware format handler registered in UotanStudio "
                    + "(name, file extensions, read/pack capability).",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

        var detect = McpServerTool.Create(
            ([Description("Absolute path inside the sandbox, or sandbox-relative path.")] string path) => FirmwareToolHandlers.DetectFormatAsync(sandbox, path),
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

        var metadata = McpServerTool.Create(
            ([Description("Absolute path inside the sandbox, or sandbox-relative path.")] string path) => FirmwareToolHandlers.ReadMetadataAsync(sandbox, path),
            new McpServerToolCreateOptions
            {
                Name = "read_metadata",
                Description = "Read format metadata for a firmware file without unpacking it.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

        var unpack = McpServerTool.Create(
            ([Description("Absolute path inside the sandbox, or sandbox-relative path.")] string inputPath,
             [Description("Output directory; created when missing. Must stay inside the sandbox.")] string outputDirectory,
             [Description("Per-file decompression budget in bytes for formats without a trusted output size.")] long maximumOutputBytes = 64L * 1024 * 1024 * 1024) => FirmwareToolHandlers.UnpackAsync(sandbox, inputPath, outputDirectory, maximumOutputBytes),
            new McpServerToolCreateOptions
            {
                Name = "unpack_firmware",
                Description = "Unpack a firmware file (boot image, sparse, ext4/erofs/f2fs image, "
                    + "payload, APK/DEX, vendor formats, ...) into an output directory.",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });

        var pack = McpServerTool.Create(
            ([Description("Input directory or file, depending on the detected format.")] string inputPath,
             [Description("Target file path for the packed image. Must stay inside the sandbox.")] string outputPath) => FirmwareToolHandlers.PackAsync(sandbox, inputPath, outputPath),
            new McpServerToolCreateOptions
            {
                Name = "pack_firmware",
                Description = "Pack an unpacked directory/file back into the image format "
                    + "detected from the input (only writable format handlers support this).",
                ReadOnly = false,
                Idempotent = false,
                OpenWorld = false,
                Destructive = false
            });

        return [list, detect, metadata, unpack, pack];
    }
}

/// <summary>
/// Implementations for the firmware tool delegates; kept separate for direct unit testing.
/// <para>固件工具委托的实现；独立存放便于直接单元测试。</para>
/// All handlers stay async end-to-end; the MCP SDK awaits the returned task.
/// <para>所有处理器全程异步；MCP SDK 会等待返回的任务。</para>
/// </summary>
internal static class FirmwareToolHandlers
{
    public static Task<string> ListFormatsAsync()
    {
        FirmwareFormatRegistry.RegisterAll();
        var formats = FormatDetector.RegisteredHandlers
            .Select(h => new FormatListEntryPayload(h.FormatName, h.FileExtensions, h is IWritableFormatHandler))
            .ToList();
        return Task.FromResult(ToolJson.Serialize(
            new ToolResult(true, $"{formats.Count} format handler(s) registered.", ToolJson.Data(formats))));
    }

    public static Task<string> DetectFormatAsync(PathSandbox sandbox, string path)
    {
        FirmwareFormatRegistry.RegisterAll();
        var resolved = sandbox.Resolve(path, mustExist: true);
        var handler = FormatDetector.Detect(resolved);
        var json = handler is null
            ? ToolJson.Serialize(new ToolResult(false, $"No registered handler can process: {resolved}"))
            : ToolJson.Serialize(new ToolResult(true, handler.FormatName, ToolJson.Data(
                new DetectedFormatPayload(handler.FormatName, handler.FileExtensions,
                    handler is IWritableFormatHandler))));
        return Task.FromResult(json);
    }

    public static async Task<string> ReadMetadataAsync(PathSandbox sandbox, string path)
    {
        FirmwareFormatRegistry.RegisterAll();
        var resolved = sandbox.Resolve(path, mustExist: true);
        var handler = FormatDetector.Detect(resolved);
        if (handler is null)
        {
            return ToolJson.Serialize(new ToolResult(false, $"No registered handler can process: {resolved}"));
        }

        var result = await handler.ReadMetadataAsync(resolved).ConfigureAwait(false);
        return ToolJson.Serialize(ToolResult.From(result));
    }

    public static async Task<string> UnpackAsync(PathSandbox sandbox, string inputPath, string outputDirectory, long maximumOutputBytes)
    {
        FirmwareFormatRegistry.RegisterAll();
        var resolvedInput = sandbox.Resolve(inputPath, mustExist: true);
        var resolvedOutput = sandbox.ResolveOutput(outputDirectory);
        var handler = FormatDetector.Detect(resolvedInput);
        if (handler is null)
        {
            return ToolJson.Serialize(new ToolResult(false, $"No registered handler can process: {resolvedInput}"));
        }

        var options = new UnpackOptions { MaximumOutputBytes = maximumOutputBytes };
        var result = await handler.UnpackAsync(resolvedInput, resolvedOutput, options).ConfigureAwait(false);
        return ToolJson.Serialize(ToolResult.From(result));
    }

    public static async Task<string> PackAsync(PathSandbox sandbox, string inputPath, string outputPath)
    {
        FirmwareFormatRegistry.RegisterAll();
        var resolvedInput = sandbox.Resolve(inputPath, mustExist: true);
        var resolvedOutput = sandbox.ResolveOutput(outputPath);
        var handler = FormatDetector.Detect(resolvedInput);
        if (handler is not IWritableFormatHandler writable)
        {
            return ToolJson.Serialize(new ToolResult(false,
                handler is null
                    ? $"No registered handler can process: {resolvedInput}"
                    : $"Format {handler.FormatName} does not support packing."));
        }

        var result = await writable.PackAsync(resolvedInput, resolvedOutput).ConfigureAwait(false);
        return ToolJson.Serialize(ToolResult.From(result));
    }
}