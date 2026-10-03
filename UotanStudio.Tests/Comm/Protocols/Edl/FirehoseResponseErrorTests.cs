using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

/// <summary>
/// Firehose rejection classification: the kinds the reference downloader can evidence, the raw
/// values it cannot explain, and the unchanged session behaviour for a plain NAK — all driven
/// through <see cref="FirehoseResponseParser"/> and <see cref="ScriptedCommTransport"/>.
/// <para>Firehose 拒绝分类：参考下载器有据可循的种类、它无法解释的原始取值，以及普通 NAK 保持不变的
/// 会话行为——全部通过 <see cref="FirehoseResponseParser"/> 与 <see cref="ScriptedCommTransport"/> 驱动。</para>
/// </summary>
public class FirehoseResponseErrorTests
{
    [Fact]
    public void Classify_AcknowledgedResponse_ReturnsNoError()
    {
        var response = ParseResponse("<data><response value=\"ACK\" rawmode=\"false\"/></data>");

        Assert.Null(FirehoseErrorClassifier.Classify(response, "Programmer ready"));
    }

    [Fact]
    public void Classify_RawModeResponse_ReturnsNoError()
    {
        var response = ParseResponse("<data><response value=\"NAK\" rawmode=\"true\"/></data>");

        Assert.Null(FirehoseErrorClassifier.Classify(response));
    }

    [Fact]
    public void Classify_NumericResponseValue_PreservesRawValueAsUnrecognized()
    {
        var response = ParseResponse("<data><response value=\"12\"/></data>");

        var error = FirehoseErrorClassifier.Classify(response);

        Assert.NotNull(error);
        Assert.Equal(FirehoseErrorKind.Unrecognized, error.Kind);
        Assert.Equal("12", error.RawValue);
        Assert.Equal(12L, error.NumericValue);
        Assert.False(error.IsFatal);
        Assert.False(error.AssignsTargetAttributes);
    }

    [Fact]
    public void Classify_NonNumericUnknownValue_PreservesTextWithoutNumericCode()
    {
        var response = ParseResponse("<data><response value=\"MEMORY\"/></data>");

        var error = FirehoseErrorClassifier.Classify(response);

        Assert.NotNull(error);
        Assert.Equal(FirehoseErrorKind.Unrecognized, error.Kind);
        Assert.Equal("MEMORY", error.RawValue);
        Assert.Null(error.NumericValue);
    }

    [Fact]
    public void Classify_NakWithoutAssignedAttributes_ClassifiesAsStorage()
    {
        var response = ParseResponse("<data><response value=\"NAK\"/></data>");

        var error = FirehoseErrorClassifier.Classify(response, "Negotiating payload size");

        Assert.NotNull(error);
        Assert.Equal(FirehoseErrorKind.Storage, error.Kind);
        Assert.Equal("NAK", error.RawValue);
        Assert.False(error.AssignsTargetAttributes);
        Assert.False(error.IsFatal);
    }

    [Fact]
    public void Classify_NakWithAttributesAndNoLog_ClassifiesAsRejected()
    {
        var response = ParseResponse("<data><response value=\"NAK\" MaxPayloadSizeToTargetInBytes=\"4096\"/></data>");

        var error = FirehoseErrorClassifier.Classify(response);

        Assert.NotNull(error);
        Assert.Equal(FirehoseErrorKind.Rejected, error.Kind);
        Assert.True(error.AssignsTargetAttributes);
        Assert.Equal(4096L, response.GetInt64("MaxPayloadSizeToTargetInBytes"));
    }

    [Fact]
    public void Classify_NakWithCleanLog_ClassifiesAsAttributeNegotiation()
    {
        var response = ParseResponse("<data><response value=\"NAK\" MaxPayloadSizeToTargetInBytes=\"4096\"/></data>");

        var error = FirehoseErrorClassifier.Classify(response, "Switching to target parameters");

        Assert.NotNull(error);
        Assert.Equal(FirehoseErrorKind.AttributeNegotiation, error.Kind);
        Assert.Null(error.LogToken);
        Assert.False(error.IsFatal);
    }

    [Fact]
    public void Classify_NakWithErrorLog_ClassifiesAsStorage()
    {
        var response = ParseResponse("<data><response value=\"NAK\" MemoryName=\"ufs\"/></data>");

        var error = FirehoseErrorClassifier.Classify(response, "ERROR: ufs init failed");

        Assert.NotNull(error);
        Assert.Equal(FirehoseErrorKind.Storage, error.Kind);
        Assert.Equal("ERROR", error.LogToken);
        Assert.True(error.AssignsTargetAttributes);
    }

    [Fact]
    public void Classify_NakWithLowercaseErrorLog_DoesNotMatchStorageToken()
    {
        var response = ParseResponse("<data><response value=\"NAK\" MemoryName=\"ufs\"/></data>");

        var error = FirehoseErrorClassifier.Classify(response, "error: ufs init failed");

        Assert.NotNull(error);
        Assert.Equal(FirehoseErrorKind.AttributeNegotiation, error.Kind);
    }

