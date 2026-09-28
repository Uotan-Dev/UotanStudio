using System.Text.Json;
using ModelContextProtocol.Protocol;
using UotanStudio.Mcp.Tools;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class McpToolGuardContractTests
{
    private static readonly JsonElement SamplePayload = ToolJson.Data(
        new PathPayload(@"D:\Firmware\out.img"));

    [Fact]
    public async Task RunAsync_SuccessfulBody_SetsNoErrorFlagAndKeepsTextEnvelope()
    {
        var result = await ToolGuard.RunAsync(
            cancellationToken => Task.FromResult(new ToolResult(true, "ok.", SamplePayload)),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using var envelope = JsonDocument.Parse(text.Text);
        Assert.True(envelope.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("ok.", envelope.RootElement.GetProperty("summary").GetString());
        Assert.NotNull(result.StructuredContent);
    }

    [Fact]
    public async Task RunAsync_FailedBusinessResult_SetsErrorFlagInsteadOfGreenTextOnly()
    {
        var result = await ToolGuard.RunAsync(
            cancellationToken => Task.FromResult(
                new ToolResult(false, "No registered handler can process: x.img")),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using var envelope = JsonDocument.Parse(text.Text);
        Assert.False(envelope.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains(
            "No registered handler",
            envelope.RootElement.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task RunAsync_HandlerThrows_PreservesExceptionTypeAndMessage()
    {
        var result = await ToolGuard.RunAsync(
            cancellationToken => Task.FromException<ToolResult>(
                new UnauthorizedAccessException("The path escapes the sandbox root.")),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using var envelope = JsonDocument.Parse(text.Text);
        var summary = envelope.RootElement.GetProperty("summary").GetString();
        Assert.Contains(nameof(UnauthorizedAccessException), summary);
        Assert.Contains("escapes the sandbox root", summary);
    }

    [Fact]
    public async Task RunAsync_OperationCanceledByClient_ReportsCancellationNotHandlerFailure()
    {
        using var cts = new CancellationTokenSource();

        var result = await ToolGuard.RunAsync(
            cancellationToken => Task.FromException<ToolResult>(new OperationCanceledException(cancellationToken)),
            cts.Token);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using var envelope = JsonDocument.Parse(text.Text);
        Assert.Contains(
            "canceled",
            envelope.RootElement.GetProperty("summary").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_RecognizerIgnoresTokenWithoutThrowing_SurfacesCompletionAsIs()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await ToolGuard.RunAsync(
            cancellationToken => Task.FromResult(
                new ToolResult(true, "completed before cancellation was observed")),
            cts.Token);

        Assert.False(result.IsError);
        Assert.Null(result.StructuredContent);
    }

    [Fact]
    public void ToResult_StructuredData_SerializesAsProtocolStructuredContent()
    {
        var result = new ToolResult(true, "written.", SamplePayload).ToResult();

        Assert.False(result.IsError);
        var structured = Assert.IsAssignableFrom<JsonElement?>(result.StructuredContent);
        Assert.NotNull(structured);
        Assert.Equal(@"D:\Firmware\out.img", structured!.Value.GetProperty("path").GetString());
    }

    [Fact]
    public void FromException_SetsFailureFlagAndNamedSummaryForWireDiagnosis()
    {
        var result = ToolResult.FromException(new InvalidDataException("Truncated payload header."));

        Assert.False(result.Success);
        Assert.Equal($"{nameof(InvalidDataException)}: Truncated payload header.", result.Summary);
        Assert.Null(result.Data);
    }
}