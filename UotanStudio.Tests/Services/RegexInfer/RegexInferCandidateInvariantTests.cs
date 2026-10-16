using System.Text.RegularExpressions;
using UotanStudio.RegexInfer;
using Xunit;
using Engine = UotanStudio.RegexInfer.RegexInfer;

namespace UotanStudio.Tests.Services.RegexInfer;

public sealed class RegexInferCandidateInvariantTests
{
    private const string BuildPropsSource =
        "ro.build.version.sdk=34\nro.product.model=Pixel 8\npersist.sys.timezone=America/New_York\n";

    private static readonly string[] BuildPropsOutputs = ["34", "Pixel 8", "America/New_York"];

    private const string WholeSource = "34";

    [Fact]
    public void Infer_TopResultsRequested_ReturnsNoMoreThanRequested()
    {
        var candidates = Engine.Infer(
            BuildPropsSource,
            BuildPropsOutputs,
            new RegexInferenceOptions { TopResults = 3 });

        Assert.NotEmpty(candidates);
        Assert.True(candidates.Count <= 3);
    }

    [Fact]
    public void Infer_TopResultsDefault_ReturnsExactlyOneCandidate()
    {
        var candidates = Engine.Infer(
            BuildPropsSource,
            BuildPropsOutputs,
            new RegexInferenceOptions());

        Assert.Single(candidates);
    }

    [Fact]
    public void Infer_TopResultsBelowOne_ReturnsAtLeastOneCandidate()
    {
        var candidates = Engine.Infer(WholeSource, ["34"], new RegexInferenceOptions { TopResults = 0 });

        Assert.NotEmpty(candidates);
    }

    [Fact]
    public void Infer_EveryCandidate_CompilesAndMatchesPositiveTotal()
    {
        var candidates = Engine.Infer(
            BuildPropsSource,
            BuildPropsOutputs,
            new RegexInferenceOptions { TopResults = 3 });

        Assert.NotEmpty(candidates);
        foreach (var candidate in candidates)
        {
            Assert.False(string.IsNullOrEmpty(candidate.Pattern));
            Assert.True(candidate.TryCreateCompiled(out _));
            Assert.Equal(candidate.PositiveTotal, candidate.PositiveMatches);
            Assert.Equal(BuildPropsOutputs.Length, candidate.PositiveTotal);
        }
    }

    [Fact]
    public void Infer_EveryCandidate_HasConfidenceInsideUnitInterval()
    {
        var candidates = Engine.Infer(
            BuildPropsSource,
            BuildPropsOutputs,
            new RegexInferenceOptions());

        Assert.NotEmpty(candidates);
        foreach (var candidate in candidates)
        {
            Assert.InRange(candidate.Confidence, 0.0, 1.0);
            Assert.True(candidate.Confidence > 0.0);
        }
    }

    [Fact]
    public void Infer_CandidatesOrderedByDescendingConfidence()
    {
        var candidates = Engine.Infer(
            BuildPropsSource,
            BuildPropsOutputs,
            new RegexInferenceOptions { TopResults = 3 });

        Assert.NotEmpty(candidates);
        for (var index = 1; index < candidates.Count; index++)
        {
            Assert.True(candidates[index - 1].Confidence >= candidates[index].Confidence);
        }
    }

    [Fact]
    public void Infer_SameInputTwice_ProducesIdenticalCandidates()
    {
        var options = new RegexInferenceOptions();

        var first = Engine.Infer(BuildPropsSource, BuildPropsOutputs, options);
        var second = Engine.Infer(BuildPropsSource, BuildPropsOutputs, options);

        Assert.Equal(first.Count, second.Count);
        Assert.NotEmpty(first);
        for (var index = 0; index < first.Count; index++)
        {
            Assert.Equal(first[index].Pattern, second[index].Pattern);
            Assert.Equal(first[index].Confidence, second[index].Confidence);
            Assert.Equal(first[index].PositiveMatches, second[index].PositiveMatches);
            Assert.Equal(first[index].TraceCount, second[index].TraceCount);
        }
    }

