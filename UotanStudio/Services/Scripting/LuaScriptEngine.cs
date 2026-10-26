using System.Text;
using Lua;
using Lua.IO;
using Lua.Platforms;
using Lua.Standard;
using UotanStudio.Projects;
using UotanStudio.Projects.Interactions;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Services.Scripting;

/// <summary>
/// Runs one Lua script in a sandboxed <see cref="LuaState"/> that opens only the pure computation
/// libraries; the platform <c>io</c>, <c>os</c>, <c>package</c> and <c>debug</c> libraries stay closed
/// and cancellation follows node execution.
/// <para>在沙箱化的 <see cref="LuaState"/> 内执行一个 Lua 脚本，只打开纯计算类库；
/// <c>io</c>、<c>os</c>、<c>package</c> 与 <c>debug</c> 平台库保持关闭，取消信号跟随节点执行。</para>
/// </summary>
public sealed class LuaScriptEngine
{
    /// <summary>
    /// Gets the maximum accepted script source size; oversized sources are rejected before
    /// parsing. The bound is shared with package validation.
    /// <para>获取接受的脚本源码大小上限；超限源码在解析前即被拒绝。该上限与包校验共用。</para>
    /// </summary>
    public const int MaximumScriptLength = VisualPluginValidation.MaximumScriptLength;

