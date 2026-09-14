using System.IO;

namespace UotanStudio.Mcp;

/// <summary>
/// Runtime configuration for the embedded MCP server, parsed from command-line arguments.
/// <para>内嵌 MCP 服务器的运行配置，来自命令行参数解析。</para>
/// Unknown arguments (e.g. <c>--mcp</c>, startup files) are ignored so the same parser works
/// for both the headless and the companion desktop launch paths.
/// <para>未知参数（如 <c>--mcp</c>、启动文件）会被忽略，同一解析器可同时服务无头与伴随模式。</para>
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>Sandbox root; every file path handed to a tool must resolve inside it.
    /// <para>沙箱根目录；所有工具收到的文件路径都必须解析到该目录内。</para></summary>
    public required string RootDirectory { get; init; }

    /// <summary>When false, flashing/wiping/reboot tools are never registered.
    /// <para>为 false 时不注册刷写、擦除、重启类工具。</para></summary>
    public bool AllowFlash { get; init; }

    /// <summary>
    /// Parses MCP-related options from launch arguments.
    /// <para>从启动参数解析 MCP 相关选项。</para>
    /// </summary>
    public static McpServerConfig Parse(string[] args)
    {
        var root = Directory.GetCurrentDirectory();
        var allowFlash = false;

        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--mcp-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                root = args[++i];
            }
            else if (string.Equals(args[i], "--allow-flash", StringComparison.OrdinalIgnoreCase))
            {
                allowFlash = true;
            }
        }

        return new McpServerConfig
        {
            RootDirectory = Path.GetFullPath(root),
            AllowFlash = allowFlash
        };
    }
}