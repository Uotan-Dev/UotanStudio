using Lua;
using Lua.Runtime;
using UotanStudio.Projects.Interactions;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Interactions;
using UotanStudio.Services.Projects;

namespace UotanStudio.Services.Scripting;

/// <summary>
/// <para>Executes one plugin Lua script inside a bare <see cref="LuaState"/>: no standard library is
/// opened, so only the host-injected <c>tool</c>, <c>ctx</c> and <c>ui</c> tables are reachable from
/// the script. The execution cancellation token flows into the interpreter, so a runaway script
/// stops when the surrounding node execution is cancelled.</para>
/// <para>在裸 <see cref="LuaState"/> 内执行一个插件 Lua 脚本：不打开任何标准库，脚本只能访问宿主注入的
/// <c>tool</c>、<c>ctx</c> 与 <c>ui</c> 三个表。执行取消令牌贯穿解释器，外层节点执行取消时失控脚本会随之停止。</para>
/// </summary>
public sealed class LuaScriptEngine
{
    /// <summary>
    /// <para>Gets the maximum accepted script source size; oversized sources are rejected before
    /// parsing to bound host memory. The bound is shared with package validation so an installed
    /// script can never exceed what the host refuses to run.</para>
    /// <para>获取接受的脚本源码大小上限；超限源码在解析前即被拒绝，以约束宿主内存。该上限与包校验共用，
    /// 因此已安装脚本不会超过宿主拒绝执行的规模。</para>
    /// </summary>
    public const int MaximumScriptLength = VisualPluginValidation.MaximumScriptLength;

    /// <summary>
    /// <para>Runs one script to completion and returns its first return value converted to a string,
    /// or an empty string when the script returns nothing.</para>
    /// <para>把脚本执行到结束并返回其首个返回值的字符串转换；脚本无返回值时返回空字符串。</para>
    /// </summary>
    /// <param name="script">
    /// <para>The Lua source to execute.</para>
    /// <para>要执行的 Lua 源码。</para>
    /// </param>
    /// <param name="chunkName">
    /// <para>Chunk name used in interpreter diagnostics, usually the plugin id.</para>
    /// <para>解释器诊断使用的块名，通常是插件 Id。</para>
    /// </param>
    /// <param name="tools">
    /// <para>Tool surface exposed to the script as <c>tool</c>; each entry is one named capability.</para>
    /// <para>以 <c>tool</c> 暴露给脚本的工具面；每个条目是一个具名能力。</para>
    /// </param>
    /// <param name="context">
    /// <para>Node execution context backing the <c>ctx</c> and <c>ui</c> tables.</para>
    /// <para>支撑 <c>ctx</c> 与 <c>ui</c> 表的节点执行上下文。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>Token cancelling script execution.</para>
    /// <para>取消脚本执行的令牌。</para>
    /// </param>
    /// <returns>The first script return value as a string, or an empty string.
    /// <para>脚本首个返回值的字符串形式；无返回值时为空字符串。</para></returns>
    /// <exception cref="ArgumentException">
    /// <para>Raised when the script is empty or exceeds <see cref="MaximumScriptLength"/>.</para>
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

        var state = LuaState.Create();
        InjectSurface(state, "tool", BuildToolTable(tools, context));
        InjectSurface(state, "ctx", BuildContextTable(context));
        InjectSurface(state, "ui", BuildUiTable(context));

        var closure = state.Load(script, chunkName);
        var results = await state.ExecuteAsync(closure,
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.CancellationToken).Token)
            .ConfigureAwait(false);
        return results.Length > 0 ? ReadDisplayString(results[0]) : string.Empty;
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
}