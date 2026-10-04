using Lua;
using UotanStudio.Projects.Interactions;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Interactions;
using UotanStudio.Services.Scripting;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class LuaScriptEngineTests
{
    [Fact]
    public async Task RunAsync_ToolCall_RoundTripsArgumentsAndOutputs()
    {
        var engine = new LuaScriptEngine();
        var context = new TestExecutionContext(TestContext.Current.CancellationToken);
        var tools = new Dictionary<string, ScriptTool>
        {
            ["Echo.Upper"] = new("Echo.Upper", invocation =>
                Task.FromResult<IReadOnlyDictionary<string, string?>>(
                    new Dictionary<string, string?> { ["value"] = invocation.Arguments["text"]?.ToUpperInvariant() }))
        };

        var result = await engine.RunAsync(
            "local out = tool.Echo.Upper('text', 'abc')\nreturn out.value",
            "tests.echo", tools, context, TestContext.Current.CancellationToken);

        Assert.Equal("ABC", result);
    }

    [Fact]
    public async Task RunAsync_CtxProgressAndLog_AreObserved()
    {
        var engine = new LuaScriptEngine();
        var progress = new List<double>();
        var logs = new List<string>();
        var context = new ObservingExecutionContext(TestContext.Current.CancellationToken, progress, logs);
        var tools = new Dictionary<string, ScriptTool>();

        await engine.RunAsync("ctx.progress(0.5)\nctx.log('hello')\nreturn 'done'",
            "tests.ctx", tools, context, TestContext.Current.CancellationToken);

        Assert.Equal([0.5], progress);
        Assert.Contains("hello", logs);
    }

    [Fact]
    public async Task RunAsync_InfiniteLoop_IsCancelledByOuterToken()
    {
        var engine = new LuaScriptEngine();
        var context = new TestExecutionContext(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource(1500);
        var tools = new Dictionary<string, ScriptTool>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.RunAsync("while true do end", "tests.cancel", tools, context, cts.Token));
    }

    [Fact]
    public async Task RunAsync_OversizedScript_IsRejectedBeforeParsing()
    {
        var engine = new LuaScriptEngine();
        var context = new TestExecutionContext(TestContext.Current.CancellationToken);
        var tools = new Dictionary<string, ScriptTool>();
        var script = new string('-', LuaScriptEngine.MaximumScriptLength + 1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            engine.RunAsync(script, "tests.size", tools, context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_ToolFailure_IsSurfacedAsLuaErrorWithLog()
    {
        var engine = new LuaScriptEngine();
        var logs = new List<string>();
        var context = new ObservingExecutionContext(TestContext.Current.CancellationToken, [], logs);
        var tools = new Dictionary<string, ScriptTool>
        {
            ["Failing.Tool"] = new("Failing.Tool", _ => throw new InvalidOperationException("boom"))
        };

        var error = await Assert.ThrowsAsync<LuaRuntimeException>(() =>
            engine.RunAsync("tool.Failing.Tool()", "tests.fail", tools, context, TestContext.Current.CancellationToken));

        Assert.Contains("boom", error.Message, StringComparison.Ordinal);
        Assert.Contains(logs, line => line.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_SandboxedLibraries_KeepPlatformSurfacesUnreachable()
    {
        var engine = new LuaScriptEngine();
        var context = new TestExecutionContext(TestContext.Current.CancellationToken);
        var tools = new Dictionary<string, ScriptTool>();

        await Assert.ThrowsAsync<LuaRuntimeException>(() =>
            engine.RunAsync("return io.open('x')", "tests.sandbox", tools, context, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<LuaRuntimeException>(() =>
            engine.RunAsync("return os.execute('echo')", "tests.sandbox", tools, context, TestContext.Current.CancellationToken));

        var types = await engine.RunAsync(
            "return type(dofile) .. type(loadfile) .. type(io) .. type(os) .. type(package) .. type(require) .. type(debug)",
            "tests.sandbox", tools, context, TestContext.Current.CancellationToken);

        Assert.Equal("nilnilnilnilnilnilnil", types);
    }

    [Fact]
    public async Task RunAsync_StandardLibraries_SupportPureComputation()
    {
        var engine = new LuaScriptEngine();
        var context = new TestExecutionContext(TestContext.Current.CancellationToken);
        var tools = new Dictionary<string, ScriptTool>();

        var result = await engine.RunAsync(
            "local parts = { 'a', 'b' }\ntable.insert(parts, 'c')\nreturn string.upper(table.concat(parts, '-'))"
            + " .. ':' .. tostring(math.floor(1.9)) .. ':' .. bit32.band(6, 3)",
            "tests.stdlib", tools, context, TestContext.Current.CancellationToken);

        Assert.Equal("A-B-C:1:2", result);
    }

    [Fact]
    public async Task RunAsync_Print_RoutesFormattedOutputIntoNodeLog()
    {
        var engine = new LuaScriptEngine();
        var logs = new List<string>();
        var context = new ObservingExecutionContext(TestContext.Current.CancellationToken, [], logs);
        var tools = new Dictionary<string, ScriptTool>();

        await engine.RunAsync(
            "print('status', 42, true, nil)\nprint(setmetatable({}, { __tostring = function() return 'MT' end }))",
            "tests.print", tools, context, TestContext.Current.CancellationToken);

        Assert.Contains("status\t42\ttrue\tnil", logs);
        Assert.Contains("MT", logs);
    }

    [Fact]
    public async Task RunAsync_ConfirmationDenied_ToolIsNotInvoked()
    {
        var engine = new LuaScriptEngine();
        var invoked = false;
        var context = new TestExecutionContext(TestContext.Current.CancellationToken,
            new InteractionStub(InteractionOutcome.Cancelled));
        var tools = new Dictionary<string, ScriptTool>
        {
            ["Danger.Tool"] = new("Danger.Tool", _ =>
            {
                invoked = true;
                return Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>());
            }, RequiresConfirmation: true)
        };

        await Assert.ThrowsAsync<LuaRuntimeException>(() =>
            engine.RunAsync("tool.Danger.Tool()", "tests.confirm", tools, context, TestContext.Current.CancellationToken));

        Assert.False(invoked);
    }

    [Fact]
    public async Task RunAsync_ConfirmationApproved_ToolIsInvoked()
    {
        var engine = new LuaScriptEngine();
        var invoked = false;
        var context = new TestExecutionContext(TestContext.Current.CancellationToken,
            new InteractionStub(InteractionOutcome.Submitted));
        var tools = new Dictionary<string, ScriptTool>
        {
            ["Danger.Tool"] = new("Danger.Tool", _ =>
            {
                invoked = true;
                return Task.FromResult<IReadOnlyDictionary<string, string?>>(
                    new Dictionary<string, string?> { ["value"] = "ok" });
            }, RequiresConfirmation: true)
        };

        var result = await engine.RunAsync("return tool.Danger.Tool().value",
            "tests.confirm", tools, context, TestContext.Current.CancellationToken);

        Assert.True(invoked);
        Assert.Equal("ok", result);
    }

    private sealed class InteractionStub(InteractionOutcome outcome) : IWorkflowInteractionService
    {
        public bool IsSupported => true;
        public Task<InteractionResult> ShowAsync(InteractionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new InteractionResult { Outcome = outcome });
    }

    private sealed class TestExecutionContext(CancellationToken cancellationToken,
        IWorkflowInteractionService? interactions = null) : INodeExecutionContext, IUnifiedLog
    {
        public IUnifiedLog Log => this;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public IWorkflowInteractionService Interactions => interactions ?? UnsupportedWorkflowInteractionService.Instance;
        public void ReportProgress(double progress) { }
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }

    private sealed class ObservingExecutionContext(CancellationToken cancellationToken,
        List<double> progress, List<string> logs) : INodeExecutionContext, IUnifiedLog
    {
        public IUnifiedLog Log => this;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public void ReportProgress(double value) => progress.Add(value);
        public void Write(LogLevel level, string source, string message) => logs.Add(message);
        public void Write(LogLevel level, string source, string format, params object[] args) => logs.Add(string.Format(format, args));
        public void SetMinimumLevel(LogLevel level) { }
    }
}