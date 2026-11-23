using System.Globalization;
using Jint;
using Jint.Native;
using UotanStudio.Scripting.Apis;

namespace UotanStudio.Scripting;

/// <summary>
/// Executes one untrusted script inside a hardened Jint engine and returns a bounded result.
/// <para>在加固后的 Jint 引擎中执行一段不可信脚本，并返回有界结果。</para>
/// </summary>
public static class ScriptRunner
{
    /// <summary>
    /// Runs one script to completion with a fresh engine.
    /// <para>使用全新引擎将一段脚本执行至完成。</para>
    /// </summary>
    /// <param name="source">JavaScript source text.<para>JavaScript 脚本文本。</para></param>
    /// <param name="context">Host capability injection point, or <see langword="null"/> for simulated capabilities.
    /// <para>宿主能力注入点；<see langword="null"/> 表示使用模拟能力。</para></param>
    /// <param name="limits">Resource ceilings, or <see langword="null"/> for <see cref="ScriptLimits.Default"/>.
    /// <para>资源上限；<see langword="null"/> 表示使用 <see cref="ScriptLimits.Default"/>。</para></param>
    /// <param name="cancellationToken">Cancels the whole operation.<para>取消整个操作。</para></param>
    /// <returns>The bounded success or failure result.<para>有界的成功或失败结果。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is empty.<para><paramref name="source"/> 为空。</para></exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.<para>操作已取消。</para></exception>
    public static async Task<ScriptExecutionResult> RunAsync(
        string source,
        ScriptApiContext? context = null,
        ScriptLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var profile = limits ?? ScriptLimits.Default;
        var apiContext = context ?? new ScriptApiContext();
        if (apiContext.Log is CollectingLogSink collector)
        {
            collector.Limit = profile.MaxLogEntries;
        }

        if (source.Length > profile.MaxSourceLength)
        {
            return ScriptExecutionResult.Failed(
                $"Script source exceeds {profile.MaxSourceLength} characters.", CollectLogs(apiContext));
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var options = new Options
            {
                Strict = true
            };
            options.TimeoutInterval(profile.Timeout);
            options.MaxStatements(profile.MaxStatements);
            options.LimitMemory(profile.MaxMemoryBytes);
            options.CancellationToken(linked.Token);
            options.Constraints.MaxRecursionDepth = profile.MaxRecursionDepth;
            options.Interop.AllowGetType = false;
            options.Interop.AllowSystemReflection = false;
            options.Interop.AllowWrite = false;
            options.Host.StringCompilationAllowed = false;
            options.Debugger.Enabled = false;

            using var engine = new Engine(options);
            ScriptApiCatalog.Register(engine, apiContext, profile);
            var value = await engine.EvaluateAsync(source, cancellationToken: linked.Token).ConfigureAwait(false);
            return ScriptExecutionResult.Succeeded(Describe(value, profile.MaxResultLength), CollectLogs(apiContext));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ScriptExecutionResult.Failed(DescribeError(exception, profile.MaxResultLength), CollectLogs(apiContext));
        }
    }

    private static IReadOnlyList<ScriptLogEntry> CollectLogs(ScriptApiContext context) =>
        context.Log is CollectingLogSink collector ? [.. collector.Entries] : [];

    private static string? Describe(JsValue value, int maxLength)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        string text;
        if (value.IsString())
        {
            text = value.AsString();
        }
        else if (value.IsNumber())
        {
            text = value.AsNumber().ToString(CultureInfo.InvariantCulture);
        }
        else if (value.IsBoolean())
        {
            text = value.AsBoolean() ? "true" : "false";
        }
        else
        {
            text = "[value]";
        }

        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string DescribeError(Exception exception, int maxLength)
    {
        var text = string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}