using System.Text.RegularExpressions;
using UotanStudio.RegexInfer;
using Xunit;
using Engine = UotanStudio.RegexInfer.RegexInfer;

namespace UotanStudio.Tests.Services.RegexInfer;

public sealed class RegexInferContractTests
{
    private const string BuildPropsSource =
        "ro.build.version.sdk=34\nro.product.model=Pixel 8\npersist.sys.timezone=America/New_York\n";

    private static readonly string[] BuildPropsOutputs = ["34", "Pixel 8", "America/New_York"];

    [Fact]
    public void Infer_OutputsEmbeddedInContext_ReturnsPatternExtractingEveryPositive()
    {
        var candidates = Engine.Infer(
            BuildPropsSource,
            BuildPropsOutputs,
            new RegexInferenceOptions());

        Assert.NotEmpty(candidates);
        foreach (var candidate in candidates)
        {
            var compiled = Compile(candidate);
            foreach (var expected in BuildPropsOutputs)
            {
                Assert.True(
                    Extracts(compiled, BuildPropsSource, expected),
                    $"pattern {candidate.Pattern} did not extract {expected}");
            }
        }
    }

    [Fact]
    public void Infer_OutputsEmbeddedInContext_TopCandidateReportsEveryPositiveMatched()
    {
        var candidates = Engine.Infer(
            BuildPropsSource,
            BuildPropsOutputs,
            new RegexInferenceOptions());

        Assert.NotEmpty(candidates);
        var top = candidates[0];
        Assert.Equal(BuildPropsOutputs.Length, top.PositiveTotal);
        Assert.Equal(BuildPropsOutputs.Length, top.PositiveMatches);
        Assert.Equal(0, top.NegativeLeaks);
        Assert.InRange(top.Confidence, 0.0, 1.0);
        Assert.True(top.Confidence > 0.0);
        var compiled = Compile(top);
        foreach (var expected in BuildPropsOutputs)
        {
            Assert.True(Extracts(compiled, BuildPropsSource, expected));
        }
    }

    [Fact]
    public void Infer_OutputsEmbeddedInContext_EveryCandidateMatchesPositiveTotal()
    {
        var candidates = Engine.Infer(
            BuildPropsSource,
            BuildPropsOutputs,
            new RegexInferenceOptions());

        Assert.NotEmpty(candidates);
        foreach (var candidate in candidates)
        {
            Assert.Equal(candidate.PositiveTotal, candidate.PositiveMatches);
            Assert.Equal(BuildPropsOutputs.Length, candidate.PositiveTotal);
            Assert.True(candidate.TraceCount >= BuildPropsOutputs.Length);
        }
    }

    [Fact]
    public void Infer_WholeSourceIsTheOutput_ProducesAnchoredDigitGoldenPattern()
    {
        var candidates = Engine.Infer("34", ["34"], new RegexInferenceOptions());

        Assert.Single(candidates);
        var top = candidates[0];
        Assert.Equal("^(\\d+)$", top.Pattern);
        Assert.Equal(1, top.PositiveMatches);
        Assert.Equal(1, top.PositiveTotal);
        Assert.Equal(0, top.NegativeLeaks);
        var compiled = Compile(top);
        Assert.Equal("34", compiled.Match("34").Value);
    }

    [Fact]
    public void Infer_WholeSourceIsAlphanumeric_ProducesAnchoredWordGoldenPattern()
    {
        var candidates = Engine.Infer("alpha42", ["alpha42"], new RegexInferenceOptions());

        Assert.Single(candidates);
        var top = candidates[0];
        Assert.Equal("^(\\w+)$", top.Pattern);
        var compiled = Compile(top);
        Assert.Equal("alpha42", compiled.Match("alpha42").Value);
    }

    [Fact]
    public void Infer_SinglePositiveAfterEquals_ProducesDigitGroupGoldenPattern()
    {
        var candidates = Engine.Infer(BuildPropsSource, ["34"], new RegexInferenceOptions());

        Assert.Single(candidates);
        var top = candidates[0];
        Assert.Equal("=(\\d+)\\n", top.Pattern);
        Assert.Equal(1, top.PositiveMatches);
        Assert.Equal(1, top.PositiveTotal);
        var compiled = Compile(top);
        Assert.True(Extracts(compiled, BuildPropsSource, "34"));
    }

    [Fact]
    public void Infer_MixedPositivesAcrossLines_ProducesNonSeparatorGroupGoldenPattern()
    {
        var candidates = Engine.Infer(
            BuildPropsSource,
            BuildPropsOutputs,
            new RegexInferenceOptions());

        Assert.Single(candidates);
        var top = candidates[0];
        Assert.Equal("=([^\\n=]+)\\n", top.Pattern);
        Assert.Equal(BuildPropsOutputs.Length, top.PositiveMatches);
    }