    [Fact]
    public void Classify_NakWithHashMismatchLog_ClassifiesAsFatalValidation()
    {
        var response = ParseResponse("<data><response value=\"NAK\"/></data>");

        var error = FirehoseErrorClassifier.Classify(response, "PACKET_HASH_MISMATCH");

        Assert.NotNull(error);
        Assert.Equal(FirehoseErrorKind.Validation, error.Kind);
        Assert.Equal("PACKET_HASH_MISMATCH", error.LogToken);
        Assert.True(error.IsFatal);
    }

    [Fact]
    public void Classify_HashMismatchLogWithAttributes_ClassifiesAsFatalValidation()
    {
        var response = ParseResponse("<data><response value=\"NAK\" num_partition_sectors=\"1\"/></data>");

        var error = FirehoseErrorClassifier.Classify(response, "WARNING PACKET_HASH_MISMATCH detected");

        Assert.NotNull(error);
        Assert.Equal(FirehoseErrorKind.Validation, error.Kind);
        Assert.True(error.AssignsTargetAttributes);
    }

    [Fact]
    public void Classify_NullResponse_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FirehoseErrorClassifier.Classify(null!));
    }

    [Fact]
    public void ParseDataBlock_LogThenNakInSameBlock_AttachesClassificationToResponse()
    {
        var block = FirehoseResponseParser.ParseDataBlock("""
            <data>
            <log value="ERROR: storage init failed"/>
            <response value="NAK" MaxPayloadSizeToTargetInBytes="4096"/>
            </data>
            """);

        Assert.NotNull(block.Response?.Error);
        Assert.Equal(FirehoseErrorKind.Storage, block.Response!.Error!.Kind);
        Assert.Equal("ERROR", block.Response.Error.LogToken);
    }

    [Fact]
    public void ParseDataBlock_HashMismatchLogThenNak_AttachesFatalValidation()
    {
        var block = FirehoseResponseParser.ParseDataBlock("""
            <data>
            <log value="PACKET_HASH_MISMATCH"/>
            <response value="NAK"/>
            </data>
            """);

        Assert.True(block.Response?.Error?.IsFatal);
        Assert.Equal(FirehoseErrorKind.Validation, block.Response?.Error?.Kind);
    }

    [Fact]
    public void ParseDataBlock_AckResponse_AttachesNoError()
    {
        var block = FirehoseResponseParser.ParseDataBlock("""
            <data>
            <log value="supported functions: program read"/>
            <response value="ACK"/>
            </data>
            """);

        Assert.Null(block.Response?.Error);
    }

    [Fact]
    public void Parse_LogOnlyBlock_AttachesNoError()
    {
        var response = ParseResponse("<data><log value=\"ERROR: storage init failed\"/></data>");

        Assert.True(response.IsLog);
        Assert.Null(response.Error);
    }

    [Fact]
    public void ToString_IncludesKindAndPreservedRawValue()
    {
        var response = ParseResponse("<data><response value=\"7\"/></data>");

        var error = FirehoseErrorClassifier.Classify(response);

        Assert.NotNull(error);
        Assert.Contains("Unrecognized", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Value=7", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PowerAsync_NakWithCleanLog_KeepsSessionUsableAndClassifiesNegotiation()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("""
            <data>
            <log value="Target uses its own payload size"/>
            <response value="NAK" rawmode="false" MaxPayloadSizeToTargetInBytes="4096"/>
            </data>
            """);
        using var protocol = CreateProtocol(transport);

        var response = await protocol.PowerOffAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsNak);
        Assert.Equal(FirehoseErrorKind.AttributeNegotiation, response.Error?.Kind);
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task PowerAsync_HashMismatchLogBeforeNak_FaultsSessionAndReportsRejection()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("""
            <data>
            <log value="PACKET_HASH_MISMATCH"/>
            <response value="NAK" rawmode="false"/>
            </data>
            """);
        using var protocol = CreateProtocol(transport);

        var response = await protocol.PowerOffAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FirehoseErrorKind.Validation, response.Error?.Kind);
        Assert.True(protocol.RequiresReconnect);
        Assert.Equal(EdlFailureKind.Rejected, protocol.LastFailureKind);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task PowerAsync_PlainNak_FaultsSessionNoDifferentlyThanBefore()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("<data><response value=\"NAK\" rawmode=\"false\"/></data>");
        using var protocol = CreateProtocol(transport);

        var response = await protocol.PowerOffAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FirehoseErrorKind.Storage, response.Error?.Kind);
        Assert.False(response.Error?.IsFatal);
        Assert.False(protocol.RequiresReconnect);
    }

    private static FirehoseResponse ParseResponse(string xml) => FirehoseResponseParser.Parse(xml);

    private static FirehoseProtocol CreateProtocol(ScriptedCommTransport transport)
        => new(transport, new FirehoseConfiguration
        {
            SectorSize = 4096,
            AutoDetectSectorSize = false
        });
}