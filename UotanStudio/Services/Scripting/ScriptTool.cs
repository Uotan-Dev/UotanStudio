using UotanStudio.Services.Execution;

namespace UotanStudio.Services.Scripting;

/// <summary>One capability exposed to a plugin script through the <c>tool</c> table. Tools with <see cref="RequiresConfirmation"/> set are gated centrally by <see cref="LuaScriptEngine"/> before <see cref="Invoke"/> runs.
/// <para>经 <c>tool</c> 表暴露给插件脚本的一项能力。设置了 <see cref="RequiresConfirmation"/> 的工具会在 <see cref="Invoke"/> 运行前由 <see cref="LuaScriptEngine"/> 统一把关。</para></summary>
/// <param name="Name">Hierarchical dot-separated tool name the script uses.
/// <para>脚本使用的以点分段的层级工具名。</para></param>
/// <param name="Invoke">Delegate invoked after any required confirmation.
/// <para>在所需确认通过后调用的执行委托。</para></param>
/// <param name="RequiresConfirmation">Whether the engine must obtain user consent before invoking this tool.
/// <para>引擎是否必须在调用该工具前取得用户确认。</para></param>
/// <param name="ConfirmationPrompt">Message shown to the user when confirmation is required; falls back to <see cref="Name"/>.
/// <para>需要确认时展示给用户的消息；缺省回退到 <see cref="Name"/>。</para></param>
public sealed record ScriptTool(
    string Name,
    Func<ScriptToolInvocation, Task<IReadOnlyDictionary<string, string?>>> Invoke,
    bool RequiresConfirmation = false,
    string? ConfirmationPrompt = null);

/// <summary>
/// Per-call inputs handed to a <see cref="ScriptTool"/>: the named arguments, the node execution
/// context, and the linked cancellation token.
/// <para>传给 <see cref="ScriptTool"/> 的单次调用输入：具名参数、节点执行上下文与已链接的取消令牌。</para>
/// </summary>
/// <param name="Arguments">Arguments the script passed to the tool call entry point.
/// <para>脚本通过工具调用入口传入的参数。</para></param>
/// <param name="Context">Node execution context of the running script.
/// <para>当前运行脚本的节点执行上下文。</para></param>
/// <param name="CancellationToken">Token cancelled together with the node execution and the outer run.
/// <para>随节点执行与外部运行一同取消的令牌。</para></param>
public sealed record ScriptToolInvocation(
    IReadOnlyDictionary<string, string?> Arguments,
    INodeExecutionContext Context,
    CancellationToken CancellationToken);