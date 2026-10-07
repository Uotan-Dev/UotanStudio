using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk.Rpmb;

public sealed class MtkRpmbXFlashTests
{
    [Fact]
    public async Task QueryXFlashRpmbStatusAsync_ValidReply_PreservesRawBytesAndControlCode()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueControlReply(transport, [1, 2, 3, 4]);
        var before = transport.Writes.Count;

        var result = await session.QueryXFlashRpmbStatusAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([.. MtkRpmbTestHarness.Word(0x010009), .. MtkRpmbTestHarness.Word(0x040010)],
            MtkRpmbTestHarness.Flatten(transport, before));
        Assert.Equal(4, transport.Writes.Count - before);
        Assert.Equal([1, 2, 3, 4], result.RawReply);
        Assert.Equal("01020304", result.RawReplyHex);
        Assert.Equal(4, result.ReplyLength);
        Assert.Equal(1536, result.ReportedRpmbCapacityBytes);
        Assert.True(result.ReportsRpmbRegion);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task QueryXFlashRpmbStatusAsync_RejectedControl_FaultsWithoutRetry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0xC0010003);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => session.QueryXFlashRpmbStatusAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0xC0010003, error.Status);
        Assert.Equal(4, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        var writes = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.QueryXFlashRpmbStatusAsync(
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(writes, transport.Writes.Count);
    }

    [Fact]
    public async Task QueryXFlashRpmbStatusAsync_StorageNotDiscovered_SendsNothing()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.QueryXFlashRpmbStatusAsync(
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStageTwoStarted, session.State);
    }

    [Fact]
    public async Task ReadXFlashRpmbFramesAsync_TwoFrames_SendsLiteralRequestsAndKeepsRawBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame(0xA1));
        MtkRpmbTestHarness.EnqueueRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame(0xA2));
        var before = transport.Writes.Count;

        var result = await session.ReadXFlashRpmbFramesAsync(new MtkRpmbFrameRange(0, 2),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([
            .. MtkRpmbTestHarness.Word(0x010009),
            .. MtkRpmbTestHarness.Word(0x0F0006),
            .. MtkRpmbTestHarness.Word(0),
            .. MtkRpmbTestHarness.Word(0x010009),
            .. MtkRpmbTestHarness.Word(0x0F0006),
            .. MtkRpmbTestHarness.Word(1)], MtkRpmbTestHarness.Flatten(transport, before));
        Assert.Equal(12, transport.Writes.Count - before);
        Assert.Equal(MtkRpmbTarget.Emmc, result.Target);
        Assert.Equal(1536, result.ReportedRpmbCapacityBytes);
        Assert.Equal(2, result.Frames.Count);
        Assert.True(result.IsComplete);
        Assert.Equal(512, result.DeliveredByteCount);
        Assert.Equal(0, result.Frames[0].Frame);
        Assert.Equal(1, result.Frames[1].Frame);
        Assert.All(result.Frames[0].RawFrame, value => Assert.Equal(0xA1, value));
        Assert.All(result.Frames[1].RawFrame, value => Assert.Equal(0xA2, value));
        Assert.Equal([.. result.Frames[0].RawFrame, .. result.Frames[1].RawFrame], result.RawBytes);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ReadXFlashRpmbFramesAsync_StartFrameIsSentAsLittleEndianWord()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame(0x5C));
        var before = transport.Writes.Count;

        var result = await session.ReadXFlashRpmbFramesAsync(new MtkRpmbFrameRange(4, 1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([.. MtkRpmbTestHarness.Word(0x010009), .. MtkRpmbTestHarness.Word(0x0F0006),
            .. MtkRpmbTestHarness.Word(4)], MtkRpmbTestHarness.Flatten(transport, before));
        Assert.Single(result.Frames);
        Assert.Equal(4, result.Frames[0].Frame);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task ReadXFlashRpmbFramesAsync_UfsGeometry_UsesUfsReadCode()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport, MtkXFlashStorageKind.Ufs);
        MtkRpmbTestHarness.EnqueueRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame(0x7E));
        var before = transport.Writes.Count;

        var result = await session.ReadXFlashRpmbFramesAsync(new MtkRpmbFrameRange(2, 1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([.. MtkRpmbTestHarness.Word(0x010009), .. MtkRpmbTestHarness.Word(0x0F0009),
            .. MtkRpmbTestHarness.Word(2)], MtkRpmbTestHarness.Flatten(transport, before));
        Assert.Equal(MtkRpmbTarget.Ufs, result.Target);
        Assert.Null(result.ReportedRpmbCapacityBytes);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task ReadXFlashRpmbFramesAsync_EmptyRpmbRegion_RefusesBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport, rpmbBytes: 0);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadXFlashRpmbFramesAsync(
            new MtkRpmbFrameRange(0, 1), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData(4, 8)]
    [InlineData(6, 1)]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    public async Task ReadXFlashRpmbFramesAsync_RangeOutsideReportedArea_RefusesBeforeIo(long start, int count)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadXFlashRpmbFramesAsync(
            new MtkRpmbFrameRange(start, count), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ReadXFlashRpmbFramesAsync_CountAboveCeiling_RefusesBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadXFlashRpmbFramesAsync(
            new MtkRpmbFrameRange(0, 2), new MtkRpmbOptions { MaximumReadFrames = 1 },
            TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData(257)]
    [InlineData(255)]
    public async Task ReadXFlashRpmbFramesAsync_WrongFrameWidth_FaultsWithoutRetry(int width)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashResult(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashFrame(transport, new byte[width]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXFlashRpmbFramesAsync(
            new MtkRpmbFrameRange(0, 1), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(6, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XFlashStorage);
    }

    [Fact]
    public async Task ReadXFlashRpmbFramesAsync_TruncatedResultFrame_FaultsWithoutRetry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashFrame(transport, [2]);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXFlashRpmbFramesAsync(
            new MtkRpmbFrameRange(0, 2), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("exactly two bytes", error.Message, StringComparison.Ordinal);
        Assert.Equal(6, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ReadXFlashRpmbFramesAsync_EmptyResponseQueue_FaultsWithoutRetry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXFlashRpmbFramesAsync(
            new MtkRpmbFrameRange(0, 1), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(2, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ReadXFlashRpmbFramesAsync_FrameRefused_StopsWithoutRetry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame(0xA1), 2);
        var before = transport.Writes.Count;

        var result = await session.ReadXFlashRpmbFramesAsync(new MtkRpmbFrameRange(0, 3),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(6, transport.Writes.Count - before);
        Assert.Single(result.Frames);
        Assert.Equal(MtkRpmbFrameStatus.AuthenticationFailure, result.Frames[0].Outcome.Status);
        Assert.Equal(2, result.Frames[0].Outcome.ReportedCode);
        Assert.Empty(result.Frames[0].RawFrame);
        Assert.Empty(result.RawBytes);
        Assert.False(result.IsComplete);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
        Assert.Contains(session.Evidence, entry => entry.Phase == "rpmb-frame" && entry.Outcome == CommandOutcomeKind.Rejected);
    }

    [Fact]
    public async Task ReadXFlashRpmbFramesAsync_UnnamedResultCode_KeepsNumberWithoutMeaning()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame(0), 9);

        var result = await session.ReadXFlashRpmbFramesAsync(new MtkRpmbFrameRange(0, 1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(9, result.Frames[0].Outcome.ReportedCode);
        Assert.Null(result.Frames[0].Outcome.Status);
        Assert.False(result.Frames[0].Outcome.IsKnown);
        Assert.False(result.Frames[0].Outcome.IsOkay);
    }

    [Fact]
    public async Task WriteXFlashRpmbFramesAsync_OptInDisabled_RefusesBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        using var source = new MemoryStream(MtkRpmbTestHarness.RpmbFrame(0xB7));
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => session.WriteXFlashRpmbFramesAsync(
            new MtkRpmbFrameRange(0, 1), source, new MtkRpmbOptions(), TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task WriteXFlashRpmbFramesAsync_OptInEnabled_SendsWholeFrameLiteral()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashResult(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        var frame = MtkRpmbTestHarness.RpmbFrame(0xB7);
        using var source = new MemoryStream(frame);
        var before = transport.Writes.Count;

        var result = await session.WriteXFlashRpmbFramesAsync(new MtkRpmbFrameRange(1, 1), source,
            new MtkRpmbOptions { AllowAuthenticatedWrites = true }, TestContext.Current.CancellationToken);

        Assert.Equal([
            .. MtkRpmbTestHarness.Word(0x010009),
            .. MtkRpmbTestHarness.Word(0x0F0007),
            .. MtkRpmbTestHarness.Word(1),
            .. MtkRpmbTestHarness.Header(256),
            .. frame], MtkRpmbTestHarness.Flatten(transport, before));
        Assert.Equal(8, transport.Writes.Count - before);
        Assert.True(result.IsComplete);
        Assert.Empty(result.Frames[0].RawFrame);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData(0x0F0007, 2)]
    [InlineData(0x0F000A, 5)]
    public async Task WriteXFlashRpmbFramesAsync_DeviceRefusal_StopsWithoutRetry(uint command, ushort code)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport,
            command == 0x0F0007 ? MtkXFlashStorageKind.Emmc : MtkXFlashStorageKind.Ufs);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashResult(transport, code);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        using var source = new MemoryStream([.. MtkRpmbTestHarness.RpmbFrame(0x11), .. MtkRpmbTestHarness.RpmbFrame(0x22)]);
        var before = transport.Writes.Count;

        var result = await session.WriteXFlashRpmbFramesAsync(new MtkRpmbFrameRange(0, 2), source,
            new MtkRpmbOptions { AllowAuthenticatedWrites = true }, TestContext.Current.CancellationToken);

        Assert.Equal(MtkRpmbTestHarness.Word(command), MtkRpmbTestHarness.Flatten(transport, before)[16..32]);
        Assert.Equal(8, transport.Writes.Count - before);
        Assert.Single(result.Frames);
        Assert.False(result.IsComplete);
        Assert.Equal(code, result.Frames[0].Outcome.ReportedCode);
        Assert.Equal(256, source.Position);
    }

    [Fact]
    public async Task WriteXFlashRpmbFramesAsync_ShortSource_StopsWithoutReplay()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        using var source = new MemoryStream(new byte[100]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<EndOfStreamException>(() => session.WriteXFlashRpmbFramesAsync(
            new MtkRpmbFrameRange(0, 1), source, new MtkRpmbOptions { AllowAuthenticatedWrites = true },
            TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task WriteXFlashRpmbFramesAsync_CountAboveWriteCeiling_RefusesBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        using var source = new MemoryStream(Enumerable.Repeat<byte>(0x5A, 1536).ToArray());
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.WriteXFlashRpmbFramesAsync(
            new MtkRpmbFrameRange(0, 3), source,
            new MtkRpmbOptions { AllowAuthenticatedWrites = true, MaximumWriteFrames = 2 },
            TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task ReadXFlashRpmbDeviceBlobAsync_FullArea_AssemblesEveryFrame()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        for (var index = 0; index < 6; index++)
        {
            MtkRpmbTestHarness.EnqueueRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame((byte)(0xA0 + index)));
        }

        var blob = await session.ReadXFlashRpmbDeviceBlobAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(MtkRpmbTarget.Emmc, blob.Target);
        Assert.Equal(1536, blob.ReportedRpmbCapacityBytes);
        Assert.Equal(6, blob.FrameCount);
        Assert.Equal(1536, blob.RawBytes.Length);
        Assert.True(blob.IsComplete);
        Assert.Equal(0xA0, blob.RawBytes[0]);
        Assert.Equal(0xA5, blob.RawBytes[1535]);
    }

    [Fact]
    public async Task ReadXFlashRpmbDeviceBlobAsync_EmptyReportedArea_RefusesBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport,
            rpmbBytes: 0);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadXFlashRpmbDeviceBlobAsync(
            new MtkRpmbOptions { MaximumReadFrames = 4096 }, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ReadXFlashRpmbFramesAsync_PreCancelled_RefusesBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadXFlashRpmbFramesAsync(
            new MtkRpmbFrameRange(0, 1), cancellationToken: cancellation.Token));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ReadXFlashRpmbFramesAsync_OrdinaryRangeValidationStillRefusesRpmb()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => session.ReadXFlashStorageAsync(
            3, 0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(0, destination.Length);
        MtkRpmbTestHarness.EnqueueRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame(0x9F));
        var result = await session.ReadXFlashRpmbFramesAsync(new MtkRpmbFrameRange(0, 1),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsComplete);
    }

}