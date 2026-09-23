using System.Text;
using System.Xml.Linq;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

public partial class MtkSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EraseXmlStorageAsync_CompleteProgressAndTrailingStatus_PreservesSynchronization(bool ufs)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport, ufs);
        var before = transport.Writes.Count;
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlControl("PROGRESS-REPORT"));
        EnqueueXmlText(transport, "0");
        EnqueueXmlText(transport, "100");
        EnqueueXmlText(transport, "OK!EOT");
        EnqueueXmlText(transport, XmlControl("END", "OK"));
        EnqueueXmlText(transport, XmlControl("START"));
        EnqueueXmlText(transport, "OK");
        var region = ufs ? "UFS-LUA2" : "EMMC-USER";

        await session.EraseXmlStorageAsync(region, 512, 1024, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(20, transport.Writes.Count - before);
        var frames = DecodeXmlWriteFrames(transport.Writes.Skip(before));
        var command = XDocument.Parse(Encoding.UTF8.GetString(frames[3]).TrimEnd('\0')).Root!;
        Assert.Equal("CMD:ERASE-FLASH", command.Element("command")!.Value);
        Assert.Equal(region, command.Element("arg")!.Element("partition")!.Value);
        Assert.Equal("0x200", command.Element("arg")!.Element("offset")!.Value);
        Assert.Equal("0x400", command.Element("arg")!.Element("length")!.Value);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
        Assert.NotNull(session.XmlStorage);
        EnqueueXmlCompletion(transport);
        await session.SetXmlHostInfoAsync("after erase", TestContext.Current.CancellationToken);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task EraseXmlStorageAsync_LargeRange_EmitsExact64BitFields()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport, capacity: 0x300000000);
        EnqueueXmlErase(transport);
        var before = transport.Writes.Count;

        await session.EraseXmlStorageAsync("EMMC-USER", 0x100000000, 0x100000200,
            cancellationToken: TestContext.Current.CancellationToken);

        var frames = DecodeXmlWriteFrames(transport.Writes.Skip(before));
        var command = XDocument.Parse(Encoding.UTF8.GetString(frames[3]).TrimEnd('\0')).Root!;
        Assert.Equal("0x100000000", command.Element("arg")!.Element("offset")!.Value);
        Assert.Equal("0x100000200", command.Element("arg")!.Element("length")!.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task EraseXmlStorageAsync_RejectedOrMissingPhase_FaultsWithoutRetry(int phase)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        if (phase == 0)
        {
            EnqueueXmlText(transport, "ERR!REJECTED");
        }
        else
        {
            EnqueueXmlCompletion(transport);
            EnqueueXmlText(transport, phase == 1 ? "ERR!REJECTED" : "OK");
            EnqueueXmlText(transport, XmlControl("END", phase == 2 ? "NOT_OK" : "OK"));
            EnqueueXmlText(transport, XmlControl(phase == 3 ? "END" : "START"));
            if (phase != 5)
            {
                EnqueueXmlText(transport, phase == 4 ? "ERR!NOT_OK" : "OK");
            }
        }

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseXmlStorageAsync(
            "EMMC-USER", 0, 512, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
        var before = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EraseXmlStorageAsync(
            "EMMC-USER", 0, 512, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(before, transport.Writes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EraseXmlStorageAsync_ProgressLimit_FaultsWithoutAssumingCompletion(bool reports)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        if (reports)
        {
            for (var index = 0; index < 17; index++)
            {
                EnqueueXmlText(transport, XmlControl("PROGRESS-REPORT"));
                EnqueueXmlText(transport, "OK!EOT");
            }
        }
        else
        {
            EnqueueXmlText(transport, XmlControl("PROGRESS-REPORT"));
            for (var index = 0; index < 257; index++)
            {
                EnqueueXmlText(transport, "100");
            }
        }

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseXmlStorageAsync(
            "EMMC-USER", 0, 512, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Fact]
    public async Task EraseXmlStorageAsync_FileOperationRequested_RejectsWithoutFileResponse()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlFileSizeRequest(512).ToString(SaveOptions.DisableFormatting));
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseXmlStorageAsync(
            "EMMC-USER", 0, 512, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(8, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task EraseXmlStorageAsync_ProgressError_StopsAcknowledgingAndFaults()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
        EnqueueXmlText(transport, XmlControl("PROGRESS-REPORT"));
        EnqueueXmlText(transport, "ERR!FAILED");
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseXmlStorageAsync(
            "EMMC-USER", 0, 512, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(10, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XmlStorage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EraseXmlStorageAsync_Cancellation_DistinguishesPreIoAndDispatchedErase(bool dispatched)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (dispatched)
        {
            EnqueueXmlCompletion(transport);
            transport.OnWrite = bytes =>
            {
                if (Encoding.UTF8.GetString(bytes).Contains("CMD:ERASE-FLASH", StringComparison.Ordinal))
                {
                    cancellation.Cancel();
                }
                return [];
            };
        }
        else
        {
            cancellation.Cancel();
        }
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.EraseXmlStorageAsync(
            "EMMC-USER", 0, 512, cancellationToken: cancellation.Token));

        Assert.Equal(dispatched ? 8 : 0, transport.Writes.Count - before);
        Assert.Equal(dispatched ? MtkSessionState.Faulted : MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task XmlMutationsAsync_QueuedErase_WaitsForCompleteWrite()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        using var source = new PausedStorageSource();
        EnqueueXmlWrite(transport, 512, 512);
        EnqueueXmlErase(transport);
        var write = session.WriteXmlStorageAsync("EMMC-USER", 0, 512, source,
            cancellationToken: TestContext.Current.CancellationToken);
        await source.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task erase;
        try
        {
            var before = transport.Writes.Count;
            erase = session.EraseXmlStorageAsync("EMMC-USER", 512, 512,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(erase.IsCompleted);
            Assert.Equal(before, transport.Writes.Count);
        }
        finally
        {
            source.Resume.TrySetResult();
            await write;
        }
        await erase;
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(86400001)]
    public async Task XmlMutationsAsync_InvalidDeadline_RejectsBeforeIo(int timeoutMs)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStorageSessionAsync(transport);
        using var source = new MemoryStream(new byte[512]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.WriteXmlStorageAsync(
            "EMMC-USER", 0, 512, source, timeoutMs, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.EraseXmlStorageAsync(
            "EMMC-USER", 0, 512, timeoutMs, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlStorageGeometryKnown, session.State);
    }

    [Fact]
    public async Task XmlMutationsAsync_NotDiscovered_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateXmlStageTwoHardwareSessionAsync(transport);
        using var source = new MemoryStream(new byte[512]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteXmlStorageAsync(
            "EMMC-USER", 0, 512, source, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EraseXmlStorageAsync(
            "EMMC-USER", 0, 512, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlStageTwoHardwareInitialized, session.State);
    }

    private static void EnqueueXmlErase(ScriptedCommTransport transport)
    {
        EnqueueXmlCompletion(transport);
        EnqueueXmlCompletion(transport);
        EnqueueXmlText(transport, "OK");
    }
}