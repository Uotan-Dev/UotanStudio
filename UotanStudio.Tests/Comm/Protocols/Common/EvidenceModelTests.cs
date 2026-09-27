using UotanStudio.Comm.Protocols.Common.Evidence;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Common;

public class EvidenceModelTests
{
    [Fact]
    public void ProtocolEvidence_RecordWithEquality_KeepsAllBoundFields()
    {
        var capturedAt = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        var first = new ProtocolEvidence
        {
            Protocol = "edl",
            Phase = "sahara-hello",
            Outcome = CommandOutcomeKind.Completed,
            RawResponseHex = "70000000",
            ResourceSha256Hex = new string('a', 64),
            TransportGeneration = 7,
            ObservedAtUtc = capturedAt,
            ElapsedMs = 12
        };
        var second = first with { Outcome = CommandOutcomeKind.Rejected };

        Assert.Equal(CommandOutcomeKind.Completed, first.Outcome);
        Assert.Equal("edl", first.Protocol);
        Assert.Equal(capturedAt, first.ObservedAtUtc);
        Assert.Equal(7UL, first.TransportGeneration);
        Assert.Equal(12L, first.ElapsedMs);
        Assert.NotEqual(first.Outcome, second.Outcome);
        Assert.Equal("sahara-hello", second.Phase);
    }

    [Fact]
    public void AllPhasesCompleted_EmptyEvidenceList_ReturnsFalse()
    {
        var result = new TestExecutionResult { Evidence = [] };

        Assert.False(result.AllPhasesCompleted);
        Assert.Equal(CommandOutcomeKind.Unknown, result.FinalOutcome);
    }

    [Fact]
    public void AllPhasesCompleted_PartialFailure_ReturnsFalseAndReportsLastPhase()
    {
        var result = new TestExecutionResult
        {
            Evidence =
            [
                Step("configure", CommandOutcomeKind.Completed),
                Step("program", CommandOutcomeKind.Partial)
            ]
        };

        Assert.False(result.AllPhasesCompleted);
        Assert.Equal(CommandOutcomeKind.Partial, result.FinalOutcome);
    }

    [Fact]
    public void AllPhasesCompleted_AllAcknowledged_ReturnsTrue()
    {
        var result = new TestExecutionResult
        {
            Evidence =
            [
                Step("configure", CommandOutcomeKind.Completed),
                Step("program", CommandOutcomeKind.Completed)
            ]
        };

        Assert.True(result.AllPhasesCompleted);
        Assert.Equal(CommandOutcomeKind.Completed, result.FinalOutcome);
    }

    [Fact]
    public void CancelledEvidence_IsNotReinterpretedAsCompleted()
    {
        var result = new TestExecutionResult
        {
            Evidence = [Step("upload", CommandOutcomeKind.Cancelled)]
        };

        Assert.False(result.AllPhasesCompleted);
        Assert.Equal(CommandOutcomeKind.Cancelled, result.FinalOutcome);
    }

    [Fact]
    public void CapabilitySnapshot_DefaultResourceBinding_IsEmpty()
    {
        var snapshot = new TestSnapshot
        {
            CapturedAtUtc = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero),
            Evidence = [Step("configure", CommandOutcomeKind.Completed)]
        };

        Assert.Null(snapshot.ResourceSha256Hex);
        Assert.Null(snapshot.TransportGeneration);
        Assert.Single(snapshot.Evidence);
    }

    [Fact]
    public void FeatureSupport_Unknown_IsDistinctFromSupported()
    {
        Assert.NotEqual(FeatureSupport.Unknown, FeatureSupport.Supported);
        Assert.NotEqual(FeatureSupport.Unknown, FeatureSupport.Unsupported);
        Assert.Equal(FeatureSupport.Unknown, default);
    }

    [Fact]
    public void ResourceProvenance_LicenseUnknown_RemainsNull()
    {
        var provenance = new ResourceProvenance
        {
            Origin = ResourceOrigin.UserSupplied,
            Sha256Hex = new string('b', 64)
        };

        Assert.Null(provenance.LicenseTag);
        Assert.Null(provenance.Source);
        Assert.Equal(ResourceOrigin.UserSupplied, provenance.Origin);
    }

    private static ProtocolEvidence Step(string phase, CommandOutcomeKind outcome) => new()
    {
        Protocol = "test",
        Phase = phase,
        Outcome = outcome
    };

    private sealed record TestExecutionResult : CommandExecutionResult;

    private sealed record TestSnapshot : CapabilitySnapshot;
}