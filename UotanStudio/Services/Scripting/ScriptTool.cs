using UotanStudio.Services.Execution;

namespace UotanStudio.Services.Scripting;

/// <summary>
/// <para>One host capability exposed to a plugin script through the <c>tool</c> table. A tool whose
/// <see cref="RequiresConfirmation"/> is set is gated centrally by <see cref="LuaScriptEngine"/> before
/// its <see cref="Invoke"/> ever runs, so dangerous capabilities share a single consent point rather
/// than each re-implementing one.</para>
/// <para>经 <c>tool</c> 表暴露给插件脚本的一项宿主能力。设置了 <see cref="RequiresConfirmation"/> 的工具会由
/// <see cref="LuaScriptEngine"/> 在调用 <see cref="Invoke"/> 之前统一征询确认，危险能力因此共用同一个确认点，
/// 而不必各自重复实现。</para>
/// </summary>
/// <param name="Name">
/// <para>Hierarchical tool name the script uses, such as <c>Device.Partition.Flash</c>.</para>
/// <para>脚本使用的层级工具名，例如 <c>Device.Partition.Flash</c>。</para>
/// </param>
/// <param name="Invoke">
/// <para>Host implementation invoked after any required confirmation.</para>
/// <para>在所需确认通过后调用的宿主实现。</para>
/// </param>
/// <param name="RequiresConfirmation">
/// <para>Whether the engine must obtain user consent before invoking this tool.</para>
/// <para>引擎是否必须在调用该工具前取得用户确认。</para>
/// </param>
/// <param name="ConfirmationPrompt">
/// <para>Message shown to the user when confirmation is required; falls back to <see cref="Name"/>.</para>
/// <para>需要确认时展示给用户的消息；缺省回退到 <see cref="Name"/>。</para>
/// </param>
public sealed record ScriptTool(
    string Name,
    Func<ScriptToolInvocation, Task<IReadOnlyDictionary<string, string?>>> Invoke,
    bool RequiresConfirmation = false,
    string? ConfirmationPrompt = null);

/// <summary>
/// <para>Per-call inputs handed to a <see cref="ScriptTool"/>: the named arguments, the node execution
/// context, and the linked cancellation token.</para>
/// <para>传给 <see cref="ScriptTool"/> 的单次调用输入：具名参数、节点执行上下文与已链接的取消令牌。</para>
/// </summary>
/// <param name="Arguments">
/// <para>Arguments the script passed to <c>tool.call</c>.</para>
/// <para>脚本传给 <c>tool.call</c> 的参数。</para>
/// </param>
/// <param name="Context">
/// <para>Node execution context of the running script.</para>
/// <para>当前运行脚本的节点执行上下文。</para>
/// </param>
/// <param name="CancellationToken">
/// <para>Token cancelled together with the node execution and the outer run.</para>
/// <para>随节点执行与外部运行一同取消的令牌。</para>
/// </param>
public sealed record ScriptToolInvocation(
    IReadOnlyDictionary<string, string?> Arguments,
    INodeExecutionContext Context,
    CancellationToken CancellationToken);