using System.Text;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk.Rpmb;

public sealed class MtkRpmbXmlTests
{
    private const string ReadCommand = "<da><version>1.0</version><command>CMD:CUSTOMRPMBR</command></da>";
    private const string UfsReadCommand = "<da><version>1.0</version><command>CMD:CUSTOMURPMBR</command></da>";
    private const string WriteCommand = "<da><version>1.0</version><command>CMD:CUSTOMRPMBW</command></da>";
    private const string Prolog = "<?xml version=\"1.0\" encoding=\"utf-8\"?>";

    [Fact]
    public async Task ReadXmlRpmbFramesAsync_ValidReply_SendsLiteralCommandAndKeepsFrameBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueXmlRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame(0xD1));
        var before = transport.Writes.Count;

        var result = await session.ReadXmlRpmbFramesAsync(new MtkRpmbFrameRange(4, 1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedCommand(ReadCommand), transport.Writes[before + 1]);
        Assert.Equal(MtkRpmbTestHarness.Header(4), transport.Writes[before + 2]);
        Assert.Equal([4, 0, 0, 0], transport.Writes[before + 3]);
        Assert.Equal("OK\0", Encoding.UTF8.GetString(transport.Writes[before + 5]));
        Assert.Equal(8, transport.Writes.Count - before);
        Assert.Equal(MtkRpmbTarget.Emmc, result.Target);
        Assert.Equal(8192, result.ReportedRpmbCapacityBytes);
        Assert.Single(result.Frames);
        Assert.True(result.IsComplete);
        Assert.All(result.RawBytes, value => Assert.Equal(0xD1, value));
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task ReadXmlRpmbFramesAsync_UfsGeometry_UsesUfsCommandName()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlRpmbSessionAsync(transport, ufs: true);
        MtkRpmbTestHarness.EnqueueXmlRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame(0x33));
        var before = transport.Writes.Count;

        var result = await session.ReadXmlRpmbFramesAsync(new MtkRpmbFrameRange(0, 1),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedCommand(UfsReadCommand), transport.Writes[before + 1]);
        Assert.Equal(MtkRpmbTarget.Ufs, result.Target);
        Assert.Equal(8192, result.ReportedRpmbCapacityBytes);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task ReadXmlRpmbFramesAsync_KeyNotProgrammed_StopsWithoutRetry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueXmlRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame(0), 7);
        var before = transport.Writes.Count;

        var result = await session.ReadXmlRpmbFramesAsync(new MtkRpmbFrameRange(0, 3),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(8, transport.Writes.Count - before);
        Assert.Single(result.Frames);
        Assert.Equal(MtkRpmbFrameStatus.AuthenticationKeyNotProgrammed, result.Frames[0].Outcome.Status);
        Assert.Empty(result.RawBytes);
        Assert.False(result.IsComplete);
        Assert.Contains(session.Evidence, entry => entry.Phase == "rpmb-frame" && entry.Outcome == CommandOutcomeKind.Rejected);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task ReadXmlRpmbFramesAsync_ShortFrame_FaultsWithoutRetry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueXmlText(transport, "OK");
        MtkRpmbTestHarness.EnqueueXFlashResult(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashFrame(transport, new byte[200]);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXmlRpmbFramesAsync(
            new MtkRpmbFrameRange(0, 2), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("fixed frame width", error.Message, StringComparison.Ordinal);
        Assert.Equal(4, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Fact]
    public async Task ReadXmlRpmbFramesAsync_ResultFrameTooWide_FaultsWithoutRetry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueXmlText(transport, "OK");
        MtkRpmbTestHarness.EnqueueXFlashFrame(transport, [0, 0, 1]);

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXmlRpmbFramesAsync(
            new MtkRpmbFrameRange(0, 1), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("receive limit", error.Message, StringComparison.Ordinal);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ReadXmlRpmbFramesAsync_RejectedEnvelope_FaultsWithoutRetry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueXmlText(transport, "ERR!UNSUPPORTED");

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXmlRpmbFramesAsync(
            new MtkRpmbFrameRange(0, 1), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ReadXmlRpmbFramesAsync_RpmbAbsentFromGeometry_RefusesBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlRpmbSessionAsync(transport, rpmbBytes: 0);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadXmlRpmbFramesAsync(
            new MtkRpmbFrameRange(0, 1), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Theory]
    [InlineData(30, 4)]
    [InlineData(0, 33)]
    [InlineData(0, 0)]
    public async Task ReadXmlRpmbFramesAsync_RangeOutsideReportedArea_RefusesBeforeIo(long start, int count)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlRpmbSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadXmlRpmbFramesAsync(
            new MtkRpmbFrameRange(start, count), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task ReadXmlRpmbFramesAsync_WrongSessionState_SendsNothing()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlControlSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadXmlRpmbFramesAsync(
            new MtkRpmbFrameRange(0, 1), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlControlReady, session.State);
    }

    [Fact]
    public async Task ReadXmlRpmbDeviceBlobAsync_WholeReportedArea_AssemblesEveryFrame()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlRpmbSessionAsync(transport, rpmbBytes: 512);
        MtkRpmbTestHarness.EnqueueXmlRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame(0x11));
        MtkRpmbTestHarness.EnqueueXmlRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame(0x22));
        var before = transport.Writes.Count;

        var blob = await session.ReadXmlRpmbDeviceBlobAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(512, blob.ReportedRpmbCapacityBytes);
        Assert.Equal(2, blob.FrameCount);
        Assert.Equal(512, blob.RawBytes.Length);
        Assert.True(blob.IsComplete);
        Assert.Equal(0x11, blob.RawBytes[0]);
        Assert.Equal(0x22, blob.RawBytes[256]);
        Assert.Equal(16, transport.Writes.Count - before);
    }

    [Fact]
    public async Task WriteXmlRpmbFramesAsync_OptInDisabled_RefusesBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlRpmbSessionAsync(transport);
        using var source = new MemoryStream(MtkRpmbTestHarness.RpmbFrame(0xEE));
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => session.WriteXmlRpmbFramesAsync(
            new MtkRpmbFrameRange(0, 1), source, new MtkRpmbOptions(), TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task WriteXmlRpmbFramesAsync_OptInEnabled_SendsCommandSectorAndFrame()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueXmlRpmbWriteReply(transport);
        var frame = MtkRpmbTestHarness.RpmbFrame(0xEE);
        using var source = new MemoryStream(frame);
        var before = transport.Writes.Count;

        var result = await session.WriteXmlRpmbFramesAsync(new MtkRpmbFrameRange(2, 1), source,
            new MtkRpmbOptions { AllowAuthenticatedWrites = true }, TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedCommand(WriteCommand), transport.Writes[before + 1]);
        Assert.Equal([2, 0, 0, 0], transport.Writes[before + 3]);
        Assert.Equal(frame, transport.Writes[before + 5]);
        Assert.Equal(10, transport.Writes.Count - before);
        Assert.True(result.IsComplete);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task WriteXmlRpmbFramesAsync_ShortSource_StopsWithoutReplay()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlRpmbSessionAsync(transport);
        using var source = new MemoryStream(new byte[80]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<EndOfStreamException>(() => session.WriteXmlRpmbFramesAsync(
            new MtkRpmbFrameRange(0, 1), source, new MtkRpmbOptions { AllowAuthenticatedWrites = true },
            TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task WriteXmlRpmbFramesAsync_DeviceRefusal_StopsWithoutRetry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueXmlRpmbWriteReply(transport, 5);
        using var source = new MemoryStream([.. MtkRpmbTestHarness.RpmbFrame(0x11), .. MtkRpmbTestHarness.RpmbFrame(0x22)]);

        var result = await session.WriteXmlRpmbFramesAsync(new MtkRpmbFrameRange(0, 2), source,
            new MtkRpmbOptions { AllowAuthenticatedWrites = true }, TestContext.Current.CancellationToken);

        Assert.Single(result.Frames);
        Assert.Equal(MtkRpmbFrameStatus.WriteFailure, result.Frames[0].Outcome.Status);
        Assert.False(result.IsComplete);
        Assert.Equal(256, source.Position);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task ReadXmlRpmbFramesAsync_OtherRegionValidationStillRefusesRpmb()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlRpmbSessionAsync(transport);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<NotSupportedException>(() => session.ReadXmlStorageAsync(
            "EMMC-RPMB", 0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, destination.Length);
        MtkRpmbTestHarness.EnqueueXmlRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame(0x77));
        var result = await session.ReadXmlRpmbFramesAsync(new MtkRpmbFrameRange(0, 1),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsComplete);
    }

    private static byte[] ExpectedCommand(string command)
    {
        var bytes = Encoding.UTF8.GetBytes(Prolog + command);
        return [.. bytes, 0];
    }
}