    /// <summary>Runs one script to completion and returns its first return value converted to a string, or an empty string when the script returns nothing.
    /// <para>把脚本执行到结束并返回其首个返回值的字符串转换；脚本无返回值时返回空字符串。</para></summary>
    /// <param name="script">The Lua source to execute.<para>要执行的 Lua 源码。</para></param>
    /// <param name="chunkName">Chunk name used in interpreter diagnostics, usually the plugin id.
    /// <para>解释器诊断使用的块名，通常是插件 Id。</para></param>
    /// <param name="tools">Tool surface exposed to the script as <c>tool</c>; each entry is one named capability.
    /// <para>以 <c>tool</c> 暴露给脚本的工具面；每个条目是一个具名能力。</para></param>
    /// <param name="context">Node execution context backing the <c>ctx</c> and <c>ui</c> tables.
    /// <para>支撑 <c>ctx</c> 与 <c>ui</c> 表的节点执行上下文。</para></param>
    /// <param name="cancellationToken">Token cancelling script execution.
    /// <para>取消脚本执行的令牌。</para></param>
    /// <returns>The first script return value as a string, or an empty string.
    /// <para>脚本首个返回值的字符串形式；无返回值时为空字符串。</para></returns>
    /// <exception cref="ArgumentException">Thrown when the script is empty or exceeds <see cref="MaximumScriptLength"/>.
    /// <para>脚本为空或超过 <see cref="MaximumScriptLength"/> 时抛出。</para></exception>
    public async Task<string> RunAsync(string script, string chunkName,
        IReadOnlyDictionary<string, ScriptTool> tools,
        INodeExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkName);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(script))
        {
            throw new ArgumentException("The script is empty.", nameof(script));
        }
        if (script.Length > MaximumScriptLength)
        {
            throw new ArgumentException($"The script exceeds {MaximumScriptLength} characters.", nameof(script));
        }

        var state = LuaState.Create(LuaPlatform.Default with { StandardIO = new UnifiedLogStandardIO(context.Log) });
        OpenSandboxedStandardLibraries(state);
        InjectSurface(state, "tool", BuildToolTable(tools, context));
        InjectSurface(state, "ctx", BuildContextTable(context));
        InjectSurface(state, "ui", BuildUiTable(context));

        var closure = state.Load(script, chunkName);
        var results = await state.ExecuteAsync(closure,
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.CancellationToken).Token)
            .ConfigureAwait(false);
        return results.Length > 0 ? ReadDisplayString(results[0]) : string.Empty;
    }

    private static void OpenSandboxedStandardLibraries(LuaState state)
    {
        state.OpenBasicLibrary();
        state.OpenBitwiseLibrary();
        state.OpenCoroutineLibrary();
        state.OpenMathLibrary();
        state.OpenStringLibrary();
        state.OpenTableLibrary();
        state.Environment["dofile"] = LuaValue.Nil;
        state.Environment["loadfile"] = LuaValue.Nil;
    }

    private static LuaTable BuildToolTable(
        IReadOnlyDictionary<string, ScriptTool> tools,
        INodeExecutionContext context)
    {
        var root = new LuaTable(0, tools.Count);
        foreach (var (name, tool) in tools)
        {
            var segments = name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                throw new ArgumentException($"The tool name '{name}' is empty.", nameof(tools));
            }
            var leaf = root;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var key = segments[index];
                if (!leaf[key].TryRead<LuaTable>(out var next))
                {
                    next = new LuaTable(0, 1);
                    leaf[key] = next;
                }
                leaf = next;
            }
            leaf[segments[^1]] = new LuaFunction("tool." + name, async (functionContext, functionToken) =>
            {
                var arguments = ReadArguments(functionContext);
                if (tool.RequiresConfirmation
                    && !await ConfirmAsync(context, tool.ConfirmationPrompt ?? name, functionToken).ConfigureAwait(false))
                {
                    throw new LuaRuntimeException(functionContext.State, $"{name} was not confirmed.", 0);
                }
                try
                {
                    var invocation = new ScriptToolInvocation(arguments, context, functionToken);
                    var outputs = await tool.Invoke(invocation).ConfigureAwait(false);
                    return functionContext.Return(ConvertTable(outputs));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    context.Log.Write(LogLevel.Error, "LuaScript", $"{name}: {ex.Message}");
                    throw new LuaRuntimeException(functionContext.State, $"{name} failed: {ex.Message}", 0);
                }
            });
        }
        return root;
    }

    private static LuaTable BuildContextTable(INodeExecutionContext context)
    {
        var table = new LuaTable(0, 2);
        table["progress"] = new LuaFunction("ctx.progress", (functionContext, _) =>
        {
            context.ReportProgress(functionContext.GetArgument<double>(0));
            return new ValueTask<int>(functionContext.Return());
        });
        table["log"] = new LuaFunction("ctx.log", (functionContext, _) =>
        {
            context.Log.Write(LogLevel.Info, "LuaScript", functionContext.GetArgument<string>(0));
            return new ValueTask<int>(functionContext.Return());
        });
        return table;
    }

    private static LuaTable BuildUiTable(INodeExecutionContext context)
    {
        var table = new LuaTable(0, 1);
        table["confirm"] = new LuaFunction("ui.confirm", async (functionContext, functionToken) =>
            functionContext.Return(await ConfirmAsync(context, functionContext.GetArgument<string>(0), functionToken)
                .ConfigureAwait(false)));
        return table;
    }

    private static async Task<bool> ConfirmAsync(INodeExecutionContext context, string message,
        CancellationToken cancellationToken)
    {
        var request = new InteractionRequest
        {
            Form = new InteractionForm
            {
                Title = "LuaScript",
                Content = new InteractionElement
                {
                    Id = "confirm.message",
                    Kind = InteractionElementKind.Text,
                    Label = message
                }
            }
        };
        var result = await context.Interactions.ShowAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Outcome == InteractionOutcome.Submitted;
    }

    private static void InjectSurface(LuaState state, string name, LuaTable table) => state.Environment[name] = table;

    private static IReadOnlyDictionary<string, string?> ReadArguments(LuaFunctionExecutionContext context)
    {
        var arguments = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < context.ArgumentCount; index += 2)
        {
            arguments[context.GetArgument<string>(index)] =
                index + 1 < context.ArgumentCount ? ReadOptionalString(context.GetArgument(index + 1)) : null;
        }
        return arguments;
    }

    private static string? ReadOptionalString(LuaValue value) =>
        value.Type == LuaValueType.Nil ? null : value.ToString();

    private static LuaTable ConvertTable(IReadOnlyDictionary<string, string?> values)
    {
        var table = new LuaTable(0, values.Count);
        foreach (var (key, value) in values)
        {
            table[key] = value is null ? LuaValue.Nil : value;
        }
        return table;
    }

    private static string ReadDisplayString(LuaValue value) =>
        value.Type == LuaValueType.Nil ? string.Empty : value.ToString() ?? string.Empty;

    private sealed class UnifiedLogStandardIO(IUnifiedLog log) : ILuaStandardIO
    {
        public ILuaStream Input { get; } = new UnifiedLogStream(log, LogLevel.Info);
        public ILuaStream Output { get; } = new UnifiedLogStream(log, LogLevel.Info);
        public ILuaStream Error { get; } = new UnifiedLogStream(log, LogLevel.Error);
    }

    private sealed class UnifiedLogStream(IUnifiedLog log, LogLevel level) : ILuaStream
    {
        private readonly StringBuilder pending = new();
        private bool isOpen = true;

        public bool IsOpen => isOpen;

        public LuaFileOpenMode Mode => LuaFileOpenMode.Write;

        public ValueTask WriteAsync(string content, CancellationToken cancellationToken = default)
        {
            pending.Append(content);
            CommitCompletedLines();
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<char> content, CancellationToken cancellationToken = default)
        {
            pending.Append(content.Span);
            CommitCompletedLines();
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            Commit(pending.ToString());
            pending.Clear();
            return ValueTask.CompletedTask;
        }

        public ValueTask<string> ReadAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The script standard input is not supported.");

        public ValueTask<string?> ReadLineAsync(bool keepEol, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The script standard input is not supported.");

        public ValueTask<string?> ReadAsync(int count, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The script standard input is not supported.");

        public ValueTask<double?> ReadNumberAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The script standard input is not supported.");

        public void SetVBuf(LuaFileBufferingMode mode, int size) { }

        public long Seek(SeekOrigin origin, long offset) =>
            throw new NotSupportedException("The script standard streams cannot seek.");

        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            FlushAsync(cancellationToken);
            isOpen = false;
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            pending.Clear();
            isOpen = false;
        }

        private void CommitCompletedLines()
        {
            var text = pending.ToString();
            pending.Clear();
            var remaining = text.AsSpan();
            while (!remaining.IsEmpty)
            {
                var index = remaining.IndexOf('\n');
                if (index < 0)
                {
                    break;
                }

                Commit(remaining[..index]);
                remaining = remaining[(index + 1)..];
            }

            if (!remaining.IsEmpty)
            {
                pending.Append(remaining);
            }
        }

        private void Commit(ReadOnlySpan<char> line)
        {
            if (line.IsEmpty)
            {
                return;
            }
            log.Write(level, "LuaScript", line.TrimEnd('\r').ToString());
        }
    }
}