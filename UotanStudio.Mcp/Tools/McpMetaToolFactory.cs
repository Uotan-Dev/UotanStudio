using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UotanStudio.Mcp.Tools;

/// <summary>
/// Builds the MCP meta-tools that expose action discovery and execution.
/// <para>构建暴露动作发现与执行的 MCP 元工具。</para>
/// </summary>
internal static class McpMetaToolFactory
{
    internal static McpServerTool CreateSearchActions(McpActionRegistry registry)
    {
        var index = new SearchIndex(registry.Actions);
        return McpServerTool.Create(
            ([Description("Natural-language or keyword query describing the desired capability.")] string query,
             [Description("Optional domain filter: parse, unpack, repack, modify, flash, verify, flow, device, project, env, misc.")] string? domain,
             [Description("Maximum results to return (1-10). Default 5.")] int? limit,
             CancellationToken cancellationToken) => ToolGuard.RunAsync(
                _ => Task.FromResult(index.Search(query, domain, limit)), cancellationToken),
            new McpServerToolCreateOptions
            {
                Name = "search_actions",
                Description = "Search internal MCP actions by keyword, domain, or capability. Use this before execute_action when the exact action name is unknown. Returns ranked action candidates with input schemas.",
                ReadOnly = true,
                Destructive = false
            });
    }

    internal static McpServerTool CreateExecuteAction(McpActionRegistry registry, McpArtifactStore artifacts) =>
        McpServerTool.Create(
            async ([Description("Action name returned by search_actions or known from catalog.")] string action,
                   [Description("Action parameters object matching the action input schema.")] JsonElement @params,
                   RequestContext<CallToolRequestParams> request,
                   CancellationToken cancellationToken) =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!registry.TryGet(action, out var descriptor))
                    {
                        return new ToolResult(false, $"Unknown action '{action}'. Use search_actions to discover available actions.",
                            ToolJson.Data(new McpActionSearchPayload(action, null, 0, [], SuggestDomains(registry, action)))).ToResult();
                    }

                    if (descriptor.FlashGated && descriptor.Destructive && !string.Equals(action, "execute_flash_plan", StringComparison.OrdinalIgnoreCase))
                    {
                        return new ToolResult(false,
                            $"Action '{action}' is a destructive flash capability. Use preview_flash_plan to create a bounded plan, then execute_flash_plan with explicit confirmation.",
                            ToolJson.Data(new FlashGatePayload(action, descriptor.FlashGated, false, "preview_flash_plan"))).ToResult();
                    }

