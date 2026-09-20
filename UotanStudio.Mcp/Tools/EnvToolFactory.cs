using System.ComponentModel;
using System.Runtime.InteropServices;
using ModelContextProtocol.Server;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.BoundedContexts.Compression;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Builds the environment/capability probe MCP tool so a client can feature-detect this server
/// instead of guessing its version, sandbox shape, registered handlers and codec set.
/// <para>构建环境/能力探测 MCP 工具，让客户端特性探测本服务器的版本、沙箱形态、已注册处理器与编解码集合，
/// 而非猜测。</para>
/// </summary>
internal static class EnvToolFactory
{
    internal static IReadOnlyList<McpServerTool> CreateTools(PathSandbox sandbox, bool allowFlash)
    {
        var getEnv = McpServerTool.Create(
            () => EnvToolHandlers.GetEnv(sandbox, allowFlash),
            new McpServerToolCreateOptions
            {
                Name = "get_env",
                Description = "Report the server version, sandbox root, flash-gate state, host OS "
                    + "and runtime, and the set of registered firmware formats and compression codecs.",
                ReadOnly = true,
                Idempotent = true,
                OpenWorld = false,
                Destructive = false
            });

        return [getEnv];
    }
}

/// <summary>
/// Implementation for the environment tool delegate; kept separate for direct unit testing.
/// <para>环境工具委托的实现；独立存放便于直接单元测试。</para>
/// </summary>
internal static class EnvToolHandlers
{
    public static string GetEnv(PathSandbox sandbox, bool allowFlash)
    {
        FirmwareFormatRegistry.RegisterAll();
        var handlers = FormatDetector.RegisteredHandlers.ToList();
        var writable = handlers.OfType<IWritableFormatHandler>().Select(h => h.FormatName).ToList();
        var codecs = CompressionFormats.All.Select(info => info.Format.ToString()).ToList();
        var version = typeof(EnvToolFactory).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        var payload = new EnvPayload(
            "uotanstudio",
            version,
            sandbox.Root,
            sandbox.IsUnrestricted,
            allowFlash,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            handlers.Count,
            writable,
            codecs);

        return ToolJson.Serialize(new ToolResult(true, "UotanStudio MCP environment report.", ToolJson.Data(payload)));
    }
}