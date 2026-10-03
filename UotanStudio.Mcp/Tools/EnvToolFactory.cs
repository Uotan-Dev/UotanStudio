using System.Runtime.InteropServices;
using ModelContextProtocol.Server;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Compression;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Builds the environment/capability probe MCP tool so a client can feature-detect this server
/// instead of guessing its version, sandbox shape, registered handlers and codec set.
/// <para>构建环境/能力探测 MCP 工具，让客户端特性探测本服务器的版本、沙箱形态、已注册处理器与编解码集合，
/// 而非猜测。</para>
/// The delegate ends with the ambient <see cref="CancellationToken"/> parameter the MCP server binds
/// itself; it does not appear in the emitted <c>inputSchema</c>, so clients cannot pass it.
/// <para>委托以 MCP 服务器自行绑定的环境参数 <see cref="CancellationToken"/> 结尾；它不出现在生成的
/// <c>inputSchema</c> 中，客户端无法传入。</para>
/// </summary>
internal static class EnvToolFactory
{
    internal static McpServerTool CreateGetEnv(PathSandbox sandbox, bool allowFlash) =>
        McpServerTool.Create(
            (CancellationToken cancellationToken) => ToolGuard.RunAsync(
                token => Task.FromResult(EnvToolHandlers.GetEnv(sandbox, allowFlash)), cancellationToken),
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
}

/// <summary>
/// Implementation for the environment tool delegate; kept separate for direct unit testing.
/// <para>环境工具委托的实现；独立存放便于直接单元测试。</para>
/// The handler returns the <see cref="ToolResult"/> envelope and <see cref="ToolGuard"/> turns that
/// envelope into the protocol result.
/// <para>处理器返回 <see cref="ToolResult"/> 信封，由 <see cref="ToolGuard"/> 把信封转为协议结果。</para>
/// </summary>
internal static class EnvToolHandlers
{
    public static ToolResult GetEnv(PathSandbox sandbox, bool allowFlash)
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

        return new ToolResult(true, "UotanStudio MCP environment report.", ToolJson.Data(payload));
    }
}