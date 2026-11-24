using System.Text.Json;
using UotanStudio.Mcp.Tools;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class Net11McpSearchTests
{
    private static readonly IReadOnlyList<McpActionDescriptor> Actions =
    [
        Descriptor("unpack_image", "unpack", "Extract firmware image"),
        Descriptor("repack_super", "repack", "Pack super image"),
        Descriptor("flash_image", "flash", "Write image to device"),
        Descriptor("verify_image", "verify", "Verify image checksum"),
        Descriptor("parse_metadata", "parse", "Read metadata"),
        Descriptor("flash_plan", "flow", "Preview flash plan")
    ];

    [Fact]
    public void Search_GoldenQuery_PreservesScoresOrderingAndSuggestions()
    {
        var index = new McpMetaToolFactory.SearchIndex(Actions);

        var payload = Search(index, "UNPACK IMAGE", null, 5);

        Assert.Equal("UNPACK IMAGE", payload.Query);
        Assert.Equal(4, payload.TotalMatches);
        Assert.Equal(["unpack_image", "flash_image", "verify_image", "repack_super"],
            payload.Results.Select(result => result.Action));
        Assert.Equal([64, 26, 26, 6], payload.Results.Select(result => result.Score));
        Assert.Equal(["unpack"], payload.SuggestedDomains);
        var repeated = Search(index, "UNPACK IMAGE", null, 5);
        Assert.Equal(payload.Results.Select(result => result.Action), repeated.Results.Select(result => result.Action));
        Assert.Equal(payload.Results.Select(result => result.Score), repeated.Results.Select(result => result.Score));
    }

    [Fact]
    public void Search_LimitValues_ClampResultsWithoutChangingTotalMatches()
    {
        var index = new McpMetaToolFactory.SearchIndex(Actions);

        var limited = Search(index, "image", null, 2);
        var minimum = Search(index, "image", null, 0);
        var maximum = Search(index, "image", null, 99);

        Assert.Equal(4, limited.TotalMatches);
        Assert.Equal(["flash_image", "unpack_image"], limited.Results.Select(result => result.Action));
        Assert.Equal([46, 46], limited.Results.Select(result => result.Score));
        Assert.Single(minimum.Results);
        Assert.Equal(4, maximum.Results.Count);
    }

    [Fact]
    public void Search_DomainAndFallback_PreservesFilterAndEmptyQuerySemantics()
    {
        var index = new McpMetaToolFactory.SearchIndex(Actions);

        var filtered = Search(index, " ", " FLASH ", null);
        var excluded = Search(index, "flash", "verify", null);
        var fallback = Search(index, "not-a-capability", null, null);
        var suggested = Search(index, "flash", null, null);

        Assert.Equal("FLASH", filtered.Domain);
        Assert.Equal(1, filtered.TotalMatches);
        Assert.Equal(["flash_image"], filtered.Results.Select(result => result.Action));
        Assert.Equal([0.1], filtered.Results.Select(result => result.Score));
        Assert.Empty(filtered.SuggestedDomains);
        Assert.Equal(0, excluded.TotalMatches);
        Assert.Empty(excluded.Results);
        Assert.Equal(0, fallback.TotalMatches);
        Assert.Empty(fallback.Results);
        Assert.Equal(["flash"], suggested.SuggestedDomains);
    }

    private static McpActionSearchPayload Search(McpMetaToolFactory.SearchIndex index, string query, string? domain, int? limit)
    {
        var result = index.Search(query, domain, limit);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        var payload = result.Data.Value.Deserialize(McpToolJsonContext.Default.McpActionSearchPayload);
        Assert.NotNull(payload);
        return payload;
    }

    private static McpActionDescriptor Descriptor(string action, string domain, string summary) =>
        new(action, domain, summary, ToolJson.EmptyObjectSchema, true, false, false, null, null!);
}