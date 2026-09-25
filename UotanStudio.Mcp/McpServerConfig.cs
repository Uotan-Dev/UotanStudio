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
    /// <value>An absolute, normalized directory path supplied by the caller or derived from the process working directory.
    /// <para>由调用方给出或取自进程当前目录的绝对、已规范化目录路径。</para></value>
    public required string RootDirectory { get; init; }

    /// <summary>When false, flashing/wiping/reboot tools are never registered.
    /// <para>为 false 时不注册刷写、擦除、重启类工具。</para></summary>
    /// <value>Requested with <c>--allow-flash</c>; defaults to <see langword="false"/>, so destructive tools stay absent unless explicitly enabled.
    /// <para>由 <c>--allow-flash</c> 开启；默认为 <see langword="false"/>，未显式启用时不提供破坏性工具。</para></value>
    public bool AllowFlash { get; init; }

    /// <summary>
    /// Parses MCP-related options from launch arguments.
    /// <para>从启动参数解析 MCP 相关选项。</para>
    /// </summary>
    /// <example><code>
    /// McpServerConfig config = McpServerConfig.Parse(["--mcp-root", @"D:\work", "--allow-flash", "file.img"]);
    /// </code></example>
    /// <param name="args">Launch arguments; <c>--mcp-root</c> consumes the following entry as the root, <c>--allow-flash</c> is a switch, and other entries are ignored. Option names ignore case.
    /// <para>启动参数；<c>--mcp-root</c> 取其后的项作为根目录，<c>--allow-flash</c> 为开关，其余项被忽略；选项名不区分大小写。</para></param>
    /// <returns>A config whose root is the full path of the last <c>--mcp-root</c> value, or of the process working directory when the option or its value is absent.
    /// <para>其根目录为最后一个 <c>--mcp-root</c> 值的完整路径；缺少该选项或其取值时为进程工作目录的完整路径。</para></returns>
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