    [Fact]
    public void Infer_TwoOverloadsOnSameInput_AgreeOnEveryCandidate()
    {
        var options = new RegexInferenceOptions();

        var withoutNegatives = Engine.Infer(BuildPropsSource, BuildPropsOutputs, options);
        var withEmptyNegatives = Engine.Infer(BuildPropsSource, BuildPropsOutputs, [], options);

        Assert.NotEmpty(withoutNegatives);
        Assert.Equal(withoutNegatives.Count, withEmptyNegatives.Count);
        for (var index = 0; index < withoutNegatives.Count; index++)
        {
            Assert.Equal(withoutNegatives[index].Pattern, withEmptyNegatives[index].Pattern);
        }
    }

    [Fact]
    public void InferDetailed_AndInfer_ReturnTheSameCandidateList()
    {
        var options = new RegexInferenceOptions();

        var detailed = Engine.InferDetailed(BuildPropsSource, BuildPropsOutputs, [], options);
        var plain = Engine.Infer(BuildPropsSource, BuildPropsOutputs, options);

        Assert.Equal(BuildPropsOutputs.Length, detailed.PositiveTotal);
        Assert.True(detailed.TraceCount >= BuildPropsOutputs.Length);
        Assert.Equal(detailed.Candidates.Count, plain.Count);
        Assert.NotEmpty(detailed.Candidates);
        for (var index = 0; index < plain.Count; index++)
        {
            Assert.Equal(detailed.Candidates[index].Pattern, plain[index].Pattern);
        }
    }

    [Fact]
    public void Infer_NegativesThatCannotBeExtracted_AreRejectedByEveryCandidate()
    {
        string[] negatives = ["!!!@@@###", "%%%^^^&&&", "no-digits-here"];
        string[] expected = ["abc123"];

        var candidates = Engine.Infer("abc123", expected, negatives, new RegexInferenceOptions());

        Assert.NotEmpty(candidates);
        foreach (var candidate in candidates)
        {
            Assert.Equal(0, candidate.NegativeLeaks);
            Assert.True(candidate.TryCreateCompiled(out var compiled));
            Assert.NotNull(compiled);
            Assert.DoesNotContain(negatives, negative => Extracts(compiled, "abc123", negative));
        }
    }

    [Fact]
    public void Infer_NegativeEqualToPositive_ReportsNegativeLeak()
    {
        string[] expected = ["34"];
        string[] negatives = ["34"];

        var result = Engine.InferDetailed("34", expected, negatives, new RegexInferenceOptions());

        Assert.NotEmpty(result.Candidates);
        Assert.Equal(1, result.Candidates[0].NegativeLeaks);
        Assert.True(result.Candidates[0].TryCreateCompiled(out var compiled));
        Assert.NotNull(compiled);
        Assert.True(Extracts(compiled, "34", "34"));
    }

    [Fact]
    public void Infer_AllowPlusDisabled_WholeSourcePatternStillExtractsPositive()
    {
        var candidates = Engine.Infer(
            "34",
            ["34"],
            new RegexInferenceOptions { AllowPlus = false, AllowStar = false });

        Assert.NotEmpty(candidates);
        Assert.True(candidates[0].TryCreateCompiled(out var compiled));
        Assert.NotNull(compiled);
        Assert.Equal("34", compiled.Match("34").Value);
    }

    [Fact]
    public void Infer_MinConstantLengthAbovePositiveLength_StillProducesCandidate()
    {
        var candidates = Engine.Infer(
            WholeSource,
            ["34"],
            new RegexInferenceOptions { MinConstantLength = 8 });

        Assert.NotEmpty(candidates);
        Assert.True(candidates[0].TryCreateCompiled(out var compiled));
        Assert.NotNull(compiled);
        Assert.Equal("34", compiled.Match("34").Value);
    }

    [Fact]
    public void Infer_MaxOutputLengthAbovePatternLength_DoesNotDiscardCandidate()
    {
        var candidates = Engine.Infer(
            WholeSource,
            ["34"],
            new RegexInferenceOptions { MaxOutputLength = 1024 });

        Assert.NotEmpty(candidates);
        Assert.True(candidates[0].Pattern.Length <= 1024);
    }

    [Fact]
    public void Infer_TraceCountIsAtLeastThePositiveCount()
    {
        var result = Engine.InferDetailed(
            BuildPropsSource,
            BuildPropsOutputs,
            [],
            new RegexInferenceOptions());

        Assert.Equal(BuildPropsOutputs.Length, result.PositiveTotal);
        Assert.True(result.TraceCount >= result.PositiveTotal);
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