                    var args = ConvertParams(@params);
                    var subRequest = new RequestContext<CallToolRequestParams>(
                        request.Server,
                        request.JsonRpcRequest,
                        new CallToolRequestParams { Name = descriptor.Action, Arguments = args })
                    {
                        Services = request.Services,
                        MatchedPrimitive = descriptor.Tool
                    };
                    var result = await descriptor.Tool.InvokeAsync(subRequest, cancellationToken).ConfigureAwait(false);
                    return MaybeExternalize(action, result, artifacts);
                }
                catch (OperationCanceledException)
                {
                    return new ToolResult(false, "The MCP tool call was cancelled.").ToResult();
                }
                catch (Exception ex)
                {
                    return ToolResult.FromException(ex).ToResult();
                }
            },
            new McpServerToolCreateOptions
            {
                Name = "execute_action",
                Description = "Execute an internal MCP action by name. Pass params matching the action's input schema from search_actions. Outputs larger than 32 KiB are stored as sandbox artifacts and returned as ArtifactRef+preview.",
                ReadOnly = false,
                Destructive = true
            });

    private static CallToolResult MaybeExternalize(string action, CallToolResult result, McpArtifactStore artifacts)
    {
        var text = result.Content?.OfType<TextContentBlock>().Select(b => b.Text).FirstOrDefault() ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(text) <= McpArtifactStore.DefaultThresholdBytes)
        {
            return result;
        }

        var preview = text.Length <= 240 ? text : text[..240] + "...";
        var artifact = artifacts.CreateTextArtifact($"action-{Sanitize(action)}", text, ".json", preview);
        var summary = result.IsError == true
            ? $"Action '{action}' failed; full output stored as artifact {artifact.Id}."
            : $"Action '{action}' output exceeded 32 KiB; stored as artifact {artifact.Id}.";
        var envelope = new ToolResult(result.IsError != true, summary,
            ToolJson.Data(new ArtifactEnvelopePayload(action, artifact, result.IsError == true)));
        return envelope.ToResult();
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? char.ToLowerInvariant(c) : '-');
        }
        return sb.Length == 0 ? "action" : sb.ToString();
    }

    internal sealed class SearchIndex(IReadOnlyList<McpActionDescriptor> actions)
    {
        private readonly SearchAction[] _actions = actions.Select(static action =>
        {
            var name = action.Action;
            var normalizedName = name.ToLowerInvariant();
            var domain = action.Domain;
            var summary = action.Summary ?? string.Empty;
            return new SearchAction(action, name.Split('_', StringSplitOptions.RemoveEmptyEntries),
                summary, $"{normalizedName} {domain.ToLowerInvariant()} {summary.ToLowerInvariant()}");
        }).ToArray();

        internal ToolResult Search(string query, string? domain, int? limit)
        {
            var q = (query ?? string.Empty).Trim();
            var normalizedQuery = q.ToLowerInvariant();
            var max = Math.Clamp(limit ?? 5, 1, 10);
            var tokens = Tokenize(normalizedQuery);
            var domainFilter = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();

            var scored = new List<(McpActionDescriptor Action, double Score)>(_actions.Length);
            foreach (var action in _actions)
            {
                if (domainFilter is not null && !action.Descriptor.Domain.Equals(domainFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var score = Score(action, q, normalizedQuery, tokens);
                if (score > 0 || domainFilter is not null && tokens.Count == 0)
                {
                    scored.Add((action.Descriptor, score > 0 ? score : 0.1));
                }
            }

            if (scored.Count == 0 && domainFilter is null)
            {
                foreach (var action in _actions)
                {
                    var score = Score(action, q, normalizedQuery, tokens);
                    if (score > 0) scored.Add((action.Descriptor, score));
                }
            }

            var results = scored
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Action.Action, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .Select(x => new McpActionSearchResult(
                    x.Action.Action, x.Action.Domain, x.Action.Summary,
                    x.Action.ReadOnly, x.Action.Destructive, x.Action.FlashGated,
                    Math.Round(x.Score, 4), x.Action.InputSchema.GetRawText()))
                .ToList();

            return new ToolResult(true,
                results.Count == 0 ? $"No actions matched '{q}'." : $"Found {scored.Count} action(s); returning top {results.Count}.",
                ToolJson.Data(new McpActionSearchPayload(q, domainFilter, scored.Count, results, SuggestDomains(_actions, tokens))));
        }

        private static double Score(SearchAction action, string query, string normalizedQuery, List<string> tokens)
        {
            if (tokens.Count == 0) return 0;
            double score = 0;
            var descriptor = action.Descriptor;
            var name = descriptor.Action;
            var summary = action.Summary;
            var domain = descriptor.Domain;

            if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) score += 100;
            if (action.Haystack.Contains(normalizedQuery, StringComparison.Ordinal)) score += 20;

            foreach (var token in tokens)
            {
                if (name.Equals(token, StringComparison.OrdinalIgnoreCase)) score += 30;
                else if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 12;
                if (domain.Equals(token, StringComparison.OrdinalIgnoreCase)) score += 10;
                else if (domain.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 4;
                if (summary.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 6;
                foreach (var part in action.NameParts)
                {
                    if (part.Equals(token, StringComparison.OrdinalIgnoreCase)) score += 8;
                    else if (part.StartsWith(token, StringComparison.OrdinalIgnoreCase)) score += 3;
                }
            }

            if (tokens.Any(t => t is "flash" or "刷写" or "write") && domain == "flash") score += 5;
            if (tokens.Any(t => t is "unpack" or "extract" or "解包") && name.Contains("unpack", StringComparison.OrdinalIgnoreCase)) score += 8;
            if (tokens.Any(t => t is "pack" or "repack" or "打包") && name.Contains("pack", StringComparison.OrdinalIgnoreCase)) score += 8;
            if (tokens.Any(t => t is "verify" or "校验" or "check") && (domain == "verify" || name.Contains("verify", StringComparison.OrdinalIgnoreCase))) score += 6;
            return score;
        }
    }

    private sealed record SearchAction(McpActionDescriptor Descriptor, string[] NameParts, string Summary, string Haystack);

    private static List<string> Tokenize(string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery)) return [];
        var sb = new StringBuilder(normalizedQuery.Length);
        foreach (var c in normalizedQuery)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c > 127 ? c : ' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> SuggestDomains(McpActionRegistry registry, string query)
    {
        var tokens = Tokenize(query.ToLowerInvariant());
        return registry.Actions.Select(a => a.Domain).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(d => (Domain: d, Score: tokens.Count == 0 ? 0 : tokens.Sum(t => d.Contains(t, StringComparison.OrdinalIgnoreCase) ? 1 : 0)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Domain)
            .Take(3)
            .ToList();
    }

    private static IReadOnlyList<string> SuggestDomains(IReadOnlyList<SearchAction> actions, List<string> tokens)
    {
        return actions.Select(a => a.Descriptor.Domain).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(d => (Domain: d, Score: tokens.Count == 0 ? 0 : tokens.Sum(t => d.Contains(t, StringComparison.OrdinalIgnoreCase) ? 1 : 0)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Domain)
            .Take(3)
            .ToList();
    }

    private static Dictionary<string, JsonElement>? ConvertParams(JsonElement @params)
    {
        if (@params.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        if (@params.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("execute_action params must be a JSON object.");
        }

        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in @params.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.Clone();
        }

        return dict.Count == 0 ? null : dict;
    }
}