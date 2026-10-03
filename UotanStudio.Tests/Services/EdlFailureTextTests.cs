using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Flash;
using Xunit;

namespace UotanStudio.Tests.Services;

public class EdlFailureTextTests
{
    private static FirehoseResponse Parse(string xml) => FirehoseResponseParser.Parse(xml);

    private static FirehoseResponse Rejection(string responseXml, string? lastLogValue)
    {
        var response = Parse(responseXml);
        response.Error = FirehoseErrorClassifier.Classify(response, lastLogValue);
        return response;
    }

    [Fact]
    public void Describe_PacketHashMismatch_ReportsTheResetRequiringValidationWithItsEvidence()
    {
        var response = Rejection("<data><response value=\"-1\"/></data>", "ERROR: PACKET_HASH_MISMATCH");

        var text = EdlFailureText.Describe(response);

        Assert.NotNull(response.Error);
        Assert.Equal(FirehoseErrorKind.Validation, response.Error.Kind);
        Assert.Contains("packet digest mismatch", text, StringComparison.Ordinal);
        Assert.Contains("Validation", text, StringComparison.Ordinal);
        Assert.Contains("PACKET_HASH_MISMATCH", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_NakWithTargetAttributes_ReportsNegotiationRatherThanAStorageFailure()
    {
        var response = Rejection("""
            <data><response value="NAK" memoryname="ufs_lu0:0" num_partition_sectors="8"
                physical_partition_number="0" start_sector="136"/></data>
            """, "INFO: Testing");

        var text = EdlFailureText.Describe(response);

        Assert.NotNull(response.Error);
        Assert.Equal(FirehoseErrorKind.AttributeNegotiation, response.Error.Kind);
        Assert.Contains("supplied its own", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_UnexplainedNumericValue_KeepsTheRawValueVerbatim()
    {
        var response = Rejection("<data><response value=\"12\"/></data>", null);

        var text = EdlFailureText.Describe(response);

        Assert.NotNull(response.Error);
        Assert.Equal(FirehoseErrorKind.Unrecognized, response.Error.Kind);
        Assert.Contains("Value=12", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_LogOnlyBlockWithoutRejection_ReportsTheObservedLogText()
    {
        var response = Parse("<data><log value=\"INFO: programmer is ready\"/></data>");

        Assert.Null(response.Error);
        Assert.False(response.IsAck);
        Assert.Equal("INFO: programmer is ready", EdlFailureText.Describe(response));
    }

    [Fact]
    public void Describe_ValuelessResponse_PreservesThatNoMeaningWasAssigned()
    {
        var response = Parse("<data><response/></data>");

        Assert.Equal("The response value is neither an acknowledgement nor a NAK; no reference " +
            "downloader assigns a meaning to it, so the raw value is preserved. (Unrecognized)",
            EdlFailureText.Describe(response));
    }
}