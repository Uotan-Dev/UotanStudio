using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Covers the vendor authentication exchange a programmer demands before it accepts configure: the command
/// shapes, the refusal classification, the fail-fast stop and the signature data phase.
/// <para>覆盖 programmer 在接受 configure 之前要求的厂商认证交换：命令形态、拒绝分类、快速停止与签名数据阶段。</para>
/// </summary>
public sealed class FirehoseVendorAuthTests
{
    private const string AuthenticationRefusal =
        "ERROR: Only nop and sig tag can be recevied before authentication.";

    private static byte[] Document(string body) => Encoding.UTF8.GetBytes(
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<data>\n{body}\n</data>\n");

    private static byte[] Log(string value) => Document($"<log value=\"{value}\" />");

    private static byte[] Response(string value, bool rawMode = false) => Document(rawMode
        ? $"<response value=\"{value}\" rawmode=\"true\" />"
        : $"<response value=\"{value}\" />");

    private static FirehoseConfiguration FastConfiguration() => new()
    {
        ResponseTimeoutSeconds = 1,
        ConfigureRetryIntervalMs = 1,
        ConfigureResponseTimeoutMs = 500,
        ConfigureFirstAttemptTimeoutMs = 500
    };

    private static byte[] Signature(byte seed)
    {
        var signature = new byte[FirehoseProtocol.VendorSignatureLength];
        for (var index = 0; index < signature.Length; index++)
        {
            signature[index] = (byte)(seed + index);
        }

        return signature;
    }

    [Fact]
    public void BuildSig_SizeDelimitedForm_EmitsDeclarationBeforeTheDataPhase()
    {
        var xml = FirehoseXmlBuilder.BuildSig("sig", FirehoseProtocol.VendorSignatureLength);

        Assert.StartsWith(EdlConstants.XmlHeader, xml);
        Assert.Contains(
            $"<sig TargetName=\"sig\" size_in_bytes=\"{FirehoseProtocol.VendorSignatureLength}\" verbose=\"1\" />",
            xml, StringComparison.Ordinal);
        Assert.EndsWith(EdlConstants.DataClose, xml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSigValue_InlineForm_KeepsTheHexValueVerbatim()
    {
        var xml = FirehoseXmlBuilder.BuildSigValue("sig", "00FF10AB");

        Assert.Contains("<sig TargetName=\"sig\" value=\"00FF10AB\" verbose=\"1\" />", xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("000")]
    [InlineData("00ZZ")]
    public void BuildSigValue_ShapeThatIsNotWholeBytes_IsRefused(string value)
        => Assert.Throws<ArgumentException>(() => FirehoseXmlBuilder.BuildSigValue("sig", value));

    [Fact]
    public void BuildSig_NonPositiveSize_IsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => FirehoseXmlBuilder.BuildSig("sig", 0));

    [Fact]
    public void Classify_RefusalPrecededByAuthenticationLog_ReportsVendorAuthentication()
    {
        var response = new FirehoseResponse { IsAck = false, IsNak = true, RawValue = "NAK" };

        var error = FirehoseErrorClassifier.Classify(response, AuthenticationRefusal);

        Assert.NotNull(error);
        Assert.Equal(FirehoseErrorKind.VendorAuthentication, error!.Kind);
        Assert.False(error.IsFatal);
        Assert.Equal("before authentication", error.LogToken);
    }

    [Fact]
    public void Classify_RefusalPrecededByPlainStorageError_StaysStorage()
    {
        var response = new FirehoseResponse { IsAck = false, IsNak = true, RawValue = "NAK" };

        var error = FirehoseErrorClassifier.Classify(response, "ERROR: ufs: storage init failed");

        Assert.NotNull(error);
        Assert.Equal(FirehoseErrorKind.Storage, error!.Kind);
    }

    [Fact]
    public async Task ConfigureAsync_AuthenticationRefusal_StopsAfterOneAttempt()
    {
        var transport = new ScriptedFirehoseTransport();
        transport.EnqueueRead(Log(AuthenticationRefusal), Response("NAK"));
        using var firehose = new FirehoseProtocol(transport, FastConfiguration());

        var exception = await Assert.ThrowsAsync<EdlException>(
            () => firehose.ConfigureAsync(maxRetries: 5, TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.VendorAuthenticationRequired, exception.ErrorCode);
        var configures = transport.Writes.Count(write =>
            Encoding.UTF8.GetString(write).Contains("<configure", StringComparison.Ordinal));
        Assert.Equal(1, configures);
        Assert.False(firehose.RequiresReconnect);
    }

    [Fact]
    public async Task SendVendorSignatureAsync_AcceptedSignature_WritesDeclarationThenRawBytes()
    {
        var transport = new ScriptedFirehoseTransport();
        transport.EnqueueRead(Response("ACK", rawMode: true), Log("INFO: Target authenticated"), Response("ACK"));
        using var firehose = new FirehoseProtocol(transport, FastConfiguration());
        var signature = Signature(7);

        var outcome = await firehose.SendVendorSignatureAsync(signature, TestContext.Current.CancellationToken);

        Assert.Equal(VendorSignatureOutcome.Accepted, outcome);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Contains("size_in_bytes=\"256\"", Encoding.UTF8.GetString(transport.Writes[0]), StringComparison.Ordinal);
        Assert.Equal(signature, transport.Writes[1]);
    }

    [Fact]
    public async Task SendVendorSignatureAsync_RejectedSignature_ReportsRejectionAndKeepsSessionUsable()
    {
        var transport = new ScriptedFirehoseTransport();
        transport.EnqueueRead(Response("ACK", rawMode: true), Log("ERROR: signature invalid"), Response("NAK"));
        using var firehose = new FirehoseProtocol(transport, FastConfiguration());

        var outcome = await firehose.SendVendorSignatureAsync(Signature(3), TestContext.Current.CancellationToken);

        Assert.Equal(VendorSignatureOutcome.SignatureRejected, outcome);
        Assert.False(firehose.RequiresReconnect);
    }

    [Fact]
    public async Task SendVendorSignatureAsync_RefusedCommand_FallsBackToInlineValue()
    {
        var transport = new ScriptedFirehoseTransport();
        transport.EnqueueRead(Response("NAK"), Log("INFO: Target authenticated"), Response("ACK"));
        using var firehose = new FirehoseProtocol(transport, FastConfiguration());
        var signature = Signature(9);

        var outcome = await firehose.SendVendorSignatureAsync(signature, TestContext.Current.CancellationToken);

        Assert.Equal(VendorSignatureOutcome.Accepted, outcome);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Contains("size_in_bytes=\"256\"", Encoding.UTF8.GetString(transport.Writes[0]), StringComparison.Ordinal);
        Assert.Contains(
            $"value=\"{Convert.ToHexString(signature)}\"",
            Encoding.UTF8.GetString(transport.Writes[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendVendorSignatureAsync_WrongLength_IsRefusedBeforeAnyWrite()
    {
        var transport = new ScriptedFirehoseTransport();
        using var firehose = new FirehoseProtocol(transport, FastConfiguration());

        await Assert.ThrowsAsync<ArgumentException>(
            () => firehose.SendVendorSignatureAsync(new byte[16], TestContext.Current.CancellationToken));
        Assert.Empty(transport.Writes);
    }
}