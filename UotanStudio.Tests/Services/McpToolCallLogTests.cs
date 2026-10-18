using UotanStudio.Mcp.Tools;
using UotanStudio.Services;
using UotanStudio.Services.Mcp;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// The global-log half of the MCP tool trace: a successful call lands as an Info line and a refused call as
/// a Warn line, both naming the tool, the verbatim argument JSON, the elapsed time and the returned summary.
/// The lines must survive the default minimum level, so they are visible without debug mode.
/// <para>MCP 工具痕迹的全局日志侧：成功调用记为 Info 行，被拒绝调用记为 Warn 行，两者都写出工具名、原样参数 JSON、
/// 耗时与返回摘要。这些行必须在默认最低级别下可见，即不依赖调试模式。</para>
/// </summary>
[Collection("Log pipeline")]
public sealed class McpToolCallLogTests : IDisposable
{
    private const string Arguments = """{"path":"D:\\rom\\boot.img"}""";
    private readonly List<LogEntry> _entries = [];
    private readonly Action _unsubscribe;

    public McpToolCallLogTests()
    {
        LogService.Instance.SetMinimumLevel(LogLevel.Trace);
        _unsubscribe = LogService.Instance.Subscribe((_, entry) => _entries.Add(entry));
    }

    public void Dispose()
    {
        _unsubscribe();
        LogService.Instance.Clear();
        LogService.Instance.SetMinimumLevel(LogLevel.Info);
    }

    [Fact]
    public void RecordToolCall_Success_WritesInfoLineNamingToolArgumentsAndSummary()
    {
        McpToolCallLog.Instance.RecordToolCall(new McpToolCallRecord(
            "detect_format", Arguments, true, "Android boot image", TimeSpan.FromMilliseconds(12)));

        var entry = Assert.Single(_entries);
        Assert.Equal("McpTool", entry.Source);
        Assert.Equal(LogLevel.Info, entry.Level);
        Assert.Contains("detect_format", entry.Message, StringComparison.Ordinal);
        Assert.Contains(Arguments, entry.Message, StringComparison.Ordinal);
        Assert.Contains("ok", entry.Message, StringComparison.Ordinal);
        Assert.Contains("12 ms", entry.Message, StringComparison.Ordinal);
        Assert.Contains("Android boot image", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordToolCall_Failure_WritesWarnLineCarryingTheCause()
    {
        McpToolCallLog.Instance.RecordToolCall(new McpToolCallRecord(
            "detect_format",
            Arguments,
            false,
            "UnauthorizedAccessException: Path is outside the MCP sandbox root: D:\\rom\\boot.img",
            TimeSpan.FromMilliseconds(3)));

        var entry = Assert.Single(_entries);
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Equal("McpTool", entry.Source);
        Assert.Contains("failed", entry.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(UnauthorizedAccessException), entry.Message, StringComparison.Ordinal);
        Assert.Contains("outside the MCP sandbox root", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordToolCall_WithoutDebugMode_RemainsVisibleInGlobalLog()
    {
        LogService.Instance.ApplyDebugMode(debug: false);

        McpToolCallLog.Instance.RecordToolCall(new McpToolCallRecord(
            "list_formats", "{}", true, "40 format handler(s) registered", TimeSpan.Zero));

        Assert.Single(_entries);
    }
}