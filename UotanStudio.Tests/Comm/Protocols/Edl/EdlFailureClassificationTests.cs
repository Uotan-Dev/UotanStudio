using UotanStudio.Comm.Protocols.Edl;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class EdlFailureClassificationTests
{
    [Fact]
    public void Classify_AttachedKindOnEdlException_WinsOverDerivedMapping()
    {
        var attached = new EdlException(EdlErrorCode.ProgramFailed, "x")
        {
            FailureKind = EdlFailureKind.Rejected
        };

        Assert.Equal(EdlFailureKind.Rejected, EdlFailureClassification.Classify(attached));
    }

    [Theory]
    [InlineData((int)EdlErrorCode.ConnectionLost, EdlFailureKind.ConnectionLost)]
    [InlineData((int)EdlErrorCode.TransportTimeout, EdlFailureKind.Timeout)]
    [InlineData((int)EdlErrorCode.ReadPortTimeout, EdlFailureKind.Timeout)]
    [InlineData((int)EdlErrorCode.WritePortTimeout, EdlFailureKind.Timeout)]
    [InlineData((int)EdlErrorCode.NotSupported, EdlFailureKind.CapabilityNotReported)]
    [InlineData((int)EdlErrorCode.ProgrammerMismatch, EdlFailureKind.ResourceMismatch)]
    [InlineData((int)EdlErrorCode.FirehoseProtocolError, EdlFailureKind.ProtocolViolation)]
    [InlineData((int)EdlErrorCode.ProgramFailed, EdlFailureKind.ExecutionUnknown)]
    public void Classify_ErrorCodes_MapToDocumentedKinds(int code, EdlFailureKind expected)
        => Assert.Equal(expected, EdlFailureClassification.Classify(
            new EdlException((EdlErrorCode)code, "failure")));

    [Fact]
    public void Classify_CancellationTimeoutAndUnknownExceptions_MapWithoutTextParsing()
    {
        Assert.Equal(EdlFailureKind.Cancelled,
            EdlFailureClassification.Classify(new OperationCanceledException()));
        Assert.Equal(EdlFailureKind.Timeout, EdlFailureClassification.Classify(new TimeoutException()));
        Assert.Equal(EdlFailureKind.ExecutionUnknown, EdlFailureClassification.Classify(new IOException("disk")));
    }

    [Fact]
    public void Classify_NullError_Throws()
        => Assert.Throws<ArgumentNullException>(() => EdlFailureClassification.Classify(null!));

    [Fact]
    public void ClassifyTransfer_CompletePayloadSent_IsCompletionLostWhateverTheError()
    {
        Assert.Equal(EdlFailureKind.CompletionLost, EdlFailureClassification.ClassifyTransfer(
            new OperationCanceledException(), transferStarted: true, payloadSent: true));
        Assert.Equal(EdlFailureKind.CompletionLost, EdlFailureClassification.ClassifyTransfer(
            new EdlException(EdlErrorCode.ProgramFailed, "x"), transferStarted: false, payloadSent: true));
    }

    [Fact]
    public void ClassifyTransfer_CancelledMidTransfer_IsCancelledAfterTransfer()
        => Assert.Equal(EdlFailureKind.CancelledAfterTransfer, EdlFailureClassification.ClassifyTransfer(
            new OperationCanceledException(), transferStarted: true, payloadSent: false));

    [Fact]
    public void ClassifyTransfer_NonCancellationFailureMidTransfer_IsExecutionUnknown()
        => Assert.Equal(EdlFailureKind.ExecutionUnknown, EdlFailureClassification.ClassifyTransfer(
            new IOException("reset"), transferStarted: true, payloadSent: false));

    [Fact]
    public void ClassifyTransfer_BeforeTransfer_DelegatesToPlainClassification()
        => Assert.Equal(EdlFailureKind.Cancelled, EdlFailureClassification.ClassifyTransfer(
            new OperationCanceledException(), transferStarted: false, payloadSent: false));
}