    [Fact]
    public void Infer_DistinguishableNegatives_ReportNoLeaksAndRejectNegatives()
    {
        string[] negatives = ["Pixel 8 Pro", "35", "America/Los_Angeles"];

        var candidates = Engine.Infer(
            BuildPropsSource,
            BuildPropsOutputs,
            negatives,
            new RegexInferenceOptions());

        Assert.NotEmpty(candidates);
        var top = candidates[0];
        Assert.Equal(0, top.NegativeLeaks);
        var compiled = Compile(top);
        Assert.DoesNotContain(negatives, negative => Extracts(compiled, BuildPropsSource, negative));
    }

    [Fact]
    public void Infer_NegativesAreOtherExtractableValues_ReportsLeakCount()
    {
        const string source = "ro.build.version.sdk=34\nro.product.model=Pixel 8\nro.product.locale=en-US\n";
        string[] expected = ["34", "Pixel 8"];
        string[] negatives = ["en-US"];

        var result = Engine.InferDetailed(source, expected, negatives, new RegexInferenceOptions());

        Assert.NotEmpty(result.Candidates);
        Assert.Equal(1, result.Candidates[0].NegativeLeaks);
        var compiled = Compile(result.Candidates[0]);
        Assert.True(Extracts(compiled, source, "en-US"));
    }

    [Fact]
    public void Infer_ExpectedOutputAbsentFromSource_ReturnsEmptyResult()
    {
        var candidates = Engine.Infer(
            BuildPropsSource,
            ["not-present-xyz"],
            new RegexInferenceOptions());

        Assert.Empty(candidates);
    }

    [Fact]
    public void Infer_ExpectedOutputIsNearMissInSource_ReturnsEmptyResult()
    {
        var candidates = Engine.Infer(BuildPropsSource, ["30"], new RegexInferenceOptions());

        Assert.Empty(candidates);
    }

    [Fact]
    public void Infer_NoExpectedOutputs_ReturnsEmptyResult()
    {
        var candidates = Engine.Infer(BuildPropsSource, [], new RegexInferenceOptions());

        Assert.Empty(candidates);
    }

    [Fact]
    public void InferDetailed_ContextExtraction_ReportsTotalsAndCandidates()
    {
        var result = Engine.InferDetailed(
            BuildPropsSource,
            BuildPropsOutputs,
            [],
            new RegexInferenceOptions());

        Assert.Equal(BuildPropsOutputs.Length, result.PositiveTotal);
        Assert.True(result.TraceCount >= BuildPropsOutputs.Length);
        Assert.NotEmpty(result.Candidates);
    }

    [Fact]
    public void InferDetailed_ExpectedOutputAbsentFromSource_ReportsPositiveTotalWithoutCandidates()
    {
        var result = Engine.InferDetailed(
            BuildPropsSource,
            ["not-present-xyz"],
            [],
            new RegexInferenceOptions());

        Assert.Equal(1, result.PositiveTotal);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Infer_PreferAnchorsDisabled_WholeSourcePatternHasNoAnchor()
    {
        var candidates = Engine.Infer(
            "34",
            ["34"],
            new RegexInferenceOptions { PreferAnchors = false });

        Assert.NotEmpty(candidates);
        var top = candidates[0];
        Assert.False(top.Pattern.StartsWith('^'));
        var compiled = Compile(top);
        Assert.Equal("34", compiled.Match("34").Value);
    }

    [Fact]
    public void Infer_PreferAnchorsEnabled_WholeSourcePatternIsAnchored()
    {
        var candidates = Engine.Infer("34", ["34"], new RegexInferenceOptions());

        Assert.NotEmpty(candidates);
        Assert.StartsWith("^", candidates[0].Pattern);
        Assert.EndsWith("$", candidates[0].Pattern);
    }

    [Fact]
    public void Infer_MultipleDistinctFieldsPerRecord_ReturnsNoCandidate()
    {
        var source = "name,age,city\nalice,30,paris\n";
        string[] expected = ["alice", "30", "paris"];

        var result = Engine.InferDetailed(source, expected, [], new RegexInferenceOptions());

        Assert.Equal(expected.Length, result.PositiveTotal);
        Assert.Empty(result.Candidates);
    }

    private static Regex Compile(InferredRegex candidate)
    {
        Assert.True(
            candidate.TryCreateCompiled(out var compiled),
            $"pattern {candidate.Pattern} failed to compile");
        Assert.NotNull(compiled);

        return compiled;
    }

    private static bool Extracts(Regex compiled, string source, string value)
    {
        foreach (Match match in compiled.Matches(source))
        {
            if (string.Equals(match.Groups[1].Value, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}